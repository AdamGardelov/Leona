namespace Harness.Models;

// A procedure the user approved, such as how they want a weekly report made. When a new request fits,
// the steps are given to the model so it repeats what worked before.
public class Skill : IProfileOwned
{
    public int Id { get; set; }
    public int ProfileId { get; set; }
    public string Name { get; set; } = "";
    public string WhenToUse { get; set; } = "";
    public string Steps { get; set; } = "";
    public int Uses { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
