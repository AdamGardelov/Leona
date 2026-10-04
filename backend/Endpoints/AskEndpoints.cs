using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Harness.Services;
using Microsoft.EntityFrameworkCore;

namespace Harness.Endpoints;

// "Hey Siri, ask Leona": an iPhone shortcut sends the dictated question with a Siri key and reads the
// plain-text answer aloud. Follow-up questions within a quarter of an hour continue the same chat.
public static partial class AskEndpoints
{
    private static readonly TimeSpan s_answerWait = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan s_followUp = TimeSpan.FromMinutes(15);
    private static readonly ConcurrentDictionary<int, (int ConversationId, DateTime At)> s_recent = new();

    private record KeyInput(string? Name);

    public static string? Bearer(HttpRequest request) =>
        request.Headers.Authorization.ToString() is { } header && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;

    public static void MapAskEndpoints(this IEndpointRouteBuilder app)
    {
        // Siri keys belong to the profile that made them; a key can only ask questions.
        app.MapGet("/api/siri", async (RemoteAccess remote, CurrentProfile profile, CancellationToken ct) =>
            Results.Ok(new
            {
                url = remote.TailscaleHost() is { } host ? $"https://{host}/api/ask" : null,
                keys = await remote.KeysAsync(profile.Id!.Value, ct)
            }));
        app.MapPost("/api/siri/keys", async (KeyInput input, RemoteAccess remote, CurrentProfile profile,
            CancellationToken ct) =>
        {
            var name = string.IsNullOrWhiteSpace(input.Name) ? "Siri" : input.Name.Trim()[..Math.Min(input.Name.Trim().Length, 60)];
            var (id, key) = await remote.CreateKeyAsync(profile.Id!.Value, name, ct);
            return Results.Ok(new { id, name, key });
        });
        app.MapDelete("/api/siri/keys/{id:int}", async (int id, RemoteAccess remote, CurrentProfile profile,
            CancellationToken ct) =>
            await remote.RemoveKeyAsync(profile.Id!.Value, id, ct) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/ask", AskAsync);
    }

    private static async Task<IResult> AskAsync(HttpRequest request, ChatDb db, CurrentProfile profile,
        ConversationService conversations, RunManager runs, OllamaClient ollama, NotificationService notifications,
        IServiceScopeFactory scopes, CancellationToken ct)
    {
        var question = await QuestionAsync(request, ct);
        var swedish = TextMatch.LanguageOf(question) != "English";
        string Say(string sv, string en) => swedish ? sv : en;
        if (question.Length == 0)
            return Results.Text(Say("Jag hörde ingen fråga.", "I did not hear a question."));

        var profileId = profile.Id!.Value;
        var model = await SettingsService.DefaultModelAsync(db, ollama, ct);
        if (model.Length == 0)
            return Results.Text(Say("Ingen modell är installerad.", "No model is installed."));

        // A follow-up continues the last Siri chat; otherwise a new chat starts.
        var conversationId = s_recent.TryGetValue(profileId, out var recent) && DateTime.UtcNow - recent.At < s_followUp &&
                             await db.Conversations.AnyAsync(c => c.Id == recent.ConversationId, ct)
            ? recent.ConversationId
            : (await conversations.CreateAsync(ct)).Id;
        var run = await runs.CreateAsync(profileId, conversationId,
            new ChatRequest(question, model, false, Web: true, Accounts: true));
        if (run is null)
            return Results.Text(Say("Leona svarar redan i det samtalet. Försök igen om en stund.",
                "Leona is already answering in that chat. Try again in a moment."));
        s_recent[profileId] = (conversationId, DateTime.UtcNow);

        var link = $"/?conversation={conversationId}";
        var deadline = DateTime.UtcNow + s_answerWait;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct);
            var status = await db.Runs.AsNoTracking().Where(r => r.Id == run.Id).Select(r => r.Status).FirstAsync(ct);
            if (status == RunStatus.Completed)
                return Results.Text(SpokenText.From(await ConversationService.LatestReplyAsync(db, conversationId, ct)));
            if (status == RunStatus.AwaitingApproval)
            {
                await notifications.NotifyAsync(profileId, "Leona needs your approval",
                    "Open Leona to approve or decline.", link, ct);
                return Results.Text(Say("Det behöver ditt godkännande. Jag har skickat en notis, öppna Leona för att godkänna.",
                    "That needs your approval. I sent a notification; open Leona to approve it."));
            }

            if (!RunStatus.IsActive(status))
                return Results.Text(Say("Något gick fel. Titta i Leona-appen.", "Something went wrong. Have a look in the Leona app."));
        }

        // Still working: the answer arrives as a notification instead.
        _ = NotifyWhenDoneAsync(scopes, notifications, profileId, run.Id, conversationId, link);
        return Results.Text(Say("Jag behöver lite mer tid. Svaret kommer som en notis.",
            "I need a little more time. The answer will come as a notification."));
    }

    private static async Task<string> QuestionAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var body = (await reader.ReadToEndAsync(ct)).Trim();
        if (body.StartsWith('{'))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                body = json.RootElement.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
            }
            catch (JsonException)
            {
            }
        }

        return AskOnly(body.Length > 4000 ? body[..4000] : body);
    }

    // People often dictate the whole phrase, "Fråga Leona vad klockan är"; the model only needs the question.
    public static string AskOnly(string text)
    {
        var question = Address().Replace(text.Trim(), "").Trim();
        return question.Length > 0 ? char.ToUpper(question[0]) + question[1..] : "";
    }

    [GeneratedRegex(@"^(hej|hey|hallå)?[\s,]*(siri)?[\s,]*(fråga|ask)?\s*leona\b[\s,.:!?]*", RegexOptions.IgnoreCase)]
    private static partial Regex Address();

    private static async Task NotifyWhenDoneAsync(IServiceScopeFactory scopes, NotificationService notifications,
        int profileId, Guid runId, int conversationId, string link)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromMinutes(15); waited += TimeSpan.FromSeconds(2))
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChatDb>();
            var status = await db.Runs.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).FirstOrDefaultAsync();
            if (status is null || RunStatus.IsActive(status))
                continue;

            var answer = status == RunStatus.Completed ? SpokenText.From(await ConversationService.LatestReplyAsync(db, conversationId, default)) : "";
            await notifications.NotifyAsync(profileId, "Leona",
                answer.Length > 0 ? ContextBudget.Excerpt(answer, 300) : "Open the chat for details.", link, default);
            return;
        }
    }
}
