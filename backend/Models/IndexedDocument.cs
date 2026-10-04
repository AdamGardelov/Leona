namespace Harness.Models;

public static class DocumentSource
{
    // A file in one of the owner's folders (Settings › Folders).
    public const string Folder = "folder";
    // An uploaded document: attached to a message, or one of a project's files.
    public const string Upload = "upload";
}

// A document in the search index: where it is, when it was read and whether that worked.
public class IndexedDocument : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public string Source { get; set; } = DocumentSource.Folder;
    // The file's full path, or the upload's id.
    public string Key { get; set; } = "";
    // What the user sees: a path inside its folder, or the upload's file name.
    public string Name { get; set; } = "";
    public int? ProjectId { get; set; }
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }
}

// A passage of an indexed document with its embedding (float32 values, normalized).
public class DocumentChunk : IProfileOwned
{
    public int ProfileId { get; set; }
    public int Id { get; set; }
    public int DocumentId { get; set; }
    public int Part { get; set; }
    // Where it is in the document, such as "page 3".
    public string Location { get; set; } = "";
    public string Text { get; set; } = "";
    public byte[] Vector { get; set; } = [];
}
