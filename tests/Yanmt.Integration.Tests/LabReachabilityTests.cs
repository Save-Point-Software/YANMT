using System.Net.NetworkInformation;

namespace Yanmt.Integration.Tests;

/// <summary>Sanity check that the lab is up before the Phase 1b SNMP/SSH tests run against it.</summary>
[Trait("Category", "Integration")]
public class LabReachabilityTests
{
    [Fact]
    public async Task Every_lab_node_answers_ping()
    {
        var lab = LabConfig.Require();
        Assert.NotEmpty(lab.Nodes);

        using var ping = new Ping();
        var failures = new List<string>();
        foreach (var node in lab.Nodes)
        {
            var reply = await ping.SendPingAsync(node.ManagementIp, TimeSpan.FromSeconds(2), cancellationToken: TestContext.Current.CancellationToken);
            if (reply.Status != IPStatus.Success)
            {
                failures.Add($"{node.Hostname} ({node.ManagementIp}): {reply.Status}");
            }
        }

        Assert.True(failures.Count == 0, "Unreachable: " + string.Join("; ", failures));
    }
}
