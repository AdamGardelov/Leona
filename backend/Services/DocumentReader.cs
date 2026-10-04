using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Harness.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Harness.Services;

// Extracts text from PDF (per page) and Word documents (per paragraph) and returns bounded windows.
public static class DocumentReader
{
    private const long MaxDocumentBytes = 50_000_000;
    private const long MaxDocxXmlBytes = 30_000_000;

    // PDF: one entry per page. Other formats: one entry per paragraph or line.
    public record Extracted(bool Paged, IReadOnlyList<string> Parts);

    public static Extracted Extract(string fullPath)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists)
            throw new ArgumentException("File not found.");
        if (info.Length > MaxDocumentBytes)
            throw new ArgumentException("Document exceeds the 50 MB limit.");

        return info.Extension.ToLowerInvariant() switch
        {
            ".pdf" => new Extracted(true, ReadPdf(fullPath)),
            ".docx" => new Extracted(false, ReadDocx(fullPath)),
            ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" =>
                new Extracted(false, File.ReadAllText(fullPath).Replace("\r", "").Split('\n')),
            _ => throw new ArgumentException("Supported documents: PDF, DOCX and plain text (txt, md, csv, json, xml, log).")
        };
    }

    private static List<string> ReadPdf(string path)
    {
        try
        {
            using var document = PdfDocument.Open(path);
            return document.GetPages().Select(page => ContentOrderTextExtractor.GetText(page).Trim()).ToList();
        }
        catch (Exception ex) when (ex is not ArgumentException and not OperationCanceledException)
        {
            throw new ArgumentException($"Could not read the PDF: {ex.Message}");
        }
    }

    private static List<string> ReadDocx(string path)
    {
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml") ?? throw new ArgumentException("Not a Word document.");
            if (entry.Length > MaxDocxXmlBytes)
                throw new ArgumentException("Word document text is too large.");
            using var stream = entry.Open();
            var document = XDocument.Load(stream);
            var paragraphs = new List<string>();
            foreach (var paragraph in document.Descendants(w + "p"))
            {
                var text = new StringBuilder();
                foreach (var node in paragraph.Descendants())
                {
                    if (node.Name == w + "t")
                        text.Append(node.Value);
                    else if (node.Name == w + "tab")
                        text.Append('\t');
                    else if (node.Name == w + "br" || node.Name == w + "cr")
                        text.Append('\n');
                }

                var style = paragraph.Element(w + "pPr")?.Element(w + "pStyle")?.Attribute(w + "val")?.Value ?? "";
                var heading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) &&
                              int.TryParse(style[7..], out var level)
                    ? new string('#', Math.Clamp(level, 1, 6)) + " "
                    : "";
                if (text.Length > 0)
                    paragraphs.Add(heading + text);
            }

            return paragraphs;
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new ArgumentException("Could not read the Word document; it may be damaged.");
        }
    }

    public static ToolResult Read(string path, Extracted document, string? find, int start, int limit)
    {
        var unit = document.Paged ? "Page" : "Paragraph";
        if (document.Parts.All(string.IsNullOrWhiteSpace))
            return new ToolResult(document.Paged
                    ? $"{path} has {document.Parts.Count} pages but no extractable text. It may be scanned images; OCR is not available."
                    : $"{path} contains no text.",
                Summary: "No extractable text");

        if (!string.IsNullOrWhiteSpace(find))
        {
            // Search the joined text and report which page or paragraph each passage came from.
            var joined = new StringBuilder();
            var starts = new List<int>();
            foreach (var part in document.Parts)
            {
                starts.Add(joined.Length);
                joined.Append(part).Append('\n').Append(PageReader.PartBreak).Append('\n');
            }

            var passages = PageReader.Find(joined.ToString(), find, limit);
            if (passages.Count > 0)
            {
                string Where(int offset) =>
                    $"{unit} {starts.FindLastIndex(s => s <= offset) + 1}";
                var body = string.Join("\n[…]\n", passages.Select(p => $"[{Where(p.Start)}] {p.Text}"));
                return new ToolResult(
                    $"{path}: {passages.Count} passages matching \"{find}\":\n\n{body}\n\n" +
                    $"[Call read_document with start set to a {unit.ToLowerInvariant()} number to read on.]",
                    Summary: $"{passages.Count} passages matching “{find}”");
            }
        }

        var first = Math.Clamp(start, 1, document.Parts.Count);
        var text = new StringBuilder();
        var last = first - 1;
        while (last < document.Parts.Count)
        {
            var part = document.Parts[last];
            var block = document.Paged ? $"--- Page {last + 1} ---\n{part}\n\n" : part + "\n";
            if (text.Length > 0 && text.Length + block.Length > limit)
                break;
            text.Append(block.Length > limit ? block[..limit] + "\n[Page text truncated]\n" : block);
            last++;
        }

        var plural = document.Paged ? "pages" : "paragraphs";
        var footer = last < document.Parts.Count
            ? $"\n[{unit}s {first}–{last} of {document.Parts.Count}. Call read_document with start={last + 1} to continue, or with find to jump to a topic.]"
            : $"\n[End of document. {document.Parts.Count} {plural}.]";
        var prefix = !string.IsNullOrWhiteSpace(find) ? $"No passages matched \"{find}\". Showing from {unit.ToLowerInvariant()} {first}.\n" : "";
        return new ToolResult($"{prefix}{path}\n\n{text.ToString().TrimEnd()}\n{footer}",
            Summary: $"{unit}s {first}–{last} of {document.Parts.Count}");
    }
}
