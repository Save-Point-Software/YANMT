using Lextm.SharpSnmpLib;
using Microsoft.EntityFrameworkCore;
using Yanmt.Core.Credentials;
using Yanmt.Infrastructure.Credentials;
using Yanmt.Infrastructure.Tests.Collectors;

namespace Yanmt.Infrastructure.Tests.Monitoring;

/// <summary>A fake IOS-XE-like device for the SNMP tests: system group, CPU and memory MIBs, a few interfaces.</summary>
internal static class SnmpTestSupport
{
    public static readonly SnmpV3Credential Cred = new("yanmt-ro", SnmpAuthProtocol.Sha256, "lab-auth-pass-01", SnmpPrivProtocol.Aes128, "lab-priv-pass-01");

    public const string SysUpTime = "1.3.6.1.2.1.1.3.0";
    public const string Cpu5Min = "1.3.6.1.4.1.9.9.109.1.1.1.1.8";
    public const string IfHcInOctets = "1.3.6.1.2.1.31.1.1.1.6";
    public const string IfHcOutOctets = "1.3.6.1.2.1.31.1.1.1.10";

    public static Dictionary<string, ISnmpData> CiscoDevice(
        uint uptimeTicks = 8_640_000,
        uint[]? cpu = null,
        bool enhancedMemory = true,
        bool legacyMemory = false,
        int interfaces = 3)
    {
        var t = new Dictionary<string, ISnmpData>
        {
            ["1.3.6.1.2.1.1.1.0"] = new OctetString("Cisco IOS XE Software, Version 17.12.04\r\nTechnical Support: http://www.cisco.com/techsupport"),
            ["1.3.6.1.2.1.1.2.0"] = new ObjectIdentifier("1.3.6.1.4.1.9.1.2391"),
            [SysUpTime] = new TimeTicks(uptimeTicks),
            ["1.3.6.1.2.1.1.4.0"] = new OctetString("noc@example.net"),
            ["1.3.6.1.2.1.1.5.0"] = new OctetString("lab-rtr-hub-01.example.net"),
            ["1.3.6.1.2.1.1.6.0"] = new OctetString("Lab rack 1"),
        };

        var cpus = cpu ?? [12];
        for (var i = 0; i < cpus.Length; i++)
        {
            t[$"{Cpu5Min}.{7 + i}"] = new Gauge32(cpus[i]);
        }

        if (enhancedMemory)
        {
            // Pool 1 is Processor (HC counters filled), pool 2 an I/O pool.
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.3.7000.1"] = new OctetString("Processor");
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.3.7000.2"] = new OctetString("lsmpi_io");
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.7.7000.1"] = new Gauge32(300_000_000);
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.8.7000.1"] = new Gauge32(700_000_000);
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.7.7000.2"] = new Gauge32(1_000);
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.8.7000.2"] = new Gauge32(3_000);
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.18.7000.1"] = new Counter64(3_000_000_000);
            t["1.3.6.1.4.1.9.9.221.1.1.1.1.20.7000.1"] = new Counter64(7_000_000_000);
        }

        if (legacyMemory)
        {
            t["1.3.6.1.4.1.9.9.48.1.1.1.2.1"] = new OctetString("Processor");
            t["1.3.6.1.4.1.9.9.48.1.1.1.5.1"] = new Gauge32(900);
            t["1.3.6.1.4.1.9.9.48.1.1.1.6.1"] = new Gauge32(100);
        }

        for (var i = 1; i <= interfaces; i++)
        {
            t[$"1.3.6.1.2.1.2.2.1.2.{i}"] = new OctetString($"GigabitEthernet{i}");
            t[$"1.3.6.1.2.1.2.2.1.5.{i}"] = new Gauge32(1_000_000_000);
            t[$"1.3.6.1.2.1.2.2.1.7.{i}"] = new Integer32(i == 3 ? 2 : 1);
            t[$"1.3.6.1.2.1.2.2.1.8.{i}"] = new Integer32(i == 1 ? 1 : 2);
            t[$"1.3.6.1.2.1.31.1.1.1.1.{i}"] = new OctetString($"Gi{i}");
            t[$"{IfHcInOctets}.{i}"] = new Counter64(1_000_000);
            t[$"{IfHcOutOctets}.{i}"] = new Counter64(2_000_000);
            t[$"1.3.6.1.2.1.31.1.1.1.15.{i}"] = new Gauge32(1000);
            t[$"1.3.6.1.2.1.31.1.1.1.18.{i}"] = new OctetString(i == 1 ? "uplink to lab-rtr-spoke-01" : "");
        }

        return t;
    }

    /// <summary>A node at 127.0.0.1 with SNMP on and a profile pointing at the agent's port.</summary>
    public static async Task<int> AddSnmpNodeAsync(this TestDatabase test, FakeSnmpAgent agent, string hostname = "lab-rtr-hub-01")
    {
        var credentials = new CredentialService(test.Factory, test.Protector, test.Clock);
        var profile = await credentials.CreateAsync(new CredentialEditModel
        {
            Name = "lab-v3-" + hostname,
            Kind = CredentialKind.SnmpV3,
            Username = Cred.Username,
            Port = agent.EndPoint.Port,
            SnmpAuth = Cred.Auth,
            SnmpPriv = Cred.Priv,
            AuthPassword = Cred.AuthPassword,
            PrivPassword = Cred.PrivPassword,
        }, TestContext.Current.CancellationToken);
        Assert.True(profile.Ok, profile.Error);

        var id = await test.AddNodeAsync(hostname, "127.0.0.1", icmp: false);
        await using var db = test.Open();
        var node = await db.Nodes.SingleAsync(n => n.Id == id, TestContext.Current.CancellationToken);
        node.SnmpEnabled = true;
        node.SnmpProfileId = profile.Id;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
