using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Harness.Contracts;

namespace Harness.Services;

// Runs one approved shell command in an allowed folder, with a timeout and bounded output.
public static partial class CommandRunner
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 600;
    private const int MaxCapturedCharacters = 1_000_000;

    // Approval is the security boundary; this only refuses obvious privilege escalation.
    public static void Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > 2000)
            throw new ArgumentException("Give a single command of at most 2,000 characters.");
        if (Elevation().IsMatch(command))
            throw new ArgumentException("Commands using sudo, su, doas or pkexec are not allowed.");
    }

    public static async Task<ToolResult> RunAsync(string command, string workingDirectory, string displayFolder,
        int timeoutSeconds, CancellationToken ct)
    {
        Validate(command);
        var start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", command } }
            : new ProcessStartInfo(File.Exists("/bin/bash") ? "/bin/bash" : "/bin/sh") { ArgumentList = { "-c", command } };
        start.WorkingDirectory = workingDirectory;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        // Non-interactive defaults: no prompts, colours or pagers.
        start.Environment["CI"] = "1";
        start.Environment["TERM"] = "dumb";
        start.Environment["NO_COLOR"] = "1";
        start.Environment["PAGER"] = "cat";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        var output = new StringBuilder();
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw new IOException("The command could not be started.");
        process.StandardInput.Close();
        var pumps = new[] { Pump(process.StandardOutput, output), Pump(process.StandardError, output) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5),
                CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        // Background children can keep the pipes open; stop waiting for them after a short grace period.
        await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None)
            .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        ct.ThrowIfCancellationRequested();

        string captured;
        lock (output)
            captured = output.ToString();
        var exitCode = process.HasExited ? process.ExitCode : -1;
        var status = timedOut
            ? $"Timed out after {timeoutSeconds} s; the process was stopped."
            : $"Exit code {exitCode} after {watch.Elapsed.TotalSeconds:0.0} s.";
        var text = $"$ {command}\n(in {displayFolder})\n{status}\n\n{Excerpt(captured)}";
        return new ToolResult(text,
            Status: !timedOut && exitCode == 0 ? ToolStatus.Completed : ToolStatus.Failed,
            Summary: timedOut ? "Timed out" : $"Exit code {exitCode}");
    }

    private static async Task Pump(StreamReader reader, StringBuilder output)
    {
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            lock (output)
            {
                // Keep draining the pipe after the cap so the process never blocks on a full buffer.
                if (output.Length < MaxCapturedCharacters)
                    output.Append(buffer, 0, Math.Min(read, MaxCapturedCharacters - output.Length));
            }
        }
    }

    // The start and end of long output are the parts that usually matter (command echo and errors).
    private static string Excerpt(string output)
    {
        if (output.Length == 0)
            return "(no output)";
        if (output.Length <= 12000)
            return output.TrimEnd();
        return output[..4000] + $"\n[… {output.Length - 12000:N0} characters omitted …]\n" + output[^8000..].TrimEnd();
    }

    [GeneratedRegex(@"(^|[;&|()`\s])(sudo|su|doas|pkexec)(\s|$)")]
    private static partial Regex Elevation();
}
