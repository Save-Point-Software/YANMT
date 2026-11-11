using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Extensions.Logging;
using Yanmt.Core.Security;

namespace Yanmt.Infrastructure.Security;

/// <summary>
/// Encrypts device credentials with DPAPI in machine scope. Machine scope alone would let any
/// process on the server decrypt the blobs, so every call also mixes in 32 random bytes from
/// <c>secrets.entropy</c> in the data folder. That file is created with the data folder's inheritable
/// ACL (which grants the service account) minus broad groups such as Users, plus the creating
/// account, SYSTEM, and Administrators, with inheritance off. So a file created by an admin's hand
/// run is still readable by the service. Losing it, or moving the DB to another server,
/// makes stored credentials unreadable; they must be re-entered or restored from a passphrase export.
/// </summary>
public sealed class SecretProtector
{
    private const byte FormatVersion = 1;
    private const int EntropyLength = 32;
    private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("YANMT device credentials v1");

    private readonly string _entropyPath;
    private readonly string _dataFolder;
    private readonly ILogger<SecretProtector> _logger;
    private readonly Lazy<byte[]> _entropy;

    public SecretProtector(YanmtPaths paths, ILogger<SecretProtector> logger)
    {
        _entropyPath = paths.EntropyFile;
        _dataFolder = paths.Root;
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
        var creating = false;
        try
        {
            if (!File.Exists(_entropyPath))
            {
                creating = true;
                if (TryCreateEntropyFile())
                {
                    CreatedNewKey = true;
                    _logger.LogWarning(
                        "Created a new credential key file at {Path}. Back it up with the database; credentials saved before it existed can't be decrypted.",
                        _entropyPath);
                }

                creating = false;
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
        catch (UnauthorizedAccessException ex) when (creating)
        {
            throw new InvalidOperationException(EntropyFileRules.CannotCreateMessage(_entropyPath, _dataFolder, CurrentAccount()), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new EntropyFileUnreadableException(_entropyPath, CurrentAccount(), ex);
        }
    }

    internal static string CurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.Name;
    }

    /// <summary>Creates the file with a protected ACL. Returns false if another process created it first.</summary>
    private bool TryCreateEntropyFile()
    {
        using var identity = WindowsIdentity.GetCurrent();
        FileSecurity security;
        try
        {
            security = BuildEntropyFileSecurity(new DirectoryInfo(_dataFolder).GetAccessControl(AccessControlSections.Access), identity.User!);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            // Can't read the folder's ACL: fall back to the creator, SYSTEM, and Administrators only.
            _logger.LogWarning(ex, "Cannot read the ACL of {Folder}; {Path} will be readable only by {Account}, SYSTEM and Administrators.",
                _dataFolder, _entropyPath, identity.Name);
            security = BuildEntropyFileSecurity(null, identity.User!);
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

    /// <summary>
    /// The entropy file's ACL: what the file would inherit from <paramref name="folder"/> (so the service
    /// account the data folder grants can read it, whoever creates the file), without allow entries for
    /// broad groups such as Users, plus <paramref name="creator"/>, SYSTEM and Administrators. Inheritance
    /// is off, so a later change to the folder doesn't widen it; a folder <c>icacls ... /T</c> still applies.
    /// </summary>
    public static FileSecurity BuildEntropyFileSecurity(DirectorySecurity? folder, SecurityIdentifier creator)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (folder is not null)
        {
            foreach (FileSystemAccessRule rule in folder.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
            {
                if (!rule.InheritanceFlags.HasFlag(InheritanceFlags.ObjectInherit))
                {
                    continue; // applies to subfolders only, never to files
                }

                var sid = (SecurityIdentifier)rule.IdentityReference;
                if (sid.Value == EntropyFileRules.CreatorOwnerSid)
                {
                    sid = creator;
                }

                if (rule.AccessControlType == AccessControlType.Allow && EntropyFileRules.IsBroadPrincipal(sid.Value))
                {
                    continue;
                }

                var rights = (FileSystemRights)EntropyFileRules.MapGenericFileRights((int)rule.FileSystemRights);
                security.AddAccessRule(new FileSystemAccessRule(sid, rights, rule.AccessControlType));
            }
        }

        foreach (var sid in new[]
                 {
                     creator,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                 })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }
}

/// <summary>
/// secrets.entropy exists but this account can't read it. The message names the account and the
/// icacls fix; startup adds whether credentials are saved (and so whether deleting it is safe).
/// </summary>
public sealed class EntropyFileUnreadableException(string path, string account, Exception inner)
    : InvalidOperationException(EntropyFileRules.UnreadableMessage(path, account, savedCredentialProfiles: null), inner)
{
    public string Path { get; } = path;
    public string Account { get; } = account;
}

public sealed class SecretUnreadableException(string message, Exception? inner = null) : Exception(message, inner);
