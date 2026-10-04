using System.Text;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Stores attached files outside the workspace. Images are sent to vision models; documents are
// read into the message and stay readable through the "uploads" folder when file tools are on.
public sealed class UploadStore(IHostEnvironment environment, IConfiguration configuration)
{
    public const long MaxBytes = 20_000_000;
    public const int MaxPerMessage = 8;
    public const string FolderName = "uploads";

    public string Root { get; } = Path.GetFullPath(configuration["Tools:UploadsPath"] ??
                                                   Path.Combine(environment.ContentRootPath, "uploads"));

    private static readonly Dictionary<string, string> s_documents = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".json"] = "application/json",
        [".log"] = "text/plain"
    };

    // Each profile's files live in their own folder, which is all file tools see as "uploads".
    public string RootFor(int profileId) => Path.Combine(Root, profileId.ToString());

    public string PathFor(int profileId, Guid id, string name) =>
        Path.Combine(RootFor(profileId), id.ToString("N"), name);

    public string PathFor(Upload upload) => PathFor(upload.ProfileId, upload.Id, upload.Name);

    // Checks the content, not just the name: images must be JPEG or PNG (the browser converts others),
    // documents must match their format, and text must be UTF-8.
    public static (string Kind, string Mime) Classify(string name, byte[] head)
    {
        bool Starts(params byte[] magic) => head.Length >= magic.Length && head.AsSpan(0, magic.Length).SequenceEqual(magic);
        if (Starts(0xFF, 0xD8, 0xFF))
            return (UploadKind.Image, "image/jpeg");
        if (Starts(0x89, 0x50, 0x4E, 0x47))
            return (UploadKind.Image, "image/png");

        var extension = Path.GetExtension(name);
        if (!s_documents.TryGetValue(extension, out var mime))
            throw new ArgumentException("Attach JPEG or PNG images, PDF, Word (.docx) or plain-text files.");
        var valid = extension.ToLowerInvariant() switch
        {
            ".pdf" => Starts((byte)'%', (byte)'P', (byte)'D', (byte)'F'),
            ".docx" => Starts((byte)'P', (byte)'K', 3, 4),
            _ => !head.Contains((byte)0)
        };
        if (!valid)
            throw new ArgumentException($"The file does not look like a {extension} file.");
        return (UploadKind.Document, mime);
    }

    public static string SafeName(string name)
    {
        var file = Path.GetFileName(name.Replace('\\', '/')).Trim();
        var cleaned = new string(file.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or ' ' ? c : '_').ToArray())
            .TrimStart('.', ' ');
        if (cleaned.Length == 0)
            cleaned = "file";
        return cleaned.Length <= 100 ? cleaned : cleaned[^100..];
    }

    public async Task<Upload> SaveAsync(ChatDb db, string name, Stream content, long length, CancellationToken ct)
    {
        if (length is <= 0 or > MaxBytes)
            throw new ArgumentException("Files can be at most 20 MB.");
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var (kind, mime) = Classify(name, bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).ToArray());
        if (mime.StartsWith("text/") || mime == "application/json")
        {
            try
            {
                _ = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                throw new ArgumentException("Text files must be UTF-8.");
            }
        }

        var safe = SafeName(name);
        if (kind == UploadKind.Image)
            safe = Path.ChangeExtension(safe, mime == "image/png" ? ".png" : ".jpg");
        var upload = new Upload
        {
            ProfileId = db.ProfileId ?? throw new InvalidOperationException("Uploads need a profile."),
            Name = safe,
            Kind = kind,
            Mime = mime,
            Size = bytes.Length
        };
        var path = PathFor(upload);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        db.Uploads.Add(upload);
        await db.SaveChangesAsync(ct);
        return upload;
    }

    public static AttachmentRef Reference(Upload upload) =>
        new(upload.Id, upload.Name, upload.Kind, upload.Mime, upload.Size);

    // Removes uploads that no message refers to after a day, and stray folders without a record.
    public async Task CleanupAsync(ChatDb db, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow.AddDays(-1);
        var old = await db.Uploads.Where(u => u.CreatedAt < cutoff).ToListAsync(ct);
        foreach (var upload in old)
        {
            var id = upload.Id.ToString();
            if (await db.Messages.AnyAsync(m => m.AttachmentsJson.Contains(id), ct))
                continue;
            db.Uploads.Remove(upload);
            DeleteFolder(Path.Combine(RootFor(upload.ProfileId), upload.Id.ToString("N")));
        }

        await db.SaveChangesAsync(ct);
        if (!Directory.Exists(Root))
            return;
        var ids = await db.Uploads.AsNoTracking().Select(u => u.Id).ToListAsync(ct);
        var known = ids.Select(id => id.ToString("N")).ToHashSet();
        // The root holds one folder per profile and nothing else.
        foreach (var folder in Directory.EnumerateDirectories(Root).ToList())
        {
            if (!int.TryParse(Path.GetFileName(folder), out _))
                DeleteFolder(folder);
        }

        foreach (var profileFolder in Directory.EnumerateDirectories(Root))
        {
            foreach (var folder in Directory.EnumerateDirectories(profileFolder))
            {
                if (!known.Contains(Path.GetFileName(folder)))
                    DeleteFolder(folder);
            }
        }
    }

    public void DeleteProfile(int profileId) => DeleteFolder(RootFor(profileId));

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, true);
    }
}
