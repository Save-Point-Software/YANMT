using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using Yanmt.Core.Connectivity;
using Yanmt.Core.Ssh;
using Yanmt.Infrastructure.Credentials;

namespace Yanmt.Infrastructure.Collectors.Ssh;

/// <summary>
/// The only class in YANMT that talks SSH to a device. Every command goes through
/// <see cref="SendAsync"/>, which checks <see cref="CommandAllowlist"/> right before sending;
/// <see cref="RunAsync"/> also checks the whole batch before it even opens a TCP connection.
/// The device account is plain privilege 15, so this is the read-only enforcement.
/// Commands run on the exec channel (one channel per command). Phase 5 adds a shell-stream
/// fallback, which must go through the same allowlist check.
/// </summary>
public sealed class ReadOnlySshCollector
{
    public async Task<SshRunResult> RunAsync(SshTarget target, IReadOnlyList<string> commands, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(commands.Count);
        foreach (var command in commands)
        {
            CommandAllowlist.EnsureAllowed(command);
        }

        SshHostKey? presented = null;
        var hostKeyRejected = false;
        using var client = new SshClient(CreateConnectionInfo(target));
        client.HostKeyReceived += (_, e) =>
        {
            presented = new SshHostKey(e.HostKeyName, "SHA256:" + e.FingerPrintSHA256, e.KeyLength);
            e.CanTrust = target.PinnedHostKeyFingerprint is null || FingerprintsEqual(target.PinnedHostKeyFingerprint, presented.Fingerprint);
            hostKeyRejected = !e.CanTrust;
        };

        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(target.ConnectTimeout);
                try
                {
                    await client.ConnectAsync(connectTimeout.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new SshCollectorException(FailureCategory.Timeout,
                        $"No SSH session with {target.Host}:{target.Port} within {target.ConnectTimeout.TotalSeconds:0.#} s.");
                }
            }

            var info = client.ConnectionInfo;
            var session = new SshSessionInfo(
                info.ServerVersion ?? "",
                info.CurrentKeyExchangeAlgorithm ?? "",
                info.CurrentHostKeyAlgorithm ?? "",
                info.CurrentServerEncryption ?? "",
                info.CurrentServerHmacAlgorithm ?? "");

            var outputs = new List<SshCommandOutput>(commands.Count);
            foreach (var command in commands)
            {
                outputs.Add(await SendAsync(client, command, target.CommandTimeout, cancellationToken));
            }

            client.Disconnect();
            return new SshRunResult(presented!, session, outputs);
        }
        catch (Exception ex) when (Classify(ex, target, hostKeyRejected, presented) is { } classified)
        {
            throw classified;
        }
    }

    /// <summary>The single place a command string reaches the device.</summary>
    private static async Task<SshCommandOutput> SendAsync(SshClient client, string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        CommandAllowlist.EnsureAllowed(command);
        using var cmd = client.CreateCommand(command, Encoding.UTF8);
        cmd.CommandTimeout = timeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await cmd.ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SshCollectorException(FailureCategory.Timeout, $"'{command}' didn't finish within {timeout.TotalSeconds:0.#} s.");
        }

        return new SshCommandOutput(command, cmd.Result, cmd.ExitStatus);
    }

    private static ConnectionInfo CreateConnectionInfo(SshTarget target)
    {
        var username = target.Credential.Username;
        var password = new PasswordAuthenticationMethod(username, target.Credential.Password);

        // IOS-XE offers keyboard-interactive as well as password, depending on the AAA setup.
        var interactive = new KeyboardInteractiveAuthenticationMethod(username);
        interactive.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                prompt.Response = target.Credential.Password;
            }
        };

        return new ConnectionInfo(target.Host, target.Port, username, password, interactive)
        {
            Timeout = target.ConnectTimeout,
        };
    }

    private static bool FingerprintsEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static SshCollectorException? Classify(Exception ex, SshTarget target, bool hostKeyRejected, SshHostKey? presented)
    {
        var where = $"{target.Host}:{target.Port}";
        return ex switch
        {
            SshCollectorException or CommandNotAllowedException => null,
            _ when hostKeyRejected => new SshCollectorException(FailureCategory.HostKeyChanged,
                $"The SSH host key of {where} ({presented?.Fingerprint}) doesn't match the key pinned for this node ({target.PinnedHostKeyFingerprint}). " +
                "If the device was replaced or its key regenerated, trust the new key; otherwise investigate before connecting.", ex, presented),
            SocketException { SocketErrorCode: SocketError.ConnectionRefused } => new SshCollectorException(FailureCategory.Refused,
                $"{where} refused the TCP connection. Check that SSH is enabled and the VTY lines allow 'transport input ssh'.", ex),
            SocketException { SocketErrorCode: SocketError.TimedOut } => new SshCollectorException(FailureCategory.Timeout,
                $"TCP connection to {where} timed out.", ex),
            SocketException { SocketErrorCode: SocketError.HostUnreachable or SocketError.NetworkUnreachable } s => new SshCollectorException(
                FailureCategory.Unreachable, $"{where} is unreachable ({s.SocketErrorCode}).", ex),
            SocketException s => new SshCollectorException(FailureCategory.Other, $"Socket error connecting to {where}: {s.SocketErrorCode}.", ex),
            SshOperationTimeoutException => new SshCollectorException(FailureCategory.Timeout, $"SSH operation with {where} timed out.", ex),
            SshAuthenticationException => new SshCollectorException(FailureCategory.AuthFailure,
                $"{where} rejected the username or password ({ex.Message}).", ex),
            SshConnectionException { DisconnectReason: DisconnectReason.KeyExchangeFailed } => new SshCollectorException(FailureCategory.UnsupportedAlgorithm,
                $"SSH key exchange with {where} failed: {ex.Message}", ex),
            SshException when ex.Message.Contains("No matching", StringComparison.OrdinalIgnoreCase) => new SshCollectorException(
                FailureCategory.UnsupportedAlgorithm, $"No SSH algorithm in common with {where}: {ex.Message}", ex),
            SshException => new SshCollectorException(FailureCategory.Other, $"SSH error with {where}: {ex.Message}", ex),
            _ => null,
        };
    }
}

/// <param name="Host">IP address (or name) to connect to.</param>
/// <param name="PinnedHostKeyFingerprint">
/// "SHA256:…" of the key trusted for this node. Null means trust on first use: the key is accepted
/// and returned in <see cref="SshRunResult.HostKey"/> so the caller can pin it.
/// </param>
public sealed record SshTarget(
    string Host,
    int Port,
    SshCredential Credential,
    string? PinnedHostKeyFingerprint,
    TimeSpan ConnectTimeout,
    TimeSpan CommandTimeout);

public sealed record SshHostKey(string Algorithm, string Fingerprint, int KeyLength);

public sealed record SshSessionInfo(string ServerVersion, string KeyExchange, string HostKeyAlgorithm, string Cipher, string Mac);

public sealed record SshCommandOutput(string Command, string Output, int? ExitStatus);

public sealed record SshRunResult(SshHostKey HostKey, SshSessionInfo Session, IReadOnlyList<SshCommandOutput> Outputs);

public sealed class SshCollectorException(FailureCategory category, string message, Exception? inner = null, SshHostKey? presentedHostKey = null)
    : Exception(message, inner)
{
    public FailureCategory Category { get; } = category;

    /// <summary>The key the device offered, when the failure is <see cref="FailureCategory.HostKeyChanged"/>.</summary>
    public SshHostKey? PresentedHostKey { get; } = presentedHostKey;
}
