namespace Harness.Models;

// A paired phone or other device, or a Siri key. Only a hash of its token is stored.
public class DeviceSession : IProfileOwned
{
    public const string Device = "device";
    // A key for the "Ask Leona" shortcut: it can only ask questions (POST /api/ask), nothing else.
    public const string Siri = "siri";

    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Kind { get; set; } = Device;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
