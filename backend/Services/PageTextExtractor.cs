using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;

namespace Harness.Services;

public static class PageTextExtractor
{
    // Upper bound on extracted text kept per page; read_page shows windows of it.
    public const int MaxCharacters = 300_000;

    public static async Task<(string Title, string Text)> ExtractAsync(FetchedPage page, CancellationToken ct)
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
        using var stream = new MemoryStream(page.Bytes);
        if (page.Mime == "text/plain")
        {
            using var reader =
                new StreamReader(stream, encoding ?? Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            body = await reader.ReadToEndAsync(ct);
            title = "Page";
        }
        else
        {
            var parser = new HtmlParser();
            using var document = encoding is null
                ? await parser.ParseDocumentAsync(stream, ct)
                : await parser.ParseDocumentAsync(encoding.GetString(page.Bytes), ct);
            title = document.Title?.Trim() is { Length: > 0 } documentTitle ? documentTitle : "Page";
            foreach (var node in document.QuerySelectorAll("script,style,nav,footer,header,noscript,svg"))
                node.Remove();
            // Keep block boundaries as line breaks so passages and paragraphs stay readable.
            foreach (var node in document.QuerySelectorAll(
                         "p,div,li,h1,h2,h3,h4,h5,h6,br,tr,section,article,blockquote,pre,dt,dd,table"))
                node.After(document.CreateTextNode("\n"));
            body = document.Body?.TextContent ?? "";
        }

        body = Regex.Replace(body, @"[^\S\n]+", " ");
        body = Regex.Replace(body, @" ?\n[\s]*", "\n").Trim();
        if (body.Length > MaxCharacters)
            body = body[..MaxCharacters];
        var suffix = page.Truncated ? " [Page truncated after 1 MB]" : "";
        return (title, body + suffix);
    }
}
