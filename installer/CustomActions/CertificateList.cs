using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

/// <summary>
/// The settings dialog's certificate dropdown (issue #49, M2). Its property is YANMT_CERT_CHOICE: a thumbprint,
/// or <see cref="SelfSignedChoice"/>. ValidateSettingsUi turns it into CERT_THUMBPRINT or CREATE_SELF_SIGNED_CERT.
/// </summary>
public static class CertificateList
{
    public const string ChoiceProperty = "YANMT_CERT_CHOICE";
    public const string SelfSignedChoice = "SELF_SIGNED";

    /// <summary>The static first row (Ui.wxs; ICE17 needs one): "Choose a certificate...". Means nothing chosen.</summary>
    public const string NoChoice = "NONE";

    private const string SelfSignedText = "Create a self-signed certificate for this server (for testing / lab)";

    /// <summary>The ComboBox table's Text column is 64 characters; longer text is tried first, then cut.</summary>
    private const int ShortTextLength = 64;

    /// <summary>
    /// Immediate, UI sequence after CostFinalize (before the first dialog). Lists the LocalMachine\My certificates
    /// that have a private key and aren't expired, plus the stored or given thumbprint even if it doesn't qualify
    /// (so an upgrade or repair shows it selected, and Next explains what's wrong with it). The self-signed choice is
    /// offered only when no thumbprint is stored: an upgrade, repair or change never creates a second certificate.
    /// </summary>
    [CustomAction]
    public static ActionResult ListCertificates(Session session)
    {
        try
        {
            var current = InstallSettings.NormalizeThumbprint(session["CERT_THUMBPRINT"] ?? "", out _);
            var canCreate = string.IsNullOrWhiteSpace(session["YANMT_STORED_CERT_THUMBPRINT"]);
            var entries = new List<(string Value, string Text, string Short)>();
            var now = DateTime.Now;
            var currentFound = false;

            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var cert in store.Certificates.Cast<X509Certificate2>()
                             .OrderBy(c => Name(c), StringComparer.OrdinalIgnoreCase).ThenByDescending(c => c.NotAfter))
                {
                    using (cert)
                    {
                        var thumbprint = cert.Thumbprint.ToUpperInvariant();
                        var isCurrent = thumbprint == current;
                        var problem = !cert.HasPrivateKey ? "no private key" : cert.NotAfter <= now ? "expired" : null;
                        if (problem != null && !isCurrent)
                        {
                            continue;
                        }

                        currentFound |= isCurrent;
                        var name = Name(cert);
                        var suffix = problem is null ? "" : $" ({problem})";
                        entries.Add((thumbprint,
                            $"{name}  |  expires {cert.NotAfter:yyyy-MM-dd}  |  {thumbprint}{suffix}",
                            $"{Cut(name, 12)} {cert.NotAfter:yyyy-MM-dd} {thumbprint}"));
                    }
                }
            }

            if (current != null && !currentFound)
            {
                entries.Add((current, $@"{current}  (not in LocalMachine\My)", current + " (missing)"));
            }

            if (canCreate)
            {
                entries.Add((SelfSignedChoice, SelfSignedText, SelfSignedText));
            }

            Fill(session, entries);

            // Pre-select: the stored (or given) thumbprint; else the self-signed choice if asked for, or if it's the only one.
            session[ChoiceProperty] =
                current != null ? current
                : canCreate && (session["CREATE_SELF_SIGNED_CERT"] == "1" || entries.Count == 1) ? SelfSignedChoice
                : NoChoice;
            session.Log($"YANMT: certificate list: {entries.Count(e => e.Value != SelfSignedChoice)} certificate(s)" +
                (canCreate ? " and the self-signed choice" : " (a thumbprint is stored, so no self-signed choice)") +
                $"; selected [{session[ChoiceProperty]}].");
        }
        catch (Exception ex)
        {
            // The dialog still opens; Next then says no certificate was chosen.
            session.Log("YANMT: could not list the certificates: " + ex.Message);
        }

        return ActionResult.Success;
    }

    private static void Fill(Session session, List<(string Value, string Text, string Short)> entries)
    {
        using var view = session.Database.OpenView("SELECT `Property`, `Order`, `Value`, `Text` FROM `ComboBox`");
        view.Execute();
        var order = 2;   // 1 is the static "Choose a certificate..." row.
        foreach (var (value, text, shortText) in entries)
        {
            using var record = session.Database.CreateRecord(4);
            record.SetString(1, ChoiceProperty);
            record.SetInteger(2, order++);
            record.SetString(3, value);
            record.SetString(4, text);
            try
            {
                view.Modify(ViewModifyMode.InsertTemporary, record);
            }
            catch (InstallerException)
            {
                record.SetString(4, Cut(shortText, ShortTextLength));
                view.Modify(ViewModifyMode.InsertTemporary, record);
            }
        }
    }

    /// <summary>The first DNS name (SAN, else the subject's CN), else the subject.</summary>
    private static string Name(X509Certificate2 cert)
    {
        var dns = cert.GetNameInfo(X509NameType.DnsName, forIssuer: false);
        var name = string.IsNullOrEmpty(dns) ? cert.Subject : dns;
        return Cut(string.IsNullOrEmpty(name) ? "(no subject)" : name, 40);
    }

    private static string Cut(string text, int length) => text.Length <= length ? text : text.Substring(0, length - 1) + "~";
}
