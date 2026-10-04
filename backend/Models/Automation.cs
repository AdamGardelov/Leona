namespace Harness.Models;

public static class AccountKind
{
    public const string Mail = "mail";
    public const string Calendar = "calendar";
    public const string Home = "home";
    public const string Spotify = "spotify";
}

// A connected account (mail, calendar or Home Assistant). The secret is encrypted with Data Protection.
public class Account : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Kind { get; set; } = AccountKind.Mail;
    public string Label { get; set; } = "";
    public string SettingsJson { get; set; } = "{}";
    public string Secret { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// An in-app notification, also pushed to subscribed devices.
public class Notification : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string? Url { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Read { get; set; }
}

public class PushSubscription : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// The server's VAPID key pair for Web Push (single row, Id 1). The private key is encrypted.
public class PushKeys
{
    public int Id { get; set; } = 1;
    public string PublicKey { get; set; } = "";
    public string PrivateKey { get; set; } = "";
}

// A prompt that runs on a schedule in its own conversation, for example a morning brief.
public class ScheduledTask : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Prompt { get; set; } = "";
    // Local time "HH:mm" and weekdays as a bit mask: Monday = 1 ... Sunday = 64.
    public string Time { get; set; } = "07:00";
    public int Days { get; set; } = 31;
    public string Model { get; set; } = "";
    public bool Web { get; set; }
    public bool Files { get; set; }
    public bool Accounts { get; set; }
    public bool Enabled { get; set; } = true;
    public int? ConversationId { get; set; }
    public DateTime? LastRunAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A web page checked on an interval; notifies when the watched value changes or drops below a limit.
public class Watch : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public string Find { get; set; } = "";
    public double? Below { get; set; }
    public int IntervalMinutes { get; set; } = 60;
    public string? LastValue { get; set; }
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastChangedAt { get; set; }
    public string? LastError { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
