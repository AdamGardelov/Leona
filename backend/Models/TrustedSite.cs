namespace Harness.Models;

// A website the user lets Leona open without asking, also after a chat has read untrusted content.
// Host is stored in lower case without "www." and also covers its subdomains.
public class TrustedSite : IProfileOwned
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string Host { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
