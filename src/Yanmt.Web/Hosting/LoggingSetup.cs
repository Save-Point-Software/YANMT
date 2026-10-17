using System.Diagnostics;
using System.Security;
using Microsoft.Extensions.Logging.EventLog;
using Yanmt.Infrastructure;
using Yanmt.Infrastructure.Logging;

namespace Yanmt.Web.Hosting;

public static class LoggingSetup
{
    public const string EventSource = "YANMT";

    /// <summary>
    /// Console (useful interactively), rotating files in the data folder, and the Windows
    /// Application event log for service-level errors. The event source must be registered by the
    /// installer (creating it needs admin rights); without it, event log output is skipped rather
    /// than failing every log call.
    /// </summary>
    public static void Configure(WebApplicationBuilder builder, YanmtPaths paths)
    {
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        if (builder.Environment.IsDevelopment())
        {
            builder.Logging.AddDebug();
        }

        var fileOptions = builder.Configuration.GetSection("Yanmt:Logging").Get<RollingFileLoggerOptions>() ?? new();
        fileOptions.Directory = paths.Logs;
        builder.Services.AddSingleton<ILoggerProvider>(_ => new RollingFileLoggerProvider(fileOptions));

        if (EventSourceExists())
        {
            builder.Logging.AddEventLog(new EventLogSettings { SourceName = EventSource, LogName = "Application" });
        }
    }

    public static bool EventSourceExists()
    {
        try
        {
            return EventLog.SourceExists(EventSource);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Non-admins can't enumerate every log; an unregistered source ends up here.
            return false;
        }
    }

    /// <summary>
    /// Last-resort reporting for failures before or during host startup. When the logger isn't
    /// built yet (e.g. a bad certificate), the console is invisible under the service manager,
    /// so the error also goes to logs\startup-failure.log and the event log if the source exists.
    /// </summary>
    public static void ReportStartupFailure(Exception ex, ILogger? logger, string? logsDirectory)
    {
        logger?.LogCritical(ex, "YANMT failed to start");
        Console.Error.WriteLine($"YANMT failed to start: {ex}");
        if (logger is null && logsDirectory is not null)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(logsDirectory, "startup-failure.log"),
                    $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z YANMT failed to start: {ex}{Environment.NewLine}");
            }
            catch (Exception)
            {
                // Data folder not writable either; the event log is the last option.
            }
        }

        if (logger is null && EventSourceExists())
        {
            try
            {
                EventLog.WriteEntry(EventSource, $"YANMT failed to start: {ex}", EventLogEntryType.Error);
            }
            catch (Exception)
            {
                // Nothing left to report to.
            }
        }
    }
}
