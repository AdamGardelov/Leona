using System.Security.Cryptography;
using System.Text;
using Harness.Contracts;

namespace Harness.Services;

// Searching, windowed reading and exact-patch editing of text files inside an allowed folder.
public static class FileTools
{
    private const int MaxSearchFiles = 5000;
    private const int MaxSearchFileBytes = 1_000_000;
    private const long MaxSearchTotalBytes = 100_000_000;
    private const int MaxEditBytes = 1_000_000;
    private static readonly HashSet<string> s_skippedFolders =
        ["node_modules", "bin", "obj", "dist", "build", "target", "__pycache__", "venv", "coverage"];

    public static ToolResult Search(string root, string path, string query, string? glob, int maxResults = 60)
    {
        if (query.Trim().Length < 2)
            throw new ArgumentException("Search for at least two characters.");
        var start = WorkspaceFiles.Resolve(root, path);
        if (!Directory.Exists(start))
            throw new ArgumentException("Folder not found.");

        var results = new List<string>();
        var matchedFiles = new HashSet<string>();
        var scanned = 0;
        long scannedBytes = 0;
        var truncated = false;
        var pending = new Stack<string>([start]);
        while (pending.Count > 0 && !truncated)
        {
            var folder = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).Order(StringComparer.Ordinal))
            {
                var name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                // Hidden entries, links and generated folders are never searched.
                if (name.StartsWith('.') || (attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!s_skippedFolders.Contains(name))
                        pending.Push(entry);
                    continue;
                }

                if (glob is not null &&
                    !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(glob, name, ignoreCase: true))
                    continue;
                scannedBytes += new FileInfo(entry).Length;
                if (++scanned > MaxSearchFiles || scannedBytes > MaxSearchTotalBytes)
                {
                    truncated = true;
                    break;
                }

                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add($"{relative}  (file name matches)");
                    matchedFiles.Add(relative);
                }

                foreach (var (number, line) in MatchingLines(entry, query))
                {
                    results.Add($"{relative}:{number}: {line}");
                    matchedFiles.Add(relative);
                    if (results.Count >= maxResults)
                        break;
                }

                if (results.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
            }
        }

        if (results.Count == 0)
            return new ToolResult($"No files or lines matched \"{query}\".", Summary: "No matches");
        var footer = truncated
            ? $"\n\n[Search stopped early ({results.Count} matches, {scanned:N0} files). Narrow it with path or glob.]"
            : "";
        return new ToolResult(string.Join('\n', results) + footer,
            Summary: $"{results.Count} {(results.Count == 1 ? "match" : "matches")} in {matchedFiles.Count} {(matchedFiles.Count == 1 ? "file" : "files")}");
    }

    private static IEnumerable<(int Number, string Line)> MatchingLines(string file, string query)
    {
        var info = new FileInfo(file);
        if (info.Length > MaxSearchFileBytes)
            yield break;
        var bytes = File.ReadAllBytes(file);
        // Binary files are matched by name only.
        if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            yield break;
        var number = 0;
        foreach (var line in Encoding.UTF8.GetString(bytes).Split('\n'))
        {
            number++;
            if (!line.Contains(query, StringComparison.OrdinalIgnoreCase))
                continue;
            var trimmed = line.Trim();
            yield return (number, trimmed.Length <= 200 ? trimmed : trimmed[..200] + "…");
        }
    }

    // Small files are returned whole; larger ones in line windows of about `limit` characters.
    public static ToolResult ReadWindow(string path, string text, int startLine, int limit)
    {
        if (text.Length <= limit && startLine <= 1)
            return new ToolResult(text, Summary: $"Read {text.Length:N0} characters");

        var lines = text.Split('\n');
        var first = Math.Clamp(startLine, 1, Math.Max(1, lines.Length));
        var last = first - 1;
        var used = 0;
        while (last < lines.Length && (used + lines[last].Length + 1 <= limit || last == first - 1))
        {
            used += lines[last].Length + 1;
            last++;
        }

        var body = string.Join('\n', lines[(first - 1)..last]);
        var footer = last < lines.Length
            ? $"\n\n[Lines {first}–{last} of {lines.Length}. Call read_file with start_line={last + 1} to continue.]"
            : $"\n\n[End of file. Lines {first}–{last} of {lines.Length}.]";
        return new ToolResult($"{path} (lines {first}–{last} of {lines.Length})\n\n{body}{footer}",
            Summary: $"Lines {first:N0}–{last:N0} of {lines.Length:N0}");
    }

    public record EditPlan(string FullPath, string NewContent, string Diff, string Fingerprint, bool Bom);

    // Validates an exact replacement and builds its diff. The old text must occur exactly once.
    public static EditPlan PlanEdit(string root, string path, string oldText, string newText)
    {
        if (oldText.Length == 0)
            throw new ArgumentException("old_text must not be empty. Use create_file for new files.");
        var full = WorkspaceFiles.Resolve(root, path);
        if (!File.Exists(full))
            throw new ArgumentException("File not found. Use create_file for new files.");
        var bytes = File.ReadAllBytes(full);
        if (bytes.Length > MaxEditBytes)
            throw new ArgumentException("File exceeds the 1 MB edit limit.");
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string content;
        try
        {
            content = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        }
        catch (DecoderFallbackException)
        {
            throw new ArgumentException("Only UTF-8 text files can be edited.");
        }

        // Models write \n; match files that use Windows line endings as well.
        if (content.Contains("\r\n") && !oldText.Contains("\r\n"))
        {
            oldText = oldText.Replace("\n", "\r\n");
            newText = newText.Replace("\n", "\r\n");
        }

        var index = content.IndexOf(oldText, StringComparison.Ordinal);
        if (index < 0)
            throw new ArgumentException(
                "old_text was not found. Read the file again and copy the exact text, including whitespace.");
        var second = content.IndexOf(oldText, index + 1, StringComparison.Ordinal);
        if (second >= 0)
            throw new ArgumentException("old_text occurs more than once. Include more surrounding lines so it is unique.");

        var updated = content[..index] + newText + content[(index + oldText.Length)..];
        return new EditPlan(full, updated, Diff(path, content, index, oldText, newText), Fingerprint(bytes), bom);
    }

    public static string Fingerprint(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static async Task<string> ApplyEditAsync(EditPlan plan, string path, CancellationToken ct)
    {
        await File.WriteAllTextAsync(plan.FullPath, plan.NewContent, new UTF8Encoding(plan.Bom), ct);
        return $"Edited {path}";
    }

    // A unified-style diff of the changed lines with three lines of context.
    private static string Diff(string path, string content, int index, string oldText, string newText)
    {
        var lineStart = index == 0 ? 0 : content.LastIndexOf('\n', index - 1) + 1;
        var end = index + oldText.Length;
        var lineEnd = content.IndexOf('\n', end);
        if (lineEnd < 0)
            lineEnd = content.Length;
        var firstLine = content[..lineStart].Count(c => c == '\n') + 1;
        string[] Lines(string text) => text.Replace("\r", "").Split('\n');
        var removed = Lines(content[lineStart..lineEnd]);
        var added = Lines(content[lineStart..index] + newText + content[end..lineEnd]);
        var before = Lines(content[..lineStart]).SkipLast(1).TakeLast(3).ToArray();
        var after = lineEnd < content.Length ? Lines(content[(lineEnd + 1)..]).Take(3).ToArray() : [];

        var diff = new StringBuilder($"--- {path}\n+++ {path}\n@@ line {firstLine} @@\n");
        foreach (var line in before)
            diff.Append("  ").Append(line).Append('\n');
        foreach (var line in removed)
            diff.Append("- ").Append(line).Append('\n');
        foreach (var line in added)
            diff.Append("+ ").Append(line).Append('\n');
        foreach (var line in after)
            diff.Append("  ").Append(line).Append('\n');
        return diff.ToString().TrimEnd('\n');
    }
}
