using System.Text.RegularExpressions;

namespace Harness.Services;

// Loose word matching for Swedish and English: "kontoret" matches "Kontor", "lamporna" matches "lampa",
// and "skrivbordslampan" matches both "skrivbord" and "lampa".
public static partial class TextMatch
{
    // Runs of whitespace, line breaks included, become one space.
    public static string Collapse(string? text) => Whitespace().Replace(text ?? "", " ").Trim();

    public static List<string> Words(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : WordPattern().Matches(text.ToLowerInvariant()).Select(m => m.Value).Where(w => w.Length >= 2).ToList();

    private static readonly HashSet<string> s_swedish =
        ["och", "att", "det", "är", "som", "för", "med", "på", "inte", "jag", "du", "kan", "har", "till", "den", "om",
         "eller", "mina", "min", "mitt", "vad", "hur", "vilka", "vilken", "visa", "mejl", "idag", "imorgon", "ska"];
    private static readonly HashSet<string> s_english =
        ["the", "and", "is", "to", "of", "that", "for", "with", "you", "it", "are", "can", "this", "my", "what", "how",
         "show", "me", "please", "which", "today", "tomorrow"];

    // The language of a message when it is clear, as "Swedish" or "English"; null when it is not.
    public static string? LanguageOf(string? text)
    {
        var words = Words(text);
        var swedish = words.Count(s_swedish.Contains) + (text?.IndexOfAny(['å', 'ä', 'ö', 'Å', 'Ä', 'Ö']) >= 0 ? 1 : 0);
        var english = words.Count(s_english.Contains);
        if (swedish > english && swedish >= 1)
            return "Swedish";

        return english > swedish && english >= 2 ? "English" : null;
    }

    public static bool Similar(string word, string token)
    {
        if (word.Length < 3 || token.Length < 3)
            return word == token;
        if (token.Contains(word) || (token.Length >= 4 && word.Contains(token)))
            return true;
        // A short word with a Swedish ending: "köket" is the room "Kök".
        if (token.Length == 3 && word.StartsWith(token) && word.Length <= 6)
            return true;
        // A shared stem with at most one letter of difference: "lamporna" and "lampa", but not "kontor" and "kontakt".
        var shared = 0;
        var shorter = Math.Min(word.Length, token.Length);
        while (shared < shorter && word[shared] == token[shared])
            shared++;
        return shared >= 4 && shared >= shorter - 1;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();
}
