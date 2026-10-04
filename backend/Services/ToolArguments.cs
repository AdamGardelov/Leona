using System.Text.Json;

namespace Harness.Services;

// Reads tool arguments from the model, which may send numbers as strings or omit optional values.
internal sealed class ToolArguments(JsonElement args)
{
    private bool TryGet(string key, out JsonElement value)
    {
        value = default;
        return args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out value) &&
               value.ValueKind != JsonValueKind.Null;
    }

    public string? Optional(string key, int max = 2000)
    {
        if (!TryGet(key, out var value) ||
            (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
            return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > max)
            throw new ArgumentException($"Invalid argument: {key}.");
        return value.GetString()!;
    }

    public string Required(string key, int max = 2000) =>
        Optional(key, max) ?? throw new ArgumentException($"Invalid argument: {key}.");

    // Like Required, but an empty string is a valid value (for example deleting text).
    public string Text(string key, int max)
    {
        if (!TryGet(key, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString()!.Length > max)
            throw new ArgumentException($"Invalid argument: {key}.");
        return value.GetString()!;
    }

    public int Integer(string key, int fallback)
    {
        if (!TryGet(key, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= 0)
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) &&
            number >= 0)
            return number;
        throw new ArgumentException($"Invalid argument: {key}.");
    }

    public bool Bool(string key, bool fallback = false)
    {
        if (!TryGet(key, out var value))
            return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw new ArgumentException($"Invalid argument: {key}.")
        };
    }

    public double? Number(string key)
    {
        if (!TryGet(key, out var value))
            return null;
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String && AutomationService.ParseNumber(value.GetString() ?? "") is { } parsed)
            return parsed;
        throw new ArgumentException($"Invalid argument: {key}.");
    }

    public JsonElement? Raw(string key) => TryGet(key, out var value) ? value : null;
}
