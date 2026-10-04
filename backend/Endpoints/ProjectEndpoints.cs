using Harness.Services;

namespace Harness.Endpoints;

public static class ProjectEndpoints
{
    private record FileInput(Guid UploadId);

    public static void MapProjectEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/projects");
        group.MapGet("", (ProjectService projects, CancellationToken ct) => projects.ListAsync(ct));
        group.MapPost("", async (ProjectInput input, ProjectService projects, CancellationToken ct) =>
            Results.Ok(await projects.SaveAsync(null, input, ct))).WithInputErrors();
        group.MapPut("/{id:int}", async (int id, ProjectInput input, ProjectService projects, CancellationToken ct) =>
            Results.Ok(await projects.SaveAsync(id, input, ct))).WithInputErrors();
        group.MapDelete("/{id:int}", async (int id, ProjectService projects, DocumentIndexer indexer, CancellationToken ct) =>
        {
            if (!await projects.DeleteAsync(id, ct))
                return Results.NotFound();
            indexer.Trigger();
            return Results.NoContent();
        });
        group.MapPost("/{id:int}/files", async (int id, FileInput input, ProjectService projects, DocumentIndexer indexer,
            CancellationToken ct) =>
        {
            var file = await projects.AddFileAsync(id, input.UploadId, ct);
            indexer.Trigger();
            return Results.Ok(file);
        }).WithInputErrors();
        group.MapDelete("/{id:int}/files/{uploadId:guid}", async (int id, Guid uploadId, ProjectService projects,
            DocumentIndexer indexer, CancellationToken ct) =>
        {
            if (!await projects.RemoveFileAsync(id, uploadId, ct))
                return Results.NotFound();
            indexer.Trigger();
            return Results.NoContent();
        });
    }
}
