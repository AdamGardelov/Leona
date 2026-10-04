using Harness.Data;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

public static class SkillEndpoints
{
    private record DraftRequest(int ConversationId, string Model);

    public static void MapSkillEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills");
        group.MapGet("", (SkillService skills, CancellationToken ct) => skills.ListAsync(ct));
        group.MapPost("", (SkillInput input, SkillService skills, CancellationToken ct) =>
            SaveAsync(null, input, skills, ct));
        group.MapPut("/{id:int}", (int id, SkillInput input, SkillService skills, CancellationToken ct) =>
            SaveAsync(id, input, skills, ct));
        group.MapDelete("/{id:int}", async (int id, SkillService skills, CancellationToken ct) =>
            await skills.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        group.MapPost("/draft", DraftAsync);
    }

    private static async Task<IResult> SaveAsync(int? id, SkillInput input, SkillService skills, CancellationToken ct)
    {
        try
        {
            return Results.Ok(await skills.SaveAsync(id, input, ct));
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

    // The model writes a first version from a conversation; the user edits it before saving.
    private static async Task<IResult> DraftAsync(DraftRequest request, ChatDb db, ChatService chat,
        CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == request.ConversationId, ct))
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Model))
            return Results.BadRequest(new { error = "Choose a model first." });

        try
        {
            var draft = await chat.DraftSkillAsync(request.ConversationId, request.Model, ct);
            return draft is null
                ? Results.UnprocessableEntity(new { error = "The model did not write a usable skill. Write it yourself or try again." })
                : Results.Ok(draft);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { error = "The model could not be reached." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
