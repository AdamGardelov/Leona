using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Access from other devices on the local network. The computer itself (loopback) needs no sign-in;
// every other device must be paired with a one-time code created on the computer, for one profile.
public sealed class RemoteAccess(IConfiguration configuration, IServiceScopeFactory scopes)
{
    public const string CookieName = "leona_session";
    private const int MaxFailedAttempts = 10;
    private static readonly TimeSpan s_pairingLifetime = TimeSpan.FromMinutes(10);
    // Session token hash to the paired device's profile.
    private readonly ConcurrentDictionary<string, int> _sessions = new();
    // Siri key hash to its profile; these only work for asking questions.
    private readonly ConcurrentDictionary<string, int> _keys = new();
    private readonly Lock _pairingLock = new();
    private (string Code, DateTime ExpiresAt, int ProfileId)? _pairing;
    private int _failedAttempts;
    private (DateTime At, IReadOnlyList<string> Addresses) _addressCache = (DateTime.MinValue, []);
    private (DateTime At, string? Host) _tailscaleCache = (DateTime.MinValue, null);
    private readonly Lock _tailscaleLock = new();

    public bool Enabled => configuration.GetValue("Remote:Enabled", false);
    public int Port => configuration.GetValue("Remote:Port", 5080);

    // Requests from the computer itself, not from a phone or through Tailscale Serve.
    public static bool IsLocal(HttpContext context) => IsLoopback(context.Connection.RemoteIpAddress);

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return IPAddress.IsLoopback(address);
    }

    // Private IPv4 addresses of active network adapters, excluding container and VM bridges.
    public IReadOnlyList<string> LanAddresses()
    {
        if (DateTime.UtcNow - _addressCache.At < TimeSpan.FromSeconds(30))
            return _addressCache.Addresses;
        string[] virtualPrefixes = ["docker", "br-", "veth", "virbr", "vmnet", "tailscale"];
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        !virtualPrefixes.Any(prefix => n.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a))
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
        _addressCache = (DateTime.UtcNow, addresses);
        return addresses;
    }

    private static bool IsPrivate(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    // This machine's Tailscale name (for example laptop.tailnet.ts.net), from Remote:TailscaleHost or
    // `tailscale status`. Requests through Tailscale Serve arrive with it as host.
    public string? TailscaleHost()
    {
        var configured = configuration["Remote:TailscaleHost"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim().TrimEnd('.').ToLowerInvariant();
        // One request asks tailscale at a time; the others use the answer.
        lock (_tailscaleLock)
        {
            if (DateTime.UtcNow - _tailscaleCache.At >= TimeSpan.FromMinutes(1))
                _tailscaleCache = (DateTime.UtcNow, AskTailscale());
            return _tailscaleCache.Host;
        }
    }

    private static string? AskTailscale()
    {
        string? host = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo("tailscale", "status --json")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            });
            // Reading while it runs keeps a large status from filling the pipe and stalling the process.
            var output = process?.StandardOutput.ReadToEndAsync();
            _ = process?.StandardError.ReadToEndAsync();
            if (process is not null && output is not null && process.WaitForExit(3000) && process.ExitCode == 0)
            {
                using var json = System.Text.Json.JsonDocument.Parse(output.Result);
                if (json.RootElement.TryGetProperty("Self", out var self) &&
                    self.TryGetProperty("DNSName", out var name))
                    host = name.GetString()?.TrimEnd('.').ToLowerInvariant();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                       or System.Text.Json.JsonException)
        {
            // Tailscale is not installed or not running.
        }

        return string.IsNullOrEmpty(host) ? null : host;
    }

    // Host names other than these are refused, which keeps DNS rebinding out.
    public bool IsAllowedHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]" || (Enabled && LanAddresses().Contains(host)) ||
        (TailscaleHost() is { } tailscale && host.Equals(tailscale, StringComparison.OrdinalIgnoreCase));

    public bool IsAllowedOrigin(Uri origin) =>
        (origin.Scheme == "http" &&
         ((origin.Host is "localhost" or "127.0.0.1" or "[::1]" && origin.Port is 5173 or 5080) ||
          (Enabled && origin.Port == Port && LanAddresses().Contains(origin.Host)))) ||
        (origin.Scheme == "https" && origin.IsDefaultPort && TailscaleHost() is { } tailscale &&
         origin.Host.Equals(tailscale, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> Addresses()
    {
        var addresses = Enabled ? LanAddresses().Select(a => $"http://{a}:{Port}").ToList() : [];
        if (TailscaleHost() is { } tailscale)
            addresses.Insert(0, $"https://{tailscale}");
        return addresses;
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        foreach (var device in await db.DeviceSessions.AsNoTracking().ToListAsync(ct))
            (device.Kind == DeviceSession.Siri ? _keys : _sessions)[device.TokenHash] = device.ProfileId;
    }

    // A new six-digit code replaces any earlier one and is valid once, for ten minutes. The device that
    // uses it signs in as the given profile.
    public (string Code, DateTime ExpiresAt) CreatePairing(int profileId)
    {
        lock (_pairingLock)
        {
            _pairing = (RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6"),
                DateTime.UtcNow.Add(s_pairingLifetime), profileId);
            _failedAttempts = 0;
            return (_pairing.Value.Code, _pairing.Value.ExpiresAt);
        }
    }

    // Returns a session token for a correct code. Repeated wrong guesses cancel the pairing.
    public async Task<string?> PairAsync(string code, string deviceName, CancellationToken ct)
    {
        int profileId;
        lock (_pairingLock)
        {
            if (_pairing is not { } pairing || pairing.ExpiresAt <= DateTime.UtcNow)
                return null;
            var match = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(code.Trim()),
                Encoding.UTF8.GetBytes(pairing.Code));
            if (!match)
            {
                if (++_failedAttempts >= MaxFailedAttempts)
                    _pairing = null;
                return null;
            }

            profileId = pairing.ProfileId;
            _pairing = null;
        }

        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var device = new DeviceSession { ProfileId = profileId, Name = deviceName, TokenHash = Hash(token) };
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        db.DeviceSessions.Add(device);
        await db.SaveChangesAsync(ct);
        _sessions[device.TokenHash] = device.ProfileId;
        return token;
    }

    // A pending code for a removed profile must not sign anyone in.
    public void ForgetPairing(int profileId)
    {
        lock (_pairingLock)
        {
            if (_pairing?.ProfileId == profileId)
                _pairing = null;
        }
    }

    // The profile a paired device signs in as, or null for an unknown or removed session.
    public int? ProfileOf(string? token) =>
        token is { Length: > 0 } && _sessions.TryGetValue(Hash(token), out var profileId) ? profileId : null;

    // The profile a Siri key asks for, or null for an unknown or removed key.
    public int? KeyProfileOf(string? key) =>
        key is { Length: > 0 } && _keys.TryGetValue(Hash(key), out var profileId) ? profileId : null;

    // A new Siri key for the profile. The key is shown once; only its hash is kept.
    public async Task<(int Id, string Key)> CreateKeyAsync(int profileId, string name, CancellationToken ct)
    {
        var key = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var device = new DeviceSession { ProfileId = profileId, Name = name, TokenHash = Hash(key), Kind = DeviceSession.Siri };
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        db.DeviceSessions.Add(device);
        await db.SaveChangesAsync(ct);
        _keys[device.TokenHash] = profileId;
        return (device.Id, key);
    }

    public record Key(int Id, string Name, DateTime CreatedAt);

    public async Task<List<Key>> KeysAsync(int profileId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        return await db.DeviceSessions.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.ProfileId == profileId && d.Kind == DeviceSession.Siri)
            .OrderByDescending(d => d.Id).Select(d => new Key(d.Id, d.Name, d.CreatedAt)).ToListAsync(ct);
    }

    public async Task<bool> RemoveKeyAsync(int profileId, int id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var key = await db.DeviceSessions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == id && d.ProfileId == profileId && d.Kind == DeviceSession.Siri, ct);
        if (key is null)
            return false;
        _keys.TryRemove(key.TokenHash, out _);
        db.DeviceSessions.Remove(key);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public record Device(int Id, string Name, DateTime CreatedAt, int ProfileId, string Profile);

    public async Task<List<Device>> DevicesAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return (await db.DeviceSessions.AsNoTracking().OrderByDescending(d => d.Id).ToListAsync(ct))
            .Select(d => new Device(d.Id, d.Name, d.CreatedAt, d.ProfileId, names.GetValueOrDefault(d.ProfileId, "")))
            .ToList();
    }

    public async Task RemoveDevicesAsync(int profileId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        foreach (var device in await db.DeviceSessions.Where(d => d.ProfileId == profileId).ToListAsync(ct))
        {
            _sessions.TryRemove(device.TokenHash, out _);
            _keys.TryRemove(device.TokenHash, out _);
            db.DeviceSessions.Remove(device);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RemoveDeviceAsync(int id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var device = await db.DeviceSessions.FindAsync([id], ct);
        if (device is null)
            return false;
        _sessions.TryRemove(device.TokenHash, out _);
        _keys.TryRemove(device.TokenHash, out _);
        db.DeviceSessions.Remove(device);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // "Android · Chrome" and similar, from the browser's user agent.
    public static string DeviceName(string userAgent)
    {
        var system = userAgent switch
        {
            _ when userAgent.Contains("iPhone") => "iPhone",
            _ when userAgent.Contains("iPad") => "iPad",
            _ when userAgent.Contains("Android") => "Android",
            _ when userAgent.Contains("Mac OS") => "Mac",
            _ when userAgent.Contains("Windows") => "Windows",
            _ when userAgent.Contains("Linux") => "Linux",
            _ => "Device"
        };
        var browser = userAgent switch
        {
            _ when userAgent.Contains("Firefox") || userAgent.Contains("FxiOS") => "Firefox",
            _ when userAgent.Contains("Edg") => "Edge",
            _ when userAgent.Contains("Chrome") || userAgent.Contains("CriOS") => "Chrome",
            _ when userAgent.Contains("Safari") => "Safari",
            _ => "browser"
        };
        return $"{system} · {browser}";
    }

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
