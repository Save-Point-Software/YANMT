using System.Diagnostics;
using System.Globalization;
using Yanmt.Core.Startup;

namespace Yanmt.Infrastructure.Logging;

/// <summary>
/// Startup timing written to logs\startup.log as each phase ends, so a slow or hung start (which
/// Windows only reports as a 1053/7009 timeout) shows how far it got and where the time went.
/// Marks made before the logs folder is known are buffered and written when <see cref="Attach"/> is called.
/// </summary>
public sealed class StartupTrace
{
    public const string FileName = "startup.log";
    private const long MaxFileBytes = 1024 * 1024;

    private readonly Func<DateTimeOffset> _now;
    private readonly StartupTimeline _timeline = new();
    private readonly List<string> _pending = [];
    private readonly Lock _lock = new();
    private string? _path;

    public StartupTrace(DateTimeOffset processStartedUtc, Func<DateTimeOffset>? now = null)
    {
        ProcessStartedUtc = processStartedUtc;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Starts a trace for this process and marks the runtime phase (process creation to Main).</summary>
    public static StartupTrace ForCurrentProcess()
    {
        DateTimeOffset started;
        try
        {
            using var process = Process.GetCurrentProcess();
            started = process.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            started = DateTimeOffset.UtcNow;
        }

        var trace = new StartupTrace(started);
        trace.Mark("runtime start (incl. single-file bundle)");
        return trace;
    }

    public DateTimeOffset ProcessStartedUtc { get; }

    public TimeSpan Elapsed => _now() - ProcessStartedUtc;

    /// <summary>Starts writing to logs\startup.log: a header line, then every buffered mark.</summary>
    public void Attach(string logsDirectory, string header)
    {
        lock (_lock)
        {
            _path = Path.Combine(logsDirectory, FileName);
            try
            {
                var file = new FileInfo(_path);
                if (file.Exists && file.Length > MaxFileBytes)
                {
                    File.Move(_path, Path.Combine(logsDirectory, "startup.1.log"), overwrite: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Rotation is best effort.
            }

            var lines = new List<string>
            {
                $"{Stamp(_now())} ---- process started {Stamp(ProcessStartedUtc)}: {header}",
            };
            lines.AddRange(_pending);
            _pending.Clear();
            Append(lines);
        }
    }

    /// <summary>Records that a phase just ended, and writes it straight to the file.</summary>
    /// <returns>How long the phase took.</returns>
    public TimeSpan Mark(string phase)
    {
        var now = _now();
        var since = now - ProcessStartedUtc;
        var took = _timeline.Mark(phase, since);
        Write($"{Stamp(now)} +{StartupTimeline.Seconds(since)} {phase} (took {StartupTimeline.Seconds(took)})");
        return took;
    }

    /// <summary>A free-form line (a warning, or the outcome).</summary>
    public void Note(string text) => Write($"{Stamp(_now())} {text}");

    /// <summary>"4.2 s total: runtime start 0.6 s, config 0.1 s, ..."</summary>
    public string Summary() => _timeline.Summary();

    private void Write(string line)
    {
        lock (_lock)
        {
            if (_path is null)
            {
                _pending.Add(line);
            }
            else
            {
                Append([line]);
            }
        }
    }

    private void Append(IEnumerable<string> lines)
    {
        try
        {
            File.AppendAllLines(_path!, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The trace must never stop startup.
        }
    }

    private static string Stamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
}
