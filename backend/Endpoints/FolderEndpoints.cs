using Harness.Services;

namespace Harness.Endpoints;

public static class FolderEndpoints
{
    private record AddFolder(string Path);

    public static void MapFolderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/folders");
        group.MapGet("", ListAsync);
        group.MapPost("", AddAsync);
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
        CurrentProfile profile, CancellationToken ct)
    {
        if (!CanManage(context, profile))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            return Results.Ok(await folders.AddAsync(request.Path, ct));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DeleteAsync(int id, FolderService folders, HttpContext context,
        CurrentProfile profile, CancellationToken ct)
    {
        if (!CanManage(context, profile))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await folders.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound();
    }
}
