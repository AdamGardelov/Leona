using System.Text;
using Harness.Data;
using Harness.Models;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

public static class AutomationEndpoints
{
    private record PushKeysInput(string P256dh, string Auth);

    private record SubscribeInput(string Endpoint, PushKeysInput Keys);

    private record UnsubscribeInput(string Endpoint);

    public static void MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/api/accounts");
        accounts.MapGet("", (AccountService service, CancellationToken ct) => service.ListAsync(ct));
        accounts.MapPost("", (AccountInput input, AccountService service, HttpContext context, CancellationToken ct) =>
            SaveAccountAsync(null, input, service, context, ct));
        accounts.MapPut("/{id:int}", (int id, AccountInput input, AccountService service, HttpContext context, CancellationToken ct) =>
            SaveAccountAsync(id, input, service, context, ct));
        accounts.MapDelete("/{id:int}", async (int id, AccountService service, CancellationToken ct) =>
            await service.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        accounts.MapPost("/{id:int}/test", TestAccountAsync);
        // The current profile's receipts, as recorded by record_expense.
        app.MapGet("/api/expenses.csv", (PersonalFiles files, CurrentProfile profile) =>
            File.Exists(files.ExpensesPath(profile.Id!.Value))
                ? Results.File(files.ExpensesPath(profile.Id!.Value), "text/csv", "expenses.csv")
                : Results.File(Encoding.UTF8.GetBytes(PersonalTools.ExpensesHeader), "text/csv", "expenses.csv"));

        var notifications = app.MapGroup("/api/notifications");
        notifications.MapGet("", async (ChatDb db, CancellationToken ct) => Results.Ok(new
        {
            unread = await db.Notifications.CountAsync(n => !n.Read, ct),
            items = await db.Notifications.AsNoTracking().OrderByDescending(n => n.Id).Take(50).ToListAsync(ct)
        }));
        notifications.MapPost("/read", async (ChatDb db, CancellationToken ct) =>
        {
            await db.Notifications.Where(n => !n.Read).ExecuteUpdateAsync(s => s.SetProperty(n => n.Read, true), ct);
            return Results.NoContent();
        });

        var push = app.MapGroup("/api/push");
        push.MapGet("/key", async (NotificationService service, CancellationToken ct) =>
            Results.Ok(new { publicKey = (await service.KeysAsync(ct)).PublicKey }));
        push.MapPost("/subscribe", async (SubscribeInput input, NotificationService service, HttpContext context,
            CurrentProfile profile, CancellationToken ct) =>
        {
            try
            {
                await service.SubscribeAsync(profile.Id!.Value, input.Endpoint, input.Keys.P256dh, input.Keys.Auth,
                    RemoteAccess.DeviceName(context.Request.Headers.UserAgent.ToString()), ct);
                return Results.NoContent();
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });
        push.MapPost("/unsubscribe", async (UnsubscribeInput input, NotificationService service, CurrentProfile profile,
            CancellationToken ct) =>
        {
            await service.UnsubscribeAsync(profile.Id!.Value, input.Endpoint, ct);
            return Results.NoContent();
        });
        push.MapPost("/test", async (NotificationService service, CurrentProfile profile, CancellationToken ct) =>
        {
            await service.NotifyAsync(profile.Id!.Value, "Leona", "Notifications work on this device.", "/", ct);
            return Results.NoContent();
        });

        var tasks = app.MapGroup("/api/tasks");
        tasks.MapGet("", async (AutomationService service, CancellationToken ct) =>
            Results.Ok((await service.TasksAsync(ct)).Select(TaskView)));
        tasks.MapPost("", (TaskInput input, AutomationService service, CancellationToken ct) =>
            SaveTaskAsync(null, input, service, ct));
        tasks.MapPut("/{id:int}", (int id, TaskInput input, AutomationService service, CancellationToken ct) =>
            SaveTaskAsync(id, input, service, ct));
        tasks.MapDelete("/{id:int}", async (int id, AutomationService service, CancellationToken ct) =>
            await service.DeleteTaskAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        tasks.MapPost("/{id:int}/run", async (int id, ChatDb db, SchedulerService scheduler, CancellationToken ct) =>
        {
            var task = await db.ScheduledTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
            if (task is null)
                return Results.NotFound();

            // The last run may still be going in the task's chat, often waiting for an approval.
            var status = await db.Runs.Where(r => r.ConversationId == task.ConversationId && RunStatus.Active.Contains(r.Status))
                .Select(r => r.Status).FirstOrDefaultAsync(ct);
            if (status is not null)
                return Results.Conflict(new
                {
                    error = status == RunStatus.AwaitingApproval
                        ? "The last run is waiting for your approval. Open results to approve or decline it."
                        : "The last run is still working. Open results to follow it."
                });

            return await scheduler.StartAsync(id, ct)
                ? Results.Accepted()
                : Results.Conflict(new { error = "The task is already running or no model is installed." });
        });
        tasks.MapPost("/concert-radar", (AutomationService service, CancellationToken ct) =>
            SaveTaskAsync(null, new TaskInput("Konsertradar", AutomationService.ConcertRadarPrompt, "09:00", 1, null,
                false, false, true), service, ct));
        tasks.MapPost("/morning-brief", (AutomationService service, CancellationToken ct) =>
            SaveTaskAsync(null, new TaskInput("Morning brief", AutomationService.MorningBriefPrompt, "07:00", 31, null,
                true, false, true), service, ct));

        var watches = app.MapGroup("/api/watches");
        watches.MapGet("", (AutomationService service, CancellationToken ct) => service.WatchesAsync(ct));
        watches.MapPost("", (WatchInput input, AutomationService service, CancellationToken ct) =>
            SaveWatchAsync(null, input, service, ct));
        watches.MapPut("/{id:int}", (int id, WatchInput input, AutomationService service, CancellationToken ct) =>
            SaveWatchAsync(id, input, service, ct));
        watches.MapDelete("/{id:int}", async (int id, AutomationService service, CancellationToken ct) =>
            await service.DeleteWatchAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        watches.MapPost("/{id:int}/check", async (int id, ChatDb db, WatchService watcher, CancellationToken ct) =>
        {
            var watch = await db.Watches.FindAsync([id], ct);
            if (watch is null)
                return Results.NotFound();
            await watcher.CheckAsync(db, watch, ct);
            return Results.Ok(watch);
        });
    }

    // Passwords and tokens travel only from this computer or over HTTPS (for example Tailscale), never plain Wi-Fi.
    private static bool CanSendSecrets(HttpContext context) =>
        RemoteAccess.IsLocal(context) || context.Request.IsHttps;

    private static async Task<IResult> SaveAccountAsync(int? id, AccountInput input, AccountService service,
        HttpContext context, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(input.Secret) && !CanSendSecrets(context))
            return Results.BadRequest(new { error = "Add passwords on the computer or over HTTPS (Tailscale), not over plain Wi-Fi." });
        try
        {
            return Results.Ok(await service.SaveAsync(id, input, ct));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> TestAccountAsync(int id, ChatDb db, MailService mail, CalendarService calendar,
        HomeAssistantService home, SpotifyService spotify, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account is null)
            return Results.NotFound();
        try
        {
            var message = account.Kind switch
            {
                AccountKind.Mail => await mail.TestAsync(account, ct),
                AccountKind.Calendar => await calendar.TestAsync(account, ct),
                AccountKind.Spotify => await spotify.TestAsync(account, ct),
                _ => await home.TestAsync(account, ct)
            };
            return Results.Ok(new { message });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static object TaskView(ScheduledTask task) => new
    {
        task.Id,
        task.Name,
        task.Prompt,
        task.Time,
        task.Days,
        task.Model,
        task.Web,
        task.Files,
        task.Accounts,
        task.Enabled,
        task.ConversationId,
        task.LastRunAt,
        schedule = $"{AutomationService.DescribeDays(task.Days)} at {task.Time}",
        nextRun = task.Enabled ? AutomationService.NextRun(task, DateTime.Now) : null
    };

    private static async Task<IResult> SaveTaskAsync(int? id, TaskInput input, AutomationService service,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(TaskView(await service.SaveTaskAsync(id, input, ct)));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> SaveWatchAsync(int? id, WatchInput input, AutomationService service,
        CancellationToken ct)
    {
        try
        {
            return Results.Ok(await service.SaveWatchAsync(id, input, ct));
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }
}
