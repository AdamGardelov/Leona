namespace Harness.Models;

// One person using Leona. Chats, accounts, automations, notifications, memories and uploads belong to a
// profile, and each profile only sees its own.
public class Profile
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    // The computer owner's profile. Only it may use file and terminal tools or change shared settings.
    public bool Owner { get; set; }
    public string CustomInstructions { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// Rows that belong to one profile. New rows get the current profile when they are saved.
public interface IProfileOwned
{
    int ProfileId { get; set; }
}
