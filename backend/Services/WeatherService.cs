using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Harness.Services;

// The weather from SMHI's open point forecast (snow1g), for Göteborg unless another place is given.
public sealed class WeatherService(IHttpClientFactory clients)
{
    private const string Api = "https://opendata-download-metfcst.smhi.se/api/category/snow1g/version/1/geotype/point";

    // Johanneberg, Göteborg.
    public const double GoteborgLatitude = 57.689;
    public const double GoteborgLongitude = 11.974;

    // SMHI's weather symbols (Wsymb2).
    private static readonly string[] s_symbols =
    [
        "", "clear sky", "nearly clear sky", "variable cloudiness", "halfclear sky", "cloudy sky", "overcast", "fog",
        "light rain showers", "moderate rain showers", "heavy rain showers", "thunderstorm", "light sleet showers",
        "moderate sleet showers", "heavy sleet showers", "light snow showers", "moderate snow showers", "heavy snow showers",
        "light rain", "moderate rain", "heavy rain", "thunder", "light sleet", "moderate sleet", "heavy sleet",
        "light snowfall", "moderate snowfall", "heavy snowfall"
    ];

    public async Task<string> ForecastAsync(DateOnly day, double latitude, double longitude, string place, CancellationToken ct)
    {
        var url = string.Create(CultureInfo.InvariantCulture, $"{Api}/lon/{longitude:0.###}/lat/{latitude:0.###}/data.json");
        using var json = JsonDocument.Parse(await clients.CreateClient("jobs").GetStringAsync(url, ct));
        var hours = json.RootElement.GetProperty("timeSeries").EnumerateArray()
            .Select(t => (Time: DateTime.Parse(t.GetProperty("time").GetString()!, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal).ToLocalTime(), Data: t.GetProperty("data")))
            .Where(t => DateOnly.FromDateTime(t.Time) == day && t.Time.Hour is >= 7 and <= 21)
            .ToList();
        if (hours.Count == 0)
            return $"SMHI has no forecast for {place} on {day:yyyy-MM-dd} (it covers about ten days ahead).";

        double Value(JsonElement data, string name) =>
            data.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : 0;
        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"Weather in {place} on {day.ToString("dddd yyyy-MM-dd", CultureInfo.InvariantCulture)} from SMHI: " +
            $"{hours.Min(h => Value(h.Data, "air_temperature")):0}–{hours.Max(h => Value(h.Data, "air_temperature")):0} °C, " +
            $"rain {hours.Sum(h => Value(h.Data, "precipitation_amount_mean")):0.#} mm in total, " +
            $"highest chance of rain {hours.Max(h => Value(h.Data, "probability_of_precipitation")):0} %.\n"));
        // Three-hour steps through the day.
        foreach (var (time, data) in hours.Where((h, i) => i == 0 || h.Time.Hour % 3 == 0))
        {
            var symbol = (int)Value(data, "symbol_code");
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- {time:HH:mm}: {Value(data, "air_temperature"):0} °C, {(symbol > 0 && symbol < s_symbols.Length ? s_symbols[symbol] : "")}, " +
                $"rain {Value(data, "precipitation_amount_mean"):0.#} mm ({Value(data, "probability_of_precipitation"):0} %), " +
                $"wind {Value(data, "wind_speed"):0} m/s"));
        }

        return text.ToString().TrimEnd();
    }
}
