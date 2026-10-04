using Harness.Services;

namespace Harness.Endpoints;

// Profiles are managed and switched only on the computer itself; a paired device keeps its profile.
public static class ProfileEndpoints
{
    private record ProfileInput(string Name);

    public static void MapProfileEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/profiles");
        group.MapGet("", ListAsync);
        group.MapPost("", (ProfileInput input, ProfileService profiles, HttpContext context, CancellationToken ct) =>
            SaveAsync(null, input, profiles, context, ct));
        group.MapPut("/{id:int}", (int id, ProfileInput input, ProfileService profiles, HttpContext context,
            CancellationToken ct) => SaveAsync(id, input, profiles, context, ct));
        group.MapDelete("/{id:int}", DeleteAsync);
        group.MapPost("/{id:int}/use", UseAsync);
    }

    private static async Task<IResult> ListAsync(ProfileService profiles, HttpContext context, CancellationToken ct)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Ok(await profiles.ListAsync(ct));
    }

    private static async Task<IResult> SaveAsync(int? id, ProfileInput input, ProfileService profiles,
        HttpContext context, CancellationToken ct)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            return Results.Ok(await profiles.SaveAsync(id, input.Name, ct));
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

    private static async Task<IResult> DeleteAsync(int id, ProfileService profiles, HttpContext context,
        CancellationToken ct)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        try
        {
            await profiles.DeleteAsync(id, ct);
            return Results.NoContent();
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Conflict(new { error = ex.Message });
        }
    }

    // Switches the computer to another profile.
    private static async Task<IResult> UseAsync(int id, ProfileService profiles, HttpContext context,
        CancellationToken ct)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (!await profiles.UseAsync(id, false, ct))
            return Results.NotFound();

        context.Response.Cookies.Append(ProfileService.CookieName, id.ToString(), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddYears(1)
        });
        return Results.NoContent();
    }
}
