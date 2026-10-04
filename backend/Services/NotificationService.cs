using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Harness.Data;
using Harness.Models;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using WebPushSubscription = Lib.Net.Http.WebPush.PushSubscription;

namespace Harness.Services;

// Notifications are stored for one profile's in-app list and pushed to that profile's subscribed devices
// (Web Push needs HTTPS, for example through Tailscale Serve, or localhost).
public sealed class NotificationService(
    IServiceScopeFactory scopes,
    IHttpClientFactory clients,
    IDataProtectionProvider protection,
    IConfiguration configuration,
    RemoteAccess remote,
    ILogger<NotificationService> logger)
{
    private readonly IDataProtector _protector = protection.CreateProtector("Leona.Push.v1");
    private readonly SemaphoreSlim _keysLock = new(1, 1);

    // The VAPID key pair is created once and kept in the database, the private key encrypted.
    public async Task<(string PublicKey, string PrivateKey)> KeysAsync(CancellationToken ct)
    {
        await _keysLock.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
            var keys = await db.PushKeys.FindAsync([1], ct);
            if (keys is null)
            {
                using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var parameters = ecdsa.ExportParameters(true);
                keys = new PushKeys
                {
                    PublicKey = Base64Url.EncodeToString([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]),
                    PrivateKey = _protector.Protect(Base64Url.EncodeToString(parameters.D!))
                };
                db.PushKeys.Add(keys);
                await db.SaveChangesAsync(ct);
            }

            return (keys.PublicKey, _protector.Unprotect(keys.PrivateKey));
        }
        finally
        {
            _keysLock.Release();
        }
    }

    public async Task SubscribeAsync(int profileId, string endpoint, string p256dh, string auth, string device,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            string.IsNullOrWhiteSpace(p256dh) || string.IsNullOrWhiteSpace(auth))
            throw new ArgumentException("Invalid push subscription.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var existing = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, ct);
        if (existing is null)
        {
            db.PushSubscriptions.Add(new Models.PushSubscription
            {
                ProfileId = profileId, Endpoint = endpoint, P256dh = p256dh, Auth = auth, DeviceName = device
            });
        }
        else
        {
            // A browser follows the profile it last subscribed for, for example after switching on the computer.
            existing.ProfileId = profileId;
            existing.P256dh = p256dh;
            existing.Auth = auth;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task UnsubscribeAsync(int profileId, string endpoint, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        await db.PushSubscriptions.Where(s => s.ProfileId == profileId && s.Endpoint == endpoint).ExecuteDeleteAsync(ct);
    }

    public async Task<Notification> NotifyAsync(int profileId, string title, string body, string? url,
        CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
        var notification = new Notification
        {
            ProfileId = profileId,
            Title = ContextBudget.Excerpt(title, 120, "…"),
            Body = ContextBudget.Excerpt(body, 600, "…"),
            Url = url
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);
        // Keep the list bounded.
        var old = await db.Notifications.Where(n => n.ProfileId == profileId).OrderByDescending(n => n.Id).Skip(200)
            .Select(n => n.Id).ToListAsync(ct);
        if (old.Count > 0)
            await db.Notifications.Where(n => old.Contains(n.Id)).ExecuteDeleteAsync(ct);

        var subscriptions = await db.PushSubscriptions.Where(s => s.ProfileId == profileId).ToListAsync(ct);
        if (subscriptions.Count > 0)
            await PushAsync(db, subscriptions, notification, ct);
        return notification;
    }

    private async Task PushAsync(ChatDb db, List<Models.PushSubscription> subscriptions, Notification notification,
        CancellationToken ct)
    {
        var (publicKey, privateKey) = await KeysAsync(ct);
        var client = new PushServiceClient(clients.CreateClient("push"))
        {
            DefaultAuthentication = new VapidAuthentication(publicKey, privateKey)
            {
                Subject = configuration["Push:Subject"] ??
                          (remote.TailscaleHost() is { } host ? $"https://{host}" : "mailto:leona@example.com")
            }
        };
        var payload = JsonSerializer.Serialize(new
        {
            title = notification.Title,
            body = notification.Body,
            url = notification.Url ?? "/",
            tag = $"leona-{notification.Id}"
        });
        foreach (var subscription in subscriptions)
        {
            try
            {
                var target = new WebPushSubscription { Endpoint = subscription.Endpoint };
                target.SetKey(PushEncryptionKeyName.P256DH, subscription.P256dh);
                target.SetKey(PushEncryptionKeyName.Auth, subscription.Auth);
                await client.RequestPushMessageDeliveryAsync(target,
                    new PushMessage(payload) { TimeToLive = 6 * 3600, Urgency = PushMessageUrgency.Normal }, ct);
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.NotFound)
            {
                // The device unsubscribed or the browser data was cleared.
                db.PushSubscriptions.Remove(subscription);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Push to {Device} failed", subscription.DeviceName);
            }
        }

        await db.SaveChangesAsync(ct);
    }
}
