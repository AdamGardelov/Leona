namespace Harness.Models;

public static class RunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string AwaitingApproval = "awaiting_approval";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
    public const string Interrupted = "interrupted";
    public static readonly string[] Active = [Queued, Running, AwaitingApproval];

    public static bool IsActive(string status) => status is Queued or Running or AwaitingApproval;
}

public static class ActionStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Executing = "executing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
    public const string Interrupted = "interrupted";
    public static readonly string[] Unfinished = [Pending, Approved, Executing];
}

public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int ConversationId { get; set; }
    public int BaseMessageId { get; set; }
    public string RequestJson { get; set; } = "";
    public string Status { get; set; } = RunStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class RunEvent
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public string Json { get; set; } = "";
}

public class RunAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public string ToolName { get; set; } = "";
    public string ArgumentsJson { get; set; } = "";
    public string Status { get; set; } = ActionStatus.Pending;
    public string? Result { get; set; }
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
}
