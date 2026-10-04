using System.Text;
using System.Text.RegularExpressions;
using Whisper.net;

namespace Harness.Services;

// Speech to text on this computer with KB-Whisper (KBLab's Swedish Whisper), so recordings never leave
// the house. It runs on the processor and leaves the graphics card to the chat model. The model is loaded
// on first use, and one recording is transcribed at a time.
public sealed partial class SpeechService(IConfiguration config, IWebHostEnvironment env, ILogger<SpeechService> logger)
    : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private WhisperFactory? _factory;

    public string ModelPath => config["Speech:ModelPath"] is { Length: > 0 } path
        ? Path.GetFullPath(path, env.ContentRootPath)
        : Path.Combine(env.ContentRootPath, "models", "kb-whisper-small-q5_0.bin");

    public bool Available => File.Exists(ModelPath);

    // wav: 16 kHz mono 16-bit PCM, as the app records it. language: an ISO code such as "sv", or "auto".
    public async Task<string> TranscribeAsync(Stream wav, string language, CancellationToken ct)
    {
        if (!Available)
            throw new InvalidOperationException("No speech model is installed.");

        await _lock.WaitAsync(ct);
        try
        {
            _factory ??= WhisperFactory.FromPath(ModelPath);
            var builder = _factory.CreateBuilder()
                .WithThreads(Math.Max(1, Environment.ProcessorCount))
                .WithNoSpeechThreshold(0.6f);
            builder = language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(language);
            await using var processor = builder.Build();
            var text = new StringBuilder();
            var started = DateTime.UtcNow;
            await foreach (var segment in processor.ProcessAsync(wav, ct))
            {
                if (!Phantom(segment.Text))
                    text.Append(segment.Text);
            }

            logger.LogInformation("Transcribed speech in {Seconds:0.0} s", (DateTime.UtcNow - started).TotalSeconds);
            return TextMatch.Collapse(text.ToString());
        }
        finally
        {
            _lock.Release();
        }
    }

    // Whisper was trained on subtitles and fills silence with their credits, such as "Textning.nu" or
    // "Tack för att ni tittade".
    public static bool Phantom(string segment) => PhantomPattern().IsMatch(segment.Trim());

    [GeneratedRegex(@"^\W*(text(ning|at av)|undertext|svensktextning|tack för att (ni|du) (tittade|lyssnade)|prenumerera|amara\.org|www\.|thanks for watching|subtitles by)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PhantomPattern();

    public void Dispose()
    {
        _factory?.Dispose();
        _lock.Dispose();
    }
}
