using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Core.Inventory;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Data;

public class YanmtDbContext(DbContextOptions<YanmtDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CredentialProfile> CredentialProfiles => Set<CredentialProfile>();
    public DbSet<NodeTag> NodeTags => Set<NodeTag>();
    public DbSet<StatusSource> StatusSources => Set<StatusSource>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Setting> Settings => Set<Setting>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    /// <summary>Audit rows captured by <see cref="AuditInterceptor"/> between SavingChanges and SavedChanges.</summary>
    internal List<AuditInterceptor.PendingAudit> PendingAudits { get; } = [];

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored by name so the DB stays readable and reordering an enum can't corrupt data.
        configurationBuilder.Properties<Severity>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<StatusSourceType>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<NodeIconType>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<CredentialKind>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<SnmpAuthProtocol>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<SnmpPrivProtocol>().HaveConversion<string>().HaveMaxLength(16);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Node>(e =>
        {
            e.Property(n => n.Hostname).HasMaxLength(NodeRules.MaxHostnameLength).UseCollation("NOCASE");
            e.HasIndex(n => n.Hostname).IsUnique();
            e.Property(n => n.ManagementIp).HasMaxLength(45);
            e.HasIndex(n => n.ManagementIp);
            e.Property(n => n.Site).HasMaxLength(NodeRules.MaxSiteLength);
            e.HasIndex(n => n.Site);
            e.Property(n => n.SshHostKeyAlgorithm).HasMaxLength(64);
            e.Property(n => n.SshHostKeyFingerprint).HasMaxLength(100);

            // A profile in use can't be deleted; CredentialService reports which nodes use it.
            e.HasOne(n => n.SnmpProfile).WithMany().HasForeignKey(n => n.SnmpProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(n => n.SshProfile).WithMany().HasForeignKey(n => n.SshProfileId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CredentialProfile>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(CredentialRules.MaxNameLength).UseCollation("NOCASE");
            e.HasIndex(p => p.Name).IsUnique();
            e.Property(p => p.Username).HasMaxLength(CredentialRules.MaxUsernameLength);
        });

        modelBuilder.Entity<Tag>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(NodeRules.MaxTagLength).UseCollation("NOCASE");
            e.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<NodeTag>(e =>
        {
            e.HasKey(nt => new { nt.NodeId, nt.TagId });
            e.HasOne(nt => nt.Node).WithMany(n => n.Tags).HasForeignKey(nt => nt.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(nt => nt.Tag).WithMany(t => t.Nodes).HasForeignKey(nt => nt.TagId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StatusSource>(e =>
        {
            e.Property(s => s.SourceKey).HasMaxLength(64);
            e.HasIndex(s => new { s.NodeId, s.SourceType, s.SourceKey }).IsUnique();
            e.HasOne(s => s.Node).WithMany().HasForeignKey(s => s.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Event>(e =>
        {
            e.Property(x => x.SourceKey).HasMaxLength(64);
            e.Property(x => x.Message).HasMaxLength(1000);
            e.Property(x => x.AcknowledgedBy).HasMaxLength(256);
            e.HasIndex(x => x.TimestampUtc);
            e.HasIndex(x => new { x.NodeId, x.TimestampUtc });

            // Events outlive the node they describe; the message carries the hostname.
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Setting>(e =>
        {
            e.HasKey(s => s.Key);
            e.Property(s => s.Key).HasMaxLength(100);
        });

        modelBuilder.Entity<AuditEntry>(e =>
        {
            e.Property(a => a.User).HasMaxLength(256);
            e.Property(a => a.Action).HasMaxLength(16);
            e.Property(a => a.EntityType).HasMaxLength(64);
            e.Property(a => a.EntityKey).HasMaxLength(128);
            e.HasIndex(a => a.TimestampUtc);
            e.HasIndex(a => new { a.EntityType, a.EntityKey });
        });
    }
}
