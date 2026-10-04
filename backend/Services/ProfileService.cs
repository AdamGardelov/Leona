using Harness.Data;
using Harness.Models;
using Microsoft.EntityFrameworkCore;

namespace Harness.Services;

public record ProfileView(int Id, string Name, bool Owner);

// Profiles for the people in the household. The computer chooses one with a cookie; a paired device is
// tied to the profile it was paired for.
public class ProfileService(ChatDb db, CurrentProfile current, RemoteAccess remote, UploadStore uploads,
    PersonalFiles personal)
{
    public const string CookieName = "leona_profile";

    public static ProfileView View(Profile profile) => new(profile.Id, profile.Name, profile.Owner);

    public Task<List<ProfileView>> ListAsync(CancellationToken ct) =>
        db.Profiles.AsNoTracking().OrderByDescending(p => p.Owner).ThenBy(p => p.Id)
            .Select(p => new ProfileView(p.Id, p.Name, p.Owner)).ToListAsync(ct);

    // Signs the request in as the given profile, or as the owner when the computer has not chosen one.
    public async Task<bool> UseAsync(int? id, bool fallBackToOwner, CancellationToken ct)
    {
        var profile = id is { } wanted ? await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == wanted, ct) : null;
        if (profile is null && fallBackToOwner)
            profile = await db.Profiles.AsNoTracking().OrderByDescending(p => p.Owner).ThenBy(p => p.Id)
                .FirstOrDefaultAsync(ct);
        if (profile is null)
            return false;

        current.Id = profile.Id;
        current.Owner = profile.Owner;
        current.Name = profile.Name;
        return true;
    }

    public static string Validate(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Give the profile a name."
        : name.Trim().Length > 30 ? "Profile names can be at most 30 characters."
        : "";

    public async Task<ProfileView> SaveAsync(int? id, string name, CancellationToken ct)
    {
        var error = Validate(name);
        if (error.Length > 0)
            throw new ArgumentException(error);
        name = name.Trim();
        if (await db.Profiles.AnyAsync(p => p.Id != id && p.Name.ToLower() == name.ToLower(), ct))
            throw new ArgumentException("Another profile already has that name.");

        var profile = id is { } existing ? await db.Profiles.FindAsync([existing], ct) : new Profile();
        if (profile is null)
            throw new KeyNotFoundException();
        profile.Name = name;
        if (id is null)
            db.Profiles.Add(profile);
        await db.SaveChangesAsync(ct);
        return View(profile);
    }

    // Removes a profile with everything that belongs to it. The owner's profile cannot be removed.
    public async Task DeleteAsync(int id, CancellationToken ct)
    {
        var profile = await db.Profiles.FindAsync([id], ct) ?? throw new KeyNotFoundException();
        if (profile.Owner)
            throw new ArgumentException("The computer owner's profile cannot be removed.");
        if (await db.Runs.IgnoreQueryFilters().AnyAsync(r => RunStatus.Active.Contains(r.Status) &&
                db.Conversations.IgnoreQueryFilters().Any(c => c.Id == r.ConversationId && c.ProfileId == id), ct))
            throw new InvalidOperationException("Wait until this profile's running chats have finished.");

        remote.ForgetPairing(id);
        await remote.RemoveDevicesAsync(id, ct);
        await db.Conversations.IgnoreQueryFilters().Where(c => c.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.Uploads.IgnoreQueryFilters().Where(u => u.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.Accounts.IgnoreQueryFilters().Where(a => a.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.ScheduledTasks.IgnoreQueryFilters().Where(t => t.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.Watches.IgnoreQueryFilters().Where(w => w.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.Notifications.IgnoreQueryFilters().Where(n => n.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.PushSubscriptions.IgnoreQueryFilters().Where(s => s.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.Memories.IgnoreQueryFilters().Where(m => m.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.Skills.IgnoreQueryFilters().Where(s => s.ProfileId == id).ExecuteDeleteAsync(ct);
        await db.TrustedSites.IgnoreQueryFilters().Where(s => s.ProfileId == id).ExecuteDeleteAsync(ct);
        db.Profiles.Remove(profile);
        await db.SaveChangesAsync(ct);
        uploads.DeleteProfile(id);
        personal.DeleteProfile(id);
    }
}
