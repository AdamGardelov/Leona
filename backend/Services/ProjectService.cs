using Harness.Contracts;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public record ProjectInput(string? Name, string? Instructions);

public record ProjectView(int Id, string Name, string Instructions, int Chats, IReadOnlyList<AttachmentRef> Files);

public class ProjectService(ChatDb db)
{
    public const int MaxInstructions = 4000;

    public async Task<List<ProjectView>> ListAsync(CancellationToken ct)
    {
        var projects = await db.Projects.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var chats = await db.Conversations.AsNoTracking().Where(c => c.ProjectId != null && !c.Archived)
            .GroupBy(c => c.ProjectId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var files = (await db.Uploads.AsNoTracking().Where(u => u.ProjectId != null).OrderBy(u => u.Name).ToListAsync(ct))
            .ToLookup(u => u.ProjectId!.Value);
        return projects.Select(p => new ProjectView(p.Id, p.Name, p.Instructions,
            chats.FirstOrDefault(c => c.Key == p.Id)?.Count ?? 0,
            files[p.Id].Select(UploadStore.Reference).ToList())).ToList();
    }

    public async Task<Project> SaveAsync(int? id, ProjectInput input, CancellationToken ct)
    {
        var name = input.Name?.Trim() ?? "";
        var instructions = input.Instructions?.Trim() ?? "";
        if (name.Length is 0 or > 60)
            throw new ArgumentException("A project needs a name of at most 60 characters.");
        if (instructions.Length > MaxInstructions)
            throw new ArgumentException($"Project instructions can be at most {MaxInstructions} characters.");
        var project = id is { } existing
            ? await db.Projects.FindAsync([existing], ct) ?? throw new KeyNotFoundException()
            : db.Projects.Add(new Project()).Entity;
        project.Name = name;
        project.Instructions = instructions;
        await db.SaveChangesAsync(ct);
        return project;
    }

    // The chats stay and move back to the main list; the project's files go with it.
    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.Projects.Where(p => p.Id == id).ExecuteDeleteAsync(ct) > 0;

    // An uploaded document becomes one of the project's files, searchable from its chats.
    public async Task<AttachmentRef> AddFileAsync(int projectId, Guid uploadId, CancellationToken ct)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, ct))
            throw new KeyNotFoundException();
        var upload = await db.Uploads.FindAsync([uploadId], ct) ?? throw new KeyNotFoundException();
        if (upload.Kind != UploadKind.Document)
            throw new ArgumentException("Projects keep documents: PDF, Word and text files.");
        upload.ProjectId = projectId;
        await db.SaveChangesAsync(ct);
        return UploadStore.Reference(upload);
    }

    public async Task<bool> RemoveFileAsync(int projectId, Guid uploadId, CancellationToken ct) =>
        await db.Uploads.Where(u => u.Id == uploadId && u.ProjectId == projectId).ExecuteDeleteAsync(ct) > 0;

    // The instructions and file count a chat in the project is given.
    public async Task<(Project Project, int Files)?> ForChatAsync(int? projectId, CancellationToken ct)
    {
        if (projectId is not { } id || await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } project)
            return null;
        return (project, await db.Uploads.CountAsync(u => u.ProjectId == id, ct));
    }
}
