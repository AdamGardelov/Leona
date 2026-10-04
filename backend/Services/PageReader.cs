using System.Text;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Services;

// Turns extracted page text into model-sized windows: sequential reading by offset, or passages matching a query.
public static partial class PageReader
{
    private const int PassageSize = 800;
    private const int MaxPassages = 8;

    public record Passage(int Start, string Text);

    public static ToolResult Read(string title, string url, string text, string? find, int offset, int limit)
    {
        var sources = new[] { new SourceLink(title, url) };
        var header = $"{title}\n{url}\n";
        var prefix = "";
        if (!string.IsNullOrWhiteSpace(find))
        {
            var passages = Find(text, find, limit);
            if (passages.Count > 0)
            {
                var body = string.Join("\n[…]\n", passages.Select(p => $"[offset {p.Start}] {p.Text}"));
                return new ToolResult(
                    $"{header}{passages.Count} passages matching \"{find}\" ({text.Length} characters in page):\n\n{body}\n\n" +
                    "[Call read_page with offset to read on from a passage.]",
                    sources,
                    Summary: $"{passages.Count} passages matching “{find}”");
            }

            prefix = $"No passages matched \"{find}\". Showing the start of the page.\n";
            offset = 0;
        }

        offset = Math.Clamp(offset, 0, text.Length);
        var end = Math.Min(text.Length, offset + limit);
        if (end < text.Length)
        {
            // Prefer ending at whitespace so words are not split between windows.
            var space = text.LastIndexOfAny([' ', '\n'], end - 1, end - offset);
            if (space > offset + limit / 2)
                end = space;
        }

        var footer = end < text.Length
            ? $"\n\n[Characters {offset}–{end} of {text.Length}. Call read_page with offset={end} to continue, or with find to jump to a topic.]"
            : $"\n\n[End of page. Characters {offset}–{end} of {text.Length}.]";
        var summary = offset == 0 && end == text.Length
            ? $"Read {text.Length:N0} characters"
            : $"Characters {offset:N0}–{end:N0} of {text.Length:N0}";
        return new ToolResult($"{prefix}{header}\n{text[offset..end]}{footer}", sources, Summary: summary);
    }

    public static List<Passage> Find(string text, string query, int limit)
    {
        var terms = Words().Matches(query.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length >= 3 || w.All(char.IsDigit)).Distinct().ToList();
        var phrase = query.Trim().ToLowerInvariant();
        if (terms.Count == 0 && phrase.Length == 0)
            return [];

        var scored = new List<(Passage Passage, int Score)>();
        foreach (var passage in Split(text))
        {
            var lower = passage.Text.ToLowerInvariant();
            var score = terms.Sum(term => Count(lower, term));
            if (phrase.Length > 0 && lower.Contains(phrase))
                score += 5;
            if (score > 0)
                scored.Add((passage, score));
        }

        var chosen = new List<Passage>();
        var used = 0;
        foreach (var (passage, _) in scored.OrderByDescending(s => s.Score).ThenBy(s => s.Passage.Start))
        {
            if (chosen.Count >= MaxPassages)
                break;
            var excerpt = passage.Text;
            if (used + excerpt.Length > limit)
            {
                // The best passage is always shown, shortened if needed; later ones must fit whole.
                if (chosen.Count > 0)
                    continue;
                excerpt = excerpt[..limit];
            }

            chosen.Add(passage with { Text = excerpt });
            used += excerpt.Length;
        }

        return chosen.OrderBy(p => p.Start).ToList();
    }

    // A line holding only this character ends a passage, so passages never span two pages.
    public const char PartBreak = '\f';

    // Groups lines into passages of roughly PassageSize characters, splitting long lines at whitespace.
    private static IEnumerable<Passage> Split(string text)
    {
        var start = 0;
        var current = new StringBuilder();
        var position = 0;
        foreach (var line in text.Split('\n'))
        {
            if (line == PartBreak.ToString())
            {
                if (current.ToString().Trim() is { Length: > 0 } part)
                    yield return new Passage(start, part);
                current.Clear();
                position += line.Length + 1;
                continue;
            }

            var remaining = line;
            var lineStart = position;
            while (remaining.Length > PassageSize)
            {
                var cut = remaining.LastIndexOf(' ', PassageSize);
                cut = cut < PassageSize / 2 ? PassageSize : cut;
                if (current.Length > 0)
                {
                    yield return new Passage(start, current.ToString().Trim());
                    current.Clear();
                }

                yield return new Passage(lineStart, remaining[..cut].Trim());
                lineStart += cut;
                remaining = remaining[cut..];
            }

            if (current.Length > 0 && current.Length + remaining.Length > PassageSize)
            {
                yield return new Passage(start, current.ToString().Trim());
                current.Clear();
            }

            if (current.Length == 0)
                start = lineStart;
            current.Append(remaining).Append('\n');
            position += line.Length + 1;
        }

        if (current.ToString().Trim() is { Length: > 0 } last)
            yield return new Passage(start, last);
    }

    private static int Count(string text, string term)
    {
        var count = 0;
        for (var index = text.IndexOf(term, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(term, index + term.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex Words();
}
