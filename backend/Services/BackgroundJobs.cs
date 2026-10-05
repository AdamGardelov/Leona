using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Starts scheduled tasks when they are due, follows their runs and notifies with the result.
public sealed partial class SchedulerService(
    IServiceScopeFactory scopes,
    RunManager runs,
    NotificationService notifications,
    ILogger<SchedulerService> logger) : BackgroundService
{
    // Runs are followed for the life of the service, not of the request that started them.
    private CancellationToken _stopping;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Scheduled task check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task TickAsync(CancellationToken ct)
    {
        List<ScheduledTask> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
            var now = DateTime.Now;
            due = (await db.ScheduledTasks.AsNoTracking().Where(t => t.Enabled).ToListAsync(ct))
                .Where(t => AutomationService.NextRun(t, t.LastRunAt ?? t.CreatedAt.ToLocalTime()) <= now)
                .ToList();
        }

        foreach (var task in due)
            await StartAsync(task.Id, ct, background: true);
    }

    // Also used by "Run now", which does not wait like scheduled runs do. Returns false when the task's
    // conversation already has a run going.
    public async Task<bool> StartAsync(int taskId, CancellationToken ct, bool background = false)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var task = await db.ScheduledTasks.FindAsync([taskId], ct) ?? throw new KeyNotFoundException();
        task.LastRunAt = DateTime.Now;
        if (task.ConversationId is not { } conversationId ||
            !await db.Conversations.AnyAsync(c => c.Id == conversationId && c.ProfileId == task.ProfileId, ct))
        {
            var conversation = new Conversation { ProfileId = task.ProfileId, Title = task.Name };
            db.Conversations.Add(conversation);
            await db.SaveChangesAsync(ct);
            task.ConversationId = conversation.Id;
        }

        await db.SaveChangesAsync(ct);
        // A task's own model while it is installed, else the default model from Settings.
        var ollama = scope.ServiceProvider.GetRequiredService<OllamaClient>();
        var model = task.Model;
        if (string.IsNullOrWhiteSpace(model) || !(await ollama.GetModelsAsync(ct)).Contains(model))
            model = await SettingsService.DefaultModelAsync(db, ollama, ct);
        if (model.Length == 0)
        {
            await notifications.NotifyAsync(task.ProfileId, task.Name,
                "No model is installed, so the task could not run.", null, ct);
            return false;
        }

        var run = await runs.CreateAsync(task.ProfileId, task.ConversationId!.Value,
            new ChatRequest(task.Prompt, model, false, task.Web, task.Files, Accounts: task.Accounts, Background: background,
                Scheduled: true));
        if (run is null)
            return false;
        _ = FollowAsync(task.ProfileId, task.Name, run.Id, task.ConversationId.Value, _stopping);
        return true;
    }

    private async Task FollowAsync(int profileId, string name, Guid runId, int conversationId, CancellationToken ct)
    {
        var url = $"/?conversation={conversationId}";
        var askedForApproval = false;
        try
        {
            for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(45); waited += TimeSpan.FromSeconds(5))
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
                var status = await db.Runs.Where(r => r.Id == runId).Select(r => r.Status).FirstOrDefaultAsync(ct);
                if (status == RunStatus.AwaitingApproval && !askedForApproval)
                {
                    askedForApproval = true;
                    await notifications.NotifyAsync(profileId, $"{name} needs your approval",
                        "Open Leona to approve or decline.", url, ct);
                }

                if (status is null || RunStatus.IsActive(status))
                    continue;
                if (status == RunStatus.Completed)
                {
                    var events = await db.RunEvents.Where(e => e.RunId == runId && e.Json.Contains("\"tool_finished\""))
                        .Select(e => e.Json).ToListAsync(ct);
                    if (NothingNew(events))
                    {
                        logger.LogInformation("{Task} found nothing new; no notification", name);
                        return;
                    }

                    var answer = await ConversationService.LatestReplyAsync(db, conversationId, ct);
                    if (SaysNothingNew(answer))
                    {
                        logger.LogInformation("{Task} answered that nothing is new; no notification", name);
                        return;
                    }

                    await notifications.NotifyAsync(profileId, name, Plain(answer), url, ct);
                }
                else
                {
                    await notifications.NotifyAsync(profileId, $"{name} {status}", "Open the conversation for details.",
                        url, ct);
                }

                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Following scheduled run {RunId} failed", runId);
        }
    }

    // True when the run used tools that report only news (such as find_concerts with new_only) and every
    // one of them found nothing new, so there is nothing worth a notification.
    public static bool NothingNew(IEnumerable<string> eventJson)
    {
        var flags = new List<bool>();
        foreach (var json in eventJson)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "tool_finished" &&
                root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.Object &&
                detail.TryGetProperty("hasNews", out var news) && news.ValueKind is JsonValueKind.True or JsonValueKind.False)
                flags.Add(news.GetBoolean());
        }

        return flags.Count > 0 && flags.All(news => !news);
    }

    // A task told to answer "Inget nytt." when there is nothing to report stays quiet, even when its tools
    // found something the model then judged irrelevant.
    public static bool SaysNothingNew(string answer)
    {
        var text = Plain(answer).Trim();
        return text.Length <= 40 &&
               (text.StartsWith("inget nytt", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("nothing new", StringComparison.OrdinalIgnoreCase));
    }

    // Markdown and the appended source list read poorly in a notification.
    public static string Plain(string markdown)
    {
        var text = markdown.Split("\n### Sources")[0];
        // A job card becomes one line: role, employer and place.
        text = JobBlock().Replace(text, match =>
        {
            var block = match.Groups[1].Value;
            var place = Field(block, "Ort", "Place", "Plats");
            return $"• {Field(block, "Roll", "Role")} – {Field(block, "Företag", "Arbetsgivare", "Company")}{(place.Length > 0 ? $" ({place})" : "")}\n";
        });
        // A day plan's suggestion becomes one line too: what, where and when.
        text = ActivityBlock().Replace(text, match =>
        {
            var block = match.Groups[1].Value;
            var time = Field(block, "Tid", "Time");
            return $"• {Field(block, "Vad", "Aktivitet", "What", "Title")} – {Field(block, "Plats", "Place")}{(time.Length > 0 ? $" ({time})" : "")}\n";
        });
        text = MarkdownSyntax().Replace(text, "");
        return Regex.Replace(text, @"\n{2,}", "\n").Trim();
    }

    // The first "Key: value" line of a card block for any of the keys.
    private static string Field(string block, params string[] keys) => keys.Select(key =>
            Regex.Match(block, $@"^\s*{key}\s*:\s*(.+)$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        .FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim() ?? "";

    [GeneratedRegex(@"(^#+\s*|\*\*|__|`|^\s*[-*]\s+(?=\S))", RegexOptions.Multiline)]
    private static partial Regex MarkdownSyntax();

    [GeneratedRegex(@"```job\n([\s\S]*?)```\n?")]
    private static partial Regex JobBlock();

    [GeneratedRegex(@"```activity\n([\s\S]*?)```\n?")]
    private static partial Regex ActivityBlock();
}

// Checks watched pages on their interval and notifies on a change or when a number drops below the limit.
public sealed class WatchService(
    IServiceScopeFactory scopes,
    PublicWebClient web,
    NotificationService notifications,
    ILogger<WatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
                var now = DateTime.UtcNow;
                var due = (await db.Watches.Where(w => w.Enabled).ToListAsync(stoppingToken))
                    .Where(w => w.LastCheckedAt is null || w.LastCheckedAt.Value.AddMinutes(w.IntervalMinutes) <= now);
                foreach (var watch in due)
                    await CheckAsync(db, watch, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Watch check failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task CheckAsync(ChatDb db, Watch watch, CancellationToken ct)
    {
        watch.LastCheckedAt = DateTime.UtcNow;
        try
        {
            var page = await web.ReadAsync(watch.Url, ct);
            var (_, text, _) = await PageTextExtractor.ExtractAsync(page, ct);
            var (value, number) = AutomationService.Extract(text, watch.Find);
            var previous = watch.LastValue;
            var alert = watch.Below is { } limit
                ? number < limit && !(previous is not null && AutomationService.Extract(previous, watch.Find).Number < limit)
                : previous is not null && previous != value;
            watch.LastValue = value;
            watch.LastError = null;
            if (alert)
            {
                watch.LastChangedAt = DateTime.UtcNow;
                await notifications.NotifyAsync(watch.ProfileId, watch.Name,
                    watch.Below is { } below ? $"{value}\nBelow {below:0.##} now." : $"Changed: {value}", watch.Url, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.LastError = ContextBudget.Excerpt(ex.Message, 300);
        }

        await db.SaveChangesAsync(ct);
    }
}
