using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public sealed class RunManager(IServiceScopeFactory scopes, ILogger<RunManager> logger) : BackgroundService
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _cancellations = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<Decision>> _approvals = new();

    // TrustedHost is set when the user approved a page and chose to always allow its site.
    private sealed record Decision(bool Approve, string? TrustedHost = null);
    private readonly ConcurrentDictionary<Guid, Task> _tasks = new();

    // Changes made for a profile only see that profile's conversations and runs; bookkeeping sees all.
    private async Task<T> ChangeAsync<T>(Func<ChatDb, Task<T>> action, int? profileId = null)
    {
        await _changes.WaitAsync();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<CurrentProfile>().Id = profileId;
            return await action(scope.ServiceProvider.GetRequiredService<ChatDb>());
        }
        finally
        {
            _changes.Release();
        }
    }

    private Task ChangeAsync(Func<ChatDb, Task> action, int? profileId = null) =>
        ChangeAsync(async db =>
        {
            await action(db);
            return true;
        }, profileId);

    // Rewinding deletes the given user message and everything after it, so the run replaces that turn.
    public Task<AgentRun?> CreateAsync(int profileId, int conversationId, ChatRequest request,
        int? rewindFromMessageId = null) =>
        ChangeAsync(async db =>
        {
            if (!await db.Conversations.AnyAsync(c => c.Id == conversationId))
                throw new KeyNotFoundException();
            // File and terminal tools work on the computer owner's files, so only the owner gets them.
            if (!await db.Profiles.AnyAsync(p => p.Id == profileId && p.Owner))
                request = request with { Files = false, Commands = false };
            if (await db.Runs.AnyAsync(r => r.ConversationId == conversationId && RunStatus.Active.Contains(r.Status)))
                return null;
            if (rewindFromMessageId is { } from)
            {
                var target = await db.Messages.AsNoTracking()
                    .SingleOrDefaultAsync(m => m.Id == from && m.ConversationId == conversationId);
                if (target?.Role != "user")
                    throw new ArgumentException("Only your own messages can be edited or regenerated.");
                // Saved tool evidence for the removed turns cascades with their user messages.
                await db.Messages.Where(m => m.ConversationId == conversationId && m.Id >= from).ExecuteDeleteAsync();
            }

            // Attachment details always come from the upload store, never from the client.
            var requested = request.Attachments ?? [];
            if (requested.Count > UploadStore.MaxPerMessage)
                throw new ArgumentException($"Attach at most {UploadStore.MaxPerMessage} files per message.");
            var ids = requested.Select(a => a.Id).Distinct().ToList();
            var uploads = await db.Uploads.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync();
            if (uploads.Count != ids.Count)
                throw new ArgumentException("An attachment was not found. Attach the file again.");
            request = request with
            {
                Attachments = ids.Select(id => UploadStore.Reference(uploads.Single(u => u.Id == id))).ToList()
            };

            var run = new AgentRun
            {
                ConversationId = conversationId,
                RequestJson = JsonSerializer.Serialize(request, Json),
                BaseMessageId = await db.Messages.Where(m => m.ConversationId == conversationId)
                    .Select(m => (int?)m.Id).MaxAsync() ?? 0
            };
            db.Runs.Add(run);
            // A new message brings the conversation back from the archive and to the top of the list.
            await db.Conversations.Where(c => c.Id == conversationId).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Archived, false)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
            await db.SaveChangesAsync();
            _queue.Writer.TryWrite(run.Id);
            return run;
        }, profileId);

    public Task<int> DeleteConversationAsync(int profileId, int id) => ChangeAsync(async db =>
    {
        if (await db.Runs.AnyAsync(r => r.ConversationId == id && RunStatus.Active.Contains(r.Status)))
            return 409;
        return await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync() > 0 ? 204 : 404;
    }, profileId);

    public Task<bool> CancelAsync(int profileId, Guid id) => ChangeAsync(async db =>
    {
        var run = await db.Runs.FindAsync(id);
        if (run is null)
            return false;
        if (RunStatus.IsActive(run.Status))
        {
            // The worker finalizes a running request only after partial output is saved.
            if (_cancellations.TryGetValue(id, out var cancellation))
                await cancellation.CancelAsync();
            else
            {
                run.Status = RunStatus.Cancelled;
                db.RunEvents.Add(Event(id, new { type = "run_finished", status = RunStatus.Cancelled }));
            }

            await db.RunActions.Where(a =>
                    a.RunId == id && (a.Status == ActionStatus.Pending || a.Status == ActionStatus.Approved))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ActionStatus.Cancelled));
            await db.SaveChangesAsync();
        }

        return true;
    }, profileId);

    public Task<bool> DecideAsync(int profileId, Guid runId, Guid approvalId, bool approve, bool always = false) =>
        ChangeAsync(async db =>
        {
            var run = await db.Runs.FindAsync(runId);
            var action = await db.RunActions.FindAsync(approvalId);
            if (run?.Status != RunStatus.AwaitingApproval || action?.RunId != runId ||
                action.Status != ActionStatus.Pending ||
                action.ExpiresAt <= DateTime.UtcNow || !_approvals.TryGetValue(approvalId, out var waiting) ||
                (_cancellations.TryGetValue(runId, out var cancellation) && cancellation.IsCancellationRequested))
                return false;
            action.Status = approve ? ActionStatus.Approved : ActionStatus.Rejected;
            run.Status = RunStatus.Running;
            db.RunEvents.Add(Event(runId, new { type = "approval_resolved", approvalId, approved = approve }));
            string? trusted = null;
            if (approve && always && action.ToolName == "read_page" &&
                JsonSerializer.Deserialize<JsonElement>(action.ArgumentsJson) is { ValueKind: JsonValueKind.Object } arguments &&
                arguments.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String &&
                TrustedSiteService.HostOf(url.GetString()!) is { } host)
            {
                trusted = host;
                if (!await db.TrustedSites.AnyAsync(s => s.Host == host))
                    db.TrustedSites.Add(new TrustedSite { Host = host });
            }

            await db.SaveChangesAsync();
            waiting.TrySetResult(new Decision(approve, trusted));
            return true;
        }, profileId);

    private static RunEvent Event(Guid id, object value) =>
        new() { RunId = id, Json = JsonSerializer.Serialize(value, Json) };

    private Task EmitAsync(Guid id, object value) => ChangeAsync(async db =>
    {
        db.RunEvents.Add(Event(id, value));
        await db.SaveChangesAsync();
    });

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ChangeAsync(async db =>
        {
            var unfinished = await db.Runs
                .Where(r => RunStatus.Active.Contains(r.Status))
                .ToListAsync(cancellationToken: cancellationToken);
            foreach (var run in unfinished)
            {
                run.Status = RunStatus.Interrupted;
                db.RunEvents.Add(Event(run.Id,
                    new
                    {
                        type = "run_finished",
                        status = RunStatus.Interrupted,
                        text =
                            "Backend restarted. Unfinished actions were not replayed; check any action whose result is unknown."
                    }));
            }

            await db.RunActions.Where(a => ActionStatus.Unfinished.Contains(a.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ActionStatus.Interrupted),
                    cancellationToken: cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        });
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var task = ProcessAsync(id, stoppingToken);
                _tasks[id] = task;
                _ = task.ContinueWith(_ => { _tasks.TryRemove(id, out var _); }, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_tasks.Values);
        }
    }

    private async Task ProcessAsync(Guid id, CancellationToken stoppingToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var ct = cancellation.Token;
        var status = RunStatus.Completed;
        try
        {
            var (run, profile) = await ChangeAsync(async db =>
            {
                var item = await db.Runs.FindAsync(id, ct);
                if (item?.Status != RunStatus.Queued)
                    return (null, null);
                _cancellations[id] = cancellation;
                item.Status = RunStatus.Running;
                await db.SaveChangesAsync(ct);
                var profileId = await db.Conversations.Where(c => c.Id == item.ConversationId)
                    .Select(c => c.ProfileId).SingleAsync(ct);
                var found = await db.Profiles.AsNoTracking().SingleAsync(p => p.Id == profileId, ct);
                return ((AgentRun?)item, (Profile?)found);
            });
            if (run is null || profile is null)
                return;
            await using var scope = scopes.CreateAsyncScope();
            // Everything the run reads or saves (accounts, memories, uploads) belongs to its conversation's profile.
            var current = scope.ServiceProvider.GetRequiredService<CurrentProfile>();
            current.Id = profile.Id;
            current.Owner = profile.Owner;
            current.Name = profile.Name;
            var request = JsonSerializer.Deserialize<ChatRequest>(run.RequestJson, Json)!;
            var chat = scope.ServiceProvider.GetRequiredService<ChatService>();
            var tools = scope.ServiceProvider.GetRequiredService<ToolRegistry>();
            var code = await chat.GenerateAsync(run.ConversationId, request, async item =>
            {
                if (item.Type == "error")
                    status = RunStatus.Failed;
                await EmitAsync(id, item);
            }, ct, (callId, call, input, token) => ExecuteToolAsync(id, callId, call, input, tools, token));
            if (ct.IsCancellationRequested)
                status = stoppingToken.IsCancellationRequested ? RunStatus.Interrupted : RunStatus.Cancelled;
            else if (code != 200)
                status = RunStatus.Failed;
            // Scheduled runs keep their task's name.
            if (status == RunStatus.Completed && !request.Background)
                await NameConversationAsync(id, run.ConversationId, request.Model, chat, ct);
            // Research takes minutes, so its report is announced like a scheduled task's.
            if (status == RunStatus.Completed && request.Research)
                await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyAsync(profile.Id, "Research ready",
                    ContextBudget.Excerpt(request.Text, 140, "…"), $"/?conversation={run.ConversationId}", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = stoppingToken.IsCancellationRequested ? RunStatus.Interrupted : RunStatus.Cancelled;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} failed", id);
            status = RunStatus.Failed;
        }
        finally
        {
            await ChangeAsync(async db =>
            {
                var run = await db.Runs.FindAsync(id);
                if (run is not null && RunStatus.IsActive(run.Status))
                {
                    run.Status = status;
                    db.RunEvents.Add(Event(id, new { type = "run_finished", status }));
                    await db.RunActions.Where(a => a.RunId == id && ActionStatus.Unfinished.Contains(a.Status))
                        .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ActionStatus.Interrupted));
                    await db.SaveChangesAsync();
                }

                _cancellations.TryRemove(id, out _);
            });
        }
    }

    // Best effort: the answer is already saved, so a failed or cancelled title never changes the run status.
    private async Task NameConversationAsync(Guid runId, int conversationId, string model, ChatService chat,
        CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var title = await chat.SuggestTitleAsync(conversationId, model, timeout.Token);
            if (title is not null)
                await EmitAsync(runId, new ChatEvent("title", title));
        }
        catch (Exception ex)
        {
            logger.LogInformation(ex, "Conversation title skipped for run {RunId}", runId);
        }
    }

    private async Task<ToolResult> ExecuteToolAsync(Guid runId, Guid callId, ToolCall call, ChatRequest input,
        ToolRegistry tools, CancellationToken ct)
    {
        var needsApproval = tools.NeedsApproval(call, input);
        // The tool call ID doubles as the approval ID so the interface can attach the approval to its step.
        var action = new RunAction
        {
            Id = callId,
            RunId = runId,
            ToolName = call.Function.Name,
            ArgumentsJson = call.Function.Arguments.GetRawText(),
            Status = needsApproval ? ActionStatus.Pending : ActionStatus.Approved
        };
        ToolRegistry.Proposal? proposal = null;
        if (needsApproval)
        {
            try
            {
                proposal = await tools.ProposeAsync(call, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A call that cannot succeed is recorded and returned to the model without asking the user.
                var failure = new ToolResult($"Tool failed: {ex.Message}", Status: ToolStatus.Failed,
                    Summary: ex.Message);
                action.Status = ActionStatus.Failed;
                action.Result = failure.Content;
                await ChangeAsync(async db =>
                {
                    db.RunActions.Add(action);
                    await db.SaveChangesAsync(ct);
                });
                return failure;
            }
        }

        var waiting = new TaskCompletionSource<Decision>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (needsApproval)
            _approvals[action.Id] = waiting;
        try
        {
            await ChangeAsync(async db =>
            {
                ct.ThrowIfCancellationRequested();
                db.RunActions.Add(action);
                if (needsApproval)
                {
                    var run = await db.Runs.FindAsync(runId, ct);
                    run!.Status = RunStatus.AwaitingApproval;
                    db.RunEvents.Add(Event(runId,
                        new
                        {
                            type = "approval_required",
                            approvalId = action.Id,
                            toolName = action.ToolName,
                            arguments = call.Function.Arguments,
                            preview = proposal?.Preview,
                            expiresAt = action.ExpiresAt
                        }));
                }

                await db.SaveChangesAsync(ct);
            });
            if (needsApproval)
            {
                bool approved;
                try
                {
                    var decision = await waiting.Task.WaitAsync(TimeSpan.FromMinutes(10), ct);
                    approved = decision.Approve;
                    // Later pages on the same site in this run open without asking again.
                    if (decision.TrustedHost is { } host)
                        tools.TrustedSites.Add(host);
                }
                catch (TimeoutException)
                {
                    await ChangeAsync(async db =>
                    {
                        await db.RunActions.Where(a => a.Id == action.Id && a.Status == ActionStatus.Pending)
                            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ActionStatus.Expired),
                                cancellationToken: ct);
                        await db.Runs.Where(r => r.Id == runId)
                            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatus.Running),
                                cancellationToken: ct);
                        db.RunEvents.Add(Event(runId,
                            new { type = "approval_resolved", approvalId = action.Id, approved = false }));
                        await db.SaveChangesAsync(ct);
                    });
                    return new ToolResult("Tool unavailable: approval expired. No action executed.",
                        Status: ToolStatus.Expired, Summary: "Approval expired");
                }

                if (!approved)
                    return new ToolResult(
                        "Tool unavailable: user rejected the action. Nothing was created or changed. " +
                        "Tell the user plainly that you did not do it because they declined, and do not repeat it unless they ask.",
                        Status: ToolStatus.Rejected, Summary: "Declined");
            }

            var authorized = await ChangeAsync(async db =>
            {
                ct.ThrowIfCancellationRequested();
                return await db.RunActions.Where(a => a.Id == action.Id && a.Status == ActionStatus.Approved)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, ActionStatus.Executing),
                        cancellationToken: ct) == 1;
            });
            if (!authorized)
                return new ToolResult("Tool unavailable: approval is no longer valid.", Status: ToolStatus.Unavailable,
                    Summary: "Approval no longer valid");
            // Execute the persisted arguments, never replacement arguments from the approval request.
            var persistedCall = new ToolCall(new ToolFunction(action.ToolName,
                JsonSerializer.Deserialize<JsonElement>(action.ArgumentsJson)));
            var result = await tools.ExecuteAsync(persistedCall, input, ct, proposal?.Fingerprint);
            await ChangeAsync(async db =>
            {
                await db.RunActions.Where(a => a.Id == action.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status,
                        result.Status == ToolStatus.Failed ? ActionStatus.Failed : ActionStatus.Completed)
                    .SetProperty(a => a.Result, ContextBudget.Excerpt(result.Content, 1800)), cancellationToken: ct);
            });
            return result;
        }
        finally
        {
            _approvals.TryRemove(action.Id, out _);
        }
    }
}
