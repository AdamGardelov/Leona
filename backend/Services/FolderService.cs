using System.Text.RegularExpressions;
using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

// Folders the user opens to the file and command tools. Only the user can add them, never the model.
public partial class FolderService(ChatDb db, WorkspaceFiles workspace)
{
    public const string WorkspaceName = "workspace";

    public Task<List<AllowedFolder>> ListAsync(CancellationToken ct) =>
        db.Folders.AsNoTracking().OrderBy(f => f.Name).ToListAsync(ct);

    // Short name to absolute path, always including the workspace.
    public static async Task<Dictionary<string, string>> LoadAsync(ChatDb db, string workspaceRoot,
        CancellationToken ct)
    {
        var folders = await db.Folders.AsNoTracking().ToListAsync(ct);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [WorkspaceName] = workspaceRoot
        };
        foreach (var folder in folders.Where(f => Directory.Exists(f.Path)))
            map[folder.Name] = folder.Path;
        return map;
    }

    public async Task<AllowedFolder> AddAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path.Trim()))
            throw new ArgumentException("Give the folder's full path, for example /home/you/projects/app.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        if (!Directory.Exists(full))
            throw new ArgumentException("That folder does not exist.");
        if (Path.GetPathRoot(full) == full + Path.DirectorySeparatorChar || Path.GetPathRoot(full) == full)
            throw new ArgumentException("A whole drive or the file system root cannot be added.");
        if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Add the real folder, not a symbolic link.");
        if (full == workspace.Root || await db.Folders.AnyAsync(f => f.Path == full, ct))
            throw new ArgumentException("That folder is already available.");

        var baseName = Slug().Replace(Path.GetFileName(full).ToLowerInvariant(), "-").Trim('-');
        if (baseName.Length == 0 || baseName == WorkspaceName)
            baseName = "folder";
        var name = baseName;
        for (var i = 2; await db.Folders.AnyAsync(f => f.Name == name, ct); i++)
            name = $"{baseName}-{i}";

        var folder = new AllowedFolder { Name = name, Path = full };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        await db.Folders.Where(f => f.Id == id).ExecuteDeleteAsync(ct) > 0;

    [GeneratedRegex(@"[^a-z0-9_-]+")]
    private static partial Regex Slug();
}
