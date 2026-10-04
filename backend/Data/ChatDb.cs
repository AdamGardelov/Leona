using Harness.Models;
using Harness.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Harness.Data;

// Every query is limited to the current profile's rows. System work (no profile) sees all of them.
public class ChatDb(DbContextOptions<ChatDb> options, CurrentProfile? current = null) : DbContext(options)
{
    public int? ProfileId => current?.Id;

    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ToolEvidence> ToolEvidence => Set<ToolEvidence>();

    public DbSet<AgentRun> Runs => Set<AgentRun>();
    public DbSet<RunEvent> RunEvents => Set<RunEvent>();
    public DbSet<RunAction> RunActions => Set<RunAction>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<AllowedFolder> Folders => Set<AllowedFolder>();
    public DbSet<Memory> Memories => Set<Memory>();
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();
    public DbSet<PushKeys> PushKeys => Set<PushKeys>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<Watch> Watches => Set<Watch>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<TrustedSite> TrustedSites => Set<TrustedSite>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<AgentRun>().HasOne<Conversation>().WithMany().HasForeignKey(r => r.ConversationId);
        model.Entity<RunEvent>().HasOne<AgentRun>().WithMany().HasForeignKey(e => e.RunId);
        model.Entity<RunAction>().HasOne<AgentRun>().WithMany().HasForeignKey(a => a.RunId);
        model.Entity<RunEvent>().HasIndex(e => new { e.RunId, e.Id });
        model.Entity<ToolEvidence>().HasOne<Conversation>().WithMany().HasForeignKey(e => e.ConversationId);
        model.Entity<ToolEvidence>().HasOne<Message>().WithMany().HasForeignKey(e => e.UserMessageId);
        model.Entity<Message>().HasOne<Conversation>().WithMany()
            .HasForeignKey(message => message.ConversationId);
        model.Entity<AppSettings>().ToTable("Settings").Property(s => s.Id).ValueGeneratedNever();
        model.Entity<AllowedFolder>().ToTable("AllowedFolders").HasIndex(f => f.Name).IsUnique();
        model.Entity<Memory>().ToTable("Memories");
        model.Entity<DeviceSession>().HasIndex(d => d.TokenHash).IsUnique();
        model.Entity<PushSubscription>().HasIndex(p => p.Endpoint).IsUnique();
        model.Entity<PushKeys>().Property(k => k.Id).ValueGeneratedNever();
        model.Entity<Profile>().HasIndex(p => p.Name).IsUnique();

        model.Entity<Conversation>().HasIndex(c => c.ProfileId);
        model.Entity<Conversation>().HasQueryFilter(c => ProfileId == null || c.ProfileId == ProfileId);
        model.Entity<Message>().HasQueryFilter(m =>
            ProfileId == null || Conversations.Any(c => c.Id == m.ConversationId));
        model.Entity<ToolEvidence>().HasQueryFilter(e =>
            ProfileId == null || Conversations.Any(c => c.Id == e.ConversationId));
        model.Entity<AgentRun>().HasQueryFilter(r =>
            ProfileId == null || Conversations.Any(c => c.Id == r.ConversationId));
        model.Entity<RunEvent>().HasQueryFilter(e => ProfileId == null || Runs.Any(r => r.Id == e.RunId));
        model.Entity<RunAction>().HasQueryFilter(a => ProfileId == null || Runs.Any(r => r.Id == a.RunId));
        Owned<Memory>(model);
        Owned<Upload>(model);
        Owned<Account>(model);
        Owned<Notification>(model);
        Owned<PushSubscription>(model);
        Owned<ScheduledTask>(model);
        Owned<Watch>(model);
        Owned<Skill>(model);
        Owned<TrustedSite>(model);
        model.Entity<TrustedSite>().HasIndex(s => new { s.ProfileId, s.Host }).IsUnique();
        // Devices are listed across profiles on the computer only, so they are not filtered.
        model.Entity<DeviceSession>().HasIndex(d => d.ProfileId);

        // Times are stored in UTC, but SQLite gives them back without a zone; mark them so they reach the
        // browser with one. A task's last run is the exception: it is kept in local time.
        foreach (var entity in model.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                if (entity.ClrType == typeof(ScheduledTask) && property.Name == nameof(ScheduledTask.LastRunAt))
                    continue;
                if (property.ClrType == typeof(DateTime))
                    property.SetValueConverter(s_utc);
                else if (property.ClrType == typeof(DateTime?))
                    property.SetValueConverter(s_utcOrNull);
            }
        }
    }

    private static readonly ValueConverter<DateTime, DateTime> s_utc =
        new(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> s_utcOrNull =
        new(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    private void Owned<T>(ModelBuilder model) where T : class, IProfileOwned
    {
        model.Entity<T>().HasIndex(e => e.ProfileId);
        model.Entity<T>().HasQueryFilter(e => ProfileId == null || e.ProfileId == ProfileId);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampProfiles();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        StampProfiles();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
    }

    // New rows belong to the current profile. System work must set the profile itself.
    private void StampProfiles()
    {
        foreach (var entry in ChangeTracker.Entries<IProfileOwned>())
        {
            if (entry.State != EntityState.Added || entry.Entity.ProfileId != 0)
                continue;
            entry.Entity.ProfileId = ProfileId ??
                                     throw new InvalidOperationException(
                                         $"A new {entry.Entity.GetType().Name} needs a profile.");
        }
    }
}
