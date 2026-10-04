using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harness.Models;

namespace Harness.Services;

// The Spotify app the user created (developer.spotify.com) and the Spotify user it is connected for.
public record SpotifySettings(string ClientId = "", string User = "");

public record TasteArtist(string Name, IReadOnlyList<string> Genres, double Score);

// Logins in progress: the state sent to Spotify maps back to the profile and PKCE verifier. Single use,
// ten minutes.
public sealed class SpotifyLogins
{
    public record Pending(int ProfileId, string Label, string ClientId, string Verifier, string RedirectUri,
        string ReturnTo, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, Pending> _pending = new();

    public string Start(Pending pending)
    {
        foreach (var (key, old) in _pending)
        {
            if (old.ExpiresAt <= DateTime.UtcNow)
                _pending.TryRemove(key, out _);
        }

        var state = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _pending[state] = pending;
        return state;
    }

    public Pending? Take(string? state) =>
        state is not null && _pending.TryRemove(state, out var pending) && pending.ExpiresAt > DateTime.UtcNow
            ? pending
            : null;
}

// Reads the user's listening (top artists, recently played) through the Spotify Web API. Uses the
// authorization code flow with PKCE, so no client secret is needed; the refresh token is the account's
// encrypted secret. Development Mode apps need Spotify Premium and allow five users.
public sealed class SpotifyService(AccountService accounts, IHttpClientFactory clients)
{
    public const string Scopes = "user-top-read user-read-recently-played";
    private const string Api = "https://api.spotify.com/v1/";
    private const string TokenUrl = "https://accounts.spotify.com/api/token";
    private static readonly ConcurrentDictionary<int, (string Token, DateTime ExpiresAt)> s_tokens = new();

    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(64));
        return (verifier, Challenge(verifier));
    }

    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string AuthorizeUrl(string clientId, string redirectUri, string state, string challenge) =>
        "https://accounts.spotify.com/authorize?response_type=code" +
        $"&client_id={Uri.EscapeDataString(clientId)}&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
        $"&scope={Uri.EscapeDataString(Scopes)}&state={state}&code_challenge_method=S256&code_challenge={challenge}";

    public record Tokens(string AccessToken, string? RefreshToken, int ExpiresIn);

    private async Task<Tokens> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await clients.CreateClient("spotify").PostAsync(TokenUrl, new FormUrlEncodedContent(form), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Spotify refused the login ({(int)response.StatusCode}). Connect Spotify again under Settings › Accounts.");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new Tokens(root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 3600);
    }

    public Task<Tokens> ExchangeAsync(string clientId, string code, string redirectUri, string verifier,
        CancellationToken ct) =>
        TokenAsync(new()
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        }, ct);

    private async Task<string> AccessTokenAsync(Account account, CancellationToken ct)
    {
        if (s_tokens.TryGetValue(account.Id, out var cached) && cached.ExpiresAt > DateTime.UtcNow.AddMinutes(1))
            return cached.Token;
        var settings = AccountService.Settings<SpotifySettings>(account);
        var tokens = await TokenAsync(new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = accounts.Secret(account),
            ["client_id"] = settings.ClientId
        }, ct);
        // Spotify may rotate the refresh token; keep the newest.
        if (!string.IsNullOrEmpty(tokens.RefreshToken))
            await accounts.ReplaceSecretAsync(account.Id, tokens.RefreshToken, ct);
        s_tokens[account.Id] = (tokens.AccessToken, DateTime.UtcNow.AddSeconds(tokens.ExpiresIn));
        return tokens.AccessToken;
    }

    public async Task<JsonDocument> GetAsync(string accessToken, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await clients.CreateClient("spotify").SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden =>
                    "Spotify said no (403). In your Spotify app's User Management, add the e-mail of this Spotify account.",
                System.Net.HttpStatusCode.TooManyRequests => "Spotify is rate limiting; try again in a minute.",
                _ => $"Spotify answered {(int)response.StatusCode}."
            });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public async Task<string> UserAsync(string accessToken, CancellationToken ct)
    {
        using var me = await GetAsync(accessToken, "me", ct);
        return me.RootElement.TryGetProperty("display_name", out var name) && name.GetString() is { Length: > 0 } display
            ? display
            : me.RootElement.GetProperty("id").GetString() ?? "Spotify";
    }

    public async Task<string> TestAsync(Account account, CancellationToken ct) =>
        $"Connected as {await UserAsync(await AccessTokenAsync(account, ct), ct)}.";

    // Top artists over the last month, half year and years, plus recently played, merged into one ranking.
    public async Task<IReadOnlyList<TasteArtist>> TasteAsync(Account account, CancellationToken ct)
    {
        var token = await AccessTokenAsync(account, ct);
        var scores = new Dictionary<string, (double Score, HashSet<string> Genres)>(StringComparer.OrdinalIgnoreCase);

        void Add(string name, double score, IEnumerable<string> genres)
        {
            var entry = scores.TryGetValue(name, out var found) ? found : (0, new HashSet<string>());
            entry.Score += score;
            entry.Genres.UnionWith(genres);
            scores[name] = entry;
        }

        // The four requests run together; their answers are added in the same order as before.
        (string Range, double Weight)[] ranges = [("short_term", 1.5), ("medium_term", 1.2), ("long_term", 1.0)];
        var tops = ranges.Select(r => GetAsync(token, $"me/top/artists?time_range={r.Range}&limit=50", ct)).ToList();
        var played = GetAsync(token, "me/player/recently-played?limit=50", ct);
        foreach (var (request, weight) in tops.Zip(ranges.Select(r => r.Weight)))
        {
            using var top = await request;
            var rank = 0;
            foreach (var artist in top.RootElement.GetProperty("items").EnumerateArray())
            {
                var genres = artist.TryGetProperty("genres", out var list) && list.ValueKind == JsonValueKind.Array
                    ? list.EnumerateArray().Select(g => g.GetString() ?? "").Where(g => g.Length > 0)
                    : [];
                Add(artist.GetProperty("name").GetString() ?? "", weight * (50 - rank++) / 50.0, genres);
            }
        }

        using var recent = await played;
        foreach (var item in recent.RootElement.GetProperty("items").EnumerateArray())
        {
            foreach (var artist in item.GetProperty("track").GetProperty("artists").EnumerateArray())
                Add(artist.GetProperty("name").GetString() ?? "", 0.1, []);
        }

        return scores.Where(s => s.Key.Length > 0)
            .Select(s => new TasteArtist(s.Key, s.Value.Genres.ToList(), Math.Round(s.Value.Score, 2)))
            .OrderByDescending(a => a.Score).ToList();
    }

    // Genres weighted by the artists who have them, most listened first.
    public static IReadOnlyList<string> TopGenres(IReadOnlyList<TasteArtist> artists, int take = 8) =>
        artists.SelectMany(a => a.Genres.Select(g => (Genre: g, a.Score)))
            .GroupBy(g => g.Genre, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Sum(x => x.Score)).Take(take).Select(g => g.Key).ToList();
}
