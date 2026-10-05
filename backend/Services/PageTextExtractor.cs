using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace Harness.Services;

public static partial class PageTextExtractor
{
    // Upper bound on extracted text kept per page; read_page shows windows of it.
    public const int MaxCharacters = 300_000;

    // Image is the picture the page offers for sharing (og:image), shown with suggestions that link to it.
    public static async Task<(string Title, string Text, string? Image)> ExtractAsync(FetchedPage page, CancellationToken ct)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding? encoding = null;
        if (!string.IsNullOrWhiteSpace(page.Charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(page.Charset.Trim('"', '\''));
            }
            catch (ArgumentException)
            {
            }
        }

        string title;
        string body;
        string? image = null;
        using var stream = new MemoryStream(page.Bytes);
        if (page.Mime == "text/plain")
        {
            using var reader =
                new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            body = Tidy(await reader.ReadToEndAsync(ct));
            title = "Page";
        }
        else
        {
            var parser = new HtmlParser();
            using var document = encoding is null
                ? await parser.ParseDocumentAsync(stream, ct)
                : await parser.ParseDocumentAsync(encoding.GetString(page.Bytes), ct);
            title = document.Title?.Trim() is { Length: > 0 } documentTitle ? documentTitle : "Page";
            var shared = document.QuerySelector("meta[property='og:image'], meta[name='twitter:image']")
                ?.GetAttribute("content")?.Trim();
            if (Uri.TryCreate(new Uri(page.Url), shared, out var picture) && picture.Scheme == "https")
                image = picture.AbsoluteUri;
            body = TextOf(document, "script,style,nav,footer,header,noscript,svg");
        }

        if (body.Length > MaxCharacters)
            body = body[..MaxCharacters];
        var suffix = page.Truncated ? " [Page truncated after 1 MB]" : "";
        return (title, body + suffix, image);
    }

    // The text of an HTML fragment, such as a mail body or a feed's description.
    public static string HtmlToText(string html)
    {
        if (html.Length == 0)
            return "";
        using var document = new HtmlParser().ParseDocument(html);
        return TextOf(document, "script,style,head");
    }

    private static string TextOf(IHtmlDocument document, string remove)
    {
        foreach (var node in document.QuerySelectorAll(remove))
            node.Remove();
        // Keep block boundaries as line breaks so passages and paragraphs stay readable.
        foreach (var node in document.QuerySelectorAll(
                     "p,div,li,h1,h2,h3,h4,h5,h6,br,tr,section,article,blockquote,pre,dt,dd,table"))
            node.After(document.CreateTextNode("\n"));
        return Tidy(document.Body?.TextContent ?? "");
    }

    private static string Tidy(string text) => LineBreaks().Replace(Spaces().Replace(text, " "), "\n").Trim();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@" ?\n[\s]*")]
    private static partial Regex LineBreaks();
}
