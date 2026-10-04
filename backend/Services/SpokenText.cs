using System.Text.RegularExpressions;

namespace Harness.Services;

// A reply as Siri should read it: no code, links, sources or Markdown signs. The app does the same in
// voice.ts for replies read on the phone.
public static partial class SpokenText
{
    public static string From(string markdown)
    {
        var text = SourcesHeading().Split(markdown)[0];
        text = DraftBlock().Replace(text, "$1");
        text = CodeBlock().Replace(text, " (kod) ");
        text = Link().Replace(text, "$1");
        text = Url().Replace(text, "");
        text = InlineCode().Replace(text, "$1");
        text = ListMark().Replace(text, "");
        text = Heading().Replace(text, "");
        text = Markup().Replace(text, "");
        text = Paragraphs().Replace(text, ".\n");
        return Spaces().Replace(text, " ").Trim();
    }

    [GeneratedRegex(@"\n#{1,3} Sources\n")]
    private static partial Regex SourcesHeading();

    [GeneratedRegex(@"```draft\n([\s\S]*?)```")]
    private static partial Regex DraftBlock();

    [GeneratedRegex(@"```[\s\S]*?```")]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"!?\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex Url();

    [GeneratedRegex(@"`([^`]*)`")]
    private static partial Regex InlineCode();

    [GeneratedRegex(@"^\s*[-*+]\s+", RegexOptions.Multiline)]
    private static partial Regex ListMark();

    [GeneratedRegex(@"^\s*#+\s*", RegexOptions.Multiline)]
    private static partial Regex Heading();

    [GeneratedRegex(@"[*_~|>]")]
    private static partial Regex Markup();

    [GeneratedRegex(@"\n{2,}")]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
