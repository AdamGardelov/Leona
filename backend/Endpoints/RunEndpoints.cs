using System.Text.Json;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

public static class RunEndpoints
{
    private record CreateRun(int ConversationId, ChatRequest Input, int? RewindFromMessageId = null);

    // Always also trusts the page's site from now on; it only applies to read_page.
    private record ApprovalDecision(bool Approve, bool Always = false);
    public static void MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/runs");
        group.MapPost("", CreateAsync);
        group.MapGet("/active", ActiveAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/events", EventsAsync);
        group.MapGet("/{id:guid}/log", LogAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapPost("/{id:guid}/approvals/{approvalId:guid}", DecideAsync);
    }
    public static object View(AgentRun run) => new
    {
        run.Id,
        run.ConversationId,
        run.BaseMessageId,
        run.Status,
        run.CreatedAt,
        input = JsonSerializer.Deserialize<ChatRequest>(run.RequestJson, RunManager.Json)
    };
    private static async Task<IResult> CreateAsync(CreateRun request, RunManager runs, CurrentProfile profile)
    {
        var hasAttachments = request.Input?.Attachments is { Count: > 0 };
        if (request.Input is null || request.Input.Text is null || (string.IsNullOrWhiteSpace(request.Input.Text) && !hasAttachments) ||
            request.Input.Text.Length > 12000 || string.IsNullOrWhiteSpace(request.Input.Model))
            return Results.BadRequest();
        try
        {
            var run = await runs.CreateAsync(profile.Id!.Value, request.ConversationId,
                request.Input with { Background = false, Scheduled = false }, request.RewindFromMessageId);
            return run is null ? Results.Conflict(new { error = "This conversation already has an active run." })
                : Results.Accepted($"/api/runs/{run.Id}", View(run));
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
    private static async Task<IResult> ActiveAsync(ChatDb db, CancellationToken ct)
    {
        var runs = await db.Runs.AsNoTracking().Where(r => RunStatus.Active.Contains(r.Status))
            .OrderByDescending(r => r.CreatedAt).ToListAsync(ct);
        return Results.Ok(runs.Select(View));
    }
    private static async Task<IResult> GetAsync(Guid id, ChatDb db, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? Results.NotFound() : Results.Ok(View(run));
    }
    // Everything recorded for one run, for the inspection view: ordered events and the tool action ledger.
    private static async Task<IResult> LogAsync(Guid id, ChatDb db, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        if (run is null)
            return Results.NotFound();
        var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == id).OrderBy(e => e.Id)
            .Select(e => e.Json).ToListAsync(ct);
        var actions = await db.RunActions.AsNoTracking().Where(a => a.RunId == id).ToListAsync(ct);
        return Results.Ok(new
        {
            run = View(run),
            events = events.Select(json => JsonSerializer.Deserialize<JsonElement>(json)),
            actions = actions.Select(a => new
            {
                a.Id,
                a.ToolName,
                arguments = JsonSerializer.Deserialize<JsonElement>(a.ArgumentsJson),
                a.Status,
                a.Result,
                a.ExpiresAt
            })
        });
    }
    private static async Task<IResult> CancelAsync(Guid id, RunManager runs, CurrentProfile profile)
    {
        return await runs.CancelAsync(profile.Id!.Value, id) ? Results.Accepted() : Results.NotFound();
    }
    private static async Task<IResult> DecideAsync(Guid id, Guid approvalId, ApprovalDecision decision, RunManager runs,
        CurrentProfile profile)
    {
        return await runs.DecideAsync(profile.Id!.Value, id, approvalId, decision.Approve, decision.Always)
            ? Results.NoContent()
            : Results.Conflict(new { error = "This approval has expired or already been answered." });
    }
    private static async Task EventsAsync(Guid id, ChatDb db, HttpContext context)
    {
        var ct = context.RequestAborted;
        if (!await db.Runs.AnyAsync(r => r.Id == id, ct))
        {
            context.Response.StatusCode = 404;
            return;
        }
        var cursor = long.TryParse(context.Request.Headers["Last-Event-ID"], out var last) ? Math.Max(0, last) : 0;
        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["X-Accel-Buffering"] = "no";
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var status = await db.Runs.AsNoTracking().Where(r => r.Id == id).Select(r => r.Status).SingleOrDefaultAsync(ct);
                var events = await db.RunEvents.AsNoTracking().Where(e => e.RunId == id && e.Id > cursor).OrderBy(e => e.Id).Take(256).ToListAsync(ct);
                foreach (var item in events)
                {
                    await context.Response.WriteAsync($"id: {item.Id}\ndata: {item.Json}\n\n", ct);
                    cursor = item.Id;
                }
                await context.Response.WriteAsync(": heartbeat\n\n", ct);
                await context.Response.Body.FlushAsync(ct);
                if (events.Count < 256 && (status is null || !RunStatus.IsActive(status)))
                    return;
                await Task.Delay(300, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
