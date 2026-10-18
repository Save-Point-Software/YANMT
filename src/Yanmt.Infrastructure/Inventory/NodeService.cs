using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Core.Inventory;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;

namespace Yanmt.Infrastructure.Inventory;

/// <summary>Node inventory: CRUD, tags, and CSV import/export. All writes go through the audit interceptor.</summary>
public sealed class NodeService(IDbContextFactory<YanmtDbContext> dbFactory, TimeProvider clock)
{
    public async Task<List<NodeListItem>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.Nodes.AsNoTracking()
            .Include(n => n.Tags).ThenInclude(nt => nt.Tag)
            .OrderBy(n => n.Hostname)
            .ToListAsync(cancellationToken);

        var states = await db.StatusSources.AsNoTracking()
            .Where(s => s.Enabled)
            .Select(s => new { s.NodeId, s.State })
            .ToListAsync(cancellationToken);
        var stateByNode = states
            .GroupBy(s => s.NodeId)
            .ToDictionary(g => g.Key, g => StatusRollup.Worst(g.Select(s => s.State)));

        return nodes.Select(n => new NodeListItem(
                n.Id,
                n.Hostname,
                n.ManagementIp,
                n.Site,
                TagNames(n),
                n.Icon,
                n.Enabled,
                n.IcmpEnabled,
                n.SnmpEnabled,
                n.SshEnabled,
                n.Enabled ? stateByNode.GetValueOrDefault(n.Id, Severity.Unknown) : Severity.Unknown))
            .ToList();
    }

    public async Task<NodeEditModel?> GetForEditAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var n = await db.Nodes.AsNoTracking()
            .Include(x => x.Tags).ThenInclude(nt => nt.Tag)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return n is null
            ? null
            : new NodeEditModel
            {
                Hostname = n.Hostname,
                ManagementIp = n.ManagementIp,
                Site = n.Site,
                Tags = string.Join("; ", TagNames(n)),
                Icon = n.Icon,
                Enabled = n.Enabled,
                IcmpEnabled = n.IcmpEnabled,
                SnmpEnabled = n.SnmpEnabled,
                SshEnabled = n.SshEnabled,
                SnmpProfileId = n.SnmpProfileId,
                SshProfileId = n.SshProfileId,
                SshHostKey = n.SshHostKeyFingerprint is null ? null : $"{n.SshHostKeyAlgorithm} {n.SshHostKeyFingerprint}",
            };
    }

    public async Task<List<string>> AllTagsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tags.AsNoTracking().OrderBy(t => t.Name).Select(t => t.Name).ToListAsync(cancellationToken);
    }

    public async Task<List<string>> AllSitesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Nodes.AsNoTracking()
            .Where(n => n.Site != null)
            .Select(n => n.Site!)
            .Distinct()
            .OrderBy(s => s)
            .ToListAsync(cancellationToken);
    }

    public Task<SaveResult> CreateAsync(NodeEditModel model, CancellationToken cancellationToken = default) =>
        SaveAsync(null, model, cancellationToken);

    public Task<SaveResult> UpdateAsync(int id, NodeEditModel model, CancellationToken cancellationToken = default) =>
        SaveAsync(id, model, cancellationToken);

    public async Task<bool> DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.Include(n => n.Tags).FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (node is null)
        {
            return false;
        }

        db.Nodes.Remove(node);
        await db.SaveChangesAsync(cancellationToken);
        await PruneOrphanTagsAsync(db, cancellationToken);
        return true;
    }

    public async Task<string> ExportCsvAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var nodes = await db.Nodes.AsNoTracking()
            .Include(n => n.Tags).ThenInclude(nt => nt.Tag)
            .Include(n => n.SnmpProfile)
            .Include(n => n.SshProfile)
            .ToListAsync(cancellationToken);
        return NodeCsv.Export(nodes.Select(ToCsvRecord));
    }

    /// <summary>Classifies each CSV row against current inventory without changing anything.</summary>
    public async Task<ImportPreview> PreviewImportAsync(NodeCsvParseResult parsed, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await LoadByHostnameAsync(db, tracked: false, cancellationToken);
        return BuildPreview(parsed, existing, await LoadProfilesAsync(db, cancellationToken));
    }

    /// <summary>
    /// Applies a CSV import in one transaction: rows matching an existing hostname update it,
    /// others create new nodes. Nodes missing from the file are left alone (import never deletes).
    /// Refuses to apply anything if any row has an error.
    /// </summary>
    public async Task<ImportResult> ApplyImportAsync(NodeCsvParseResult parsed, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var existing = await LoadByHostnameAsync(db, tracked: true, cancellationToken);
        var profiles = await LoadProfilesAsync(db, cancellationToken);
        var preview = BuildPreview(parsed, existing, profiles);
        if (!preview.CanApply)
        {
            throw new InvalidOperationException("The import has errors; fix them and upload the file again.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var tagCache = await db.Tags.ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        int created = 0, updated = 0, unchanged = 0;

        foreach (var row in parsed.Rows)
        {
            var rec = row.Record!;
            if (existing.TryGetValue(rec.Hostname, out var node))
            {
                if (Matches(node, rec, profiles))
                {
                    unchanged++;
                    continue;
                }

                updated++;
            }
            else
            {
                node = new Node { Hostname = rec.Hostname, ManagementIp = rec.ManagementIp, CreatedUtc = now };
                db.Nodes.Add(node);
                created++;
            }

            node.ManagementIp = rec.ManagementIp;
            node.Site = rec.Site;
            node.Icon = rec.Icon;
            node.Enabled = rec.Enabled;
            node.IcmpEnabled = rec.IcmpEnabled;
            node.SnmpEnabled = rec.SnmpEnabled;
            node.SshEnabled = rec.SshEnabled;
            node.SnmpProfileId = profiles.Resolve(rec.SnmpProfile);
            node.SshProfileId = profiles.Resolve(rec.SshProfile);
            node.UpdatedUtc = now;
            SyncTags(db, node, rec.Tags, tagCache);
        }

        await db.SaveChangesAsync(cancellationToken);
        await PruneOrphanTagsAsync(db, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new ImportResult(created, updated, unchanged);
    }

    private async Task<SaveResult> SaveAsync(int? id, NodeEditModel model, CancellationToken cancellationToken)
    {
        if (!NodeRules.TryNormalizeIp(model.ManagementIp, out var ip))
        {
            return SaveResult.Fail(nameof(model.ManagementIp), NodeRules.ValidateManagementIp(model.ManagementIp)!);
        }

        var hostname = model.Hostname.Trim();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Nodes.AnyAsync(n => n.Hostname == hostname && n.Id != id, cancellationToken))
        {
            return SaveResult.Fail(nameof(model.Hostname), $"A node named '{hostname}' already exists.");
        }

        foreach (var (profileId, kind, field) in new[]
                 {
                     (model.SnmpProfileId, CredentialKind.SnmpV3, nameof(model.SnmpProfileId)),
                     (model.SshProfileId, CredentialKind.Ssh, nameof(model.SshProfileId)),
                 })
        {
            if (profileId is not null && !await db.CredentialProfiles.AnyAsync(p => p.Id == profileId && p.Kind == kind, cancellationToken))
            {
                return SaveResult.Fail(field, "That credential profile no longer exists.");
            }
        }

        var now = clock.GetUtcNow().UtcDateTime;
        Node? node;
        if (id is null)
        {
            node = new Node { Hostname = hostname, ManagementIp = ip, CreatedUtc = now };
            db.Nodes.Add(node);
        }
        else
        {
            node = await db.Nodes.Include(n => n.Tags).ThenInclude(nt => nt.Tag).FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
            if (node is null)
            {
                return SaveResult.Fail(null, "This node no longer exists.");
            }
        }

        node.Hostname = hostname;
        node.ManagementIp = ip;
        node.Site = string.IsNullOrWhiteSpace(model.Site) ? null : model.Site.Trim();
        node.Icon = model.Icon;
        node.Enabled = model.Enabled;
        node.IcmpEnabled = model.IcmpEnabled;
        node.SnmpEnabled = model.SnmpEnabled;
        node.SshEnabled = model.SshEnabled;
        node.SnmpProfileId = model.SnmpProfileId;
        node.SshProfileId = model.SshProfileId;
        node.UpdatedUtc = now;

        var tagCache = await db.Tags.ToDictionaryAsync(t => t.Name, StringComparer.OrdinalIgnoreCase, cancellationToken);
        SyncTags(db, node, NodeRules.ParseTags(model.Tags), tagCache);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // SQLITE_CONSTRAINT: lost a race with another admin creating the same hostname.
            return SaveResult.Fail(nameof(model.Hostname), $"A node named '{hostname}' already exists.");
        }

        await PruneOrphanTagsAsync(db, cancellationToken);
        return SaveResult.Success(node.Id);
    }

    private static ImportPreview BuildPreview(NodeCsvParseResult parsed, IReadOnlyDictionary<string, Node> existing, ProfileLookup profiles)
    {
        var rows = parsed.Rows.Select(row =>
        {
            if (!row.IsValid)
            {
                return new ImportPreviewRow(row.Line, row.Record?.Hostname, ImportAction.Error, row.Errors);
            }

            var rec = row.Record!;
            var profileErrors = new[] { profiles.Check(rec.SnmpProfile, CredentialKind.SnmpV3), profiles.Check(rec.SshProfile, CredentialKind.Ssh) }
                .OfType<string>()
                .ToList();
            if (profileErrors.Count > 0)
            {
                return new ImportPreviewRow(row.Line, rec.Hostname, ImportAction.Error, profileErrors);
            }

            if (!existing.TryGetValue(rec.Hostname, out var node))
            {
                return new ImportPreviewRow(row.Line, rec.Hostname, ImportAction.Create, []);
            }

            return Matches(node, rec, profiles)
                ? new ImportPreviewRow(row.Line, rec.Hostname, ImportAction.Unchanged, [])
                : new ImportPreviewRow(row.Line, rec.Hostname, ImportAction.Update, DescribeDiff(node, rec, profiles));
        }).ToList();

        return new ImportPreview(rows, parsed.FileErrors);
    }

    private static async Task<Dictionary<string, Node>> LoadByHostnameAsync(YanmtDbContext db, bool tracked, CancellationToken cancellationToken)
    {
        var query = db.Nodes.Include(n => n.Tags).ThenInclude(nt => nt.Tag).AsQueryable();
        if (!tracked)
        {
            query = query.AsNoTracking();
        }

        return await query.ToDictionaryAsync(n => n.Hostname, StringComparer.OrdinalIgnoreCase, cancellationToken);
    }

    private static bool Matches(Node node, NodeCsvRecord rec, ProfileLookup profiles) => DescribeDiff(node, rec, profiles).Count == 0;

    private static List<string> DescribeDiff(Node node, NodeCsvRecord rec, ProfileLookup profiles)
    {
        var diffs = new List<string>();
        Compare("management IP", node.ManagementIp, rec.ManagementIp);
        Compare("site", node.Site, rec.Site);
        Compare("icon", node.Icon.ToString(), rec.Icon.ToString());
        Compare("enabled", node.Enabled, rec.Enabled);
        Compare("ICMP", node.IcmpEnabled, rec.IcmpEnabled);
        Compare("SNMP", node.SnmpEnabled, rec.SnmpEnabled);
        Compare("SSH", node.SshEnabled, rec.SshEnabled);
        Compare("SNMP profile", profiles.NameOf(node.SnmpProfileId), profiles.Canonical(rec.SnmpProfile));
        Compare("SSH profile", profiles.NameOf(node.SshProfileId), profiles.Canonical(rec.SshProfile));

        var oldTags = TagNames(node);
        if (!new HashSet<string>(oldTags, StringComparer.OrdinalIgnoreCase).SetEquals(rec.Tags))
        {
            diffs.Add($"tags: '{string.Join("; ", oldTags)}' → '{string.Join("; ", rec.Tags)}'");
        }

        return diffs;

        void Compare<T>(string name, T oldValue, T newValue)
        {
            if (!EqualityComparer<T>.Default.Equals(oldValue, newValue))
            {
                diffs.Add($"{name}: '{oldValue}' → '{newValue}'");
            }
        }
    }

    private static void SyncTags(YanmtDbContext db, Node node, IReadOnlyList<string> wanted, Dictionary<string, Tag> tagCache)
    {
        var wantedSet = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
        foreach (var link in node.Tags.Where(nt => !wantedSet.Contains(nt.Tag.Name)).ToList())
        {
            node.Tags.Remove(link);
            db.NodeTags.Remove(link);
        }

        var have = new HashSet<string>(node.Tags.Select(nt => nt.Tag.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var name in wanted.Where(w => !have.Contains(w)))
        {
            if (!tagCache.TryGetValue(name, out var tag))
            {
                tag = new Tag { Name = name };
                db.Tags.Add(tag);
                tagCache[name] = tag;
            }

            node.Tags.Add(new NodeTag { Node = node, Tag = tag });
        }
    }

    private static async Task PruneOrphanTagsAsync(YanmtDbContext db, CancellationToken cancellationToken) =>
        await db.Tags.Where(t => !t.Nodes.Any()).ExecuteDeleteAsync(cancellationToken);

    private static IReadOnlyList<string> TagNames(Node n) =>
        n.Tags.Select(nt => nt.Tag.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private static NodeCsvRecord ToCsvRecord(Node n) =>
        new(n.Hostname, n.ManagementIp, n.Site, TagNames(n), n.Icon, n.Enabled, n.IcmpEnabled, n.SnmpEnabled, n.SshEnabled,
            n.SnmpProfile?.Name, n.SshProfile?.Name);

    private static async Task<ProfileLookup> LoadProfilesAsync(YanmtDbContext db, CancellationToken cancellationToken) =>
        new(await db.CredentialProfiles.AsNoTracking().Select(p => new ProfileRef(p.Id, p.Name, p.Kind)).ToListAsync(cancellationToken));

    private sealed record ProfileRef(int Id, string Name, CredentialKind Kind);

    /// <summary>Credential profiles by name (case-insensitive), for CSV import.</summary>
    private sealed class ProfileLookup(IReadOnlyList<ProfileRef> profiles)
    {
        private readonly Dictionary<string, ProfileRef> _byName = profiles.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, ProfileRef> _byId = profiles.ToDictionary(p => p.Id);

        public string? Check(string? name, CredentialKind kind)
        {
            var column = kind == CredentialKind.Ssh ? "ssh_profile" : "snmp_profile";
            if (name is null)
            {
                return null;
            }

            if (!_byName.TryGetValue(name, out var p))
            {
                return $"{column}: no credential profile named '{name}'. Create it under Credentials first.";
            }

            return p.Kind == kind ? null : $"{column}: '{name}' is an {p.Kind} profile.";
        }

        /// <summary>Only called after <see cref="Check"/> passed for every row.</summary>
        public int? Resolve(string? name) => name is null ? null : _byName[name].Id;

        public string? NameOf(int? id) => id is null ? null : _byId.GetValueOrDefault(id.Value)?.Name;

        public string? Canonical(string? name) => name is null ? null : _byName.GetValueOrDefault(name)?.Name ?? name;
    }
}
