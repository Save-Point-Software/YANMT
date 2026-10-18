using System.Net;
using System.Net.Sockets;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Ssh;
using Yanmt.Infrastructure.Collectors.Ssh;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Tests.Collectors;

public class ReadOnlySshCollectorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Windows retries a refused TCP connect for about 2 s, so the connect timeout must be longer.
    private static SshTarget Target(int port, double connectSeconds = 2) => new(
        "127.0.0.1", port, new SshCredential("yanmt", "lab-password"), null, TimeSpan.FromSeconds(connectSeconds), TimeSpan.FromSeconds(2));

    [Theory]
    [InlineData("configure terminal")]
    [InlineData("conf t")]
    [InlineData("write memory")]
    [InlineData("copy running-config startup-config")]
    [InlineData("show version\nconfigure terminal")]
    [InlineData("show running-config | redirect flash:x")]
    public async Task Forbidden_command_is_refused_before_any_connection(string forbidden)
    {
        // A listener stands in for the device: if the collector so much as opened a TCP
        // connection, it would show up as a pending accept.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var ex = await Assert.ThrowsAsync<CommandNotAllowedException>(() =>
            new ReadOnlySshCollector().RunAsync(Target(port), [CommandAllowlist.ShowVersion, forbidden], Ct));

        Assert.Equal(forbidden, ex.Command);
        Assert.False(listener.Pending(), "The collector connected to the device before refusing the command.");
    }

    [Fact]
    public async Task Refused_port_is_classified()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        var ex = await Assert.ThrowsAsync<SshCollectorException>(() =>
            new ReadOnlySshCollector().RunAsync(Target(port, connectSeconds: 8), [CommandAllowlist.ShowVersion], Ct));

        Assert.Equal(FailureCategory.Refused, ex.Category);
    }

    [Fact]
    public async Task Server_that_never_speaks_ssh_times_out()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(Ct).AsTask();

        var ex = await Assert.ThrowsAsync<SshCollectorException>(() =>
            new ReadOnlySshCollector().RunAsync(Target(port), [CommandAllowlist.ShowVersion], Ct));

        Assert.Equal(FailureCategory.Timeout, ex.Category);
        (await accept).Dispose();
    }

    [Fact]
    public void Credentials_never_print_their_secrets()
    {
        var ssh = new SshCredential("yanmt", "s3cret-ssh-pass");
        var snmp = new SnmpV3Credential("yanmt-ro", Core.Credentials.SnmpAuthProtocol.Sha256, "s3cret-auth-pass",
            Core.Credentials.SnmpPrivProtocol.Aes256, "s3cret-priv-pass");

        Assert.DoesNotContain("s3cret", ssh.ToString());
        Assert.DoesNotContain("s3cret", snmp.ToString());
        Assert.DoesNotContain("s3cret", Target(22).ToString());
    }
}
