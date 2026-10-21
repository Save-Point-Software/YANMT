using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Credentials;
using Yanmt.Core.Ssh;
using Yanmt.Infrastructure.Collectors.Icmp;
using Yanmt.Infrastructure.Collectors.Snmp;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Data;
using Yanmt.Infrastructure.Data.Entities;
using Yanmt.Infrastructure.Security;

namespace Yanmt.Infrastructure.Connectivity;

/// <summary>
/// The admin "Test connection" buttons. Each test uses the node's saved IP and assigned profile,
/// and turns every failure into a <see cref="FailureCategory"/> plus a message saying what to check.
/// Tests run whether or not polling is enabled for that protocol, so a node can be checked first.
/// </summary>
public sealed class ConnectionTester(
    IDbContextFactory<YanmtDbContext> dbFactory,
    CredentialService credentials,
    IIcmpProbe icmp,
    ISnmpReader snmp,
    ReadOnlySshCollector ssh,
    ILogger<ConnectionTester> logger)
{
    internal const string SysDescr = "1.3.6.1.2.1.1.1.0";
    internal const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    internal const string SysName = "1.3.6.1.2.1.1.5.0";

    internal static readonly TimeSpan SnmpTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan SshConnectTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan SshCommandTimeout = TimeSpan.FromSeconds(30);
    private const int ShowVersionPreviewLines = 25;

    public async Task<ConnectionTestResult> TestIcmpAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        const ConnectionTestKind kind = ConnectionTestKind.Icmp;
        var sw = Stopwatch.StartNew();
        var node = await FindNodeAsync(nodeId, cancellationToken);
        if (node is null)
        {
            return NodeMissing(kind);
        }

        var result = await icmp.ProbeAsync(IPAddress.Parse(node.ManagementIp), count: 4, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(200), cancellationToken);
        var details = new List<KeyValuePair<string, string>>
        {
            new("Address", node.ManagementIp),
            new("Replies", $"{result.Received} of {result.Sent} ({result.LossPercent:0}% loss)"),
        };
        if (result.AvgMs is not null)
        {
            details.Add(new("Round trip", $"min {result.MinMs:0} / avg {result.AvgMs:0.#} / max {result.MaxMs:0} ms"));
        }

        var outcome = result.Received switch
        {
            0 => ConnectionTestResult.Fail(kind, result.LastFailure == IPStatus.TimedOut ? FailureCategory.Timeout : FailureCategory.Unreachable,
                $"No echo replies from {node.ManagementIp} ({result.LastFailure}).", sw.Elapsed, details),
            _ => ConnectionTestResult.Ok(kind, result.Received == result.Sent
                ? $"{node.ManagementIp} answered every echo."
                : $"{node.ManagementIp} answered {result.Received} of {result.Sent} echoes.", details, sw.Elapsed),
        };
        return Logged(node, outcome);
    }

    public async Task<ConnectionTestResult> TestSnmpAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        const ConnectionTestKind kind = ConnectionTestKind.Snmp;
        var sw = Stopwatch.StartNew();
        var node = await FindNodeAsync(nodeId, cancellationToken);
        if (node is null)
        {
            return NodeMissing(kind);
        }

        if (node.SnmpProfileId is not { } profileId)
        {
            return ConnectionTestResult.Fail(kind, FailureCategory.NotConfigured, "No SNMPv3 profile is assigned to this node.", sw.Elapsed);
        }

        try
        {
            if (await credentials.LoadSnmpAsync(profileId, cancellationToken) is not var (credential, port))
            {
                return ConnectionTestResult.Fail(kind, FailureCategory.NotConfigured, "The assigned SNMPv3 profile no longer exists.", sw.Elapsed);
            }

            var details = new List<KeyValuePair<string, string>>
            {
                new("Target", $"{node.ManagementIp} udp/{port}"),
                new("Security", $"authPriv, user {credential.Username}, {CredentialRules.Label(credential.Auth)} / {CredentialRules.Label(credential.Priv)}"),
            };

            var target = new SnmpTarget(IPAddress.Parse(node.ManagementIp), port, credential, SnmpTimeout);
            var response = await snmp.GetAsync(target, [SysDescr, SysName, SysUpTime], cancellationToken);
            details.Add(new("Engine ID", response.Engine.EngineId));
            details.Add(new("Engine boots / time", $"{response.Engine.Boots} / {response.Engine.Time} s"));

            var values = response.Values.ToDictionary(v => v.Oid.TrimStart('.'));
            var missing = new[] { (SysDescr, "sysDescr"), (SysName, "sysName"), (SysUpTime, "sysUpTime") }
                .Where(x => !values.TryGetValue(x.Item1, out var v) || v.IsMissing)
                .Select(x => x.Item2)
                .ToList();
            if (missing.Count > 0)
            {
                return Logged(node, ConnectionTestResult.Fail(kind, FailureCategory.DeviceError,
                    $"Authenticated and decrypted, but {string.Join(", ", missing)} came back empty (noSuchObject). " +
                    "The SNMP view for this user's group probably excludes the system subtree.", sw.Elapsed, details));
            }

            details.Add(new("sysName", values[SysName].Text));
            details.Add(new("sysUpTime", values[SysUpTime].Text));
            return Logged(node, ConnectionTestResult.Ok(kind,
                $"SNMPv3 authPriv {CredentialRules.Describe(credential.Auth)}/{CredentialRules.Describe(credential.Priv)} works.",
                details, sw.Elapsed, values[SysDescr].Text));
        }
        catch (SnmpQueryException ex)
        {
            return Logged(node, ConnectionTestResult.Fail(kind, ex.Category, ex.Message, sw.Elapsed), ex);
        }
        catch (SecretUnreadableException ex)
        {
            return Logged(node, ConnectionTestResult.Fail(kind, FailureCategory.CredentialUnreadable, ex.Message, sw.Elapsed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Logged(node, ConnectionTestResult.Fail(kind, FailureCategory.Other, $"Unexpected error: {ex.Message}", sw.Elapsed), ex);
        }
    }

    /// <param name="trustFingerprint">
    /// Set when the admin chose to trust a key that differs from the pinned one: the connection is
    /// allowed only if the device presents exactly this key, which then replaces the pin.
    /// </param>
    public async Task<ConnectionTestResult> TestSshAsync(int nodeId, string? trustFingerprint = null, CancellationToken cancellationToken = default)
    {
        const ConnectionTestKind kind = ConnectionTestKind.Ssh;
        var sw = Stopwatch.StartNew();
        var node = await FindNodeAsync(nodeId, cancellationToken);
        if (node is null)
        {
            return NodeMissing(kind);
        }

        if (node.SshProfileId is not { } profileId)
        {
            return ConnectionTestResult.Fail(kind, FailureCategory.NotConfigured, "No SSH profile is assigned to this node.", sw.Elapsed);
        }

        try
        {
            var credential = await credentials.LoadSshAsync(profileId, cancellationToken);
            var port = await ProfilePortAsync(profileId, cancellationToken);
            if (credential is null || port is null)
            {
                return ConnectionTestResult.Fail(kind, FailureCategory.NotConfigured, "The assigned SSH profile no longer exists.", sw.Elapsed);
            }

            var pinned = trustFingerprint ?? node.SshHostKeyFingerprint;
            var target = new SshTarget(node.ManagementIp, port.Value, credential, pinned, SshConnectTimeout, SshCommandTimeout);
            var run = await ssh.RunAsync(target, [CommandAllowlist.ShowVersion], cancellationToken);

            var pinNote = node.SshHostKeyFingerprint is null ? "pinned now (first connection)"
                : node.SshHostKeyFingerprint == run.HostKey.Fingerprint ? "matches the pinned key"
                : "trusted and pinned (replaced the previous key)";
            if (node.SshHostKeyFingerprint != run.HostKey.Fingerprint)
            {
                await PinHostKeyAsync(node.Id, run.HostKey, cancellationToken);
            }

            var output = run.Outputs[0];
            var details = new List<KeyValuePair<string, string>>
            {
                new("Target", $"{node.ManagementIp} tcp/{port}, user {credential.Username}"),
                new("Server", run.Session.ServerVersion),
                new("Host key", $"{run.HostKey.Algorithm} {run.HostKey.KeyLength}-bit {run.HostKey.Fingerprint}: {pinNote}"),
                new("Key exchange", run.Session.KeyExchange),
                new("Cipher / MAC", $"{run.Session.Cipher} / {run.Session.Mac}"),
            };

            var preview = string.Join('\n', output.Output.ReplaceLineEndings("\n").Split('\n').Where(l => l.Length > 0).Take(ShowVersionPreviewLines));
            if (output.ExitStatus is { } status and not 0 || string.IsNullOrWhiteSpace(output.Output))
            {
                return Logged(node, ConnectionTestResult.Fail(kind, FailureCategory.DeviceError,
                    $"Logged in, but '{output.Command}' returned {(string.IsNullOrWhiteSpace(output.Output) ? "no output" : $"exit status {output.ExitStatus}")}. " +
                    "Check that the account is privilege 15 and exec commands are permitted.", sw.Elapsed, details) with { Output = preview });
            }

            return Logged(node, ConnectionTestResult.Ok(kind, $"SSH login and '{output.Command}' work.", details, sw.Elapsed, preview));
        }
        catch (SshCollectorException ex)
        {
            var details = ex.PresentedHostKey is { } key
                ? new List<KeyValuePair<string, string>>
                {
                    new("Pinned key", node.SshHostKeyFingerprint ?? ""),
                    new("Presented key", $"{key.Algorithm} {key.KeyLength}-bit {key.Fingerprint}"),
                }
                : null;
            return Logged(node, ConnectionTestResult.Fail(kind, ex.Category, ex.Message, sw.Elapsed, details) with
            {
                PresentedHostKey = ex.PresentedHostKey?.Fingerprint,
            }, ex);
        }
        catch (SecretUnreadableException ex)
        {
            return Logged(node, ConnectionTestResult.Fail(kind, FailureCategory.CredentialUnreadable, ex.Message, sw.Elapsed));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Logged(node, ConnectionTestResult.Fail(kind, FailureCategory.Other, $"Unexpected error: {ex.Message}", sw.Elapsed), ex);
        }
    }

    /// <summary>Forgets the pinned SSH host key, so the next connection pins whatever the device presents.</summary>
    public async Task ForgetHostKeyAsync(int nodeId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken);
        if (node is { SshHostKeyFingerprint: not null })
        {
            node.SshHostKeyAlgorithm = null;
            node.SshHostKeyFingerprint = null;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task PinHostKeyAsync(int nodeId, SshHostKey key, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var node = await db.Nodes.FirstAsync(n => n.Id == nodeId, cancellationToken);
        node.SshHostKeyAlgorithm = key.Algorithm;
        node.SshHostKeyFingerprint = key.Fingerprint;
        await db.SaveChangesAsync(cancellationToken); // audited: who pinned or replaced the key
    }

    private async Task<int?> ProfilePortAsync(int profileId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CredentialProfiles.Where(p => p.Id == profileId).Select(p => (int?)p.Port).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<Node?> FindNodeAsync(int nodeId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == nodeId, cancellationToken);
    }

    private static ConnectionTestResult NodeMissing(ConnectionTestKind kind) =>
        ConnectionTestResult.Fail(kind, FailureCategory.NotConfigured, "This node no longer exists.", TimeSpan.Zero);

    private ConnectionTestResult Logged(Node node, ConnectionTestResult result, Exception? ex = null)
    {
        // Never logs credentials: only the outcome, category, and our own message.
        logger.Log(result.Success ? LogLevel.Information : LogLevel.Warning, ex is SnmpQueryException or SshCollectorException ? null : ex,
            "{Kind} connection test for {Hostname} ({Ip}): {Outcome}{Category} in {ElapsedMs} ms. {Summary}",
            result.Kind, node.Hostname, node.ManagementIp, result.Success ? "OK" : "FAILED ", result.Failure?.ToString() ?? "",
            ((int)result.Elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture), result.Summary);
        return result;
    }
}
