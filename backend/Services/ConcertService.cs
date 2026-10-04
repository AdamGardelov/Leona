using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Services;

public record Concert(string Title, DateTime? Start, string Venue, string Url, string? Tickets, string Summary,
    string Source);

public record ConcertMatch(Concert Concert, string Artist, bool Tribute);

// Upcoming concerts in Göteborg. The main source is the city's own event calendar (goteborg.com, run by
// Göteborg & Co), whose public WordPress API has a "Musik & konserter" category with dates, venue and
// ticket links. Venue calendar pages that are missing there (Concerts:Calendars, Pustervik by default) are
// read as text and searched for artist names. Results are cached for six hours.
public sealed partial class ConcertService(
    IHttpClientFactory clients,
    PublicWebClient web,
    IConfiguration configuration,
    ILogger<ConcertService> logger)
{
    private const string GoteborgApi = "https://cms.goteborg.com/wp-json/wp/v2/";
    private const int MaxPages = 8;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private (DateTime At, List<Concert> Events, List<(string Name, string Url, string Text)> Pages) _cache =
        (DateTime.MinValue, [], []);

    private string[] Calendars => configuration.GetSection("Concerts:Calendars").Get<string[]>() is { Length: > 0 } list
        ? list
        : ["https://www.pustervik.nu/kalender"];

    public async Task<(List<Concert> Events, List<(string Name, string Url, string Text)> Pages)> LoadAsync(
        CancellationToken ct)
    {
        await _refresh.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _cache.At < TimeSpan.FromHours(6) && _cache.Events.Count > 0)
                return (_cache.Events, _cache.Pages);

            var events = await GoteborgAsync(ct);
            var pages = new List<(string, string, string)>();
            foreach (var url in Calendars)
            {
                try
                {
                    var page = await web.ReadAsync(url, ct);
                    var (_, text) = await PageTextExtractor.ExtractAsync(page, ct);
                    // Calendars laid out as month, day, title and time become events; others are searched as text.
                    var parsed = ParseCalendar(VenueName(url), url, text, DateTime.Now);
                    if (parsed.Count >= 3)
                        events.AddRange(parsed);
                    else
                        pages.Add((VenueName(url), url, text));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogInformation(ex, "Concert calendar {Url} could not be read", url);
                }
            }

            // The same concert can be on goteborg.com and a venue calendar; keep the first (with ticket link).
            events = events.DistinctBy(e => (Normalize(e.Title), e.Start?.Date)).ToList();
            _cache = (DateTime.UtcNow, events, pages);
            return (events, pages);
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task<List<Concert>> GoteborgAsync(CancellationToken ct)
    {
        var client = clients.CreateClient("concerts");
        int category;
        using (var categories = JsonDocument.Parse(
                   await client.GetStringAsync(GoteborgApi + "categories?slug=musik-konserter&_fields=id", ct)))
            category = categories.RootElement.EnumerateArray().First().GetProperty("id").GetInt32();

        var events = new List<Concert>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var response = await client.GetAsync(
                $"{GoteborgApi}events?categories={category}&per_page=100&page={page}&_fields=slug,title,excerpt,information",
                ct);
            if (!response.IsSuccessStatusCode)
                break;
            events.AddRange(ParseGoteborg(await response.Content.ReadAsStringAsync(ct), DateTime.Now));
            var total = response.Headers.TryGetValues("X-WP-TotalPages", out var values) &&
                        int.TryParse(values.FirstOrDefault(), out var pages)
                ? pages
                : 1;
            if (page >= total)
                break;
        }

        return events;
    }

    // One entry per event with its next date from now on; events that are over are left out.
    public static List<Concert> ParseGoteborg(string json, DateTime now)
    {
        using var doc = JsonDocument.Parse(json);
        var concerts = new List<Concert>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("information", out var info) || info.ValueKind != JsonValueKind.Object)
                continue;
            var next = info.TryGetProperty("dates", out var dates) && dates.ValueKind == JsonValueKind.Array
                ? dates.EnumerateArray()
                    .Select(d => DateTime.TryParse(d.GetProperty("start").GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal, out var start) ? start : (DateTime?)null)
                    .Where(d => d >= now.Date).Min()
                : null;
            if (next is null)
                continue;
            var place = info.TryGetProperty("place", out var p) && p.ValueKind == JsonValueKind.Object
                ? Html(p.GetProperty("title").GetString())
                : "";
            string? tickets = null;
            if (info.TryGetProperty("contact", out var contact) && contact.ValueKind == JsonValueKind.Object)
            {
                tickets = new[] { "tickets", "website" }
                    .Select(key => contact.TryGetProperty(key, out var link) ? link.GetString() : null)
                    .FirstOrDefault(link => Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme == "https");
                // Ticket links carry tracking parameters that only make them long.
                if (tickets is not null && tickets.IndexOf("?_gl=", StringComparison.Ordinal) is > 0 and var cut)
                    tickets = tickets[..cut];
            }

            var slug = item.GetProperty("slug").GetString() ?? "";
            concerts.Add(new Concert(
                Html(item.GetProperty("title").GetProperty("rendered").GetString()),
                next,
                place,
                $"https://www.goteborg.com/nara/{slug}?type=event",
                tickets,
                Html(item.TryGetProperty("excerpt", out var excerpt) ? excerpt.GetProperty("rendered").GetString() : ""),
                "goteborg.com"));
        }

        return concerts;
    }

    // "https://www.pustervik.nu/kalender" becomes "Pustervik".
    public static string VenueName(string url)
    {
        var host = new Uri(url).Host;
        var name = (host.StartsWith("www.") ? host[4..] : host).Split('.')[0];
        return name.Length == 0 ? host : char.ToUpperInvariant(name[0]) + name[1..];
    }

    // Swedish venue calendars as text: a heading such as "Oktober 2026", then per event a day line such
    // as "02Fre" or "2 fre", the title, and a line with the time ("Stora Klubben•19:00") and genre.
    public static List<Concert> ParseCalendar(string venue, string url, string text, DateTime now)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToArray();
        var concerts = new List<Concert>();
        var (year, month) = (0, 0);
        for (var i = 0; i < lines.Length; i++)
        {
            if (MonthHeading().Match(lines[i]) is { Success: true } heading)
            {
                month = Array.IndexOf(s_months, heading.Groups[1].Value.ToLowerInvariant()) + 1;
                year = int.Parse(heading.Groups[2].Value, CultureInfo.InvariantCulture);
                continue;
            }

            if (month == 0 || i + 1 >= lines.Length || DayLine().Match(lines[i]) is not { Success: true } dayLine)
                continue;
            var day = int.Parse(dayLine.Groups[1].Value, CultureInfo.InvariantCulture);
            if (day < 1 || day > DateTime.DaysInMonth(year, month))
                continue;
            var details = i + 2 < lines.Length ? lines[i + 2] : "";
            var start = new DateTime(year, month, day);
            if (TimeOfDay().Match(details) is { Success: true } time)
                start = start.Add(TimeSpan.Parse(time.Value.Replace('.', ':'), CultureInfo.InvariantCulture));
            if (start.Date < now.Date || lines[i + 1].Length is < 2 or > 150)
                continue;
            var genre = i + 3 < lines.Length && lines[i + 3].Length < 40 ? lines[i + 3] : "";
            concerts.Add(new Concert(lines[i + 1], start, venue, url, null, $"{details} {genre}".Trim(), new Uri(url).Host));
        }

        return concerts;
    }

    private static readonly string[] s_months =
        ["januari", "februari", "mars", "april", "maj", "juni", "juli", "augusti", "september", "oktober", "november", "december"];

    private static string Html(string? text) =>
        WebUtility.HtmlDecode(Tags().Replace(text ?? "", " ")).Replace(' ', ' ').Trim();

    // Lower case without accents or punctuation, so "Håkan Hellström" matches "Hakan Hellstrom".
    public static string Normalize(string text)
    {
        var decomposed = text.ToLowerInvariant().Replace("&", " and ").Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return Spaces().Replace(plain.ToString(), " ").Trim();
    }

    // Whole-word match, so the artist "Kent" does not match "Kentkören". Names shorter than three
    // characters are ignored.
    public static bool Mentions(string text, string artist)
    {
        var name = Normalize(artist);
        if (name.StartsWith("the "))
            name = name[4..];
        return name.Length >= 3 && $" {Normalize(text)} ".Contains($" {name} ", StringComparison.Ordinal);
    }

    public static bool IsTribute(string title) => TributeWords().IsMatch(title);

    public static List<ConcertMatch> Match(IEnumerable<Concert> concerts, IEnumerable<string> artists) =>
        concerts.SelectMany(c => artists.Where(a => Mentions(c.Title, a))
                .Take(1)
                .Select(a => new ConcertMatch(c, a, IsTribute(c.Title))))
            .OrderBy(m => m.Tribute).ThenBy(m => m.Concert.Start).ToList();

    // Calendar pages are plain text: every line that names an artist becomes a lead.
    public static List<ConcertMatch> MatchPages(IEnumerable<(string Name, string Url, string Text)> pages,
        IEnumerable<string> artists)
    {
        var names = artists.ToList();
        var leads = new List<ConcertMatch>();
        foreach (var (name, url, text) in pages)
        {
            foreach (var line in text.Split('\n').Select(l => l.Trim()).Where(l => l.Length is > 2 and < 200))
            {
                if (names.FirstOrDefault(a => Mentions(line, a)) is { } artist &&
                    leads.All(l => l.Artist != artist || l.Concert.Url != url))
                    leads.Add(new ConcertMatch(new Concert(line, null, name, url, null, "", new Uri(url).Host), artist,
                        IsTribute(line)));
            }
        }

        return leads;
    }

    // Concerts whose title or description mentions one of the user's genres, for "maybe" suggestions.
    public static List<(Concert Concert, string Genre)> ByGenre(IEnumerable<Concert> concerts,
        IEnumerable<string> genres, int take)
    {
        var words = genres.SelectMany(g => GenreWords(g)).Distinct().ToList();
        return concerts.Select(c => (Concert: c,
                Genre: words.FirstOrDefault(w => $" {Normalize(c.Title + " " + c.Summary)} ".Contains($" {w} "))))
            .Where(c => c.Genre is not null)
            .OrderBy(c => c.Concert.Start).Take(take).Select(c => (c.Concert, c.Genre!)).ToList();
    }

    // Spotify genres such as "swedish indie pop" become the words a concert text would use, in English
    // and Swedish.
    private static IEnumerable<string> GenreWords(string genre)
    {
        var normal = Normalize(genre);
        foreach (var (word, aliases) in s_genres)
        {
            if ($" {normal} ".Contains($" {word} "))
            {
                foreach (var alias in aliases.Prepend(word))
                    yield return alias;
            }
        }
    }

    private static readonly (string Word, string[] Aliases)[] s_genres =
    [
        ("indie", []), ("rock", []), ("punk", []), ("metal", []), ("jazz", []), ("blues", []), ("soul", []),
        ("funk", []), ("folk", ["visor", "visa"]), ("country", ["americana"]), ("reggae", []), ("pop", []),
        ("hip hop", ["hiphop", "rap"]), ("rap", ["hiphop", "hip hop"]), ("techno", ["elektronisk"]),
        ("house", []), ("electronic", ["elektronisk"]), ("classical", ["klassisk", "symfoni"]),
        ("opera", []), ("singer songwriter", ["singer songwriter", "visor"]), ("gospel", []), ("disco", []),
        ("r b", ["rnb"]), ("schlager", ["dansband"]), ("latin", ["salsa"]), ("afrobeats", ["afro"])
    ];

    [GeneratedRegex(@"^(januari|februari|mars|april|maj|juni|juli|augusti|september|oktober|november|december)\s+(\d{4})$", RegexOptions.IgnoreCase)]
    private static partial Regex MonthHeading();

    [GeneratedRegex(@"^(\d{1,2})\s*(mån|tis|ons|tors?|fre|lör|sön)\w*$", RegexOptions.IgnoreCase)]
    private static partial Regex DayLine();

    [GeneratedRegex(@"\b\d{1,2}[:.]\d{2}\b")]
    private static partial Regex TimeOfDay();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\b(tribute|hyllning|hyllar|cover|plays the music of|spelar .+s musik)", RegexOptions.IgnoreCase)]
    private static partial Regex TributeWords();
}
