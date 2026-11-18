using WixToolset.Dtf.WindowsInstaller;

namespace Yanmt.Installer.CustomActions;

internal static class SessionLog
{
    /// <summary>
    /// Writes <paramref name="message"/> to the MSI log as is. Session.Log formats it as MSI formatted text, so
    /// "[.\YANMT-Admins]" was read as a property reference and logged as empty (issue #49, M4). Here the text is
    /// a record field, and field values aren't formatted again.
    /// </summary>
    public static void LogRaw(this Session session, string message)
    {
        using var record = new Record(1) { FormatString = "[1]" };
        record[1] = message;
        session.Message(InstallMessage.Info, record);
    }
}
