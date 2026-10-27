using System.Globalization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Monitoring;
using Yanmt.Core.Monitors;
using Yanmt.Core.Status;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Monitors;

namespace Yanmt.Infrastructure.Monitoring;

/// <summary>A finished poll, queued for the status engine.</summary>
public abstract record PollResult(int NodeId, DateTime TimestampUtc);

/// <summary>An ICMP burst. Result is null when the poll itself failed (not the same as no reply).</summary>
public sealed record IcmpPollResult(int NodeId, DateTime TimestampUtc, IcmpSettings Settings, IcmpResult? Result, string? Error = null)
    : PollResult(NodeId, TimestampUtc);

/// <summary>A default-stats SNMP poll. Reading is null when the poll failed; Failure says why.</summary>
public sealed record SnmpPollResult(
    int NodeId,
    DateTime TimestampUtc,
    Thresholds Cpu,
    Thresholds Memory,
    DefaultStatsReading? Reading,
    FailureCategory? Failure = null,
    string? Error = null)
    : PollResult(NodeId, TimestampUtc);

/// <summary>
/// A finished config backup attempt. <see cref="Kind"/> is set when the config was fetched (and, if
/// it changed, stored); <see cref="Error"/> is set when the attempt failed.
/// </summary>
public sealed record BackupPollResult(int NodeId, DateTime TimestampUtc, ConfigChangeKind? Kind, string? Error = null)
    : PollResult(NodeId, TimestampUtc);

/// <summary>
/// The single consumer of poll results. It evaluates each result, updates StatusSource (with
/// dampening), records samples and state-change events, then refreshes the in-memory cache and
/// tells the UI which nodes changed. Results are applied in batches, one transaction each, so the
/// DB sees a few writes per second however many nodes are polled.
/// </summary>
public sealed class StatusEngine(
    IDbContextFactory<YanmtDbContext> dbFactory,
    StatusCache cache,
    StatusBroadcaster broadcaster,
    TimeProvider clock,
    ILogger<StatusEngine> logger)
{
    private const int MaxBatch = 500;

    private readonly Channel<PollResult> _queue = Channel.CreateBounded<PollResult>(
        new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public ValueTask SubmitAsync(PollResult result, CancellationToken cancellationToken = default) =>
        _queue.Writer.WriteAsync(result, cancellationToken);

    /// <summary>Drains the queue until cancelled. A failing batch is logged and dropped; the loop carries on.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        var batch = new List<PollResult>(MaxBatch);
        while (await reader.WaitToReadAsync(cancellationToken))
        {
            batch.Clear();
            while (batch.Count < MaxBatch && reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            try
            {
                await ApplyAsync(batch, cancellationToken);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Failed to apply {Count} poll result(s); they were dropped", batch.Count);
            }
        }
    }

    private static readonly StatusSourceType[] SnmpSources = [StatusSourceType.Uptime, StatusSourceType.Cpu, StatusSourceType.Memory];

    /// <summary>
    /// Makes StatusSource rows match the inventory: a node that is disabled, or has ICMP turned
    /// off, gets its ICMP source disabled (Unknown, out of the rollup); turning it back on
    /// re-enables it as Unknown until the next poll. SNMP sources are disabled the same way when
    /// SNMP is off or no profile is assigned, but only a successful poll re-enables them, since a
    /// device may legitimately lack the CPU or memory MIB. Then reloads the cache for every node.
    /// </summary>
    public async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var nodes = await db.Nodes.AsNoTracking()
            .Select(n => new
            {
                n.Id,
                Icmp = n.Enabled && n.IcmpEnabled,
                Snmp = n.Enabled && n.SnmpEnabled && n.SnmpProfileId != null,
                Backup = n.Enabled && n.SshEnabled && n.SshProfileId != null,
            })
            .ToDictionaryAsync(n => n.Id, cancellationToken);
        var backupsOn = await ConfigBackupsEnabledAsync(db, cancellationToken);
        var sources = await db.StatusSources.ToListAsync(cancellationToken);

        foreach (var source in sources)
        {
            var node = nodes.GetValueOrDefault(source.NodeId);
            bool wanted;
            if (source.SourceType == StatusSourceType.Icmp)
            {
                wanted = node?.Icmp ?? false;
            }
            else if (source.SourceType == StatusSourceType.Backup && source.Enabled && !(backupsOn && node?.Backup == true))
            {
                wanted = false; // only switched off here, so a stale Warning can't colour a node that is no longer backed up
            }
            else if (SnmpSources.Contains(source.SourceType) && source.Enabled && node?.Snmp != true)
            {
                wanted = false; // only switched off here; a successful poll switches it back on
            }
            else
            {
                continue;
            }

            if (source.Enabled == wanted)
            {
                continue;
            }

            source.Enabled = wanted;
            source.State = Severity.Unknown;
            source.PendingState = null;
            source.PendingCount = 0;
            source.Value = null;
            source.ValueText = null;
            source.SinceUtc = now;
            source.LastUpdateUtc = null; // the first poll after re-enabling isn't a "state change"
        }

        await SyncMonitorsAsync(db, sources, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Publish(await cache.RefreshAsync(db, null, cancellationToken));
    }

    /// <summary>The global config backup switch, read straight from the settings row (default on).</summary>
    private static async Task<bool> ConfigBackupsEnabledAsync(YanmtDbContext db, CancellationToken cancellationToken)
    {
        var key = Settings.SettingKeys.ConfigBackup.Key;
        var json = await db.Settings.AsNoTracking().Where(x => x.Key == key).Select(x => x.JsonValue).FirstOrDefaultAsync(cancellationToken);
        if (json is null)
        {
            return Settings.SettingKeys.ConfigBackup.Default.Enabled;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<ConfigBackupSettings>(json, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Enabled
                ?? Settings.SettingKeys.ConfigBackup.Default.Enabled;
        }
        catch (System.Text.Json.JsonException)
        {
            return Settings.SettingKeys.ConfigBackup.Default.Enabled;
        }
    }

    /// <summary>
    /// Custom monitors: instances whose assignment, tag membership or monitor is gone are deleted
    /// with their state and history. Instances that still apply but aren't polled right now (the
    /// monitor or node is disabled, SNMP is off) keep their history, with their state switched off.
    /// </summary>
    private static async Task SyncMonitorsAsync(YanmtDbContext db, List<StatusSource> sources, DateTime now, CancellationToken cancellationToken)
    {
        var applicable = await MonitorPlanner.PlanAsync(db, cancellationToken, activeOnly: false);
        var active = await MonitorPlanner.PlanAsync(db, cancellationToken, activeOnly: true);
        static bool Covers(Dictionary<int, List<MonitorWork>> plan, MonitorInstance i) =>
            plan.TryGetValue(i.NodeId, out var works) && works.FirstOrDefault(w => w.Spec.Id == i.DefinitionId) is { } w
            && (w.AllRows || w.Instances.ContainsKey(i.InstanceKey));

        var instances = await db.MonitorInstances.ToListAsync(cancellationToken);
        var stale = instances.Where(i => !Covers(applicable, i)).ToList();
        db.MonitorInstances.RemoveRange(stale);
        var live = instances.Except(stale).ToDictionary(i => i.Id.ToString(CultureInfo.InvariantCulture));

        foreach (var source in sources.Where(s => s.SourceType == StatusSourceType.Custom))
        {
            if (!live.TryGetValue(source.SourceKey, out var instance))
            {
                db.StatusSources.Remove(source); // its samples cascade
            }
            else if (source.Enabled && !Covers(active, instance))
            {
                source.Enabled = false;
                source.State = Severity.Unknown;
                source.PendingState = null;
                source.PendingCount = 0;
                source.ValueText = null;
                source.SinceUtc = now;
                source.LastUpdateUtc = null;
            }
        }
    }

    internal async Task ApplyAsync(IReadOnlyList<PollResult> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var nodeIds = batch.Select(r => r.NodeId).Distinct().ToList();
        var nodes = await db.Nodes.AsNoTracking()
            .Where(n => nodeIds.Contains(n.Id))
            .Select(n => new { n.Id, n.Hostname, n.Enabled, n.IcmpEnabled, Snmp = n.SnmpEnabled && n.SnmpProfileId != null })
            .ToDictionaryAsync(n => n.Id, cancellationToken);
        var sources = (await db.StatusSources
                .Where(s => nodeIds.Contains(s.NodeId) && s.SourceKey == "")
                .ToListAsync(cancellationToken))
            .ToDictionary(s => (s.NodeId, s.SourceType));
        var infos = await db.NodeSystemInfo.Where(i => nodeIds.Contains(i.NodeId)).ToDictionaryAsync(i => i.NodeId, cancellationToken);

        StatusSource Source(int nodeId, StatusSourceType type, DateTime now, bool enabledWhenNew)
        {
            if (!sources.TryGetValue((nodeId, type), out var source))
            {
                source = new StatusSource { NodeId = nodeId, SourceType = type, SinceUtc = now, Enabled = enabledWhenNew };
                db.StatusSources.Add(source);
                sources[(nodeId, type)] = source;
            }

            return source;
        }

        foreach (var result in batch.OrderBy(r => r.TimestampUtc))
        {
            // The node may have been deleted or switched off while the poll was in flight.
            if (!nodes.TryGetValue(result.NodeId, out var node) || !node.Enabled)
            {
                continue;
            }

            switch (result)
            {
                case IcmpPollResult icmp when node.IcmpEnabled:
                    ApplyIcmp(db, Source(node.Id, StatusSourceType.Icmp, icmp.TimestampUtc, true), node.Hostname, icmp);
                    break;
                case BackupPollResult backup:
                    ApplyBackup(db, Source(node.Id, StatusSourceType.Backup, backup.TimestampUtc, true), node.Hostname, backup);
                    break;
                case SnmpPollResult snmp when node.Snmp:
                    if (!infos.TryGetValue(node.Id, out var info))
                    {
                        info = new NodeSystemInfo { NodeId = node.Id };
                        db.NodeSystemInfo.Add(info);
                        infos[node.Id] = info;
                    }

                    ApplySnmp(db, info, t => Source(node.Id, t, snmp.TimestampUtc, false), node.Hostname, snmp);
                    break;
            }
        }

        var monitors = batch.OfType<MonitorPollResult>()
            .Where(r => nodes.TryGetValue(r.NodeId, out var n) && n.Enabled && n.Snmp)
            .OrderBy(r => r.TimestampUtc)
            .ToList();
        if (monitors.Count > 0)
        {
            await ApplyMonitorsAsync(db, monitors, nodes.ToDictionary(n => n.Key, n => n.Value.Hostname), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        broadcaster.Publish(await cache.RefreshAsync(db, nodeIds, cancellationToken));
    }

    /// <summary>
    /// Applies custom-monitor polls: materialises an instance per polled row (dropping all-rows
    /// rows that disappeared or no longer pass the row filters), computes each value (rate,
    /// percent), runs the rules with dampening, and records samples and events.
    /// </summary>
    private static async Task ApplyMonitorsAsync(YanmtDbContext db, List<MonitorPollResult> results, Dictionary<int, string> hostnames, CancellationToken cancellationToken)
    {
        var nodeIds = results.Select(r => r.NodeId).Distinct().ToList();
        var defIds = results.SelectMany(r => r.Works.Select(w => w.Spec.Id)).Distinct().ToList();
        var instances = (await db.MonitorInstances.Where(i => nodeIds.Contains(i.NodeId) && defIds.Contains(i.DefinitionId)).ToListAsync(cancellationToken))
            .ToDictionary(i => (i.DefinitionId, i.NodeId, i.InstanceKey));
        var updates = new List<(MonitorPollResult Result, MonitorWork Work, MonitorRow? Row, MonitorInstance Instance)>();
        var removed = new List<MonitorInstance>();

        foreach (var r in results)
        {
            foreach (var w in r.Works)
            {
                var existing = instances.Values.Where(i => i.NodeId == r.NodeId && i.DefinitionId == w.Spec.Id).ToList();
                if (r.Reading?.Works.FirstOrDefault(x => x.DefinitionId == w.Spec.Id) is not { } reading)
                {
                    // The poll failed: existing instances go Unknown (quietly; the SNMP source reports the failure).
                    updates.AddRange(existing.Select(i => (r, w, (MonitorRow?)null, i)));
                    continue;
                }

                var keep = reading.Rows
                    .Where(row => w.Instances.ContainsKey(row.Key) || (w.AllRows && RuleEvaluator.KeepRow(w.Spec, row.Filter, row.Label)))
                    .ToList();
                foreach (var row in keep)
                {
                    if (!instances.TryGetValue((w.Spec.Id, r.NodeId, row.Key), out var instance))
                    {
                        instance = new MonitorInstance { DefinitionId = w.Spec.Id, NodeId = r.NodeId, InstanceKey = row.Key };
                        db.MonitorInstances.Add(instance);
                        instances[(w.Spec.Id, r.NodeId, row.Key)] = instance;
                    }

                    instance.Label = Clip(row.Label ?? w.Instances.GetValueOrDefault(row.Key) ?? instance.Label, 255);
                    updates.Add((r, w, row, instance));
                }

                // A complete walk without a row means the row is gone (or filtered out now).
                if (w.AllRows && !reading.Truncated)
                {
                    var kept = keep.Select(k => k.Key).ToHashSet();
                    removed.AddRange(existing.Where(i => !kept.Contains(i.InstanceKey) && !w.Instances.ContainsKey(i.InstanceKey)));
                }
            }
        }

        if (removed.Count > 0)
        {
            var keys = removed.Select(i => i.Id.ToString(CultureInfo.InvariantCulture)).ToList();
            db.StatusSources.RemoveRange(await db.StatusSources
                .Where(s => s.SourceType == StatusSourceType.Custom && nodeIds.Contains(s.NodeId) && keys.Contains(s.SourceKey))
                .ToListAsync(cancellationToken));
            db.MonitorInstances.RemoveRange(removed);
        }

        await db.SaveChangesAsync(cancellationToken); // new instances get their ids, which key their sources

        var sourceKeys = updates.Select(u => u.Instance.Id.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
        var sources = await db.StatusSources
            .Where(s => s.SourceType == StatusSourceType.Custom && nodeIds.Contains(s.NodeId) && sourceKeys.Contains(s.SourceKey))
            .ToDictionaryAsync(s => s.SourceKey, cancellationToken);

        foreach (var (result, work, row, instance) in updates)
        {
            var spec = work.Spec;
            var ts = result.TimestampUtc;
            var key = instance.Id.ToString(CultureInfo.InvariantCulture);
            if (!sources.TryGetValue(key, out var source))
            {
                source = new StatusSource { NodeId = instance.NodeId, SourceType = StatusSourceType.Custom, SourceKey = key, SinceUtc = ts, Enabled = false };
                db.StatusSources.Add(source);
                sources[key] = source;
            }

            var name = spec.OidMode == Core.Monitors.OidMode.Scalar ? spec.Name : $"{spec.Name} · {instance.Label ?? instance.InstanceKey}";
            source.DisplayName = Clip(name, 200);

            if (row is null)
            {
                if (source.Enabled)
                {
                    SetState(db, source, Severity.Unknown, null, $"SNMP {Describe(result.Failure)}", ts, message: null);
                }

                continue;
            }

            var baseline = instance.LastRaw is { } raw && instance.LastRawUtc is { } at ? new RateBaseline(raw, at, (uint?)instance.LastUptimeTicks) : null;
            var computed = MonitorMath.Compute(spec, row.Value, row.Denominator, ts, result.Reading!.UptimeTicks, baseline);
            if (computed.Baseline is { } b)
            {
                (instance.LastRaw, instance.LastRawUtc, instance.LastUptimeTicks) = (b.Raw, b.AtUtc, b.UptimeTicks);
            }

            Severity candidate;
            string text;
            string why;
            var picked = work.Instances.GetValueOrDefault(row.Key);
            if (picked is not null && row.Label is not null && !string.Equals(picked.Trim(), row.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                candidate = Severity.Warning;
                text = $"instance changed: was {picked}, now {row.Label}";
                why = "the row at this index has a different label; re-pick the instance";
            }
            else if (computed.Value is not { } value)
            {
                candidate = Severity.Unknown;
                text = computed.Problem ?? "no value";
                why = text;
            }
            else
            {
                var (state, rule) = RuleEvaluator.Evaluate(spec, value);
                candidate = state;
                text = MonitorMath.Display(value, spec.ValueMap, spec.Unit);
                why = rule is { } i
                    ? $"rule {i + 1} ({MonitorValidation.Symbol(spec.Rules[i].Operator)} {spec.Rules[i].Value}{(spec.Rules[i].Value2 is { } v2 ? " and " + v2 : "")})"
                    : "no rule matched, default state";
                if (value.Number is { } n)
                {
                    db.MetricSamples.Add(new MetricSample { StatusSource = source, TimestampUtc = ts, Value = (double)n });
                }
            }

            var next = candidate == Severity.Unknown
                ? DampedState.Unknown
                : MonitorDampening.Apply(new DampedState(source.State, source.PendingState, source.PendingCount), candidate, spec.ConsecutiveMatches);

            // Waiting for a second rate sample is expected, not news.
            var quiet = !spec.RaiseEvents || (candidate == Severity.Unknown && computed.Baseline is not null);
            var message = quiet ? null : $"{hostnames.GetValueOrDefault(instance.NodeId)}: {name} {source.State} → {next.State}: {text}, {why}";
            SetState(db, source, next.State, computed.Value?.Number is { } num ? (double)num : null, text, ts, message);
            (source.PendingState, source.PendingCount) = (next.PendingState, next.PendingCount);
            if (candidate == Severity.Unknown && computed.Baseline is not null)
            {
                source.LastUpdateUtc = null; // still waiting for data: the first real value isn't a state change
            }
        }
    }

    /// <summary>
    /// Backup state: a failed pull is Warning, a successful one Normal. A config change also gets its
    /// own event (Normal to Normal), since it is worth seeing even though the state doesn't move.
    /// </summary>
    private static void ApplyBackup(YanmtDbContext db, StatusSource source, string hostname, BackupPollResult result)
    {
        var evaluation = result.Kind is { } kind
            ? ConfigBackupEvaluator.EvaluateSuccess(kind)
            : ConfigBackupEvaluator.EvaluateFailure(result.Error ?? "");
        SetState(db, source, evaluation.State, null, evaluation.ValueText, result.TimestampUtc,
            $"{hostname}: config backup {source.State} → {evaluation.State}: {evaluation.Reason}");

        if (result.Kind == ConfigChangeKind.Changed)
        {
            db.Events.Add(new Event
            {
                TimestampUtc = result.TimestampUtc,
                NodeId = result.NodeId,
                Source = StatusSourceType.Backup,
                OldState = Severity.Normal,
                NewState = Severity.Normal,
                Message = Truncate($"{hostname}: running-config changed (new version stored)", 1000),
            });
        }
    }

    private static void ApplyIcmp(YanmtDbContext db, StatusSource source, string hostname, IcmpPollResult result)
    {
        var previous = new DampedState(source.State, source.PendingState, source.PendingCount);
        DampedState next;
        string reason;
        if (result.Result is not { } r)
        {
            // The poll itself failed (not "no reply"): we have no data, so the state is Unknown.
            next = DampedState.Unknown;
            source.Value = null;
            source.ValueText = "poll failed";
            reason = $"poll failed: {result.Error}";
        }
        else
        {
            db.IcmpSamples.Add(new IcmpSample
            {
                NodeId = result.NodeId,
                TimestampUtc = result.TimestampUtc,
                Sent = r.Sent,
                Received = r.Received,
                MinMs = r.MinMs,
                AvgMs = r.AvgMs,
                MaxMs = r.MaxMs,
                LossPercent = r.LossPercent,
            });

            var evaluation = IcmpEvaluator.Evaluate(previous, new IcmpMeasurement(r.Sent, r.Received, r.AvgMs), result.Settings);
            next = evaluation.Next;
            source.Value = evaluation.Value;
            source.ValueText = evaluation.ValueText;
            reason = evaluation.Reason;
        }

        if (next.State != source.State)
        {
            // The first data a source ever gets (or gets after being re-enabled) isn't a change.
            if (source.LastUpdateUtc is not null || source.State != Severity.Unknown)
            {
                db.Events.Add(new Event
                {
                    TimestampUtc = result.TimestampUtc,
                    NodeId = result.NodeId,
                    Source = StatusSourceType.Icmp,
                    OldState = source.State,
                    NewState = next.State,
                    Message = Truncate($"{hostname}: ICMP {source.State} → {next.State}: {reason}", 1000),
                });
            }

            source.SinceUtc = result.TimestampUtc;
        }

        source.State = next.State;
        source.PendingState = next.PendingState;
        source.PendingCount = next.PendingCount;
        source.Enabled = true;
        source.LastUpdateUtc = result.TimestampUtc;
    }

    private static void ApplySnmp(YanmtDbContext db, NodeSystemInfo info, Func<StatusSourceType, StatusSource> source, string hostname, SnmpPollResult result)
    {
        var ts = result.TimestampUtc;
        info.LastPollUtc = ts;
        var uptime = source(StatusSourceType.Uptime);

        if (result.Reading is not { } reading)
        {
            // One event for the failure, on the Uptime source (which stands for "SNMP polling works").
            // CPU and memory go Unknown quietly: they have no data, not a problem of their own.
            var what = Describe(result.Failure);
            SetState(db, uptime, Severity.Unknown, null, $"SNMP {what}", ts, $"{hostname}: SNMP poll failed ({what}): {result.Error}");
            foreach (var type in new[] { StatusSourceType.Cpu, StatusSourceType.Memory })
            {
                var s = source(type);
                if (s.Enabled)
                {
                    SetState(db, s, Severity.Unknown, null, null, ts, message: null);
                }
            }

            return;
        }

        // Uptime and reboots.
        var baseline = info.UptimeTicks is { } prev ? new UptimeBaseline((uint)prev, info.EngineId, info.EngineBoots) : null;
        if (reading.System.UptimeTicks is { } ticks)
        {
            var rebooted = RebootDetector.IsReboot(baseline, ticks, reading.Engine.EngineId, reading.Engine.Boots);
            var up = DefaultStats.Uptime(ticks, rebooted);
            var message = rebooted ? $"{hostname}: rebooted (sysUpTime {Units.Uptime(ticks)}, engine boots {reading.Engine.Boots})"
                : uptime.State == Severity.Unknown ? $"{hostname}: SNMP polling recovered ({up.Reason})"
                : null; // Warning -> Normal one poll after a reboot is the expected expiry, not news
            SetState(db, uptime, up.State, up.Value, up.ValueText, ts, message);
            if (rebooted)
            {
                info.LastRebootUtc = ts;
            }

            info.UptimeTicks = ticks;
        }
        else
        {
            SetState(db, uptime, Severity.Unknown, null, "sysUpTime not readable", ts, $"{hostname}: sysUpTime came back empty; check the SNMP view");
        }

        info.EngineId = reading.Engine.EngineId;
        info.EngineBoots = reading.Engine.Boots;
        info.SysDescr = Clip(reading.System.Descr, 1024);
        info.SysObjectId = Clip(reading.System.ObjectId, 256);
        info.SysName = Clip(reading.System.Name, 255);
        info.SysLocation = Clip(reading.System.Location, 255);
        info.SysContact = Clip(reading.System.Contact, 255);
        info.LastSuccessUtc = ts;

        // A device without the CPU or memory MIB (or a view that hides it) just doesn't have that
        // source, rather than sitting at Unknown and graying the node forever.
        var cpu = source(StatusSourceType.Cpu);
        if (reading.CpuFiveMinute.Count == 0)
        {
            Unsupported(cpu, "CPU MIB not available", ts);
        }
        else
        {
            var r = DefaultStats.Cpu(reading.CpuFiveMinute, result.Cpu);
            SetState(db, cpu, r.State, r.Value, r.ValueText, ts, QuietRecovery(cpu, r.State) ? null : $"{hostname}: CPU {cpu.State} → {r.State}: {r.Reason}");
            db.MetricSamples.Add(new MetricSample { StatusSource = cpu, TimestampUtc = ts, Value = r.Value!.Value });
        }

        var memory = source(StatusSourceType.Memory);
        if (DefaultStats.PickMemory(reading.MemoryPools) is not { } usage)
        {
            Unsupported(memory, "memory MIB not available", ts);
            info.MemoryPool = null;
        }
        else
        {
            var r = DefaultStats.Memory(usage, result.Memory);
            SetState(db, memory, r.State, r.Value, r.ValueText, ts, QuietRecovery(memory, r.State) ? null : $"{hostname}: Memory {memory.State} → {r.State}: {r.Reason}");
            db.MetricSamples.Add(new MetricSample { StatusSource = memory, TimestampUtc = ts, Value = r.Value!.Value });
            info.MemoryPool = usage.Pool;
        }
    }

    /// <summary>
    /// CPU and memory go Unknown quietly when a poll fails (the Uptime source logs the failure), so
    /// coming back Normal is quiet too. Coming back at Warning or Critical is still an event.
    /// </summary>
    private static bool QuietRecovery(StatusSource source, Severity next) => source.State == Severity.Unknown && next == Severity.Normal;

    /// <summary>
    /// Applies a state to a source and records an event when it changes, except for a source's
    /// first data (new, re-enabled, or never updated) or when <paramref name="message"/> is null.
    /// </summary>
    private static void SetState(YanmtDbContext db, StatusSource source, Severity state, double? value, string? valueText, DateTime ts, string? message)
    {
        var firstData = !source.Enabled || (source.LastUpdateUtc is null && source.State == Severity.Unknown);
        if (state != source.State && message is not null && !firstData)
        {
            db.Events.Add(new Event
            {
                TimestampUtc = ts,
                NodeId = source.NodeId,
                Source = source.SourceType,
                OldState = source.State,
                NewState = state,
                Message = Truncate(message, 1000),
            });
        }

        if (state != source.State || !source.Enabled)
        {
            source.SinceUtc = ts;
        }

        source.State = state;
        source.Value = value;
        source.ValueText = valueText;
        source.PendingState = null;
        source.PendingCount = 0;
        source.Enabled = true;
        source.LastUpdateUtc = ts;
    }

    private static void Unsupported(StatusSource source, string text, DateTime ts)
    {
        if (source.Enabled || source.ValueText != text)
        {
            source.SinceUtc = ts;
        }

        source.Enabled = false;
        source.State = Severity.Unknown;
        source.Value = null;
        source.ValueText = text;
        source.LastUpdateUtc = null;
    }

    private static string Describe(FailureCategory? failure) => failure switch
    {
        FailureCategory.Timeout => "timeout",
        FailureCategory.AuthFailure => "authentication failure",
        FailureCategory.UnknownUser => "unknown user",
        FailureCategory.DecryptionError => "decryption error",
        FailureCategory.NotInTimeWindow => "not in time window",
        FailureCategory.UnknownEngine => "unknown engine",
        FailureCategory.UnsupportedSecurityLevel => "unsupported security level",
        FailureCategory.UnsupportedAlgorithm => "unsupported algorithm",
        FailureCategory.CredentialUnreadable => "credentials unreadable",
        FailureCategory.NotConfigured => "not configured",
        FailureCategory.Refused => "port unreachable",
        FailureCategory.Unreachable => "unreachable",
        FailureCategory.DeviceError => "device error",
        _ => "error",
    };

    private static string? Clip(string? s, int max) => s is null || s.Length <= max ? s : s[..max];

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
