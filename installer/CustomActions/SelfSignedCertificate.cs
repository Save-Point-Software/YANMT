using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>
/// CREATE_SELF_SIGNED_CERT=1 (issue #49, M2): a certificate for test and lab servers, made by the installer as
/// LocalSystem in LocalMachine\My. PrepareInstall picks a random serial number before anything runs, so the
/// rollback action can find (and remove) exactly the certificate and key this install created, even though
/// its thumbprint is only known once it exists.
/// </summary>
public static class SelfSignedCertificate
{
    public const string FriendlyName = "YANMT (self-signed)";

    private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
    private const int KeySize = 3072;

    /// <summary>The same key as Package.wxs's SettingsKey; the app reads CertificateThumbprint from it.</summary>
    private const string SettingsKey = @"SOFTWARE\YANMT";

    /// <summary>A positive 128-bit serial number, as upper-case hex (X509Certificate2.SerialNumber's form).</summary>
    internal static string NewSerial()
    {
        var bytes = new byte[16];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(bytes);
        }

        // Top bit clear (positive) and the first byte non-zero (no leading zero that DER would drop).
        bytes[0] = (byte)((bytes[0] & 0x7F) | 0x40);
        return string.Concat(bytes.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }

    internal static string KeyName(string serial) => "YANMT-self-signed-" + serial;

    /// <summary>
    /// Deferred, as LocalSystem, before GrantKeyRead. RSA 3072 in a non-exportable machine key (Microsoft Software
    /// Key Storage Provider), Server Authentication EKU, SAN with the FQDN and computer name, valid for two years.
    /// Writes the thumbprint to HKLM\SOFTWARE\YANMT, overwriting the empty value the InstallerSettings component
    /// wrote (CERT_THUMBPRINT was empty when the script was built). The rollback is RemoveSelfSignedCert.
    /// </summary>
    [CustomAction]
    public static ActionResult CreateSelfSignedCert(Session session)
    {
        var serial = session.CustomActionData["Serial"];
        var names = MachineNames();
        CngKey? key = null;
        try
        {
            var parameters = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = CngKeyCreationOptions.MachineKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.AllUsages,
            };
            parameters.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(KeySize), CngPropertyOptions.None));
            key = CngKey.Create(CngAlgorithm.Rsa, KeyName(serial), parameters);

            string thumbprint;
            DateTime notAfter;
            using (var rsa = new RSACng(key))
            {
                var subject = new X500DistinguishedName("CN=" + names[0]);
                var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid(ServerAuthenticationOid, "Server Authentication") }, false));
                var san = new SubjectAlternativeNameBuilder();
                foreach (var name in names)
                {
                    san.AddDnsName(name);
                }

                request.CertificateExtensions.Add(san.Build());
                request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

                // A few minutes back, so a client whose clock is slightly behind doesn't see it as not yet valid.
                var notBefore = DateTimeOffset.UtcNow.AddMinutes(-10);
                using var signed = request.Create(subject, X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1),
                    notBefore, notBefore.AddYears(2), HexToBytes(serial));

                // Linked to the persisted machine key (not a copy of it): the key itself is non-exportable.
                using var withKey = signed.CopyWithPrivateKey(rsa);
                withKey.FriendlyName = FriendlyName;
                using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                    store.Add(withKey);
                }

                thumbprint = withKey.Thumbprint.ToUpperInvariant();
                notAfter = withKey.NotAfter;
            }

            // Read back from the store, as GrantKeyRead and the service will open it.
            using (var stored = Find(serial))
            {
                if (stored is null || !stored.HasPrivateKey || !string.Equals(stored.Thumbprint, thumbprint, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(session, $@"Created the self-signed certificate {thumbprint}, but LocalMachine\My doesn't show it with its private key.");
                }
            }

            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var settings = hklm.CreateSubKey(SettingsKey))
            {
                settings.SetValue("CertificateThumbprint", thumbprint, RegistryValueKind.String);
            }

            session.Log($@"YANMT: created the self-signed certificate {thumbprint} in LocalMachine\My: CN={names[0]}, " +
                $"SAN DNS [{string.Join(", ", names)}], RSA {KeySize} (non-exportable), Server Authentication, " +
                $"expires {notAfter:yyyy-MM-dd}, serial {serial}, friendly name \"{FriendlyName}\".");
            return ActionResult.Success;
        }
        catch (Exception ex)
        {
            // RemoveSelfSignedCert (rollback) removes the certificate and the key; this only adds the reason.
            return Fail(session, "Could not create the self-signed certificate: " + ex.Message);
        }
        finally
        {
            key?.Dispose();
        }
    }

    /// <summary>
    /// Rollback of CreateSelfSignedCert: removes the certificate with this install's serial number and friendly
    /// name, and its key. Never fails (a rollback action must not), and touches nothing else.
    /// </summary>
    [CustomAction]
    public static ActionResult RemoveSelfSignedCert(Session session)
    {
        var serial = session.CustomActionData["Serial"];
        try
        {
            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
                foreach (var cert in store.Certificates.Cast<X509Certificate2>().ToList())
                {
                    using (cert)
                    {
                        if (IsOurs(cert, serial))
                        {
                            store.Remove(cert);
                            session.Log($"YANMT: rollback: removed the self-signed certificate {cert.Thumbprint} this install created.");
                        }
                    }
                }
            }

            if (CngKey.Exists(KeyName(serial), CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey))
            {
                using var key = CngKey.Open(KeyName(serial), CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.MachineKey);
                key.Delete();
                session.Log($"YANMT: rollback: deleted the key {KeyName(serial)}.");
            }
        }
        catch (Exception ex)
        {
            session.Log($"YANMT: rollback: could not remove the self-signed certificate (serial {serial}): {ex.Message}");
        }

        return ActionResult.Success;
    }

    /// <summary>The certificate this install created, or null.</summary>
    internal static X509Certificate2? Find(string serial)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        X509Certificate2? found = null;
        foreach (var cert in store.Certificates.Cast<X509Certificate2>())
        {
            if (found is null && IsOurs(cert, serial))
            {
                found = cert;
            }
            else
            {
                cert.Reset();
            }
        }

        return found;
    }

    private static bool IsOurs(X509Certificate2 cert, string serial) =>
        string.Equals(cert.SerialNumber, serial, StringComparison.OrdinalIgnoreCase)
        && cert.FriendlyName == FriendlyName;

    /// <summary>The FQDN first (the subject), then the computer name if it differs.</summary>
    internal static List<string> MachineNames()
    {
        var names = new List<string>();
        try
        {
            var ip = IPGlobalProperties.GetIPGlobalProperties();
            if (!string.IsNullOrEmpty(ip.HostName))
            {
                names.Add(string.IsNullOrEmpty(ip.DomainName) ? ip.HostName : ip.HostName + "." + ip.DomainName);
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to the computer name alone.
        }

        if (!names.Contains(Environment.MachineName, StringComparer.OrdinalIgnoreCase))
        {
            names.Add(Environment.MachineName);
        }

        return names;
    }

    private static byte[] HexToBytes(string hex) =>
        Enumerable.Range(0, hex.Length / 2)
            .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
            .ToArray();

    private static ActionResult Fail(Session session, string message)
    {
        session.Log("YANMT: " + message);
        SettingsActions.Fail(session, message);
        return ActionResult.Failure;
    }
}
