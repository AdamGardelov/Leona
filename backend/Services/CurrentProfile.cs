namespace Harness.Services;

// The profile a request or a run acts for, set by the sign-in middleware or the run manager.
// Null means system work (startup, run bookkeeping, schedulers), which sees every profile.
public sealed class CurrentProfile
{
    public int? Id { get; set; }
    public bool Owner { get; set; }
    public string Name { get; set; } = "";
}
