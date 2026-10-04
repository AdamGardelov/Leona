using Harness.Services;

namespace Harness.Endpoints;

public static class SpeechEndpoints
{
    // About a minute of 16 kHz 16-bit mono audio, with room to spare.
    private const long MaxBytes = 4_000_000;

    public static void MapSpeechEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/speech");
        group.MapGet("", (SpeechService speech) => Results.Ok(new { available = speech.Available }));
        group.MapPost("/transcribe", async (HttpRequest request, SpeechService speech, CancellationToken ct,
            string? language) =>
        {
            if (!speech.Available)
                return Results.Json(new { error = "Speech recognition is not installed." }, statusCode: 503);
            if (request.ContentLength is > MaxBytes)
                return Results.BadRequest(new { error = "The recording is too long. Keep it under a minute." });

            using var audio = new MemoryStream();
            await request.Body.CopyToAsync(audio, ct);
            if (audio.Length is < 1000 or > MaxBytes)
                return Results.BadRequest(new { error = "The recording is empty or too long." });

            audio.Position = 0;
            try
            {
                var text = await speech.TranscribeAsync(audio, Language(language), ct);
                return Results.Ok(new { text });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Results.BadRequest(new { error = "The recording could not be understood as audio." });
            }
        });
    }

    private static string Language(string? language) =>
        language is { Length: >= 2 and <= 5 } code && code.All(char.IsAsciiLetter) ? code.ToLowerInvariant() : "sv";
}
