using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Models;

namespace Harness.Services;

public static class ContextBudget
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web);

    // Raw units charged per attached image, instead of counting its base64 bytes.
    private const int ImageUnits = 2000;

    // Conservative byte-based estimate, not a tokenizer. TokenCalibration scales it with measured counts.
    public static int RawEstimate(IReadOnlyList<OllamaMessage> messages, IReadOnlyList<object> tools)
    {
        var images = messages.Sum(m => m.Images?.Count ?? 0);
        var text = images == 0 ? messages : messages.Select(m => m.Images is null ? m : m with { Images = null }).ToList();
        return (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text, s_jsonOptions)) +
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(tools, s_jsonOptions))) / 2 + 256 + images * ImageUnits;
    }

    public static int Estimate(IReadOnlyList<OllamaMessage> messages, IReadOnlyList<object> tools, double factor) =>
        (int)Math.Ceiling(RawEstimate(messages, tools) * factor);

    // The context window minus the output reserve. At least half the window stays available for the prompt.
    public static ChatLimits Limits(AppSettings settings, ModelCapabilities capabilities, ChatRequest input)
    {
        var window = Math.Min(settings.ContextWindow, capabilities.ContextLength ?? settings.ContextWindow);
        var predict = settings.MaxOutputTokens + (input.Think && capabilities.Thinking ? settings.ThinkingTokens : 0);

        return new ChatLimits(window, Math.Min(predict, window / 2), settings.KeepAlive);
    }

    public static bool Fit(List<OllamaMessage> messages, IReadOnlyList<object> tools, int inputBudget, double factor)
    {
        var changed = false;
        while (Estimate(messages, tools, factor) > inputBudget)
        {
            var latestUser = messages.FindLastIndex(m => m.Role == "user");
            if (latestUser > 1)
            {
                // Remove a complete older user/assistant turn, retaining the active request.
                var nextUser = messages.FindIndex(2, m => m.Role == "user");
                messages.RemoveRange(1, (nextUser < 0 ? latestUser : nextUser) - 1);
                changed = true;
                continue;
            }

            // Halve the largest tool result first, keeping at least a short excerpt of each.
            var index = -1;
            for (var i = 0; i < messages.Count; i++)
            {
                if (messages[i].Role == "tool" && messages[i].Content.Length > 600 &&
                    (index < 0 || messages[i].Content.Length > messages[index].Content.Length))
                    index = i;
            }

            if (index >= 0)
            {
                var content = messages[index].Content;
                messages[index] = messages[index] with
                {
                    Content = Excerpt(content, Math.Max(450, content.Length / 2))
                };
                changed = true;
                continue;
            }

            throw new ContextBudgetException(
                "This request exceeds the context budget. Shorten your message or start a new conversation.");
        }

        return changed;
    }

    public static string Excerpt(string text, int limit) => text.Length <= limit
        ? text
        : text[..limit] + "\n[Excerpt truncated]";
}

public sealed class ContextBudgetException(string message) : Exception(message);

// Learns how much the byte-based estimate overshoots per model, from Ollama's measured prompt_eval_count.
public sealed class TokenCalibration
{
    private const double Margin = 1.15;
    private readonly ConcurrentDictionary<string, double> _factors = new();

    public double Factor(string model) => _factors.TryGetValue(model, out var factor) ? factor : 1.0;

    public void Observe(string model, int rawEstimate, int measured)
    {
        if (rawEstimate <= 0 || measured <= 0)
            return;

        var ratio = (double)measured / rawEstimate;
        // Ignore implausible samples; the estimate never assumes fewer tokens than 30% of the raw value.
        if (ratio < 0.1)
            return;

        var target = Math.Clamp(ratio * Margin, 0.3, 1.0);
        _factors.AddOrUpdate(model, target, (_, previous) => previous * 0.5 + target * 0.5);
    }
}
