using Harness.Models;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

// Connecting Spotify: the browser goes to Spotify's login and comes back to /spotify/callback, which
// saves the refresh token for the profile that started the login.
public static class SpotifyEndpoints
{
    private record ConnectRequest(string Label, string ClientId);

    public static void MapSpotifyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/spotify/setup", (HttpContext context, RemoteAccess remote) =>
            Results.Ok(new { redirectUris = RedirectUris(context, remote) }));
        app.MapPost("/api/spotify/connect", Connect);
        app.MapGet("/spotify/callback", CallbackAsync);
    }

    // Spotify accepts http only for loopback IP addresses, so the computer uses 127.0.0.1 and phones the
    // Tailscale HTTPS address.
    private static List<string> RedirectUris(HttpContext context, RemoteAccess remote)
    {
        var uris = new List<string> { $"http://127.0.0.1:{context.Connection.LocalPort}/spotify/callback" };
        if (remote.TailscaleHost() is { } host)
            uris.Add($"https://{host}/spotify/callback");
        return uris;
    }

    private static IResult Connect(ConnectRequest request, HttpContext context, RemoteAccess remote,
        SpotifyLogins logins, CurrentProfile profile)
    {
        var local = RemoteAccess.IsLocal(context);
        if (!local && !context.Request.IsHttps)
            return Results.BadRequest(new { error = "Connect Spotify on the computer or through Tailscale (HTTPS)." });
        var clientId = request.ClientId?.Trim() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(clientId, "^[0-9a-fA-F]{32}$"))
            return Results.BadRequest(new { error = "Paste the Client ID from your app at developer.spotify.com." });
        var label = string.IsNullOrWhiteSpace(request.Label) ? "Spotify" : request.Label.Trim();
        if (label.Length > 40)
            return Results.BadRequest(new { error = "Use a shorter name." });

        var uris = RedirectUris(context, remote);
        var redirect = local ? uris[0] : uris.Last();
        var origin = context.Request.Headers.Origin.ToString();
        var returnTo = Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && remote.IsAllowedOrigin(originUri)
            ? origin.TrimEnd('/')
            : $"{context.Request.Scheme}://{context.Request.Host}";
        var (verifier, challenge) = SpotifyService.Pkce();
        var state = logins.Start(new SpotifyLogins.Pending(profile.Id!.Value, label, clientId, verifier, redirect,
            returnTo, DateTime.UtcNow.AddMinutes(10)));
        return Results.Ok(new { url = SpotifyService.AuthorizeUrl(clientId, redirect, state, challenge) });
    }

    private static async Task<IResult> CallbackAsync(string? code, string? state, string? error, SpotifyLogins logins,
        IServiceScopeFactory scopes, ILogger<SpotifyLogins> logger, CancellationToken ct)
    {
        var pending = logins.Take(state);
        if (pending is null)
            return Results.Redirect("/?spotify=expired");
        if (error is not null || string.IsNullOrEmpty(code))
            return Results.Redirect($"{pending.ReturnTo}/?spotify=denied");

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var current = scope.ServiceProvider.GetRequiredService<CurrentProfile>();
            current.Id = pending.ProfileId;
            var spotify = scope.ServiceProvider.GetRequiredService<SpotifyService>();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var tokens = await spotify.ExchangeAsync(pending.ClientId, code, pending.RedirectUri, pending.Verifier, ct);
            var user = await spotify.UserAsync(tokens.AccessToken, ct);
            var existing = (await accounts.OfKindAsync(AccountKind.Spotify, ct))
                .FirstOrDefault(a => a.Label.Equals(pending.Label, StringComparison.OrdinalIgnoreCase));
            await accounts.SaveAsync(existing?.Id, new AccountInput(AccountKind.Spotify, pending.Label,
                System.Text.Json.JsonSerializer.SerializeToElement(new SpotifySettings(pending.ClientId, user)),
                tokens.RefreshToken), ct);
            return Results.Redirect($"{pending.ReturnTo}/?spotify=connected");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Spotify login failed");
            return Results.Redirect($"{pending.ReturnTo}/?spotify=failed");
        }
    }
}
