using System.Globalization;
using System.Text.Json;

namespace Harness.Services;

// Reads values from the JSON of outside APIs (job boards, event calendars), where fields may be missing.
public static class JsonPath
{
    // The value at the path as text: strings as they are, numbers and booleans as written, otherwise "".
    public static string Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
                return "";
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? "",
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.GetRawText(),
            _ => ""
        };
    }

    public static DateTime? Date(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
