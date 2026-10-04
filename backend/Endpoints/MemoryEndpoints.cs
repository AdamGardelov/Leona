using Harness.Services;

namespace Harness.Endpoints;

public static class MemoryEndpoints
{
    public static void MapMemoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/memories");
        group.MapGet("", ListAsync);
        group.MapDelete("/{id:int}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(MemoryService memories, CancellationToken ct) =>
        Results.Ok(await memories.ListAsync(ct));

    private static async Task<IResult> DeleteAsync(int id, MemoryService memories, CancellationToken ct) =>
        await memories.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
}
