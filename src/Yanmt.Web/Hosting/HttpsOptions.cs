using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Yanmt.Web.Hosting;

/// <summary>Bound from "Yanmt:Https".</summary>
public sealed class HttpsOptions
{
    public const string SectionName = "Yanmt:Https";

    public int Port { get; set; } = 8443;

    /// <summary>IP address to bind, or empty for all interfaces.</summary>
    public string? ListenAddress { get; set; }

    /// <summary>Thumbprint of the server certificate in the Windows certificate store.</summary>
    public string? CertificateThumbprint { get; set; }

    public StoreName StoreName { get; set; } = StoreName.My;
    public StoreLocation StoreLocation { get; set; } = StoreLocation.LocalMachine;
}

public static class CertificateLoader
{
    /// <summary>
    /// Loads the HTTPS certificate from the Windows store and checks that this process can use its
    /// private key. Every failure throws with a message saying exactly what to fix.
    /// Returns null only in Development with no thumbprint set, meaning "use the ASP.NET dev cert".
    /// </summary>
    public static X509Certificate2? Load(HttpsOptions options, IHostEnvironment environment)
    {
        var thumbprint = new string((options.CertificateThumbprint ?? "").Where(char.IsAsciiHexDigit).ToArray()).ToUpperInvariant();
        if (thumbprint.Length == 0)
        {
            if (environment.IsDevelopment())
            {
                return null;
            }

            throw new InvalidOperationException(
                "Yanmt:Https:CertificateThumbprint is not set. Put the thumbprint of a certificate in " +
                $"{options.StoreLocation}\\{options.StoreName} into appsettings.json.");
        }

        using var store = new X509Store(options.StoreName, options.StoreLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"No certificate with thumbprint {thumbprint} in {options.StoreLocation}\\{options.StoreName}.");
        }

        var cert = matches[0];
        var now = DateTime.Now;
        if (now < cert.NotBefore || now > cert.NotAfter)
        {
            throw new InvalidOperationException(
                $"Certificate {thumbprint} is not valid now (valid {cert.NotBefore:u} to {cert.NotAfter:u}).");
        }

        if (!cert.HasPrivateKey)
        {
            throw new InvalidOperationException($"Certificate {thumbprint} has no private key in the store.");
        }

        try
        {
            // Opening the key is what fails when the service account has no read ACL on it.
            using var key = (AsymmetricAlgorithm?)cert.GetRSAPrivateKey() ?? cert.GetECDsaPrivateKey();
            if (key is null)
            {
                throw new InvalidOperationException($"Certificate {thumbprint} uses an unsupported key type (need RSA or ECDSA).");
            }
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Cannot open the private key of certificate {thumbprint} as {Environment.UserDomainName}\\{Environment.UserName}. " +
                "Grant the service account Read on the key (certlm.msc > certificate > All Tasks > Manage Private Keys).", ex);
        }

        return cert;
    }
}
