using Harness.Data;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

public static class ConversationEndpoints
{
    public static void MapConversationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/conversations");
        group.MapGet("", ListAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
        group.MapPost("", CreateAsync);
        group.MapPatch("/{id:int}", UpdateAsync);
        group.MapPost("/{id:int}/read", async (int id, ConversationService conversations, CancellationToken ct) =>
            await conversations.MarkReadAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        group.MapGet("/{id:int}/messages", GetMessagesAsync);
        group.MapGet("/{id:int}/runs", GetRunsAsync);
    }

    private static async Task<IResult> DeleteAsync(int id, RunManager runs, CurrentProfile profile)
    {
        var status = await runs.DeleteConversationAsync(profile.Id!.Value, id);
        return status switch
        {
            204 => Results.NoContent(),
            404 => Results.NotFound(),
            _ => Results.Conflict(new { error = "Stop the current run before deleting this conversation." })
        };
    }

    private static async Task<IResult> ListAsync(ConversationService conversations, CancellationToken ct,
        bool archived = false) =>
        Results.Ok(await conversations.ListAsync(archived, ct));

    private static async Task<IResult> CreateAsync(ConversationService conversations, CancellationToken ct) =>
        Results.Ok(await conversations.CreateAsync(ct));

    private static async Task<IResult> UpdateAsync(int id, ConversationUpdate update,
        ConversationService conversations, CancellationToken ct)
    {
        var conversation = await conversations.UpdateAsync(id, update, ct);
        return conversation is null ? Results.NotFound() : Results.Ok(conversation);
    }

    private static async Task<IResult>
        GetMessagesAsync(int id, ConversationService conversations, CancellationToken ct) =>
        Results.Ok(await conversations.GetMessagesAsync(id, ct));

    private static async Task<IResult> GetRunsAsync(int id, ChatDb db, CancellationToken ct)
    {
        var runs = await db.Runs.AsNoTracking().Where(r => r.ConversationId == id)
            .OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(ct);
        return Results.Ok(runs.Select(RunEndpoints.View));
    }
}
