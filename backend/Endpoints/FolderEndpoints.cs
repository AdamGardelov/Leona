using Harness.Services;

namespace Harness.Endpoints;

public static class FolderEndpoints
{
    private record AddFolder(string Path);

    public static void MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/folders");
        group.MapGet("", ListAsync);
        group.MapPost("", AddAsync).WithInputErrors();
        group.MapDelete("/{id:int}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(FolderService folders, WorkspaceFiles workspace,
        CurrentProfile profile, CancellationToken ct)
    {
        if (!profile.Owner)
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Ok(new { workspace = workspace.Root, folders = await folders.ListAsync(ct) });
    }

    // Opening folders to the tools is reserved for the owner on the computer itself, never a paired device.
    private static bool CanManage(HttpContext context, CurrentProfile profile) =>
        RemoteAccess.IsLocal(context) && profile.Owner;

    private static async Task<IResult> AddAsync(AddFolder request, FolderService folders, HttpContext context,
        CurrentProfile profile, DocumentIndexer indexer, CancellationToken ct)
    {
        if (!CanManage(context, profile))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        var folder = await folders.AddAsync(request.Path, ct);
        // Its documents become searchable.
        indexer.Trigger();
        return Results.Ok(folder);
    }

    private static async Task<IResult> DeleteAsync(int id, FolderService folders, HttpContext context,
        CurrentProfile profile, DocumentIndexer indexer, CancellationToken ct)
    {
        if (!CanManage(context, profile))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        if (!await folders.DeleteAsync(id, ct))
            return Results.NotFound();
        indexer.Trigger();
        return Results.NoContent();
    }
}
