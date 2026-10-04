namespace Harness.Models;

public class ToolEvidence
{
    public int Id { get; set; }
    public int ConversationId { get; set; }
    public int UserMessageId { get; set; }
    public string ToolName { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string Excerpt { get; set; } = "";
    public string SourcesJson { get; set; } = "[]";
}
