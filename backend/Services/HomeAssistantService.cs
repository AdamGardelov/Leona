using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Harness.Models;

namespace Harness.Services;

// Home Assistant's REST API with a long-lived access token: read states, call services after approval.
public class HomeAssistantService(AccountService accounts, IHttpClientFactory clients)
{
    public record EntityState(string EntityId, string Name, string State, string? Unit, string? Area = null);

    // The state list has no rooms, so they come from the template endpoint: [[entity_id, area], ...] for
    // every entity whose own or device's area is set.
    public const string AreaTemplate =
        "{%- set ns = namespace(pairs=[]) -%}{%- for s in states -%}{%- set a = area_name(s.entity_id) -%}" +
        "{%- if a -%}{%- set ns.pairs = ns.pairs + [[s.entity_id, a]] -%}{%- endif -%}{%- endfor -%}{{ ns.pairs | tojson }}";

    private static readonly ConcurrentDictionary<int, (DateTime At, Dictionary<string, string> Areas)> s_areas = new();
    private static readonly ConcurrentDictionary<int, (DateTime At, IReadOnlyCollection<string> Words)> s_vocabulary = new();

    // Names of people, zones and bookkeeping entities would make ordinary messages look like home questions.
    private static readonly string[] s_notDevices =
    [
        "person", "device_tracker", "zone", "sun", "update", "automation", "script", "persistent_notification",
        "conversation", "tts", "stt", "event", "todo"
    ];

    // Lights and switches first, so a long list keeps what people usually ask about.
    private static readonly string[] s_domainOrder =
        ["light", "switch", "cover", "climate", "fan", "media_player", "lock", "vacuum", "sensor", "binary_sensor"];

    private HttpClient Client(Account account)
    {
        var client = clients.CreateClient("home");
        client.BaseAddress = new Uri(AccountService.Settings<HomeSettings>(account).Url + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accounts.Secret(account));
        return client;
    }

    public async Task<List<EntityState>> StatesAsync(Account account, CancellationToken ct)
    {
        using var response = await Client(account).GetAsync("api/states", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new ArgumentException($"{account.Label}: Home Assistant refused the access token.");
        response.EnsureSuccessStatusCode();
        var areas = await AreasAsync(account, ct);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.EnumerateArray().Select(e =>
            {
                var attributes = e.GetProperty("attributes");
                var id = e.GetProperty("entity_id").GetString() ?? "";
                return new EntityState(
                    id,
                    attributes.TryGetProperty("friendly_name", out var name) ? name.GetString() ?? "" : "",
                    e.GetProperty("state").GetString() ?? "",
                    attributes.TryGetProperty("unit_of_measurement", out var unit) ? unit.GetString() : null,
                    areas.GetValueOrDefault(id));
            })
            .ToList();
    }

    // Rooms change rarely, so they are kept for ten minutes. A token without admin rights or an old Home
    // Assistant may refuse templates; entities are then listed without rooms.
    private async Task<Dictionary<string, string>> AreasAsync(Account account, CancellationToken ct)
    {
        if (s_areas.TryGetValue(account.Id, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            return cached.Areas;
        var areas = new Dictionary<string, string>();
        try
        {
            using var response = await Client(account).PostAsJsonAsync("api/template", new { template = AreaTemplate }, ct);
            if (response.IsSuccessStatusCode)
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var pair in json.RootElement.EnumerateArray())
                    areas[pair[0].GetString() ?? ""] = pair[1].GetString() ?? "";
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException ||
                                   (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Listed without rooms.
        }

        s_areas[account.Id] = (DateTime.UtcNow, areas);
        return areas;
    }

    // Room names and device names, so a message that only says "kontoret" or "skrivbordet" still gets the
    // Home Assistant tools. Kept for ten minutes; an unreachable Home Assistant gives no words.
    public async Task<IReadOnlyCollection<string>> VocabularyAsync(Account account, CancellationToken ct)
    {
        if (s_vocabulary.TryGetValue(account.Id, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromMinutes(10))
            return cached.Words;
        IReadOnlyCollection<string> words = [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            words = (await StatesAsync(account, timeout.Token))
                .Where(s => !s_notDevices.Contains(s.EntityId.Split('.')[0]))
                .SelectMany(s => TextMatch.Words(s.Name).Concat(TextMatch.Words(s.Area)))
                .Where(w => w.Length >= 4 && !w.All(char.IsDigit))
                .ToHashSet();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Home Assistant is offline; the fixed word list still applies.
        }

        s_vocabulary[account.Id] = (DateTime.UtcNow, words);
        return words;
    }

    // Matches every query word against the entity id, name and room ("kontoret" finds the room Kontor).
    // Results are grouped by room and the reply says how many were left out.
    public static string Format(IReadOnlyList<EntityState> states, string? query, int limit = 60)
    {
        var words = TextMatch.Words(query);
        var scored = states.Select(s => (State: s, Score: words.Count == 0 ? 1 : words.Count(w => Tokens(s).Any(t => TextMatch.Similar(w, t)))))
            .Where(x => x.Score > 0).ToList();
        // Prefer entities that match every word, such as "lampa kontor".
        if (words.Count > 1 && scored.Any(x => x.Score == words.Count))
            scored = scored.Where(x => x.Score == words.Count).ToList();
        if (scored.Count == 0)
            return $"No devices or sensors matched \"{query}\". Rooms: {Rooms(states)}.";

        var shown = scored.OrderByDescending(x => x.Score).ThenBy(x => DomainRank(x.State.EntityId))
            .ThenBy(x => x.State.EntityId).Take(limit).Select(x => x.State).ToList();
        var text = new StringBuilder();
        foreach (var room in shown.GroupBy(s => s.Area ?? "").OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key))
        {
            text.AppendLine(room.Key.Length == 0 ? "No room:" : $"{room.Key}:");
            foreach (var s in room)
                text.AppendLine($"  {s.EntityId} · {s.Name} · {s.State}{(s.Unit is null ? "" : " " + s.Unit)}");
        }

        if (scored.Count > shown.Count)
            text.AppendLine($"Showing {shown.Count} of {scored.Count}. Search by room, device name or type (query) to see the rest.");
        if (words.Count == 0)
            text.AppendLine($"Rooms: {Rooms(states)}.");
        return text.ToString().TrimEnd();
    }

    private static IEnumerable<string> Tokens(EntityState s) =>
        TextMatch.Words(s.EntityId.Replace('.', ' ').Replace('_', ' ')).Concat(TextMatch.Words(s.Name))
            .Concat(TextMatch.Words(s.Area)).Append(s.EntityId.Split('.')[0]);

    private static int DomainRank(string entityId) =>
        Array.IndexOf(s_domainOrder, entityId.Split('.')[0]) is var rank and >= 0 ? rank : s_domainOrder.Length;

    private static string Rooms(IEnumerable<EntityState> states)
    {
        var rooms = states.Where(s => s.Area is not null).GroupBy(s => s.Area!).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} ({g.Count()})").ToList();
        return rooms.Count == 0 ? "none known" : string.Join(", ", rooms);
    }

    // "light.turn_on" or "turn_on" with the domain taken from the entity.
    public static (string Domain, string Service) ParseService(string entityId, string service)
    {
        var parts = service.Split('.', 2);
        var domain = parts.Length == 2 ? parts[0] : entityId.Split('.')[0];
        var name = parts.Length == 2 ? parts[1] : parts[0];
        if (!System.Text.RegularExpressions.Regex.IsMatch(domain, "^[a-z_]+$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z_]+$"))
            throw new ArgumentException("Use a service like turn_on, turn_off or light.turn_on.");
        return (domain, name);
    }

    public async Task<string> CallAsync(Account account, string entityId, string service, JsonElement? data,
        CancellationToken ct)
    {
        var (domain, name) = ParseService(entityId, service);
        var payload = new Dictionary<string, object?> { ["entity_id"] = entityId };
        if (data is { ValueKind: JsonValueKind.Object } extra)
        {
            foreach (var property in extra.EnumerateObject().Where(p => p.Name != "entity_id"))
                payload[property.Name] = property.Value;
        }

        using var response = await Client(account).PostAsJsonAsync($"api/services/{domain}/{name}", payload, ct);
        if (!response.IsSuccessStatusCode)
            throw new ArgumentException($"Home Assistant answered {(int)response.StatusCode}: {ContextBudget.Excerpt(await response.Content.ReadAsStringAsync(ct), 200)}");
        return $"Called {domain}.{name} for {entityId}.";
    }

    public async Task<string> TestAsync(Account account, CancellationToken ct)
    {
        var states = await StatesAsync(account, ct);
        return $"Connected. {states.Count:N0} devices and sensors.";
    }
}
