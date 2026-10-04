using Harness.Data;
using Harness.Services;

namespace Harness.Endpoints;

public static class UploadEndpoints
{
    public static void MapUploadEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/uploads");
        // Writes are protected by the same-origin check in Program.cs, so form antiforgery tokens are not used.
        group.MapPost("", UploadAsync).DisableAntiforgery();
        group.MapGet("/{id:guid}", DownloadAsync);
    }

    private static async Task<IResult> UploadAsync(IFormFile file, UploadStore store, ChatDb db, CancellationToken ct)
    {
        try
        {
            await using var stream = file.OpenReadStream();
            var upload = await store.SaveAsync(db, file.FileName, stream, file.Length, ct);
            return Results.Ok(UploadStore.Reference(upload));
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    }

    private static async Task<IResult> DownloadAsync(Guid id, UploadStore store, ChatDb db, HttpContext context,
        CancellationToken ct)
    {
        var upload = await db.Uploads.FindAsync([id], ct);
        if (upload is null || !File.Exists(store.PathFor(upload)))
            return Results.NotFound();

        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(store.PathFor(upload), upload.Mime,
            upload.Kind == Harness.Models.UploadKind.Image ? null : upload.Name);
    }
}
