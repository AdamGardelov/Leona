namespace Harness.Models;

public static class UploadKind
{
    public const string Image = "image";
    public const string Document = "document";
}

// A file the user attached to a message. The bytes live in the uploads folder under Id/Name.
public class Upload : IProfileOwned
{
    public int ProfileId { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Kind { get; set; } = UploadKind.Document;
    public string Mime { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
