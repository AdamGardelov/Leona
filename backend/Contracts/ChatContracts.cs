using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harness.Contracts;

public record ChatRequest(
    string Text,
    string Model,
    bool Think,
    bool Web = false,
    bool Files = false,
    bool Commands = false,
    IReadOnlyList<AttachmentRef>? Attachments = null,
    bool Accounts = false,
    // Set by the scheduler only: the run gives way to people using Leona.
    bool Background = false,
    // Set for scheduled tasks, also with "Run now": more tool steps, since nobody waits for the answer, and
    // no e-mail drafts saved without asking.
    bool Scheduled = false);

// An uploaded file attached to a message. Only the ID is trusted from clients; the rest comes from the store.
public record AttachmentRef(Guid Id, string Name, string Kind, string Mime, long Size);

public record ChatEvent(
    string Type,
    string? Text = null,
    ToolCall? Call = null,
    int? PromptTokens = null,
    int? ContextWindow = null,
    int? EstimatedTokens = null)
{
    // Structured fields for tool, usage and inspection events. Null values are omitted from persisted events.
    public string? Id { get; init; }
    public string? Name { get; init; }
    public JsonElement? Arguments { get; init; }
    public string? Status { get; init; }
    public IReadOnlyList<SourceLink>? Sources { get; init; }
    public long? DurationMs { get; init; }
    public int? Round { get; init; }
    public int? EvalTokens { get; init; }
    public long? EvalDurationMs { get; init; }
    public string? DoneReason { get; init; }
    public object? Detail { get; init; }
}

public record ModelCapabilities(bool Thinking, bool Tools, bool Vision, int? ContextLength = null);

public record ChatLimits(int ContextWindow, int NumPredict, string? KeepAlive);

public record ToolFunction(string Name, JsonElement Arguments);

public record ToolCall(ToolFunction Function);

// These properties are read by System.Text.Json when sending messages to Ollama.
public record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")]
    string Content,
    [property: JsonPropertyName("tool_calls")]
    IReadOnlyList<ToolCall>? ToolCalls = null,
    [property: JsonPropertyName("tool_name")]
    string? ToolName = null,
    [property: JsonPropertyName("images")]
    IReadOnlyList<string>? Images = null);

public record SourceLink(string Title, string Url);

public static class ToolStatus
{
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Unavailable = "unavailable";

    // Saved evidence keeps only result text, so history derives the status from the result prefix.
    public static string FromContent(string content) =>
        content.StartsWith("Tool failed:") ? Failed
        : content.StartsWith("Tool unavailable: user rejected") ? Rejected
        : content.StartsWith("Tool unavailable: approval expired") ? Expired
        : content.StartsWith("Tool unavailable") ? Unavailable
        : Completed;
}

public record ToolResult(
    string Content,
    IReadOnlyList<SourceLink>? Sources = null,
    string Status = ToolStatus.Completed,
    string? Summary = null,
    // Set by tools that report only what is new (such as find_concerts with new_only). A scheduled run
    // whose every such call found nothing new finishes without a notification.
    bool? HasNews = null,
    // Pictures the tool made (such as an edited photo), shown with the reply and kept with it.
    IReadOnlyList<AttachmentRef>? Images = null);

public record ToolLimits(int SearchResults = 8, int PageCharacters = 8000);
