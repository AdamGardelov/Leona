using System.Text.Json;
using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public record ToolView(string Name, string Arguments, string Status, IReadOnlyList<SourceLink> Sources,
    IReadOnlyList<string> Drafts, IReadOnlyList<MailService.MailItem> Mails);

public record MessageView(
    int Id,
    string Role,
    string Content,
    string Thinking,
    bool Complete,
    bool Truncated,
    IReadOnlyList<ToolView> Tools,
    IReadOnlyList<AttachmentRef> Attachments,
    string Model);

public record ConversationUpdate(bool? Pinned, bool? Archived, string? Title);

public class ConversationService(ChatDb db)
{
    // Pinned conversations first, then newest. Archived ones are listed only on request.
    public Task<List<Conversation>> ListAsync(bool archived, CancellationToken ct) =>
        db.Conversations.AsNoTracking().Where(c => c.Archived == archived)
            .OrderByDescending(c => c.Pinned).ThenByDescending(c => c.UpdatedAt).ThenByDescending(c => c.Id)
            .ToListAsync(ct);

    public async Task<Conversation> CreateAsync(CancellationToken ct)
    {
        var conversation = new Conversation { Title = "New conversation" };
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    public async Task<Conversation?> UpdateAsync(int id, ConversationUpdate update, CancellationToken ct)
    {
        var conversation = await db.Conversations.FindAsync([id], ct);
        if (conversation is null)
            return null;

        if (update.Pinned is { } pinned)
            conversation.Pinned = pinned;
        if (update.Archived is { } archived)
        {
            conversation.Archived = archived;
            // Archiving takes a conversation out of the pinned list.
            if (archived)
                conversation.Pinned = false;
        }

        if (!string.IsNullOrWhiteSpace(update.Title))
            conversation.Title = update.Title.Trim()[..Math.Min(update.Title.Trim().Length, 60)];
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    // The user has seen the chat; its replies so far are no longer unread.
    public async Task<bool> MarkReadAsync(int id, CancellationToken ct) =>
        await db.Conversations.Where(c => c.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReadAt, DateTime.UtcNow), ct) > 0;

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        // SQLite foreign keys cascade to messages and saved tool evidence.
        return await db.Conversations.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    // The newest answer in a chat, for notifications and Siri.
    public static async Task<string> LatestReplyAsync(ChatDb db, int conversationId, CancellationToken ct) =>
        await db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId && m.Role == "assistant")
            .OrderByDescending(m => m.Id).Select(m => m.Content).FirstOrDefaultAsync(ct) ?? "";

    // Messages with the saved tool steps of each turn attached to its assistant reply.
    public async Task<List<MessageView>> GetMessagesAsync(int id, CancellationToken ct)
    {
        var messages = await db.Messages.AsNoTracking().Where(m => m.ConversationId == id)
            .OrderBy(m => m.Id).ToListAsync(ct);
        var evidence = (await db.ToolEvidence.AsNoTracking().Where(e => e.ConversationId == id)
                .OrderBy(e => e.Id).ToListAsync(ct))
            .ToLookup(e => e.UserMessageId);
        var views = new List<MessageView>();
        int? turn = null;
        foreach (var message in messages)
        {
            IReadOnlyList<ToolView> steps = [];
            if (message.Role == "user")
                turn = message.Id;
            else if (turn is { } userMessageId)
            {
                steps = evidence[userMessageId].Select(e => new ToolView(e.ToolName, e.Arguments,
                    ToolStatus.FromContent(e.Excerpt), Sources(e.SourcesJson), MailService.DraftsIn(e.Excerpt),
                    MailService.MessagesIn(e.Excerpt))).ToList();
                turn = null;
            }

            views.Add(new MessageView(message.Id, message.Role, message.Content, message.Thinking, message.Complete,
                message.Truncated, steps, ChatService.AttachmentsOf(message.AttachmentsJson), message.Model));
        }

        return views;
    }

    private static IReadOnlyList<SourceLink> Sources(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<SourceLink>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
