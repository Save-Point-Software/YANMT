using System;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>Deferred, as LocalSystem: lets NT SERVICE\YANMT read the HTTPS certificate's private key.</summary>
public static class CertificateActions
{
    /// <summary>NCRYPT_SECURITY_DESCR_PROPERTY.</summary>
    private const string SecurityDescriptorProperty = "Security Descr";

    /// <summary>DACL_SECURITY_INFORMATION, passed in the property options.</summary>
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)0x4;

    /// <summary>
    /// Grants Read (GENERIC_READ, what certlm.msc's "Read" sets) on the key. CNG keys are changed through
    /// NCrypt's security descriptor, not by editing the key file, which didn't take effect in the v1
    /// shakedown. Legacy CSP keys go through the CSP. The grant is then read back; the install fails
    /// if it isn't there.
    /// </summary>
    [CustomAction]
    public static ActionResult GrantKeyRead(Session session)
    {
        var thumbprint = session.CustomActionData["Thumbprint"];
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var cert = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false)
            .Cast<X509Certificate2>().FirstOrDefault();
        if (cert is null || !cert.HasPrivateKey)
        {
            return Fail(session, $@"Certificate {thumbprint} with a private key not found in LocalMachine\My.");
        }

        using (cert)
        {
            string kind;
            bool granted;
            using var rsa = cert.GetRSAPrivateKey();
            using var ecdsa = rsa is null ? cert.GetECDsaPrivateKey() : null;
            if (rsa is RSACng rsaCng)
            {
                kind = "CNG (RSA)";
                granted = GrantCng(rsaCng.Key);
            }
            else if (ecdsa is ECDsaCng ecdsaCng)
            {
                kind = "CNG (ECDSA)";
                granted = GrantCng(ecdsaCng.Key);
            }
            else if (rsa is RSACryptoServiceProvider csp)
            {
                kind = "legacy CSP (RSA)";
                granted = GrantCsp(csp.CspKeyContainerInfo);
            }
            else
            {
                return Fail(session, $"The private key of certificate {thumbprint} is not RSA or ECDSA, or can't be opened. " +
                    "Grant NT SERVICE\\YANMT Read on it in certlm.msc (Manage Private Keys), then repair the install.");
            }

            if (!granted)
            {
                return Fail(session, $"Granted {ServiceIdentity.Account} Read on the {kind} private key of certificate {thumbprint}, " +
                    "but reading the key's permissions back doesn't show the grant. Grant it in certlm.msc (Manage Private Keys), then repair the install.");
            }

            session.Log($"YANMT: {ServiceIdentity.Account} can read the {kind} private key of certificate {thumbprint} (checked).");
        }

        return ActionResult.Success;
    }

    private static bool GrantCng(CngKey key)
    {
        var security = ReadCng(key);
        security.AddAccessRule(new CryptoKeyAccessRule(ServiceIdentity.Sid, CryptoKeyRights.GenericRead, AccessControlType.Allow));
        key.SetProperty(new CngProperty(SecurityDescriptorProperty,
            security.GetSecurityDescriptorBinaryForm(), CngPropertyOptions.Persist | DaclSecurityInformation));
        return CanRead(ReadCng(key));
    }

    private static CryptoKeySecurity ReadCng(CngKey key)
    {
        var security = new CryptoKeySecurity();
        security.SetSecurityDescriptorBinaryForm(
            key.GetProperty(SecurityDescriptorProperty, DaclSecurityInformation).GetValue(), AccessControlSections.Access);
        return security;
    }

    private static bool GrantCsp(CspKeyContainerInfo info)
    {
        var security = info.CryptoKeySecurity;
        security.AddAccessRule(new CryptoKeyAccessRule(ServiceIdentity.Sid, CryptoKeyRights.GenericRead, AccessControlType.Allow));
        using (new RSACryptoServiceProvider(CspFor(info, security)))
        {
            // Opening the container with CryptoKeySecurity set writes it.
        }

        using var reopened = new RSACryptoServiceProvider(CspFor(info, null));
        return CanRead(reopened.CspKeyContainerInfo.CryptoKeySecurity);
    }

    private static CspParameters CspFor(CspKeyContainerInfo info, CryptoKeySecurity? security) =>
        new(info.ProviderType, info.ProviderName, info.KeyContainerName)
        {
            Flags = CspProviderFlags.UseMachineKeyStore | CspProviderFlags.UseExistingKey,
            KeyNumber = (int)info.KeyNumber,
            CryptoKeySecurity = security,
        };

    private static bool CanRead(CryptoKeySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<CryptoKeyAccessRule>()
            .Any(r => r.AccessControlType == AccessControlType.Allow
                && ServiceIdentity.Sid.Equals(r.IdentityReference)
                && (r.CryptoKeyRights & (CryptoKeyRights.GenericRead | CryptoKeyRights.GenericAll | CryptoKeyRights.FullControl | CryptoKeyRights.ReadData)) != 0);

    private static ActionResult Fail(Session session, string message)
    {
        session.Log("YANMT: " + message);
        SettingsActions.Fail(session, message);
        return ActionResult.Failure;
    }
}
