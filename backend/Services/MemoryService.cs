using System.Text.RegularExpressions;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public partial class MemoryService(ChatDb db)
{
    public const int MaxLength = 500;
    private const int MaxMemories = 1000;

    public Task<List<Memory>> ListAsync(CancellationToken ct) =>
        db.Memories.AsNoTracking().OrderByDescending(m => m.Id).ToListAsync(ct);

    public async Task<Memory> SaveAsync(string text, CancellationToken ct)
    {
        text = Regex.Replace(text.Trim(), @"\s+", " ");
        if (text.Length is 0 or > MaxLength)
            throw new ArgumentException($"A memory must be 1–{MaxLength} characters.");
        var existing = await db.Memories.FirstOrDefaultAsync(m => m.Text == text, ct);
        if (existing is not null)
            return existing;
        if (await db.Memories.CountAsync(ct) >= MaxMemories)
            throw new ArgumentException("The memory is full. Ask the user to remove old memories in Settings.");

        var memory = new Memory { Text = text };
        db.Memories.Add(memory);
        await db.SaveChangesAsync(ct);
        return memory;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.Memories.Where(m => m.Id == id).ExecuteDeleteAsync(ct) > 0;

    // Ranks memories by shared words with the query; good enough for a personal list of short notes.
    public async Task<List<Memory>> SearchAsync(string query, int take, CancellationToken ct)
    {
        var terms = Words(query);
        if (terms.Count == 0)
            return [];
        var memories = await db.Memories.AsNoTracking().ToListAsync(ct);
        return memories
            .Select(m => (Memory: m, Score: Words(m.Text).Count(terms.Contains)))
            .Where(m => m.Score > 0)
            .OrderByDescending(m => m.Score).ThenByDescending(m => m.Memory.Id)
            .Take(take)
            .Select(m => m.Memory)
            .ToList();
    }

    // Common English and Swedish words would make every memory look relevant.
    private static readonly HashSet<string> s_stopWords =
    [
        "the", "and", "for", "are", "you", "your", "with", "this", "that", "what", "how", "can", "was", "have",
        "och", "att", "det", "som", "för", "med", "har", "inte", "den", "jag", "kan", "vad", "hur", "var", "min", "mitt"
    ];

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => (w.Length >= 3 || w.All(char.IsDigit)) && !s_stopWords.Contains(w)).ToHashSet();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
