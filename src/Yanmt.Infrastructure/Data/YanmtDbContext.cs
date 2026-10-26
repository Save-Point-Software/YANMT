using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Core.Inventory;
using Yanmt.Core.Maps;
using Yanmt.Core.Monitors;
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
    public DbSet<IcmpSample> IcmpSamples => Set<IcmpSample>();
    public DbSet<MetricSample> MetricSamples => Set<MetricSample>();
    public DbSet<NodeSystemInfo> NodeSystemInfo => Set<NodeSystemInfo>();
    public DbSet<Map> Maps => Set<Map>();
    public DbSet<MonitorDefinition> MonitorDefinitions => Set<MonitorDefinition>();
    public DbSet<MonitorAssignment> MonitorAssignments => Set<MonitorAssignment>();
    public DbSet<MonitorInstance> MonitorInstances => Set<MonitorInstance>();
    public DbSet<MapElement> MapElements => Set<MapElement>();
    public DbSet<MapLink> MapLinks => Set<MapLink>();

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
        configurationBuilder.Properties<MapElementKind>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<OidMode>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<ValueMode>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<RuleOperator>().HaveConversion<string>().HaveMaxLength(16);
        configurationBuilder.Properties<AssignmentScope>().HaveConversion<string>().HaveMaxLength(16);
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
            e.Property(s => s.DisplayName).HasMaxLength(200);
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

        modelBuilder.Entity<MetricSample>(e =>
        {
            e.HasIndex(x => new { x.StatusSourceId, x.TimestampUtc });
            e.HasOne(x => x.StatusSource).WithMany().HasForeignKey(x => x.StatusSourceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<NodeSystemInfo>(e =>
        {
            e.HasKey(x => x.NodeId);
            e.HasOne(x => x.Node).WithOne().HasForeignKey<NodeSystemInfo>(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.SysDescr).HasMaxLength(1024);
            e.Property(x => x.SysObjectId).HasMaxLength(256);
            e.Property(x => x.SysName).HasMaxLength(255);
            e.Property(x => x.SysLocation).HasMaxLength(255);
            e.Property(x => x.SysContact).HasMaxLength(255);
            e.Property(x => x.EngineId).HasMaxLength(64);
            e.Property(x => x.MemoryPool).HasMaxLength(64);
        });

        modelBuilder.Entity<IcmpSample>(e =>
        {
            e.HasIndex(x => new { x.NodeId, x.TimestampUtc });
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Map>(e =>
        {
            e.Property(m => m.Name).HasMaxLength(MapRules.MaxNameLength).UseCollation("NOCASE");
            e.HasIndex(m => m.Name).IsUnique();
            e.Property(m => m.BackgroundImage).HasMaxLength(100);
            e.HasOne(m => m.ParentMap).WithMany().HasForeignKey(m => m.ParentMapId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MapElement>(e =>
        {
            e.Property(x => x.LabelSourceKey).HasMaxLength(64);
            e.HasOne(x => x.Map).WithMany(m => m.Elements).HasForeignKey(x => x.MapId).OnDelete(DeleteBehavior.Cascade);

            // Deleting a node or a map removes its icons from every map (and, through them, their links).
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.TargetMap).WithMany().HasForeignKey(x => x.TargetMapId).OnDelete(DeleteBehavior.Cascade);

            // A node or a submap appears at most once per map. SQLite treats NULLs as distinct, so
            // these don't collide between node and submap elements.
            e.HasIndex(x => new { x.MapId, x.NodeId }).IsUnique();
            e.HasIndex(x => new { x.MapId, x.TargetMapId }).IsUnique();
        });

        modelBuilder.Entity<MapLink>(e =>
        {
            e.Property(x => x.Label).HasMaxLength(MapRules.MaxLinkLabelLength);
            e.HasOne(x => x.Map).WithMany(m => m.Links).HasForeignKey(x => x.MapId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.From).WithMany().HasForeignKey(x => x.FromElementId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.To).WithMany().HasForeignKey(x => x.ToElementId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.MapId, x.FromElementId, x.ToElementId }).IsUnique();
            e.HasOne(x => x.BoundMonitorInstance).WithMany().HasForeignKey(x => x.BoundMonitorInstanceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<MonitorDefinition>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(MonitorValidation.MaxNameLength).UseCollation("NOCASE");
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Description).HasMaxLength(MonitorValidation.MaxDescriptionLength);
            e.Property(x => x.StarterKey).HasMaxLength(64);
            e.Property(x => x.Oid).HasMaxLength(MonitorValidation.MaxOidLength);
            e.Property(x => x.LabelOid).HasMaxLength(MonitorValidation.MaxOidLength);
            e.Property(x => x.DenominatorOid).HasMaxLength(MonitorValidation.MaxOidLength);
            e.Property(x => x.FilterOid).HasMaxLength(MonitorValidation.MaxOidLength);
            e.Property(x => x.FilterValue).HasMaxLength(MonitorValidation.MaxRuleValueLength);
            e.Property(x => x.LabelRegex).HasMaxLength(MonitorValidation.MaxRegexLength);
            e.Property(x => x.Unit).HasMaxLength(MonitorValidation.MaxUnitLength);
            e.Property(x => x.ValueMap).HasMaxLength(4000);
            e.Property(x => x.RulesJson).HasMaxLength(16000);
        });

        modelBuilder.Entity<MonitorAssignment>(e =>
        {
            e.Property(x => x.TagName).HasMaxLength(NodeRules.MaxTagLength).UseCollation("NOCASE");
            e.Property(x => x.InstanceSuffix).HasMaxLength(MonitorValidation.MaxOidLength);
            e.Property(x => x.InstanceLabel).HasMaxLength(255);
            e.HasOne(x => x.Definition).WithMany(d => d.Assignments).HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.DefinitionId, x.Scope, x.NodeId, x.TagName, x.InstanceSuffix });
        });

        modelBuilder.Entity<MonitorInstance>(e =>
        {
            e.Property(x => x.InstanceKey).HasMaxLength(MonitorValidation.MaxOidLength);
            e.Property(x => x.Label).HasMaxLength(255);
            e.HasIndex(x => new { x.DefinitionId, x.NodeId, x.InstanceKey }).IsUnique();
            e.HasOne(x => x.Definition).WithMany().HasForeignKey(x => x.DefinitionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Node).WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
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
