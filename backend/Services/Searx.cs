using System.Text.Json;

namespace Harness.Services;

// SearXNG's JSON search API (Tools:SearxngUrl), used by the web search tool and to discover career pages.
public static class Searx
{
    public record Hit(string Title, string Url, string Snippet);

    public static async Task<List<Hit>> SearchAsync(HttpClient client, string baseUrl, string query, CancellationToken ct)
    {
        var json = await client.GetStringAsync(
            $"{baseUrl.TrimEnd('/')}/search?q={Uri.EscapeDataString(query)}&format=json", ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => new Hit(JsonPath.Text(r, "title") is { Length: > 0 } title ? title : "Result",
                JsonPath.Text(r, "url"), JsonPath.Text(r, "content")))
            .ToList();
    }
}
