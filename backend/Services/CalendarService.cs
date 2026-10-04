using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Harness.Models;
using Ical.Net.DataTypes;
using IcalCalendar = Ical.Net.Calendar;

namespace Harness.Services;

// Calendars over CalDAV (iCloud with an app-specific password, or any CalDAV server) and read-only .ics feeds.
public class CalendarService(AccountService accounts, IHttpClientFactory clients)
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly XNamespace CalDav = "urn:ietf:params:xml:ns:caldav";
    private readonly Dictionary<int, List<CalendarInfo>> _calendars = new();

    public record CalendarInfo(int AccountId, string Account, string Name, string Url, bool Writable);

    public record EventInfo(string Calendar, string Title, DateTime Start, DateTime End, bool AllDay, string? Location);

    // One occurrence with what it takes to remove it: the event's own resource on the server, and for
    // recurring events the EXDATE line that cancels just this occurrence.
    public record Occurrence(EventInfo Event, bool Recurring, string ExDate, string? OverrideValue);

    public record Located(Occurrence Occurrence, CalendarInfo Calendar, string? Url, string? ETag);

    private record Resource(CalendarInfo Calendar, string? Url, string? ETag, string Ics);

    private async Task<HttpResponseMessage> SendAsync(Account account, HttpMethod method, string url, string? body,
        string? depth, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        var client = clients.CreateClient("calendar");
        var target = new Uri(url.Replace("webcal://", "https://"));
        // Follow redirects by hand so PROPFIND and REPORT keep their method and body.
        for (var hop = 0; hop < 4; hop++)
        {
            using var request = new HttpRequestMessage(method, target);
            if (body is not null)
                request.Content = new StringContent(body, Encoding.UTF8, method.Method == "PUT" ? "text/calendar" : "application/xml");
            if (depth is not null)
                request.Headers.Add("Depth", depth);
            var settings = AccountService.Settings<CalendarSettings>(account);
            if (settings.Username.Length > 0)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{accounts.Secret(account)}")));
            configure?.Invoke(request);
            var response = await client.SendAsync(request, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                target = new Uri(target, location);
                response.Dispose();
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new ArgumentException($"{account.Label}: the calendar refused the username or app-specific password.");
            }

            return response;
        }

        throw new ArgumentException("The calendar server redirected too many times.");
    }

    private async Task<XDocument> PropfindAsync(Account account, string url, string props, string depth, CancellationToken ct)
    {
        var body = $"<?xml version=\"1.0\"?><d:propfind xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\"><d:prop>{props}</d:prop></d:propfind>";
        using var response = await SendAsync(account, new HttpMethod("PROPFIND"), url, body, depth, ct);
        if (!response.IsSuccessStatusCode)
            throw new ArgumentException($"{account.Label}: the calendar server answered {(int)response.StatusCode}.");
        return XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string Href(XDocument document, XName property) =>
        document.Descendants(property).Descendants(Dav + "href").Select(h => h.Value.Trim()).FirstOrDefault() ??
        throw new ArgumentException("The calendar server did not say where the calendars are.");

    // Discovery: principal, then calendar home, then the calendars that hold events.
    public async Task<List<CalendarInfo>> CalendarsAsync(Account account, CancellationToken ct)
    {
        if (_calendars.TryGetValue(account.Id, out var cached))
            return cached;
        var settings = AccountService.Settings<CalendarSettings>(account);
        List<CalendarInfo> calendars;
        if (AccountService.IsIcsFeed(settings))
        {
            calendars = [new CalendarInfo(account.Id, account.Label, account.Label, settings.Url, false)];
        }
        else
        {
            var root = new Uri(settings.Url);
            var principal = new Uri(root, Href(await PropfindAsync(account, root.AbsoluteUri, "<d:current-user-principal/>", "0", ct),
                Dav + "current-user-principal"));
            var home = new Uri(principal, Href(await PropfindAsync(account, principal.AbsoluteUri, "<c:calendar-home-set/>", "0", ct),
                CalDav + "calendar-home-set"));
            var listing = await PropfindAsync(account, home.AbsoluteUri,
                "<d:resourcetype/><d:displayname/><c:supported-calendar-component-set/>", "1", ct);
            calendars = listing.Descendants(Dav + "response")
                .Where(r => r.Descendants(Dav + "resourcetype").Descendants(CalDav + "calendar").Any())
                .Where(r => !r.Descendants(CalDav + "comp").Any() ||
                            r.Descendants(CalDav + "comp").Any(c => (string?)c.Attribute("name") == "VEVENT"))
                .Select(r => new CalendarInfo(account.Id, account.Label,
                    r.Descendants(Dav + "displayname").FirstOrDefault()?.Value is { Length: > 0 } name ? name : "Calendar",
                    new Uri(home, r.Element(Dav + "href")!.Value.Trim()).AbsoluteUri, true))
                .ToList();
        }

        _calendars[account.Id] = calendars;
        return calendars;
    }

    // The calendar objects with events in the window: each with its address and ETag on CalDAV servers.
    private async Task<List<Resource>> QueryAsync(Account account, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        var resources = new List<Resource>();
        foreach (var calendar in await CalendarsAsync(account, ct))
        {
            if (!calendar.Writable)
            {
                using var response = await SendAsync(account, HttpMethod.Get, calendar.Url, null, null, ct);
                response.EnsureSuccessStatusCode();
                resources.Add(new Resource(calendar, null, null, await response.Content.ReadAsStringAsync(ct)));
                continue;
            }

            var range = $"start=\"{fromUtc:yyyyMMdd'T'HHmmss'Z'}\" end=\"{toUtc:yyyyMMdd'T'HHmmss'Z'}\"";
            var body = "<?xml version=\"1.0\"?><c:calendar-query xmlns:d=\"DAV:\" xmlns:c=\"urn:ietf:params:xml:ns:caldav\">" +
                       "<d:prop><d:getetag/><c:calendar-data/></d:prop><c:filter><c:comp-filter name=\"VCALENDAR\">" +
                       $"<c:comp-filter name=\"VEVENT\"><c:time-range {range}/></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>";
            using (var response = await SendAsync(account, new HttpMethod("REPORT"), calendar.Url, body, "1", ct))
            {
                if (!response.IsSuccessStatusCode)
                    throw new ArgumentException($"{calendar.Name}: the calendar server answered {(int)response.StatusCode}.");
                var document = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var item in document.Descendants(Dav + "response"))
                {
                    if (item.Descendants(CalDav + "calendar-data").FirstOrDefault()?.Value is not { Length: > 0 } ics)
                        continue;
                    var href = item.Element(Dav + "href")?.Value.Trim();
                    resources.Add(new Resource(calendar,
                        href is null ? null : new Uri(new Uri(calendar.Url), href).AbsoluteUri,
                        item.Descendants(Dav + "getetag").FirstOrDefault()?.Value.Trim(), ics));
                }
            }
        }

        return resources;
    }

    public async Task<List<EventInfo>> EventsAsync(Account account, DateTime fromLocal, DateTime toLocal,
        CancellationToken ct)
    {
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        return (await QueryAsync(account, fromUtc, toUtc, ct))
            .SelectMany(r => Expand(r.Calendar.Name, r.Ics, fromUtc, toUtc))
            .OrderBy(e => e.Start).ToList();
    }

    // Every occurrence on one local day, with where it is stored.
    public async Task<List<Located>> DayAsync(Account account, DateTime dayLocal, CancellationToken ct)
    {
        var fromUtc = dayLocal.Date.ToUniversalTime();
        var toUtc = dayLocal.Date.AddDays(1).ToUniversalTime();
        return (await QueryAsync(account, fromUtc, toUtc, ct))
            .SelectMany(r => Occurrences(r.Calendar.Name, r.Ics, fromUtc, toUtc)
                .Where(o => o.Event.Start.Date == dayLocal.Date)
                .Select(o => new Located(o, r.Calendar, r.Url, r.ETag)))
            .OrderBy(l => l.Occurrence.Event.Start).ToList();
    }

    // Expands recurring events into the requested window, in local time.
    public static IEnumerable<EventInfo> Expand(string calendarName, string ics, DateTime fromUtc, DateTime toUtc) =>
        Occurrences(calendarName, ics, fromUtc, toUtc).Select(o => o.Event);

    public static IEnumerable<Occurrence> Occurrences(string calendarName, string ics, DateTime fromUtc, DateTime toUtc)
    {
        var calendar = IcalCalendar.Load(ics);
        if (calendar is null)
            yield break;
        // Start a day early so events that began before the window but are still running are included.
        foreach (var occurrence in calendar.GetOccurrences(new CalDateTime(fromUtc.AddDays(-1), "UTC", true), null)
                     .TakeWhile(o => o.Period.StartTime.AsUtc < toUtc))
        {
            if (occurrence.Source is not Ical.Net.CalendarComponents.CalendarEvent source)
                continue;
            var start = occurrence.Period.StartTime;
            var end = occurrence.Period.EffectiveEndTime ?? start;
            var allDay = !start.HasTime;
            var startLocal = allDay ? start.Value.Date : start.AsUtc.ToLocalTime();
            var endLocal = allDay ? end.Value.Date : end.AsUtc.ToLocalTime();
            // Skip occurrences that were already over when the window starts.
            if (endLocal.ToUniversalTime() <= fromUtc && startLocal.ToUniversalTime() < fromUtc)
                continue;
            var info = new EventInfo(calendarName, source.Summary ?? "(no title)", startLocal, endLocal, allDay,
                string.IsNullOrWhiteSpace(source.Location) ? null : source.Location);
            // A moved occurrence is identified by the slot it replaced (RECURRENCE-ID), not its new time.
            var replaced = source.RecurrenceIdentifier?.StartTime;
            var (parameters, value) = IcalValue(replaced ?? start);
            var recurring = source.RecurrenceRule is not null || source.RecurrenceDates.GetAllDates().Any() ||
                            replaced is not null;
            yield return new Occurrence(info, recurring, $"EXDATE{parameters}:{value}", replaced is null ? null : value);
        }
    }

    // The value form iCalendar uses for this time: a date, UTC, or local time in a named time zone.
    private static (string Parameters, string Value) IcalValue(CalDateTime time) =>
        !time.HasTime ? (";VALUE=DATE", time.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
        : time.IsUtc ? ("", time.AsUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture))
        : time.TzId is { } zone && !time.IsFloating
            ? ($";TZID={zone}", time.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))
            : ("", time.Value.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));

    // Cancels one occurrence of a recurring event by adding an EXDATE to the series (and dropping the
    // moved copy, if that occurrence had been moved). Everything else in the object is kept as it was.
    public static string ExcludeOccurrence(string ics, string exDate, string? overrideValue)
    {
        var lines = Regex.Replace(ics, @"\r?\n[ \t]", "").Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0);
        var output = new List<string>();
        List<string>? block = null;
        var series = false;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                block = [line];
                continue;
            }

            if (block is null)
            {
                output.Add(line);
                continue;
            }

            block.Add(line);
            if (!line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
                continue;
            var recurrenceId = block.FirstOrDefault(l => l.StartsWith("RECURRENCE-ID", StringComparison.OrdinalIgnoreCase));
            if (recurrenceId is not null && overrideValue is not null && recurrenceId[(recurrenceId.IndexOf(':') + 1)..] == overrideValue)
            {
                block = null;
                continue;
            }

            if (recurrenceId is null && block.Any(l => l.StartsWith("RRULE", StringComparison.OrdinalIgnoreCase) ||
                                                       l.StartsWith("RDATE", StringComparison.OrdinalIgnoreCase)))
            {
                block.Insert(block.Count - 1, exDate);
                series = true;
            }

            output.AddRange(block);
            block = null;
        }

        if (!series)
            throw new ArgumentException("The repeating event could not be found, so nothing was changed.");
        return string.Join("\r\n", output.Select(Fold)) + "\r\n";
    }

    // What to change; null keeps the current value. Times are local; AllDay keeps whole days.
    public record Change(string? Title, DateTime? Start, DateTime? End, bool AllDay, string? Location, string? Notes)
    {
        public bool MovesTime => Start is not null || End is not null;
    }

    // Changes one event, one occurrence of a series (as its own copy with RECURRENCE-ID, so the rest of
    // the series stays) or, for the title, place and notes, the whole series. Other lines are kept as they were.
    public static string EditEvent(string ics, Occurrence occurrence, Change change, bool wholeSeries)
    {
        var lines = Regex.Replace(ics, @"\r?\n[ \t]", "").Split('\n').Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0).ToList();
        var blocks = new List<(int Start, int End)>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
                continue;
            var end = lines.FindIndex(i, l => l.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase));
            blocks.Add((i, end));
            i = end;
        }

        string? Value(int start, int end, string name) => lines.Skip(start).Take(end - start)
            .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) ||
                                 l.StartsWith(name + ";", StringComparison.OrdinalIgnoreCase));
        bool Series((int Start, int End) b) => Value(b.Start, b.End, "RECURRENCE-ID") is null &&
                                               (Value(b.Start, b.End, "RRULE") is not null || Value(b.Start, b.End, "RDATE") is not null);
        var master = blocks.FirstOrDefault(b => Value(b.Start, b.End, "RECURRENCE-ID") is null);
        if (blocks.Count == 0 || master == default)
            throw new ArgumentException("The event could not be found in the calendar data, so nothing was changed.");

        List<string> block;
        int at;
        int remove;
        if (!occurrence.Recurring || wholeSeries)
        {
            if (wholeSeries && change.MovesTime)
                throw new ArgumentException("Moving every occurrence is not supported; move one occurrence, or change the series in your calendar app.");
            (at, remove) = (master.Start, master.End - master.Start + 1);
            block = lines.GetRange(master.Start, remove);
        }
        else if (occurrence.OverrideValue is { } overridden &&
                 blocks.FirstOrDefault(b => Value(b.Start, b.End, "RECURRENCE-ID") is { } id && id[(id.IndexOf(':') + 1)..] == overridden) is var copy &&
                 copy != default)
        {
            (at, remove) = (copy.Start, copy.End - copy.Start + 1);
            block = lines.GetRange(copy.Start, remove);
        }
        else
        {
            if (!Series(master))
                throw new ArgumentException("The repeating event could not be found, so nothing was changed.");
            // A new copy of the series for just this occurrence, placed after the series.
            string[] seriesOnly = ["DTSTART", "DTEND", "DURATION", "RRULE", "RDATE", "EXDATE", "RECURRENCE-ID"];
            block = lines.GetRange(master.Start, master.End - master.Start + 1)
                .Where(l => !seriesOnly.Any(p => l.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase) ||
                                                 l.StartsWith(p + ";", StringComparison.OrdinalIgnoreCase)))
                .ToList();
            block.Insert(block.Count - 1, "RECURRENCE-ID" + occurrence.ExDate["EXDATE".Length..]);
            var e = occurrence.Event;
            block.InsertRange(block.Count - 1, Times(change.Start ?? e.Start, change.End ?? e.End, change.Start is null ? e.AllDay : change.AllDay));
            (at, remove) = (master.End + 1, 0);
        }

        void Set(string name, string? value)
        {
            if (value is null)
                return;
            block.RemoveAll(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase) ||
                                 l.StartsWith(name + ";", StringComparison.OrdinalIgnoreCase));
            if (value.Length > 0)
                block.Insert(block.Count - 1, $"{name}:{Escape(value)}");
        }

        Set("SUMMARY", change.Title);
        Set("LOCATION", change.Location);
        Set("DESCRIPTION", change.Notes);
        if (change.MovesTime)
        {
            var e = occurrence.Event;
            var start = change.Start ?? e.Start;
            var end = change.End ?? start + (e.End - e.Start);
            if (end <= start && !change.AllDay)
                throw new ArgumentException("The end must be after the start.");
            block.RemoveAll(l => new[] { "DTSTART", "DTEND", "DURATION" }.Any(p =>
                l.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase) || l.StartsWith(p + ";", StringComparison.OrdinalIgnoreCase)));
            block.InsertRange(block.Count - 1, Times(start, end, change.AllDay));
        }

        // Tell calendar apps this is a newer version.
        var sequence = block.FindIndex(l => l.StartsWith("SEQUENCE:", StringComparison.OrdinalIgnoreCase));
        if (sequence >= 0 && int.TryParse(block[sequence][9..], out var number))
            block[sequence] = $"SEQUENCE:{number + 1}";
        block.RemoveAll(l => l.StartsWith("DTSTAMP", StringComparison.OrdinalIgnoreCase));
        block.Insert(1, $"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}");

        lines.RemoveRange(at, remove);
        lines.InsertRange(at, block);
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    // Timed events are written in UTC; whole days as dates with an exclusive end.
    private static string[] Times(DateTime start, DateTime end, bool allDay) => allDay
        ? [$"DTSTART;VALUE=DATE:{start:yyyyMMdd}", $"DTEND;VALUE=DATE:{(end.Date > start.Date ? end.Date : start.Date.AddDays(1)):yyyyMMdd}"]
        : [$"DTSTART:{start.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}", $"DTEND:{end.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}"];

    public async Task<string> UpdateAsync(Account account, Located target, Change change, bool wholeSeries,
        CancellationToken ct)
    {
        if (!target.Calendar.Writable || target.Url is null)
            throw new ArgumentException($"{target.Calendar.Name} is a read-only calendar link; change the event in your calendar app.");

        void Match(HttpRequestMessage request)
        {
            if (target.ETag is not null)
                request.Headers.TryAddWithoutValidation("If-Match", target.ETag);
        }

        string current;
        using (var response = await SendAsync(account, HttpMethod.Get, target.Url, null, null, ct, Match))
        {
            if (!response.IsSuccessStatusCode)
                throw new ArgumentException(response.StatusCode == HttpStatusCode.PreconditionFailed
                    ? "The event changed in the calendar since it was looked up. Nothing was changed; look it up again."
                    : $"{target.Calendar.Name}: the event could not be read ({(int)response.StatusCode}).");
            current = await response.Content.ReadAsStringAsync(ct);
        }

        var updated = EditEvent(current, target.Occurrence, change, wholeSeries);
        using (var response = await SendAsync(account, HttpMethod.Put, target.Url, updated, null, ct, Match))
        {
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new ArgumentException("The event changed in the calendar since it was looked up. Nothing was changed; look it up again.");
            if (!response.IsSuccessStatusCode)
                throw new ArgumentException($"{target.Calendar.Name}: the event was not changed ({(int)response.StatusCode}).");
        }

        return $"Changed \"{target.Occurrence.Event.Title}\" in {target.Calendar.Name} ({account.Label}).";
    }

    // Deletes one event, the whole series, or one occurrence of it. The ETag makes the server refuse if the
    // event changed since it was looked up.
    public async Task<string> DeleteAsync(Account account, Located target, bool wholeSeries, CancellationToken ct)
    {
        var e = target.Occurrence.Event;
        if (!target.Calendar.Writable || target.Url is null)
            throw new ArgumentException($"{target.Calendar.Name} is a read-only calendar link; delete the event in your calendar app.");

        void Match(HttpRequestMessage request)
        {
            if (target.ETag is not null)
                request.Headers.TryAddWithoutValidation("If-Match", target.ETag);
        }

        HttpResponseMessage response;
        if (target.Occurrence.Recurring && !wholeSeries)
        {
            using var current = await SendAsync(account, HttpMethod.Get, target.Url, null, null, ct, Match);
            if (!current.IsSuccessStatusCode)
                throw new ArgumentException(current.StatusCode == HttpStatusCode.PreconditionFailed
                    ? "The event changed in the calendar since it was looked up. Nothing was deleted; look it up again."
                    : $"{target.Calendar.Name}: the event could not be read ({(int)current.StatusCode}).");
            var updated = ExcludeOccurrence(await current.Content.ReadAsStringAsync(ct), target.Occurrence.ExDate,
                target.Occurrence.OverrideValue);
            response = await SendAsync(account, HttpMethod.Put, target.Url, updated, null, ct, Match);
        }
        else
        {
            response = await SendAsync(account, HttpMethod.Delete, target.Url, null, null, ct, Match);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
                throw new ArgumentException("The event changed in the calendar since it was looked up. Nothing was deleted; look it up again.");
            if (!response.IsSuccessStatusCode)
                throw new ArgumentException($"{target.Calendar.Name}: the event was not deleted ({(int)response.StatusCode}).");
        }

        var what = target.Occurrence.Recurring ? wholeSeries ? "every occurrence of " : "this occurrence of " : "";
        return $"Deleted {what}\"{e.Title}\" ({e.Start:ddd yyyy-MM-dd HH:mm}) from {target.Calendar.Name} ({account.Label}).";
    }

    public static string Format(IEnumerable<EventInfo> events)
    {
        var lines = events.Select(e => e.AllDay
            ? $"{e.Start:ddd yyyy-MM-dd} all day · {e.Title} · {e.Calendar}{(e.Location is null ? "" : " · " + e.Location)}"
            : $"{e.Start:ddd yyyy-MM-dd HH:mm}–{e.End:HH:mm} · {e.Title} · {e.Calendar}{(e.Location is null ? "" : " · " + e.Location)}")
            .ToList();
        return lines.Count == 0 ? "No events in that period." : string.Join("\n", lines);
    }

    public record NewEvent(string Title, DateTime StartLocal, DateTime EndLocal, string? Location, string? Notes);

    public static NewEvent ParseEvent(string title, string start, string end, string? location, string? notes)
    {
        static DateTime Local(string value, string name) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
                ? parsed
                : throw new ArgumentException($"Invalid {name}. Use a local date and time like 2026-10-02T14:00.");
        var startLocal = Local(start, "start");
        var endLocal = Local(end, "end");
        if (endLocal <= startLocal)
            throw new ArgumentException("The end must be after the start.");
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("The event needs a title.");
        return new NewEvent(title.Trim(), startLocal, endLocal, location, notes);
    }

    public async Task<CalendarInfo> TargetAsync(Account account, string? calendarName, CancellationToken ct)
    {
        var writable = (await CalendarsAsync(account, ct)).Where(c => c.Writable).ToList();
        if (writable.Count == 0)
            throw new ArgumentException($"{account.Label} has no calendar Leona can add events to.");
        return calendarName is null
            ? writable[0]
            : writable.FirstOrDefault(c => c.Name.Equals(calendarName, StringComparison.OrdinalIgnoreCase)) ??
              throw new ArgumentException($"No calendar named \"{calendarName}\". Calendars: {string.Join(", ", writable.Select(c => c.Name))}.");
    }

    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n");

    // Folds lines at 75 octets as RFC 5545 requires.
    private static string Fold(string line)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var ch in line)
        {
            var size = Encoding.UTF8.GetByteCount(ch.ToString());
            if (bytes + size > 74)
            {
                builder.Append("\r\n ");
                bytes = 1;
            }

            builder.Append(ch);
            bytes += size;
        }

        return builder.ToString();
    }

    public static string ToIcs(NewEvent e, string uid)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//Leona//Local assistant//EN", "BEGIN:VEVENT",
            $"UID:{uid}",
            $"DTSTAMP:{DateTime.UtcNow:yyyyMMdd'T'HHmmss'Z'}",
            $"DTSTART:{e.StartLocal.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}",
            $"DTEND:{e.EndLocal.ToUniversalTime():yyyyMMdd'T'HHmmss'Z'}",
            $"SUMMARY:{Escape(e.Title)}"
        };
        if (!string.IsNullOrWhiteSpace(e.Location))
            lines.Add($"LOCATION:{Escape(e.Location)}");
        if (!string.IsNullOrWhiteSpace(e.Notes))
            lines.Add($"DESCRIPTION:{Escape(e.Notes)}");
        lines.AddRange(["END:VEVENT", "END:VCALENDAR"]);
        return string.Join("\r\n", lines.Select(Fold)) + "\r\n";
    }

    public async Task<string> CreateAsync(Account account, CalendarInfo calendar, NewEvent e, CancellationToken ct)
    {
        var uid = $"{Guid.NewGuid()}@leona";
        var url = calendar.Url.TrimEnd('/') + "/" + uid + ".ics";
        using var response = await SendAsync(account, HttpMethod.Put, url, ToIcs(e, uid), null, ct,
            request => request.Headers.TryAddWithoutValidation("If-None-Match", "*"));
        if (!response.IsSuccessStatusCode)
            throw new ArgumentException($"{calendar.Name}: the event was not saved ({(int)response.StatusCode}).");
        return $"Added \"{e.Title}\" to {calendar.Name} ({account.Label}): {e.StartLocal:ddd yyyy-MM-dd HH:mm}–{e.EndLocal:HH:mm}.";
    }

    public async Task<string> TestAsync(Account account, CancellationToken ct)
    {
        var calendars = await CalendarsAsync(account, ct);
        return calendars.Count == 0
            ? "Connected, but no calendars were found."
            : $"Connected. Calendars: {string.Join(", ", calendars.Select(c => c.Name))}.";
    }
}
