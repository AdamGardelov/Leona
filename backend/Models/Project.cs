namespace Harness.Models;

// A group of chats with its own instructions and files, such as "Göra med Walle" or a house purchase.
public class Project : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Name { get; set; } = "";
    // Added to every chat in the project, like the user's own custom instructions.
    public string Instructions { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
