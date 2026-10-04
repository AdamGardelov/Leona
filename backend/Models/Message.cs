namespace Harness.Models;

public class Message
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public string Thinking { get; set; } = "";
    public bool Complete { get; set; } = true;
    // The model stopped at its output token limit; the text is kept but may end mid-sentence.
    public bool Truncated { get; set; }
    // The model that wrote an assistant message; empty for user messages and older replies.
    public string Model { get; set; } = "";
    // AttachmentRef list of the files sent with a user message.
    public string AttachmentsJson { get; set; } = "[]";
}
