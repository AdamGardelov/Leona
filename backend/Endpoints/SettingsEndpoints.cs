using Harness.Models;
using Harness.Services;

namespace Harness.Endpoints;

public static class SettingsEndpoints
{
    public static void MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/settings");
        group.MapGet("", GetAsync);
        group.MapPut("", SaveAsync);
    }

    private static async Task<IResult> GetAsync(SettingsService settings, CancellationToken ct) =>
        Results.Ok(await settings.GetAsync(ct));

    private static async Task<IResult> SaveAsync(AppSettings input, SettingsService settings, CurrentProfile profile,
        CancellationToken ct)
    {
        var errors = SettingsService.Validate(input);
        if (errors.Count > 0)
            return Results.BadRequest(new { errors });

        return Results.Ok(await settings.SaveAsync(input, profile.Owner, ct));
    }
}
