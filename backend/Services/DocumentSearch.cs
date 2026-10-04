using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Turns text into vectors with a small embedding model in Ollama (Search:EmbeddingModel), which fits on the
// graphics card next to the chat model. Ollama returns them normalized, so a dot product is the cosine.
public sealed class Embeddings(HttpClient client, IConfiguration configuration)
{
    // Always the same context, or Ollama reloads the model; passages are well under it.
    private const int Context = 1024;
    private static (DateTime At, bool Installed) s_installed;

    public string Model => configuration["Search:EmbeddingModel"] ?? "qwen3-embedding:0.6b";

    public async Task<bool> AvailableAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - s_installed.At < TimeSpan.FromMinutes(5))
            return s_installed.Installed;
        try
        {
            using var json = JsonDocument.Parse(await client.GetStringAsync("/api/tags", ct));
            var installed = json.RootElement.GetProperty("models").EnumerateArray()
                .Any(m => JsonPath.Text(m, "name") == Model || JsonPath.Text(m, "name") == Model + ":latest");
            s_installed = (DateTime.UtcNow, installed);
            return installed;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = Model,
            input = texts,
            truncate = true,
            keep_alive = "30m",
            options = new { num_ctx = Context }
        });
        using var response = await client.PostAsync("/api/embed", new StringContent(body, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("embeddings").EnumerateArray()
            .Select(v => v.EnumerateArray().Select(x => x.GetSingle()).ToArray()).ToArray();
    }

    // Questions are embedded with an instruction, as the model expects; passages as they are.
    public async Task<float[]> QueryAsync(string query, CancellationToken ct) =>
        (await EmbedAsync(["Instruct: Given a question, find passages in the user's documents that answer it\nQuery: " + query], ct))[0];

    public static byte[] Pack(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] Unpack(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}

// Finds passages in the current profile's indexed documents by meaning, with a nudge for passages that
// contain the question's own words (names and numbers are often what matters).
public sealed class DocumentIndex(ChatDb db, Embeddings embeddings)
{
    public record Hit(IndexedDocument Document, string Location, string Text, double Score);

    // Vectors per profile, kept until the index changes; a personal library fits in memory easily.
    private static readonly ConcurrentDictionary<int, (string Version, List<(int Id, int Document, float[] Vector)> Rows)> s_vectors = new();

    public Task<bool> AnyAsync(CancellationToken ct) => db.DocumentChunks.AnyAsync(ct);

    public async Task<List<Hit>> SearchAsync(string query, int? projectId, int limit, CancellationToken ct)
    {
        var rows = await VectorsAsync(ct);
        if (rows.Count == 0)
            return [];
        var question = await embeddings.QueryAsync(query, ct);
        var candidates = rows.Select(r => (r.Id, r.Document, Score: Dot(question, r.Vector)))
            .OrderByDescending(r => r.Score).Take(40).ToList();
        var ids = candidates.Select(c => c.Id).ToList();
        var texts = await db.DocumentChunks.AsNoTracking().Where(c => ids.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, ct);
        var documentIds = candidates.Select(c => c.Document).Distinct().ToList();
        var documents = await db.Documents.AsNoTracking().Where(d => documentIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var words = TextMatch.Words(query).Where(w => w.Length >= 3).Distinct().ToList();

        return candidates
            .Where(c => texts.ContainsKey(c.Id) && documents.ContainsKey(c.Document))
            .Select(c =>
            {
                var chunk = texts[c.Id];
                var document = documents[c.Document];
                var shared = words.Count == 0 ? 0 : words.Count(w => chunk.Text.Contains(w, StringComparison.OrdinalIgnoreCase)) / (double)words.Count;
                // In a project chat, the project's own files come first when they fit about as well.
                var inProject = projectId is not null && document.ProjectId == projectId ? 0.05 : 0;
                return new Hit(document, chunk.Location, chunk.Text, c.Score + 0.08 * shared + inProject);
            })
            .OrderByDescending(h => h.Score)
            // At most two passages from one document, so one long file does not crowd out the rest.
            .GroupBy(h => h.Document.Id).SelectMany(g => g.Take(2))
            .OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    private async Task<List<(int Id, int Document, float[] Vector)>> VectorsAsync(CancellationToken ct)
    {
        var profile = db.ProfileId ?? 0;
        var count = await db.DocumentChunks.CountAsync(ct);
        var version = count == 0 ? "0" : $"{count}:{await db.DocumentChunks.MaxAsync(c => c.Id, ct)}";
        if (s_vectors.TryGetValue(profile, out var cached) && cached.Version == version)
            return cached.Rows;
        var rows = (await db.DocumentChunks.AsNoTracking().Select(c => new { c.Id, c.DocumentId, c.Vector }).ToListAsync(ct))
            .Select(c => (c.Id, c.DocumentId, Embeddings.Unpack(c.Vector))).ToList();
        s_vectors[profile] = (version, rows);
        return rows;
    }

    private static double Dot(float[] a, float[] b)
    {
        var sum = 0.0;
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
            sum += a[i] * b[i];
        return sum;
    }

    // Pages (PDF), paragraphs (Word) or lines (text) gathered into passages of about a thousand characters;
    // each passage remembers where it starts.
    public static List<(string Location, string Text)> Chunk(DocumentReader.Extracted document, string unit)
    {
        const int size = 1000;
        var passages = new List<(string, string)>();
        var text = new StringBuilder();
        var start = 0;

        void Flush()
        {
            if (text.Length > 0)
                passages.Add(($"{unit} {start + 1}", text.ToString().Trim()));
            text.Clear();
        }

        for (var i = 0; i < document.Parts.Count; i++)
        {
            var part = TextMatch.Collapse(document.Parts[i]);
            if (part.Length == 0)
                continue;
            if (part.Length > size)
            {
                // A long page is split on its own, at spaces.
                Flush();
                for (var at = 0; at < part.Length;)
                {
                    var end = Math.Min(part.Length, at + size);
                    var space = part.LastIndexOf(' ', end - 1, end - at);
                    if (end < part.Length && space > at)
                        end = space;
                    passages.Add(($"{unit} {i + 1}", part[at..end].Trim()));
                    at = end;
                }

                continue;
            }

            if (text.Length + part.Length > size)
                Flush();
            if (text.Length == 0)
                start = i;
            text.Append(part).Append(' ');
        }

        Flush();
        return passages;
    }
}

// Keeps the search index in step with the documents: the owner's folders (Settings › Folders), uploaded
// documents and project files. Runs every ten minutes, and soon after something is added.
public sealed class DocumentIndexer(
    IServiceScopeFactory scopes,
    Embeddings embeddings,
    GenerationGate gate,
    ILogger<DocumentIndexer> logger) : BackgroundService
{
    public static readonly string[] Extensions = [".pdf", ".docx", ".txt", ".md", ".csv"];
    private const long MaxFileBytes = 20_000_000;
    private const int MaxFiles = 5000;
    private const int Batch = 32;
    private static readonly HashSet<string> s_skipped =
        ["node_modules", "bin", "obj", "dist", "build", "venv", ".venv", "__pycache__", "target"];

    private readonly Channel<bool> _wake = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public bool Working { get; private set; }

    public void Trigger() => _wake.Writer.TryWrite(true);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), stop);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    Working = true;
                    await IndexAllAsync(stop);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Indexing documents failed");
                }
                finally
                {
                    Working = false;
                }

                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stop);
                wait.CancelAfter(TimeSpan.FromMinutes(10));
                try
                {
                    await _wake.Reader.ReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested)
                {
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task IndexAllAsync(CancellationToken stop)
    {
        if (!await embeddings.AvailableAsync(stop))
            return;
        List<Profile> profiles;
        await using (var scope = scopes.CreateAsyncScope())
            profiles = await scope.ServiceProvider.GetRequiredService<ChatDb>().Profiles.AsNoTracking().ToListAsync(stop);
        foreach (var profile in profiles)
            await IndexProfileAsync(profile, stop);
    }

    private record Candidate(string Source, string Key, string Name, string Path, int? ProjectId, long Size, DateTime Modified);

    public async Task IndexProfileAsync(Profile profile, CancellationToken stop)
    {
        await using var scope = scopes.CreateAsyncScope();
        var current = scope.ServiceProvider.GetRequiredService<CurrentProfile>();
        current.Id = profile.Id;
        current.Owner = profile.Owner;
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var uploads = scope.ServiceProvider.GetRequiredService<UploadStore>();

        var candidates = new List<Candidate>();
        foreach (var upload in await db.Uploads.AsNoTracking().Where(u => u.Kind == UploadKind.Document).ToListAsync(stop))
        {
            var path = uploads.PathFor(upload);
            if (File.Exists(path) && Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                candidates.Add(new Candidate(DocumentSource.Upload, upload.Id.ToString(), upload.Name, path, upload.ProjectId,
                    upload.Size, upload.CreatedAt));
        }

        // Folders work on the owner's files, like the file tools.
        if (profile.Owner)
        {
            foreach (var folder in await db.Folders.AsNoTracking().ToListAsync(stop))
                candidates.AddRange(FilesIn(folder.Path).Take(MaxFiles).Select(file => new Candidate(DocumentSource.Folder, file.FullName,
                    $"{folder.Name}/{Path.GetRelativePath(folder.Path, file.FullName)}", file.FullName, null, file.Length,
                    file.LastWriteTimeUtc)));
        }

        var known = await db.Documents.ToListAsync(stop);
        var wanted = candidates.Take(MaxFiles).ToList();
        var gone = known.Where(d => !wanted.Any(c => c.Source == d.Source && c.Key == d.Key)).ToList();
        if (gone.Count > 0)
        {
            db.Documents.RemoveRange(gone);
            await db.SaveChangesAsync(stop);
        }

        foreach (var candidate in wanted)
        {
            var document = known.FirstOrDefault(d => d.Source == candidate.Source && d.Key == candidate.Key);
            if (document is not null && document.Size == candidate.Size && document.Modified == candidate.Modified &&
                document.ProjectId == candidate.ProjectId)
                continue;
            if (document is null)
            {
                document = new IndexedDocument { Source = candidate.Source, Key = candidate.Key };
                db.Documents.Add(document);
            }

            document.Name = candidate.Name;
            document.ProjectId = candidate.ProjectId;
            document.Size = candidate.Size;
            document.Modified = candidate.Modified;
            document.IndexedAt = DateTime.UtcNow;
            document.Error = null;
            await db.SaveChangesAsync(stop);
            await db.DocumentChunks.Where(c => c.DocumentId == document.Id).ExecuteDeleteAsync(stop);
            try
            {
                var unit = Path.GetExtension(candidate.Path).ToLowerInvariant() switch
                {
                    ".pdf" => "page",
                    ".docx" => "paragraph",
                    _ => "line"
                };
                var passages = DocumentIndex.Chunk(DocumentReader.Extract(candidate.Path), unit).Take(2000).ToList();
                // Uploads are few and wanted at once; a folder scan waits until nobody is using the model.
                var background = candidate.Source == DocumentSource.Folder;
                for (var at = 0; at < passages.Count; at += Batch)
                {
                    var part = passages.Skip(at).Take(Batch).ToList();
                    var vectors = await EmbedAsync(part.Select(p => $"{candidate.Name}\n{p.Text}").ToList(), background, stop);
                    db.DocumentChunks.AddRange(part.Select((p, i) => new DocumentChunk
                    {
                        DocumentId = document.Id,
                        Part = at + i,
                        Location = p.Location,
                        Text = p.Text,
                        Vector = Embeddings.Pack(vectors[i])
                    }));
                    await db.SaveChangesAsync(stop);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                document.Error = ex.Message;
                await db.SaveChangesAsync(stop);
            }
        }
    }

    private async Task<float[][]> EmbedAsync(List<string> texts, bool background, CancellationToken stop)
    {
        if (!background)
            return await embeddings.EmbedAsync(texts, stop);
        while (true)
        {
            var yield = await gate.EnterBackgroundAsync(stop);
            try
            {
                using var both = CancellationTokenSource.CreateLinkedTokenSource(stop, yield);
                return await embeddings.EmbedAsync(texts, both.Token);
            }
            catch (OperationCanceledException) when (yield.IsCancellationRequested && !stop.IsCancellationRequested)
            {
                // Someone started chatting; try again when the model is free.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private static IEnumerable<FileInfo> FilesIn(string root)
    {
        var folders = new Stack<string>([root]);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            string[] subfolders;
            string[] files;
            try
            {
                subfolders = Directory.GetDirectories(folder);
                files = Directory.GetFiles(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var sub in subfolders)
            {
                var name = Path.GetFileName(sub);
                if (!name.StartsWith('.') && !s_skipped.Contains(name) && !new FileInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                    folders.Push(sub);
            }

            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (!info.Name.StartsWith('.') && Extensions.Contains(info.Extension.ToLowerInvariant()) &&
                    info.Length is > 0 and <= MaxFileBytes && !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    yield return info;
            }
        }
    }
}
