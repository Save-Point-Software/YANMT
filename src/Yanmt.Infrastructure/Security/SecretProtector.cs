using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Yanmt.Infrastructure.Security;

/// <summary>
/// Encrypts device credentials with DPAPI in machine scope. Machine scope alone would let any
/// process on the server decrypt the blobs, so every call also mixes in 32 random bytes from
/// <c>secrets.entropy</c> in the data folder. That file is created with an ACL limited to the
/// service account, SYSTEM, and Administrators. Losing it, or moving the DB to another server,
/// makes stored credentials unreadable; they must be re-entered or restored from a passphrase export.
/// </summary>
public sealed class SecretProtector
{
    private const byte FormatVersion = 1;
    private const int EntropyLength = 32;
    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("YANMT device credentials v1");

    private readonly string _entropyPath;
    private readonly ILogger<SecretProtector> _logger;
    private readonly Lazy<byte[]> _entropy;

    public SecretProtector(YanmtPaths paths, ILogger<SecretProtector> logger)
    {
        _entropyPath = paths.EntropyFile;
        _logger = logger;
        _entropy = new Lazy<byte[]>(LoadOrCreateEntropy, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>True when this process created a new entropy file (first run, or the old one was lost).</summary>
    public bool CreatedNewKey { get; private set; }

    /// <summary>Loads or creates the entropy file now, so ACL problems fail at startup with a clear message.</summary>
    public void EnsureReady() => _ = _entropy.Value;

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var data = plaintext.ToArray();
        try
        {
            var blob = ProtectedData.Protect(data, Entropy(), DataProtectionScope.LocalMachine);
            var result = new byte[blob.Length + 1];
            result[0] = FormatVersion;
            blob.CopyTo(result, 1);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    /// <exception cref="SecretUnreadableException">The blob can't be decrypted on this server.</exception>
    public byte[] Unprotect(byte[] protectedBlob)
    {
        if (protectedBlob.Length < 2 || protectedBlob[0] != FormatVersion)
        {
            throw new SecretUnreadableException("The stored credential has an unknown format.");
        }

        try
        {
            return ProtectedData.Unprotect(protectedBlob.AsSpan(1).ToArray(), Entropy(), DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            throw new SecretUnreadableException(
                "The stored credential can't be decrypted on this server. It was probably encrypted on another machine " +
                "or with a different secrets.entropy file. Re-enter the password(s), or import a passphrase export.", ex);
        }
    }

    private byte[] Entropy()
    {
        var file = _entropy.Value;
        var entropy = new byte[Purpose.Length + file.Length];
        Purpose.CopyTo(entropy, 0);
        file.CopyTo(entropy, Purpose.Length);
        return entropy;
    }

    private byte[] LoadOrCreateEntropy()
    {
        try
        {
            if (!File.Exists(_entropyPath))
            {
                if (TryCreateEntropyFile())
                {
                    CreatedNewKey = true;
                    _logger.LogWarning(
                        "Created a new credential key file at {Path}. Back it up with the database; credentials saved before it existed can't be decrypted.",
                        _entropyPath);
                }
            }

            var bytes = File.ReadAllBytes(_entropyPath);
            if (bytes.Length != EntropyLength)
            {
                // Never regenerate silently: that would orphan every stored credential.
                throw new InvalidOperationException(
                    $"{_entropyPath} is {bytes.Length} bytes; expected {EntropyLength}. Restore it from backup, or delete it and re-enter all credentials.");
            }

            return bytes;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(
                $"Cannot read or create {_entropyPath}. The YANMT service account needs read access to it (and Modify on the data folder to create it).", ex);
        }
    }

    /// <summary>Creates the file with a protected ACL. Returns false if another process created it first.</summary>
    private bool TryCreateEntropyFile()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var self = WindowsIdentity.GetCurrent().User!;
        foreach (var sid in new[]
                 {
                     self,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 }.Distinct())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        var entropy = RandomNumberGenerator.GetBytes(EntropyLength);
        try
        {
            using var stream = new FileInfo(_entropyPath).Create(
                FileMode.CreateNew, FileSystemRights.Write | FileSystemRights.Read, FileShare.None, 4096, FileOptions.WriteThrough, security);
            stream.Write(entropy);
            return true;
        }
        catch (IOException) when (File.Exists(_entropyPath))
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }
}

public sealed class SecretUnreadableException(string message, Exception? inner = null) : Exception(message, inner);
