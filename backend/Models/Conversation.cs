namespace Harness.Models;

public class Conversation : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Archived { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Last time someone wrote in it; the sidebar sorts and groups by this (Today, Yesterday, ...).
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    // Last time Leona saved a reply, and last time the user had the chat open; a newer reply is unread.
    public DateTime? RepliedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public int? ProjectId { get; set; }

    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool Unread => RepliedAt is { } replied && (ReadAt is not { } read || replied > read);
}
