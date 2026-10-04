namespace Harness.Models;

// A short note the assistant saved about the user or their work; always treated as data.
public class Memory : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Text { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
