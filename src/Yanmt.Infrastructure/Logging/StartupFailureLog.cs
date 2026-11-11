using System.Globalization;

namespace Yanmt.Infrastructure.Logging;

/// <summary>
/// logs\startup-failure.log: every fatal startup error, whether or not logging was up yet.
/// It is written synchronously, so it survives the process exiting straight afterwards.
/// </summary>
public static class StartupFailureLog
{
    public const string FileName = "startup-failure.log";

    /// <returns>False if the file couldn't be written (the data folder isn't writable either).</returns>
    public static bool Append(string logsDirectory, Exception ex, DateTimeOffset now, string? timings = null)
    {
        var stamp = now.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var text = $"{stamp}Z YANMT failed to start: {ex}{Environment.NewLine}";
        if (!string.IsNullOrEmpty(timings))
        {
            text += $"    Startup timings: {timings}{Environment.NewLine}";
        }

        try
        {
            Directory.CreateDirectory(logsDirectory);
            File.AppendAllText(Path.Combine(logsDirectory, FileName), text);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
