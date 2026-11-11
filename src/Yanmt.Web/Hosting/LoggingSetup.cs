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

        EventLogProviderAdded = EventSourceExists();
        if (EventLogProviderAdded)
        {
            builder.Logging.AddEventLog(new EventLogSettings { SourceName = EventSource, LogName = "Application" });
        }
    }

    /// <summary>True once <see cref="Configure"/> has added the event log provider (the source is registered).</summary>
    public static bool EventLogProviderAdded { get; private set; }

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
    /// Reports a fatal startup error everywhere it can be found: the daily log (CRIT, when the logger
    /// is up), logs\startup-failure.log (always: it is written synchronously, so it survives the
    /// immediate exit), and the Application event log if the source exists. When the logger is up and
    /// already has the event log provider, the CRIT line is the event log entry, so it isn't written twice.
    /// </summary>
    public static void ReportStartupFailure(Exception ex, ILogger? logger, string? logsDirectory, string? timings)
    {
        logger?.LogCritical(ex, "YANMT failed to start. Startup timings: {Timings}", timings);
        Console.Error.WriteLine($"YANMT failed to start: {ex}");
        if (logsDirectory is not null)
        {
            StartupFailureLog.Append(logsDirectory, ex, DateTimeOffset.UtcNow, timings);
        }

        if ((logger is null || !EventLogProviderAdded) && EventSourceExists())
        {
            try
            {
                var text = $"YANMT failed to start: {ex}";
                EventLog.WriteEntry(EventSource, text.Length > MaxEventText ? text[..MaxEventText] : text, EventLogEntryType.Error);
            }
            catch (Exception)
            {
                // Nothing left to report to.
            }
        }
    }

    // An event log entry holds at most 31,839 characters.
    private const int MaxEventText = 30_000;
}
