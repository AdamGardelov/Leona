using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Removes things from the user's photos with the photo service on this computer (photo/server.py), so no
// picture leaves the house. The edit is saved as a new upload and shown with the reply; the original
// stays as it was. Photo:Url points to the service.
public sealed class PhotoTools(IHttpClientFactory clients, IConfiguration configuration, UploadStore uploads, ChatDb db)
{
    public const string Name = "remove_from_photo";

    // The chat's photos, oldest first: those attached earlier, earlier edits and the message being sent.
    private List<AttachmentRef> _photos = [];

    public bool Available => _photos.Count > 0 && !string.IsNullOrWhiteSpace(configuration["Photo:Url"]);

    public async Task LoadAsync(int conversationId, IEnumerable<AttachmentRef> current, CancellationToken ct)
    {
        var earlier = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId && m.AttachmentsJson != "[]")
            .OrderBy(m => m.Id).Select(m => m.AttachmentsJson).ToListAsync(ct);
        _photos = earlier.SelectMany(ChatService.AttachmentsOf).Concat(current)
            .Where(a => a.Kind == UploadKind.Image).DistinctBy(a => a.Id).ToList();
    }

    public object Definition() => ToolRegistry.Definition(Name,
        "Remove things from the user's photo, such as a tree, a person, a car or a sign, and fill in the background. " +
        "Works on the newest photo in this chat, which may be an earlier edit. Makes an edited copy that the user sees " +
        "with your reply; the original is kept.",
        new ToolRegistry.Param("remove", "string",
            "What to remove, in English, as short nouns: \"tree\", \"person\", \"car\". Separate several with commas; " +
            "add \"shadow\" to remove its shadow too."),
        new ToolRegistry.Param("all", "boolean",
            "Optional. true removes every match, for example all people. Default: the clearest match of each.", false),
        new ToolRegistry.Param("photo", "string",
            $"Optional. File name of an earlier photo in this chat, when not the newest. Photos: {string.Join(", ", _photos.Select(p => p.Name).Distinct())}.",
            false));

    internal async Task<ToolResult> RemoveAsync(ToolArguments args, CancellationToken ct)
    {
        var what = args.Required("remove", 200);
        var name = args.Optional("photo", 200)?.Trim();
        var photo = (name is null
                        ? _photos.LastOrDefault()
                        : _photos.LastOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) ??
                    throw new ArgumentException(name is null
                        ? "There is no photo in this chat. Ask the user to attach one."
                        : $"No photo named {name} in this chat. Photos: {string.Join(", ", _photos.Select(p => p.Name))}.");
        var upload = await db.Uploads.FindAsync([photo.Id], ct) ??
                     throw new ArgumentException($"{photo.Name} is no longer available.");
        var output = Path.Combine(uploads.RootFor(upload.ProfileId), $"edit-{Guid.NewGuid():N}.jpg");
        try
        {
            // Sent with its length: the service's small HTTP server does not read chunked bodies.
            var request = JsonSerializer.Serialize(new { input = uploads.PathFor(upload), output, remove = what, all = args.Bool("all") });
            using var response = await clients.CreateClient("photo").PostAsync(
                new Uri(new Uri(configuration["Photo:Url"]!), "remove"),
                new StringContent(request, Encoding.UTF8, "application/json"), ct);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            if (!response.IsSuccessStatusCode)
                throw new ArgumentException(JsonPath.Text(body, "error") is { Length: > 0 } error
                    ? error
                    : $"The photo could not be edited ({(int)response.StatusCode}).");

            Upload edited;
            await using (var file = File.OpenRead(output))
                edited = await uploads.SaveAsync(db, $"{Path.GetFileNameWithoutExtension(photo.Name)}-edited.jpg",
                    file, file.Length, ct);
            var reference = UploadStore.Reference(edited);
            // A further edit in the same answer works on this one.
            _photos.Add(reference);
            var found = string.Join(", ", body.GetProperty("found").EnumerateArray().Select(f => JsonPath.Text(f, "label")));
            return new ToolResult(
                $"Removed {found} from {photo.Name}. The edited copy, {edited.Name}, is shown to the user with your reply; " +
                "the original is unchanged." +
                (body.GetProperty("area").GetDouble() > 0.25
                    ? " What was removed covered a large part of the photo, so the filled-in area may look soft; say so."
                    : "") +
                " If something is left, such as a shadow, offer to remove that too.",
                Summary: $"Removed {found}", Images: [reference]);
        }
        catch (HttpRequestException)
        {
            throw new ArgumentException("The photo service is not running. Start it with: systemctl --user start leona-photo");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ArgumentException("The photo service took too long. Try again in a moment.");
        }
        finally
        {
            File.Delete(output);
        }
    }
}
