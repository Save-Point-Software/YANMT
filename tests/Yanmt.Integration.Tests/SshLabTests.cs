using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Ssh;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Integration.Tests;

/// <summary>SSH.NET against IOS-XE's algorithm set, the exec channel, and host-key pinning.</summary>
[Trait("Category", "Integration")]
[TestCaseOrderer(typeof(AlphabeticalOrderer))]
public class SshLabTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Ssh_1_negotiates_and_runs_show_version_on_every_node()
    {
        var (lab, ssh) = Require();
        var failures = new List<string>();

        foreach (var node in lab.Nodes)
        {
            try
            {
                var run = await new ReadOnlySshCollector().RunAsync(Target(node, ssh, pinned: null), [CommandAllowlist.ShowVersion], Ct);
                var output = run.Outputs[0].Output;
                if (!output.Contains("Cisco IOS", StringComparison.Ordinal))
                {
                    failures.Add($"{node.Hostname}: show version output doesn't look like IOS: {output[..Math.Min(200, output.Length)]}");
                }

                TestContext.Current.TestOutputHelper?.WriteLine(
                    $"{node.Hostname}: {run.Session.ServerVersion}; kex {run.Session.KeyExchange}; hostkey {run.HostKey.Algorithm} " +
                    $"{run.HostKey.KeyLength}-bit {run.HostKey.Fingerprint}; cipher {run.Session.Cipher}; mac {run.Session.Mac}; exit {run.Outputs[0].ExitStatus}");
            }
            catch (SshCollectorException ex)
            {
                failures.Add($"{node.Hostname} ({node.ManagementIp}): {ex.Category}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0, "SSH failed on:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task Ssh_2_pinned_key_is_accepted_and_a_different_pin_is_refused()
    {
        var (lab, ssh) = Require();
        var node = lab.Nodes[0];
        var collector = new ReadOnlySshCollector();

        var first = await collector.RunAsync(Target(node, ssh, pinned: null), [CommandAllowlist.ShowVersion], Ct);
        var again = await collector.RunAsync(Target(node, ssh, pinned: first.HostKey.Fingerprint), [CommandAllowlist.ShowVersion], Ct);
        Assert.Equal(first.HostKey.Fingerprint, again.HostKey.Fingerprint);

        var ex = await Assert.ThrowsAsync<SshCollectorException>(() =>
            collector.RunAsync(Target(node, ssh, pinned: "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"), [CommandAllowlist.ShowVersion], Ct));
        Assert.Equal(FailureCategory.HostKeyChanged, ex.Category);
        Assert.Equal(first.HostKey.Fingerprint, ex.PresentedHostKey?.Fingerprint);
    }

    [Fact]
    public async Task Ssh_3_wrong_password_is_an_auth_failure()
    {
        var (lab, ssh) = Require();
        var node = lab.Nodes[0];
        var wrong = new LabSsh { Port = ssh.Port, Username = ssh.Username, Password = "wrong-password" };

        var ex = await Assert.ThrowsAsync<SshCollectorException>(() =>
            new ReadOnlySshCollector().RunAsync(Target(node, wrong, pinned: null), [CommandAllowlist.ShowVersion], Ct));

        TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {ex.Category}: {ex.Message}");
        Assert.Equal(FailureCategory.AuthFailure, ex.Category);
    }

    [Fact]
    public async Task Ssh_4_running_config_is_complete_and_normalizes_to_a_stable_hash()
    {
        var (lab, ssh) = Require();
        var collector = new ReadOnlySshCollector();
        var failures = new List<string>();

        foreach (var node in lab.Nodes)
        {
            // Two pulls back to back: the volatile header lines must not make the hashes differ.
            var first = await collector.RunAsync(Target(node, ssh, pinned: null), [CommandAllowlist.ShowRunningConfig], Ct);
            var second = await collector.RunAsync(Target(node, ssh, pinned: first.HostKey.Fingerprint), [CommandAllowlist.ShowRunningConfig], Ct);
            var a = first.Outputs[0].Output;
            var b = second.Outputs[0].Output;

            var lastLine = a.ReplaceLineEndings("\n").TrimEnd().Split('\n')[^1].TrimEnd();
            if (lastLine != "end")
            {
                failures.Add($"{node.Hostname}: running-config doesn't end with 'end' (last line: '{lastLine}'; {a.Length} chars)");
            }

            var hashA = ConfigBackupEvaluator.Hash(ConfigNormalizer.Normalize(a));
            var hashB = ConfigBackupEvaluator.Hash(ConfigNormalizer.Normalize(b));
            if (hashA != hashB)
            {
                failures.Add($"{node.Hostname}: normalized hash changed between two back-to-back pulls; a volatile line probably needs an ignore pattern");
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"{node.Hostname}: {a.Length} chars, hash {hashA[..12]}");
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static (LabConfig Lab, LabSsh Ssh) Require()
    {
        var lab = LabConfig.Require();
        Assert.SkipWhen(lab.Ssh is null, "No ssh section in lab.json.");
        Assert.NotEmpty(lab.Nodes);
        return (lab, lab.Ssh!);
    }

    private static SshTarget Target(LabNode node, LabSsh ssh, string? pinned) =>
        new(node.ManagementIp, ssh.Port, new SshCredential(ssh.Username, ssh.Password), pinned, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
}
