using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public record SkillInput(string Name, string WhenToUse, string Steps);

public partial class SkillService(ChatDb db)
{
    // The step a reply shows when it followed a skill; it is saved with the chat but never recalled as an excerpt.
    public const string StepName = "use_skill";

    private const int MaxSkills = 200;

    public Task<List<Skill>> ListAsync(CancellationToken ct) =>
        db.Skills.AsNoTracking().OrderByDescending(s => s.Uses).ThenBy(s => s.Name).ToListAsync(ct);

    public static string Validate(SkillInput input) =>
        string.IsNullOrWhiteSpace(input.Name) || input.Name.Trim().Length > 60 ? "Give the skill a name of at most 60 characters."
        : string.IsNullOrWhiteSpace(input.WhenToUse) || input.WhenToUse.Trim().Length > 300 ? "Say when to use it, in at most 300 characters."
        : string.IsNullOrWhiteSpace(input.Steps) || input.Steps.Trim().Length > 4000 ? "Write the steps, at most 4,000 characters."
        : "";

    public async Task<Skill> SaveAsync(int? id, SkillInput input, CancellationToken ct)
    {
        var error = Validate(input);
        if (error.Length > 0)
            throw new ArgumentException(error);
        var skill = id is { } existing ? await db.Skills.FindAsync([existing], ct) : new Skill();
        if (skill is null)
            throw new KeyNotFoundException();
        if (id is null && await db.Skills.CountAsync(ct) >= MaxSkills)
            throw new ArgumentException("There are too many skills. Remove some under Settings › Skills.");

        skill.Name = input.Name.Trim();
        skill.WhenToUse = input.WhenToUse.Trim();
        skill.Steps = input.Steps.Trim();
        if (id is null)
            db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        return skill;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.Skills.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0;

    // The skill whose name and description share the most words with the request, if it shares at least
    // two (one for very short requests, three for long ones, where two common words meet by chance), so
    // unrelated skills stay out of the prompt.
    public async Task<Skill?> MatchAsync(string text, CancellationToken ct)
    {
        var terms = Words(text);
        if (terms.Count == 0)
            return null;
        var best = (await db.Skills.AsNoTracking().ToListAsync(ct))
            .Select(s => (Skill: s, Score: Words(s.Name + " " + s.WhenToUse).Count(terms.Contains)))
            .Where(s => s.Score >= Math.Min(terms.Count > 40 ? 3 : 2, terms.Count))
            .OrderByDescending(s => s.Score).ThenByDescending(s => s.Skill.Uses)
            .FirstOrDefault();
        return best.Skill;
    }

    // The skill a turn followed, from the step saved with it.
    public async Task<Skill?> FollowedAsync(int userMessageId, CancellationToken ct)
    {
        var arguments = await db.ToolEvidence.AsNoTracking()
            .Where(e => e.UserMessageId == userMessageId && e.ToolName == StepName)
            .Select(e => e.Arguments).FirstOrDefaultAsync(ct);
        if (arguments is null)
            return null;
        string? name;
        try
        {
            name = JsonDocument.Parse(arguments).RootElement.TryGetProperty("name", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }

        return name is null
            ? null
            : await db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name, ct);
    }

    public Task MarkUsedAsync(int id, CancellationToken ct) =>
        db.Skills.Where(s => s.Id == id).ExecuteUpdateAsync(u => u
            .SetProperty(s => s.Uses, s => s.Uses + 1)
            .SetProperty(s => s.LastUsedAt, DateTime.UtcNow), ct);

    public static string Prompt(Skill skill) =>
        $"A saved skill fits this request. It is a procedure the user approved; follow its steps where they apply " +
        $"and adapt them to the request.\n## {skill.Name}\nWhen: {skill.WhenToUse}\n{skill.Steps}";

    public const string DraftInstructions =
        "Turn the conversation into a reusable skill: a procedure an assistant can follow to do this kind of task " +
        "again. Write general steps, not this conversation's details, and name the tools to use. Reply in exactly " +
        "this format, in the user's language:\nNAME: <at most six words>\nWHEN: <one sentence: which requests it is for>\n" +
        "STEPS:\n1. <step>\n2. <step>";

    // Reads the NAME/WHEN/STEPS reply; returns null when the model did not follow the format.
    public static SkillInput? ParseDraft(string reply)
    {
        string? name = null, when = null;
        var steps = new StringBuilder();
        var inSteps = false;
        foreach (var raw in ChatService.CleanReply(reply).Split('\n'))
        {
            var line = raw.Trim().Trim('*').Trim();
            if (line.StartsWith("NAME:", StringComparison.OrdinalIgnoreCase))
                name = line[5..].Trim();
            else if (line.StartsWith("WHEN:", StringComparison.OrdinalIgnoreCase))
                when = line[5..].Trim();
            else if (line.StartsWith("STEPS:", StringComparison.OrdinalIgnoreCase))
                inSteps = true;
            else if (inSteps && line.Length > 0)
                steps.AppendLine(line);
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(when) || steps.Length == 0)
            return null;
        return new SkillInput(name[..Math.Min(name.Length, 60)], when[..Math.Min(when.Length, 300)],
            steps.ToString().Trim());
    }

    private static readonly HashSet<string> s_stopWords =
    [
        "the", "and", "for", "are", "you", "your", "with", "this", "that", "what", "how", "can", "was", "have", "when",
        "och", "att", "det", "som", "för", "med", "har", "inte", "den", "jag", "kan", "vad", "hur", "var", "min", "mitt",
        "när", "ska", "vill", "mig", "use", "user", "asks", "ber", "eller", "till", "från", "ett", "men", "där", "här",
        "dem", "sig", "sin", "sitt", "sina", "alla", "också", "bara", "sedan", "efter", "under", "över", "the", "and",
        "for", "with", "from"
    ];

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value)
            .Where(w => w.Length >= 3 && !s_stopWords.Contains(w)).ToHashSet();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
