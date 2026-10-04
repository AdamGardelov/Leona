using System.Text;

namespace Harness.Services;

// File access inside a root folder: the workspace, or a folder the user added in Settings.
public class WorkspaceFiles(IHostEnvironment environment, IConfiguration configuration)
{
    public const int MaxReadBytes = 2_000_000;
    public const int MaxCreateBytes = 32_000;

    public string Root { get; } = Path.GetFullPath(configuration["Tools:WorkspacePath"] ??
                                                   Path.Combine(environment.ContentRootPath, "workspace"));

    // Rejects absolute paths, parent traversal, hidden segments (.git, .env, .ssh) and symbolic links.
    public static string Resolve(string root, string path)
    {
        Directory.CreateDirectory(root);
        if (Path.IsPathRooted(path) || path.Split('/', '\\').Any(p => p == ".." || p.StartsWith('.')))
            throw new ArgumentException("Use a relative path without hidden folders or parent traversal.");
        var full = Path.GetFullPath(Path.Combine(root, path));
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Path is outside the allowed folder.");
        for (var current = full; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Symbolic links are not allowed in tool paths.");
            }

            if (current == root)
                break;
        }

        return full;
    }

    // Lists one folder; subfolders end with a slash so the model can list them next.
    public string[] List(string path = "") => List(Root, path);

    public static string[] List(string root, string path)
    {
        var folder = Resolve(root, path);
        if (!Directory.Exists(folder))
            throw new ArgumentException("Folder not found.");

        return Directory.EnumerateFileSystemEntries(folder)
            .Where(p => !Path.GetFileName(p).StartsWith('.'))
            .Order(StringComparer.Ordinal)
            .Take(200)
            .Select(p => Directory.Exists(p) ? Path.GetFileName(p) + "/" : Path.GetFileName(p))
            .ToArray();
    }

    public Task<string> ReadAsync(string path, CancellationToken ct) => ReadAsync(Root, path, ct);

    public static async Task<string> ReadAsync(string root, string path, CancellationToken ct)
    {
        var full = Resolve(root, path);
        if (!File.Exists(full))
            throw new ArgumentException("File not found.");
        if (new FileInfo(full).Length > MaxReadBytes)
            throw new ArgumentException("File exceeds the 2 MB text limit.");
        string text;
        try
        {
            text = await File.ReadAllTextAsync(full, new UTF8Encoding(false, true), ct);
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("Only UTF-8 text files are supported. Use read_document for PDF and Word files.");
        }

        return text.Contains('\0') ? throw new ArgumentException("Only text files are supported.") : text;
    }

    public Task<string> CreateAsync(string path, string content, CancellationToken ct) =>
        CreateAsync(Root, path, content, ct);

    public static async Task<string> CreateAsync(string root, string path, string content, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxCreateBytes)
            throw new ArgumentException("File exceeds the 32 KB text limit.");
        var full = Resolve(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        Resolve(root, path);
        await using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content.AsMemory(), ct);
        await writer.FlushAsync(ct);
        return $"Created {path}";
    }
}
