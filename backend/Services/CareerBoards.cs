using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Harness.Services;

// Job boards that companies run on their own career pages. Modern product companies often publish there
// rather than in Platsbanken, and most of these systems have open feeds that need no key. A board is
// recognised from its address, or from the career page's HTML when it runs on the company's own domain.
public sealed partial class CareerBoards(IHttpClientFactory clients, IConfiguration configuration)
{
    public record Board(string System, string Feed, string Name, string Url);

    private static readonly XNamespace s_tt = "https://teamtailor.com/locations";

    private static readonly string[] s_region =
    [
        "göteborg", "gothenburg", "goteborg", "mölndal", "molndal", "partille", "härryda", "lerum", "kungälv",
        "kungsbacka", "öckerö", "alingsås", "stenungsund", "lindholmen"
    ];

    // Finds the board behind a career page address. Throws when the system is not one Leona can read.
    public async Task<Board> DetectAsync(string address, CancellationToken ct)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.IsLoopback || IPAddress.TryParse(uri.Host, out _))
            throw new ArgumentException("Give the web address of the company's career page.");

        if (FromAddress(uri) is { } known)
            return await NamedAsync(known, ct);

        // A career page on the company's own domain: look for the system in the page.
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("text/html,*/*");
        using var response = await clients.CreateClient("jobs").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var board = html.Contains("teamtailor", StringComparison.OrdinalIgnoreCase)
            ? new Board("teamtailor", $"{uri.GetLeftPart(UriPartial.Authority)}/jobs.rss", "", uri.ToString())
            : EmbeddedBoard().Match(html) is { Success: true } embedded &&
              Uri.TryCreate(embedded.Value.StartsWith("http") ? embedded.Value : "https://" + embedded.Value, UriKind.Absolute, out var inner)
                ? FromAddress(inner)
                : null;
        return board is null
            ? throw new UnknownSystemException()
            : await NamedAsync(board with { Url = uri.ToString() }, ct);
    }

    // The career page runs on a system Leona cannot read; the employer can still be watched by name.
    public sealed class UnknownSystemException() : ArgumentException(
        "Leona does not recognise the job system on that page. Teamtailor, Lever, Greenhouse, Ashby, Workable, Recruitee, " +
        "SmartRecruiters, Varbi and Workday work.");

    // "carmenta.com/career" gives Carmenta.
    public static string NameFromHost(Uri uri)
    {
        var parts = uri.Host.Split('.');
        var name = parts.Length >= 2 ? parts[^2] : parts[0];
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name);
    }

    private static Board? FromAddress(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var slug = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (host == "embed" || slug == "embed")
            slug = Regex.Match(uri.Query, @"for=([\w-]+)").Groups[1].Value;
        return host switch
        {
            _ when host.EndsWith(".teamtailor.com") => new("teamtailor", $"https://{host}/jobs.rss", "", uri.ToString()),
            _ when host.EndsWith(".recruitee.com") => new("recruitee", $"https://{host}/api/offers/", "", uri.ToString()),
            // Varbi (many public employers) publishes an RSS feed per organisation.
            _ when host.EndsWith(".varbi.com") => new("varbi", $"https://{host}/what:rssfeed/", "", uri.ToString()),
            // Workday: tenant from the host, job site from the path (after an optional locale such as en-US).
            _ when host.EndsWith(".myworkdayjobs.com") && WorkdaySite(uri) is { } site =>
                new("workday", $"https://{host}/wday/cxs/{host.Split('.')[0]}/{site}", "", uri.ToString()),
            "jobs.lever.co" when slug.Length > 0 => new("lever", $"https://api.lever.co/v0/postings/{slug}?mode=json", "", uri.ToString()),
            "boards.greenhouse.io" or "job-boards.greenhouse.io" when slug.Length > 0 =>
                new("greenhouse", $"https://boards-api.greenhouse.io/v1/boards/{slug}/jobs?content=true", "", uri.ToString()),
            "jobs.ashbyhq.com" when slug.Length > 0 =>
                new("ashby", $"https://api.ashbyhq.com/posting-api/job-board/{slug}", "", uri.ToString()),
            "apply.workable.com" when slug.Length > 0 =>
                new("workable", $"https://apply.workable.com/api/v1/widget/accounts/{slug}", "", uri.ToString()),
            "careers.smartrecruiters.com" or "jobs.smartrecruiters.com" when slug.Length > 0 =>
                new("smartrecruiters", $"https://api.smartrecruiters.com/v1/companies/{slug}/postings", "", uri.ToString()),
            _ => null
        };
    }

    private static string? WorkdaySite(Uri uri) =>
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(segment => !Regex.IsMatch(segment, "^[a-z]{2}-[A-Z]{2}$"));

    // Reads the feed once to check it works and to learn the company's name.
    private async Task<Board> NamedAsync(Board board, CancellationToken ct)
    {
        var body = board.System == "workday"
            ? await WorkdayPageAsync(board, "", 1, ct)
            : await clients.CreateClient("jobs").GetStringAsync(board.Feed, ct);
        var name = board.System is "teamtailor" or "varbi"
            ? XDocument.Parse(body).Root?.Element("channel")?.Element("title")?.Value ?? ""
            : "";
        name = Regex.Replace(name, "^(Nya )?lediga jobb (hos|på|i) ", "", RegexOptions.IgnoreCase);
        if (name.Length == 0)
            name = Regex.Replace(new Uri(board.Url).Host.Split('.')[0], "^(jobs|careers|career|apply|boards|job-boards)$",
                new Uri(board.Url).AbsolutePath.Trim('/').Split('/')[0]);
        // Names from the feed keep their own casing (nShift); a name made from the address gets a capital.
        name = name.Trim();
        return board with
        {
            Name = name == name.ToLowerInvariant() ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name) : name
        };
    }

    // Jobs on a board in the Göteborg region (or remote in Sweden) whose title fits the queries.
    public async Task<IReadOnlyList<JobService.JobAd>> JobsAsync(Board board, IReadOnlyList<string> queries, CancellationToken ct)
    {
        if (board.System == "workday")
            return await WorkdayJobsAsync(board, queries, ct);
        var body = await clients.CreateClient("jobs").GetStringAsync(board.Feed, ct);
        return Read(board, body, queries);
    }

    // Workday has no feed of all jobs, but its career site searches by text: one search per query.
    private async Task<IReadOnlyList<JobService.JobAd>> WorkdayJobsAsync(Board board, IReadOnlyList<string> queries,
        CancellationToken ct)
    {
        var ads = new Dictionary<string, JobService.JobAd>();
        var site = new Uri(board.Feed);
        var publicSite = $"https://{site.Host}/{site.AbsolutePath.Split('/').Last()}";
        foreach (var body in await Task.WhenAll(queries.Take(10).Select(query => WorkdayPageAsync(board, query, 20, ct))))
        {
            using var page = JsonDocument.Parse(body);
            foreach (var job in page.RootElement.GetProperty("jobPostings").EnumerateArray())
            {
                var path = JsonPath.Text(job, "externalPath");
                var place = JsonPath.Text(job, "locationsText");
                // "2 Locations": the job's own page names them.
                if (Regex.IsMatch(place, @"^\d+ (Locations|Platser)", RegexOptions.IgnoreCase))
                {
                    using var detail = JsonDocument.Parse(await clients.CreateClient("jobs").GetStringAsync(board.Feed + path, ct));
                    var info = detail.RootElement.GetProperty("jobPostingInfo");
                    place = string.Join(", ", new[] { JsonPath.Text(info, "location") }.Concat(
                        info.TryGetProperty("additionalLocations", out var more) && more.ValueKind == JsonValueKind.Array
                            ? more.EnumerateArray().Select(l => l.GetString() ?? "")
                            : []).Where(l => l.Length > 0));
                }

                var title = JsonPath.Text(job, "title");
                if (path.Length == 0 || ads.ContainsKey(path) || !InRegion(place))
                    continue;
                var titleMatch = queries.Any(q => title.Contains(q, StringComparison.OrdinalIgnoreCase));
                if (!titleMatch && !DeveloperTitle().IsMatch(title))
                    continue;
                ads[path] = new JobService.JobAd(publicSite + path, title, board.Name, place, PostedOn(JsonPath.Text(job, "postedOn")), null,
                    publicSite + path, null, "", JobService.IsAgency(board.Name), titleMatch, Source: "career page");
            }
        }

        return ads.Values.ToList();
    }

    private async Task<string> WorkdayPageAsync(Board board, string search, int limit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, board.Feed + "/jobs")
        {
            Content = JsonContent.Create(new { appliedFacets = new { }, limit, offset = 0, searchText = search })
        };
        using var response = await clients.CreateClient("jobs").SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    // "Posted Today", "Posted Yesterday", "Posted 3 Days Ago" or "Posted 30+ Days Ago".
    private static DateTime? PostedOn(string text) =>
        text.Contains("Today", StringComparison.OrdinalIgnoreCase) ? DateTime.Today
        : text.Contains("Yesterday", StringComparison.OrdinalIgnoreCase) ? DateTime.Today.AddDays(-1)
        : Regex.Match(text, @"(\d+)\+? Days?") is { Success: true } days ? DateTime.Today.AddDays(-int.Parse(days.Groups[1].Value))
        : null;

    public static IReadOnlyList<JobService.JobAd> Read(Board board, string body, IReadOnlyList<string> queries)
    {
        return Parse(board, body).Where(j => InRegion(j.Place)).Select(j =>
        {
            var titleMatch = queries.Any(q => j.Title.Contains(q, StringComparison.OrdinalIgnoreCase));
            var fits = titleMatch || (DeveloperTitle().IsMatch(j.Title) && JobService.MentionsDotNet(j.Text));
            return fits ? new JobService.JobAd(j.Url, j.Title, board.Name, j.Place, j.Published, null, j.Url, null,
                JobService.Summarize(j.Text), JobService.IsAgency(board.Name), titleMatch, Source: "career page") : null;
        }).OfType<JobService.JobAd>().ToList();
    }

    private record Posting(string Title, string Url, string Place, DateTime? Published, string Text);

    private static IEnumerable<Posting> Parse(Board board, string body)
    {
        switch (board.System)
        {
            case "teamtailor":
                foreach (var item in XDocument.Parse(body).Descendants("item"))
                {
                    var location = item.Descendants(s_tt + "location").FirstOrDefault();
                    var remote = item.Element("remoteStatus")?.Value is "fully" or "remote";
                    var place = string.Join(", ", new[] { location?.Element(s_tt + "city")?.Value, location?.Element(s_tt + "country")?.Value }
                        .Where(p => !string.IsNullOrWhiteSpace(p)));
                    yield return new Posting(item.Element("title")?.Value ?? "", item.Element("link")?.Value ?? "",
                        remote ? $"Remote{(place.Length > 0 ? ", " + place : "")}" : place,
                        JsonPath.Date(item.Element("pubDate")?.Value),
                        PageTextExtractor.HtmlToText(item.Element("description")?.Value ?? ""));
                }
                yield break;
            case "varbi":
                foreach (var item in XDocument.Parse(body).Descendants("item"))
                {
                    var text = item.Element("description")?.Value ?? "";
                    // Varbi's feed has no place field; the ad text names the town.
                    var town = s_region.FirstOrDefault(r => text.Contains(r, StringComparison.OrdinalIgnoreCase));
                    yield return new Posting(item.Element("title")?.Value ?? "", item.Element("link")?.Value ?? "",
                        town is null ? "" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(town),
                        JsonPath.Date(item.Element("pubDate")?.Value),
                        PageTextExtractor.HtmlToText(text));
                }
                yield break;
            case "lever":
                foreach (var job in JsonDocument.Parse(body).RootElement.EnumerateArray())
                    yield return new Posting(JsonPath.Text(job, "text"), JsonPath.Text(job, "hostedUrl"),
                        JsonPath.Text(job, "categories", "location") + (JsonPath.Text(job, "workplaceType") == "remote" ? ", Remote" : ""),
                        job.TryGetProperty("createdAt", out var created) && created.TryGetInt64(out var ms)
                            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null,
                        JsonPath.Text(job, "descriptionPlain"));
                yield break;
            case "greenhouse":
                foreach (var job in JsonDocument.Parse(body).RootElement.GetProperty("jobs").EnumerateArray())
                    yield return new Posting(JsonPath.Text(job, "title"), JsonPath.Text(job, "absolute_url"), JsonPath.Text(job, "location", "name"),
                        JsonPath.Date(JsonPath.Text(job, "updated_at")),
                        PageTextExtractor.HtmlToText(WebUtility.HtmlDecode(JsonPath.Text(job, "content"))));
                yield break;
            case "ashby":
                foreach (var job in JsonDocument.Parse(body).RootElement.GetProperty("jobs").EnumerateArray())
                    yield return new Posting(JsonPath.Text(job, "title"), JsonPath.Text(job, "jobUrl"),
                        JsonPath.Text(job, "location") + (job.TryGetProperty("isRemote", out var r) && r.ValueKind == JsonValueKind.True ? ", Remote" : ""),
                        JsonPath.Date(JsonPath.Text(job, "publishedAt")),
                        JsonPath.Text(job, "descriptionPlain"));
                yield break;
            case "workable":
                foreach (var job in JsonDocument.Parse(body).RootElement.GetProperty("jobs").EnumerateArray())
                    yield return new Posting(JsonPath.Text(job, "title"), JsonPath.Text(job, "url"),
                        string.Join(", ", new[] { JsonPath.Text(job, "city"), JsonPath.Text(job, "country") }.Where(p => p.Length > 0)) +
                        (job.TryGetProperty("telecommuting", out var t) && t.ValueKind == JsonValueKind.True ? ", Remote" : ""),
                        JsonPath.Date(JsonPath.Text(job, "published_on")), JsonPath.Text(job, "description"));
                yield break;
            case "recruitee":
                foreach (var job in JsonDocument.Parse(body).RootElement.GetProperty("offers").EnumerateArray())
                    yield return new Posting(JsonPath.Text(job, "title"), JsonPath.Text(job, "careers_url"),
                        string.Join(", ", new[] { JsonPath.Text(job, "city"), JsonPath.Text(job, "country") }.Where(p => p.Length > 0)) +
                        (job.TryGetProperty("remote", out var rem) && rem.ValueKind == JsonValueKind.True ? ", Remote" : ""),
                        JsonPath.Date(JsonPath.Text(job, "published_at")),
                        PageTextExtractor.HtmlToText(JsonPath.Text(job, "description")));
                yield break;
            case "smartrecruiters":
                foreach (var job in JsonDocument.Parse(body).RootElement.GetProperty("content").EnumerateArray())
                    yield return new Posting(JsonPath.Text(job, "name"),
                        $"https://jobs.smartrecruiters.com/{JsonPath.Text(job, "company", "identifier")}/{JsonPath.Text(job, "id")}",
                        string.Join(", ", new[] { JsonPath.Text(job, "location", "city"), JsonPath.Text(job, "location", "country") }.Where(p => p.Length > 0)) +
                        (job.TryGetProperty("location", out var loc) && loc.TryGetProperty("remote", out var rm) &&
                         rm.ValueKind == JsonValueKind.True ? ", Remote" : ""),
                        JsonPath.Date(JsonPath.Text(job, "releasedDate")), "");
                yield break;
        }
    }

    // The Göteborg region, or remote work from Sweden.
    public static bool InRegion(string place)
    {
        var lower = place.ToLowerInvariant();
        return s_region.Any(lower.Contains) ||
               (lower.Contains("remote") && (lower.Contains("sweden") || lower.Contains("sverige")));
    }

    // Boards found by a web search for jobs that fit, so companies that are not watched yet turn up too.
    public async Task<IReadOnlyList<Board>> DiscoverAsync(IReadOnlyList<string> queries, IReadOnlyCollection<string> known,
        CancellationToken ct)
    {
        var searx = configuration["Tools:SearxngUrl"];
        if (string.IsNullOrWhiteSpace(searx))
            return [];

        var client = clients.CreateClient("search");
        // The searches run together; their results are taken in order, so the same boards are picked as one by one.
        var searches = queries.Take(3)
            .SelectMany(query => new[] { "teamtailor.com", "jobs.lever.co", "boards.greenhouse.io" }
                .Select(site => $"site:{site} {query} Göteborg"))
            .Select(async search =>
            {
                try
                {
                    return await Searx.SearchAsync(client, searx, search, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    return [];
                }
            });
        var boards = new Dictionary<string, Board>();
        foreach (var hit in (await Task.WhenAll(searches)).SelectMany(hits => hits))
        {
            if (Uri.TryCreate(hit.Url, UriKind.Absolute, out var url) && FromAddress(url) is { } board &&
                !known.Contains(board.Feed) && boards.Count < 8)
                boards.TryAdd(board.Feed, board);
        }

        var named = await Task.WhenAll(boards.Values.Select(async board =>
        {
            try
            {
                return await NamedAsync(board, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or System.Xml.XmlException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return null;
            }
        }));
        return named.OfType<Board>().ToList();
    }

    [GeneratedRegex(@"(?:https?://)?(?:jobs\.lever\.co/[\w-]+|(?:job-)?boards\.greenhouse\.io/(?:embed/job_board\?for=)?[\w-]+|jobs\.ashbyhq\.com/[\w-]+|apply\.workable\.com/[\w-]+|[\w-]+\.recruitee\.com|(?:careers|jobs)\.smartrecruiters\.com/[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedBoard();

    [GeneratedRegex(@"develop|utvecklare|engineer|ingenjör|architect|arkitekt|lead|programmer", RegexOptions.IgnoreCase)]
    private static partial Regex DeveloperTitle();
}
