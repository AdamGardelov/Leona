using System.Text.RegularExpressions;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public partial class SettingsService(ChatDb db)
{
    // Shared settings with the current profile's own custom instructions.
    public static async Task<AppSettings> LoadAsync(ChatDb db, CancellationToken ct)
    {
        var settings = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == 1, ct) ?? new AppSettings();
        if (db.ProfileId is { } profileId)
            settings.CustomInstructions = await db.Profiles.Where(p => p.Id == profileId)
                .Select(p => p.CustomInstructions).SingleOrDefaultAsync(ct) ?? "";
        return settings;
    }

    public Task<AppSettings> GetAsync(CancellationToken ct) => LoadAsync(db, ct);

    public static List<string> Validate(AppSettings settings)
    {
        var errors = new List<string>();
        void Range(int value, int min, int max, string label)
        {
            if (value < min || value > max)
                errors.Add($"{label} must be between {min:N0} and {max:N0}.");
        }

        Range(settings.ContextWindow, 2048, 262144, "Context window");
        Range(settings.MaxOutputTokens, 256, 16384, "Answer length");
        Range(settings.ThinkingTokens, 0, 32768, "Thinking budget");
        Range(settings.SearchResults, 1, 20, "Search results");
        Range(settings.PageCharacters, 1000, 40000, "Page excerpt");
        if (settings.DefaultModel.Length > 200)
            errors.Add("The default model name is too long.");
        if (settings.CustomInstructions.Length > 4000)
            errors.Add("Custom instructions can be at most 4,000 characters.");
        if (!KeepAlivePattern().IsMatch(settings.KeepAlive))
            errors.Add("Keep model loaded must be -1, 0 or a duration such as 30m or 2h.");
        return errors;
    }

    // Everyone sets their own custom instructions; the shared model settings belong to the computer owner.
    public async Task<AppSettings> SaveAsync(AppSettings input, bool shared, CancellationToken ct)
    {
        if (db.ProfileId is { } profileId)
            await db.Profiles.Where(p => p.Id == profileId).ExecuteUpdateAsync(
                s => s.SetProperty(p => p.CustomInstructions, input.CustomInstructions.Trim()), ct);
        if (!shared)
            return await LoadAsync(db, ct);

        var settings = await db.Settings.SingleOrDefaultAsync(s => s.Id == 1, ct);
        if (settings is null)
        {
            settings = new AppSettings();
            db.Settings.Add(settings);
        }

        settings.ContextWindow = input.ContextWindow;
        settings.MaxOutputTokens = input.MaxOutputTokens;
        settings.ThinkingTokens = input.ThinkingTokens;
        settings.KeepAlive = input.KeepAlive.Trim();
        settings.SearchResults = input.SearchResults;
        settings.PageCharacters = input.PageCharacters;
        settings.AutoTitles = input.AutoTitles;
        settings.MemoryEnabled = input.MemoryEnabled;
        settings.DefaultModel = input.DefaultModel.Trim();
        await db.SaveChangesAsync(ct);
        return await LoadAsync(db, ct);
    }

    // The model to use when nothing more specific is chosen: the default from Settings while it is installed,
    // else the first model Ollama lists. Empty when no model is installed.
    public static async Task<string> DefaultModelAsync(ChatDb db, OllamaClient ollama, CancellationToken ct)
    {
        var installed = await ollama.GetModelsAsync(ct);
        var chosen = (await LoadAsync(db, ct)).DefaultModel;
        return installed.Contains(chosen) && chosen.Length > 0 ? chosen : installed.FirstOrDefault() ?? "";
    }

    [GeneratedRegex(@"^(-1|0|\d{1,4}[smh])$")]
    private static partial Regex KeepAlivePattern();
}
