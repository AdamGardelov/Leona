using System.Text.Json;
using Harness.Data;
using Harness.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Loopia defaults; any IMAP/SMTP provider works by changing hosts and ports.
public record MailSettings(
    string Address = "",
    string ImapHost = "mailcluster.loopia.se",
    int ImapPort = 993,
    string SmtpHost = "mailcluster.loopia.se",
    int SmtpPort = 465);

// A CalDAV server (iCloud by default, with an app-specific password) or a read-only .ics feed.
public record CalendarSettings(string Url = "https://caldav.icloud.com", string Username = "");

public record HomeSettings(string Url = "http://homeassistant.local:8123");

public record AccountInput(string Kind, string Label, JsonElement Settings, string? Secret);

public record AccountView(int Id, string Kind, string Label, JsonElement Settings, bool HasSecret);

public class AccountService(ChatDb db, IDataProtectionProvider protection)
{
    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector _protector = protection.CreateProtector("Leona.Accounts.v1");

    public static AccountView View(Account account) => new(account.Id, account.Kind, account.Label,
        JsonSerializer.Deserialize<JsonElement>(account.SettingsJson), account.Secret.Length > 0);

    public async Task<List<AccountView>> ListAsync(CancellationToken ct) =>
        (await db.Accounts.AsNoTracking().OrderBy(a => a.Kind).ThenBy(a => a.Id).ToListAsync(ct))
        .Select(View).ToList();

    public Task<List<Account>> OfKindAsync(string kind, CancellationToken ct) =>
        db.Accounts.AsNoTracking().Where(a => a.Kind == kind).OrderBy(a => a.Id).ToListAsync(ct);

    public string Secret(Account account) =>
        account.Secret.Length == 0 ? "" : _protector.Unprotect(account.Secret);

    public static T Settings<T>(Account account) where T : class =>
        JsonSerializer.Deserialize<T>(account.SettingsJson, s_json) ??
        throw new InvalidOperationException("The account settings are missing.");

    public static bool IsIcsFeed(CalendarSettings settings) =>
        settings.Url.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) ||
        new Uri(settings.Url.Replace("webcal://", "https://")).AbsolutePath.EndsWith(".ics", StringComparison.OrdinalIgnoreCase);

    // Validates and normalises the settings for a kind; returns them as JSON.
    private static string Normalise(string kind, JsonElement raw, bool hasSecret)
    {
        static bool HttpUrl(string url, out Uri uri) =>
            Uri.TryCreate(url.Trim().Replace("webcal://", "https://"), UriKind.Absolute, out uri!) &&
            uri.Scheme is "http" or "https";

        switch (kind)
        {
            case AccountKind.Mail:
                {
                    var settings = raw.Deserialize<MailSettings>(s_json) ?? new MailSettings();
                    if (!MimeKit.MailboxAddress.TryParse(settings.Address.Trim(), out _) || !settings.Address.Contains('@'))
                        throw new ArgumentException("Enter the full e-mail address, for example name@example.com.");
                    if (string.IsNullOrWhiteSpace(settings.ImapHost) || string.IsNullOrWhiteSpace(settings.SmtpHost) ||
                        settings.ImapPort is < 1 or > 65535 || settings.SmtpPort is < 1 or > 65535)
                        throw new ArgumentException("Check the IMAP and SMTP servers and ports.");
                    if (!hasSecret)
                        throw new ArgumentException("Enter the mailbox password.");
                    return JsonSerializer.Serialize(settings with
                    {
                        Address = settings.Address.Trim(),
                        ImapHost = settings.ImapHost.Trim(),
                        SmtpHost = settings.SmtpHost.Trim()
                    }, s_json);
                }
            case AccountKind.Calendar:
                {
                    var settings = raw.Deserialize<CalendarSettings>(s_json) ?? new CalendarSettings();
                    if (!HttpUrl(settings.Url, out var uri) || (uri.Scheme != "https" && !uri.IsLoopback))
                        throw new ArgumentException("Calendar addresses must use https (or webcal).");
                    var normalised = settings with { Url = settings.Url.Trim(), Username = settings.Username.Trim() };
                    if (!IsIcsFeed(normalised) && (normalised.Username.Length == 0 || !hasSecret))
                        throw new ArgumentException("A CalDAV calendar needs a username and an app-specific password.");
                    return JsonSerializer.Serialize(normalised, s_json);
                }
            case AccountKind.Home:
                {
                    var settings = raw.Deserialize<HomeSettings>(s_json) ?? new HomeSettings();
                    if (!HttpUrl(settings.Url, out _))
                        throw new ArgumentException("Enter the Home Assistant address, for example http://homeassistant.local:8123.");
                    if (!hasSecret)
                        throw new ArgumentException("Enter a Home Assistant long-lived access token.");
                    return JsonSerializer.Serialize(settings with { Url = settings.Url.Trim().TrimEnd('/') }, s_json);
                }
            case AccountKind.Spotify:
                {
                    var settings = raw.Deserialize<SpotifySettings>(s_json) ?? new SpotifySettings();
                    if (!System.Text.RegularExpressions.Regex.IsMatch(settings.ClientId.Trim(), "^[0-9a-fA-F]{32}$"))
                        throw new ArgumentException("Paste the Client ID from your app at developer.spotify.com.");
                    if (!hasSecret)
                        throw new ArgumentException("Use Connect Spotify to sign in with Spotify.");
                    return JsonSerializer.Serialize(settings with { ClientId = settings.ClientId.Trim() }, s_json);
                }
            default:
                throw new ArgumentException("Unknown account type.");
        }
    }

    public async Task<AccountView> SaveAsync(int? id, AccountInput input, CancellationToken ct)
    {
        var label = input.Label.Trim();
        if (label.Length is 0 or > 40)
            throw new ArgumentException("Give the account a short name, for example Adam or Work.");
        var account = id is { } existing
            ? await db.Accounts.FindAsync([existing], ct) ?? throw new KeyNotFoundException()
            : new Account { Kind = input.Kind };
        if (account.Kind != input.Kind)
            throw new ArgumentException("The account type cannot change.");
        if (await db.Accounts.AnyAsync(a => a.Kind == input.Kind && a.Label == label && a.Id != account.Id, ct))
            throw new ArgumentException("Another account of this type already has that name.");

        var secret = string.IsNullOrEmpty(input.Secret) ? null : input.Secret.Trim();
        account.SettingsJson = Normalise(input.Kind, input.Settings, secret is not null || account.Secret.Length > 0);
        account.Label = label;
        if (secret is not null)
            account.Secret = _protector.Protect(secret);
        if (id is null)
            db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
        return View(account);
    }

    // Used when a service hands out a new token, such as Spotify rotating its refresh token.
    public Task ReplaceSecretAsync(int id, string secret, CancellationToken ct) =>
        db.Accounts.IgnoreQueryFilters().Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Secret, _protector.Protect(secret)), ct);

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(ct) > 0;
}
