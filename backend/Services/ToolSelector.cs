using System.Text.Json;
using System.Text.RegularExpressions;

namespace Harness.Services;

// Small models choose better, and keep more room for the conversation, when they see only the tools a
// message needs. The toggles decide which groups are allowed; this picks the families within them that
// the message (or the previous turn) is about, in Swedish or English. Words are matched as prefixes so
// Swedish inflections such as "mejlet" and "lamporna" count.
public static partial class ToolSelector
{
    private sealed record Family(string Name, string[] Tools, Regex? Words, bool Personal = false);

    private static readonly Family[] s_families =
    [
        new("mail", ["mail_search", "mail_read", "mail_attachment", "mail_manage", "mail_draft", "mail_send"], MailWords(), true),
        new("calendar", ["calendar_events", "calendar_create", "calendar_update", "calendar_delete"], CalendarWords(), true),
        new("home", ["home_states", "home_action"], HomeWords(), true),
        new("expenses", ["record_expense", "list_expenses"], ExpenseWords(), true),
        new("automations", ["schedule_task", "watch_page"], AutomationWords(), true),
        new("music", ["music_taste", "find_concerts"], MusicWords(), true),
        new("jobs", ["find_jobs"], JobWords(), true),
        new("activities", ["find_activities"], ActivityWords(), true),
        new("weather", ["weather"], WeatherWords(), true),
        new("photos", [PhotoTools.Name], PhotoWords()),
        new("documents", ["read_document"], DocumentWords()),
        new("new files", ["create_file"], CreateWords()),
        new("edits", ["edit_file"], EditWords()),
        new("skills", ["save_skill"], SkillWords()),
        // Always offered when their group is on.
        new("web", ["search_web", "read_page"], null),
        new("files", ["list_files", "search_files", "read_file"], null),
        new("terminal", ["run_command"], null),
        new("memory", ["save_memory", "search_memory"], null)
    ];

    // When Personal is on but nothing matched, reading mail and calendars (if connected) still lets the
    // model look things up. Narrower tools, such as concerts, wait until a message is about them.
    private static readonly string[] s_personalFallback = ["calendar_events", "mail_search"];

    public static string NameOf(object definition) =>
        JsonSerializer.SerializeToElement(definition).GetProperty("function").GetProperty("name").GetString()!;

    // Keeps the definitions whose family the text mentions, the families used in the previous turn and
    // the always-on core. Returns the kept definitions and the family names that were added by words.
    // learned adds words from the user's own setup per family, such as room names for "home".
    public static (IReadOnlyList<object> Tools, IReadOnlyList<string> Matched) Select(
        IReadOnlyList<object> definitions, string text, IEnumerable<string> recentTools, bool documentAttached,
        bool imageAttached, IReadOnlyDictionary<string, IReadOnlyCollection<string>>? learned = null)
    {
        var recent = recentTools.ToHashSet();
        var lower = text.ToLowerInvariant();
        var messageWords = TextMatch.Words(text);
        var keep = new HashSet<string>();
        var matched = new List<string>();
        foreach (var family in s_families)
        {
            // A message that names a tool, as scheduled tasks often do, always gets it.
            var wanted = family.Words is null ||
                         family.Words.IsMatch(lower) ||
                         family.Tools.Any(lower.Contains) ||
                         (learned is not null && learned.TryGetValue(family.Name, out var own) &&
                          messageWords.Any(w => own.Any(o => TextMatch.Similar(w, o)))) ||
                         family.Tools.Any(recent.Contains) ||
                         (family.Name == "documents" && documentAttached) ||
                         (family.Name is "expenses" or "photos" && imageAttached);
            if (!wanted)
                continue;
            keep.UnionWith(family.Tools);
            if (family.Words is not null)
                matched.Add(family.Name);
        }

        var names = definitions.Select(NameOf).ToList();
        var personal = names.Where(n => s_families.Any(f => f.Personal && f.Tools.Contains(n))).ToList();
        if (personal.Count > 0 && !personal.Any(keep.Contains))
            keep.UnionWith(s_personalFallback.Where(personal.Contains));
        // Tools that belong to no family (added later) are never hidden.
        var known = s_families.SelectMany(f => f.Tools).ToHashSet();
        var tools = definitions.Where((_, i) => keep.Contains(names[i]) || !known.Contains(names[i])).ToList();
        return (tools, matched);
    }

    [GeneratedRegex(@"\b(e-?mail|mail|mejl|e-?post|inbox|inkorg|brev|svar[ae]|reply|skicka|send|olä?st|unread|avsändar|sender|nyhetsbrev|newsletter|bilag|attachment|faktura|invoice|arkiv|archive|flagg|papperskorg|trash)")]
    private static partial Regex MailWords();

    [GeneratedRegex(@"\b(bild|foto|photo|picture|image|ta bort|tag bort|radera|remove|erase|retusch|retouch|redigera|edit)")]
    private static partial Regex PhotoWords();

    [GeneratedRegex(@"\b(kalender|calendar|möte|meeting|event|händelse|bok[an]|book|appointment|agenda|schema|idag|i dag|today|ikväll|tonight|imorgon|i morgon|tomorrow|vecka|week|helg|weekend|ledig|upptagen|busy|födelsedag|birthday|måndag|tisdag|onsdag|torsdag|fredag|lördag|söndag|monday|tuesday|wednesday|thursday|friday|saturday|sunday|morgonbrief|morning brief)")]
    private static partial Regex CalendarWords();

    // Device words also count inside compounds ("taklampan", "skrivbordsfläkten"); short words only at a word start.
    [GeneratedRegex(@"\b(ljus|light|tänd|släck|turn (on|off)|slå (på|av)|stäng (av|på)|temperatur|temperature|grader|degrees|varm|kallt|kallare|värm|heat|luftfukt|humidity|home assistant|hemma|dörr|door|lås|lock|uttag|plug|garage|larm|alarm|scen|scene|tv\b|kontor|skrivbord|kök|sovrum|vardagsrum|badrum|tvättstug|källar|balkong|altan|trädgård|office|desk|kitchen|bedroom|living room)|(lamp|termostat|thermostat|sensor|fläkt|gardin|persienn|blind|dammsug|vacuum|högtalare|speaker)")]
    private static partial Regex HomeWords();

    [GeneratedRegex(@"\b(kvitto|receipt|utgift|expense|kostnad|spent|spenderat|köpte|bought|betalade|paid|budget|kronor|\d+([,.]\d{1,2})?\s*(kr|sek|:-))")]
    private static partial Regex ExpenseWords();

    [GeneratedRegex(@"\b(varje|every|dagligen|daily|schemalägg|schedul|påminn|remind|bevaka|watch|håll koll|keep an eye|meddela (mig )?när|notify|säg till när|let me know when|prisfall|price drop|billigare|cheaper|morgonbrief|morning brief)")]
    private static partial Regex AutomationWords();

    [GeneratedRegex(@"(\.pdf|\.docx|\b(pdf|docx|word-?fil|dokument|document|rapport|report|avtal|contract|faktura|invoice|bilaga|attachment))")]
    private static partial Regex DocumentWords();

    [GeneratedRegex(@"\b(spotify|musik|music|konsert|concert|gig|spelning|spelar|spela|uppträd|plays?|playing|perform|band|artist|låt|song|lyssna|listen|festival|turné|tour|biljett|ticket|live|scen)")]
    private static partial Regex MusicWords();

    [GeneratedRegex(@"\b(göra (idag|i dag|i helgen|imorgon)|hitta på|aktivitet|evenemang|händer|utflykt|barn|ettåring|bebis|familj|sagostund|babysång|museum|museer|lekplats|liseberg|universeum|activit|things to do|events?)")]
    private static partial Regex ActivityWords();

    [GeneratedRegex(@"\b(väder|vädret|weather|regn|rain|snö|snow|sol|sunny|prognos|forecast|paraply|regnställ|blåsig|blåser|vind|wind|smhi)")]
    private static partial Regex WeatherWords();

    [GeneratedRegex(@"\b(jobb|tjänst|rekryt|ansök|lediga|platsbank|arbetsförmedl|karriär|arbetsgivare|job|position|vacanc|career|hiring|recruit)")]
    private static partial Regex JobWords();

    [GeneratedRegex(@"\b(skill|färdighet|rutin|routine|procedur|procedure|arbetsflöde|workflow|kom ihåg hur|remember how|nästa gång|next time|samma sätt|same way)")]
    private static partial Regex SkillWords();

    [GeneratedRegex(@"\b(skapa|create|ny fil|new file|spara|save|skriv (en|ner|till)|write (a|down|to)|generera|generate|exportera|export)")]
    private static partial Regex CreateWords();

    [GeneratedRegex(@"\b(ändra|edit|change|byt|replace|ersätt|uppdatera|update|fixa|fix|rätta|correct|lägg till|add|ta bort|remove|delete|stryk|döp om|rename|refaktor|refactor)")]
    private static partial Regex EditWords();
}
