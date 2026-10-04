using System.Runtime.CompilerServices;
using System.Text.Json;
using Harness.Contracts;

namespace Harness.Services;

public class OllamaClient(HttpClient client)
{
    public async Task<string?[]> GetModelsAsync(CancellationToken ct)
    {
        using var response = await client.GetAsync("/api/tags", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return json.RootElement.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString()).ToArray();
    }

    public async Task<ModelCapabilities> GetCapabilitiesAsync(string model, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync("/api/show", new { model }, ct);
        response.EnsureSuccessStatusCode();

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var capabilities = json.RootElement.TryGetProperty("capabilities", out var caps)
            ? caps.EnumerateArray().Select(c => c.GetString()).ToArray()
            : [];
        int? contextLength = null;
        if (json.RootElement.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            // Architectures prefix the key, for example "qwen3.context_length".
            foreach (var property in info.EnumerateObject())
            {
                if (property.Name.EndsWith(".context_length") && property.Value.TryGetInt32(out var length))
                    contextLength = length;
            }
        }

        return new(capabilities.Contains("thinking"), capabilities.Contains("tools"), capabilities.Contains("vision"),
            contextLength);
    }

    private static Dictionary<string, object> Payload(string model, IReadOnlyList<OllamaMessage> messages,
        bool stream, ChatLimits limits)
    {
        var payload = new Dictionary<string, object>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = stream,
            ["options"] = new { num_ctx = limits.ContextWindow, num_predict = limits.NumPredict }
        };
        // Ollama reads numbers as seconds (negative keeps the model loaded) and strings as durations.
        if (limits.KeepAlive is "-1" or "0")
        {
            payload["keep_alive"] = int.Parse(limits.KeepAlive);
        }
        else if (!string.IsNullOrWhiteSpace(limits.KeepAlive))
        {
            payload["keep_alive"] = limits.KeepAlive;
        }

        return payload;
    }

    public async Task<HttpResponseMessage> StartChatAsync(ChatRequest input, IReadOnlyList<OllamaMessage> messages,
        CancellationToken ct, ModelCapabilities capabilities, ChatLimits limits, IReadOnlyList<object>? tools = null)
    {
        var payload = Payload(input.Model, messages, true, limits);
        if (capabilities.Thinking)
        {
            payload["think"] = input.Think;
        }

        if (capabilities.Tools && tools is { Count: > 0 })
        {
            payload["tools"] = tools;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat");
        request.Content = JsonContent.Create(payload);

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.IsSuccessStatusCode)
            return response;

        response.Dispose();
        throw new HttpRequestException("Ollama rejected the request. Check the model and thinking setting.");
    }

    // A short, non-streamed completion without tools or thinking, used for conversation titles.
    public async Task<string> CompleteAsync(string model, IReadOnlyList<OllamaMessage> messages,
        ModelCapabilities capabilities, ChatLimits limits, CancellationToken ct)
    {
        var payload = Payload(model, messages, false, limits);
        if (capabilities.Thinking)
        {
            payload["think"] = false;
        }

        using var response = await client.PostAsJsonAsync("/api/chat", payload, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return json.RootElement.TryGetProperty("message", out var message) &&
               message.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : "";
    }

    public static async IAsyncEnumerable<ChatEvent> ReadChatAsync(HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var packet = JsonDocument.Parse(line);

            var root = packet.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new HttpRequestException(error.GetString());

            if (root.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("tool_calls", out var calls))
                {
                    foreach (var call in calls.EnumerateArray())
                    {
                        var function = call.GetProperty("function");
                        yield return new ChatEvent("tool_call", Call: new ToolCall(new ToolFunction(
                            function.GetProperty("name").GetString() ?? "",
                            function.GetProperty("arguments").Clone())));
                    }
                }

                foreach (var kind in new[] { "thinking", "content" })
                {
                    if (message.TryGetProperty(kind, out var value) && !string.IsNullOrEmpty(value.GetString()))
                        yield return new ChatEvent(kind, value.GetString());
                }
            }

            if (!root.TryGetProperty("done", out var done) || !done.GetBoolean())
                continue;

            yield return new ChatEvent("done",
                PromptTokens: root.TryGetProperty("prompt_eval_count", out var count) ? count.GetInt32() : null)
            {
                EvalTokens = root.TryGetProperty("eval_count", out var evalCount) ? evalCount.GetInt32() : null,
                // Ollama reports durations in nanoseconds.
                EvalDurationMs = root.TryGetProperty("eval_duration", out var duration)
                    ? duration.GetInt64() / 1_000_000
                    : null,
                DoneReason = root.TryGetProperty("done_reason", out var reason) ? reason.GetString() : null
            };
            yield break;
        }

        throw new IOException("Ollama ended the response early.");
    }
}
