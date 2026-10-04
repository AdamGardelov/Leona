using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

public static class RemoteEndpoints
{
    private record PairRequest(string Code);

    private record PairingRequest(int? ProfileId);

    public static void MapRemoteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/session", Session);
        app.MapGet("/api/status", StatusOfServiceAsync);
        app.MapPost("/api/pair", PairAsync);
        // A pairing link opened on the phone: /pair?code=123456
        app.MapGet("/pair", PairLinkAsync);
        var group = app.MapGroup("/api/remote");
        group.MapGet("", StatusAsync);
        group.MapPost("/pairings", CreatePairing);
        group.MapDelete("/devices/{id:int}", RemoveDeviceAsync);
    }

    private static IResult Session(HttpContext context, RemoteAccess remote, CurrentProfile profile)
    {
        var local = RemoteAccess.IsLocal(context);
        return Results.Ok(new
        {
            local,
            paired = profile.Id is not null,
            remoteEnabled = remote.Enabled,
            profile = profile.Id is { } id ? new ProfileView(id, profile.Name, profile.Owner) : null
        });
    }

    // For the computer's top bar and for restarts: how many answers are being written, across every profile.
    private static async Task<IResult> StatusOfServiceAsync(HttpContext context, IServiceScopeFactory scopes,
        CancellationToken ct)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Harness.Data.ChatDb>();
        var active = await db.Runs.CountAsync(r => Harness.Models.RunStatus.Active.Contains(r.Status), ct);
        return Results.Ok(new { running = true, activeRuns = active });
    }

    private static async Task<bool> SignInAsync(HttpContext context, RemoteAccess remote, string code)
    {
        var name = RemoteAccess.DeviceName(context.Request.Headers.UserAgent.ToString());
        var token = await remote.PairAsync(code, name, context.RequestAborted);
        if (token is null)
            return false;

        context.Response.Cookies.Append(RemoteAccess.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(180)
        });
        return true;
    }

    private static async Task<IResult> PairAsync(PairRequest request, HttpContext context, RemoteAccess remote)
    {
        if (RemoteAccess.IsLocal(context))
            return Results.BadRequest(new { error = "This computer does not need pairing." });

        return await SignInAsync(context, remote, request.Code ?? "")
            ? Results.NoContent()
            : Results.BadRequest(new { error = "That code is wrong or has expired. Create a new one on your computer." });
    }

    private static async Task<IResult> PairLinkAsync(string? code, HttpContext context, RemoteAccess remote)
    {
        if (RemoteAccess.IsLocal(context))
            return Results.Redirect("/");

        return Results.Redirect(await SignInAsync(context, remote, code ?? "") ? "/" : "/?pairing=failed");
    }

    // Pairing is managed only from the computer itself, never from a paired device.
    private static async Task<IResult> StatusAsync(HttpContext context, RemoteAccess remote)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return Results.Ok(new
        {
            enabled = remote.Enabled,
            addresses = remote.Addresses(),
            tailscale = remote.TailscaleHost() is not null,
            devices = await remote.DevicesAsync(context.RequestAborted)
        });
    }

    // The paired device signs in as the chosen profile, by default the one the computer is using.
    private static async Task<IResult> CreatePairing(PairingRequest? request, HttpContext context, RemoteAccess remote,
        ProfileService profiles, CurrentProfile current, CancellationToken ct)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        if (remote.Addresses().Count == 0)
            return Results.BadRequest(new { error = "Phone access is off. Turn on Remote:Enabled or set up Tailscale Serve." });
        var profileId = request?.ProfileId ?? current.Id;
        if (profileId is null || (await profiles.ListAsync(ct)).All(p => p.Id != profileId))
            return Results.BadRequest(new { error = "Choose who the phone is for." });

        var (code, expiresAt) = remote.CreatePairing(profileId.Value);
        return Results.Ok(new
        {
            code,
            expiresAt,
            links = remote.Addresses().Select(address => $"{address}/pair?code={code}")
        });
    }

    private static async Task<IResult> RemoveDeviceAsync(int id, HttpContext context, RemoteAccess remote)
    {
        if (!RemoteAccess.IsLocal(context))
            return Results.StatusCode(StatusCodes.Status403Forbidden);

        return await remote.RemoveDeviceAsync(id, context.RequestAborted) ? Results.NoContent() : Results.NotFound();
    }
}
