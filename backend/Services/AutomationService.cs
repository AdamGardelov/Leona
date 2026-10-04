using System.Globalization;
using System.Text.RegularExpressions;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public record TaskInput(string Name, string Prompt, string Time, int Days, string? Model, bool Web, bool Files,
    bool Accounts, bool Enabled = true);

public record WatchInput(string? Name, string Url, string Find, double? Below, int IntervalMinutes = 60,
    bool Enabled = true);

// Scheduled prompts and page watches: validation, schedules and storage.
public partial class AutomationService(ChatDb db)
{
    public const string MorningBriefPrompt =
        "Give me my morning brief. 1) Today's calendar events, and anything early tomorrow. " +
        "2) Unread or important e-mail from the last day, one line each. 3) Today's weather where I live. " +
        "4) Anything I asked you to remember for today. Be short and use headings.";

    public const string ConcertRadarPrompt =
        "Använd find_concerts med new_only för att hitta nya konserter i Göteborg med artister jag lyssnar på. " +
        "Lista dem kort: artist, datum, scen och biljettlänk. Nämn sedan högst tre förslag som passar min smak. " +
        "Svara på svenska.";

    private static readonly string[] s_dayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    // Monday = 1 ... Sunday = 64.
    public static int DayBit(DayOfWeek day) => 1 << (((int)day + 6) % 7);

    public static DateTime? NextRun(ScheduledTask task, DateTime after)
    {
        if (!TimeOnly.TryParseExact(task.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
            task.Days is < 1 or > 127)
            return null;
        for (var day = 0; day <= 7; day++)
        {
            var candidate = after.Date.AddDays(day).Add(time.ToTimeSpan());
            if (candidate > after && (task.Days & DayBit(candidate.DayOfWeek)) != 0)
                return candidate;
        }

        return null;
    }

    public static string DescribeDays(int days) => days switch
    {
        127 => "Every day",
        31 => "Weekdays",
        96 => "Weekends",
        _ => string.Join(", ", s_dayNames.Where((_, i) => (days & (1 << i)) != 0).Select(d => char.ToUpper(d[0]) + d[1..]))
    };

    // "weekdays", "daily", "weekends" or a list like "mon, wed, fri" (Swedish names work too).
    public static int ParseDays(string? text)
    {
        var value = (text ?? "").Trim().ToLowerInvariant();
        if (value is "" or "daily" or "every day" or "dagligen" or "varje dag")
            return 127;
        if (value is "weekdays" or "vardagar")
            return 31;
        if (value is "weekends" or "helger")
            return 96;
        string[][] names =
        [
            ["mon", "mån"], ["tue", "tis"], ["wed", "ons"], ["thu", "tor"], ["fri", "fre"], ["sat", "lör"], ["sun", "sön"]
        ];
        var mask = 0;
        foreach (var part in Regex.Split(value, @"[\s,]+").Where(p => p.Length >= 3))
        {
            var index = Array.FindIndex(names, n => n.Any(part.StartsWith));
            if (index < 0)
                throw new ArgumentException($"Unknown day \"{part}\". Use weekdays, daily, weekends or names like mon, wed.");
            mask |= 1 << index;
        }

        return mask == 0 ? throw new ArgumentException("Choose at least one day.") : mask;
    }

    public static void Validate(TaskInput input)
    {
        if (input.Name.Trim().Length is 0 or > 60)
            throw new ArgumentException("Give the task a name of at most 60 characters.");
        if (input.Prompt.Trim().Length is 0 or > 4000)
            throw new ArgumentException("The prompt must be 1–4,000 characters.");
        if (!TimeOnly.TryParseExact(input.Time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new ArgumentException("Use a time like 07:00.");
        if (input.Days is < 1 or > 127)
            throw new ArgumentException("Choose at least one day.");
    }

    public static void Validate(WatchInput input)
    {
        if (!Uri.TryCreate(input.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Watch a public http or https page.");
        if (input.Find.Trim().Length is 0 or > 200)
            throw new ArgumentException("Say what to look for on the page (at most 200 characters).");
        if (input.IntervalMinutes is < 15 or > 1440)
            throw new ArgumentException("Check every 15 minutes to once a day.");
    }

    public Task<List<ScheduledTask>> TasksAsync(CancellationToken ct) =>
        db.ScheduledTasks.AsNoTracking().OrderBy(t => t.Time).ToListAsync(ct);

    public async Task<ScheduledTask> SaveTaskAsync(int? id, TaskInput input, CancellationToken ct)
    {
        Validate(input);
        var task = id is { } existing
            ? await db.ScheduledTasks.FindAsync([existing], ct) ?? throw new KeyNotFoundException()
            : new ScheduledTask();
        task.Name = input.Name.Trim();
        task.Prompt = input.Prompt.Trim();
        task.Time = input.Time;
        task.Days = input.Days;
        task.Model = input.Model?.Trim() ?? "";
        task.Web = input.Web;
        task.Files = input.Files;
        task.Accounts = input.Accounts;
        task.Enabled = input.Enabled;
        if (id is null)
        {
            // Without this the next check would treat the task as overdue since its creation.
            task.LastRunAt = DateTime.Now;
            db.ScheduledTasks.Add(task);
        }

        await db.SaveChangesAsync(ct);
        return task;
    }

    public async Task<bool> DeleteTaskAsync(int id, CancellationToken ct) =>
        await db.ScheduledTasks.Where(t => t.Id == id).ExecuteDeleteAsync(ct) > 0;

    public Task<List<Watch>> WatchesAsync(CancellationToken ct) =>
        db.Watches.AsNoTracking().OrderBy(w => w.Id).ToListAsync(ct);

    public async Task<Watch> SaveWatchAsync(int? id, WatchInput input, CancellationToken ct)
    {
        Validate(input);
        var watch = id is { } existing
            ? await db.Watches.FindAsync([existing], ct) ?? throw new KeyNotFoundException()
            : new Watch();
        var uri = new Uri(input.Url);
        watch.Name = string.IsNullOrWhiteSpace(input.Name) ? $"{uri.Host} · {input.Find.Trim()}" : input.Name.Trim();
        watch.Url = uri.AbsoluteUri;
        watch.Find = input.Find.Trim();
        watch.Below = input.Below;
        watch.IntervalMinutes = input.IntervalMinutes;
        watch.Enabled = input.Enabled;
        if (id is null)
            db.Watches.Add(watch);
        await db.SaveChangesAsync(ct);
        return watch;
    }

    public async Task<bool> DeleteWatchAsync(int id, CancellationToken ct) =>
        await db.Watches.Where(w => w.Id == id).ExecuteDeleteAsync(ct) > 0;

    // The line around the watched text, and the first number after it (prices like "1 299 kr" or "1,299.00").
    public static (string Value, double? Number) Extract(string text, string find)
    {
        var index = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            return ("(not found)", null);
        var lineStart = text.LastIndexOf('\n', index) + 1;
        var lineEnd = text.IndexOf('\n', index);
        var line = text[lineStart..(lineEnd < 0 ? text.Length : lineEnd)].Trim();
        var after = text[index..Math.Min(text.Length, index + find.Length + 200)];
        var match = Number().Match(after, Math.Min(after.Length, find.Length));
        return (ContextBudget.Excerpt(line, 200), match.Success ? ParseNumber(match.Value) : null);
    }

    public static double? ParseNumber(string raw)
    {
        var value = Regex.Replace(raw.Trim(), @"[\s ]", "");
        var comma = value.LastIndexOf(',');
        var dot = value.LastIndexOf('.');
        if (comma >= 0 && dot >= 0)
            value = comma > dot ? value.Replace(".", "").Replace(',', '.') : value.Replace(",", "");
        else if (comma >= 0)
            value = value.Length - comma - 1 is 1 or 2 ? value.Replace(',', '.') : value.Replace(",", "");
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    [GeneratedRegex(@"\d[\d\s .,]*\d|\d")]
    private static partial Regex Number();
}
