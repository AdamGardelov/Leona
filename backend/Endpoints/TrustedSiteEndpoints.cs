using Harness.Services;

namespace Harness.Endpoints;

public static class TrustedSiteEndpoints
{
    private record SiteInput(string Address);

    public static void MapTrustedSiteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/trusted-sites");
        group.MapGet("", (TrustedSiteService sites, CancellationToken ct) => sites.ListAsync(ct));
        group.MapPost("", async (SiteInput input, TrustedSiteService sites, CancellationToken ct) =>
            Results.Ok(await sites.AddAsync(input.Address ?? "", ct))).WithInputErrors();
        group.MapDelete("/{id:int}", async (int id, TrustedSiteService sites, CancellationToken ct) =>
            await sites.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }
}
