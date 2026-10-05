using Harness.Services;

namespace Harness.Endpoints;

// Pictures shown in replies, such as an event's photo, are fetched here rather than by the browser, so
// the sites never see which device looks at them.
public static class ImageEndpoints
{
    public static void MapImageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/images", GetAsync);
    }

    private static async Task<IResult> GetAsync(string url, PublicWebClient web, HttpContext context, CancellationToken ct)
    {
        FetchedPage image;
        try
        {
            image = await web.ReadImageAsync(url, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or HttpRequestException or TaskCanceledException &&
                                   !ct.IsCancellationRequested)
        {
            return Results.NotFound();
        }

        // A cut-off picture would show half an image.
        if (image.Truncated || PublicWebClient.ImageType(image.Bytes) is not { } type)
            return Results.NotFound();
        // The browser keeps a picture for a day instead of asking again for every reply that shows it.
        context.Response.Headers.CacheControl = "private, max-age=86400";
        return Results.File(image.Bytes, type);
    }
}
