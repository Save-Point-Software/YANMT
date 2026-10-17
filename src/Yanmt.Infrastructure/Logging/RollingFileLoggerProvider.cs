using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Yanmt.Infrastructure.Logging;

public sealed class RollingFileLoggerOptions
{
    public string Directory { get; set; } = "logs";
    public string FilePrefix { get; set; } = "yanmt";
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Total log files kept across all days; the oldest are deleted first.</summary>
    public int RetainedFileCount { get; set; } = 30;
}

/// <summary>
/// Writes log lines to {prefix}-yyyyMMdd.log, rolling to a new file each UTC day and whenever
/// the current file would exceed the size limit ({prefix}-yyyyMMdd.1.log, .2.log, ...).
/// Logging calls only enqueue; a single background task does the file I/O, so a slow disk
/// never stalls a poller. Filtering by level comes from the standard "Logging:File" config section.
/// </summary>
[ProviderAlias("File")]
public sealed class RollingFileLoggerProvider : ILoggerProvider
{
    private readonly RollingFileLoggerOptions _options;
    private readonly TimeProvider _clock;

    // Items are log lines (string) or flush markers (TaskCompletionSource).
    private readonly Channel<object> _queue = Channel.CreateBounded<object>(
        new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Task _writer;

    private StreamWriter? _stream;
    private long _currentSize;
    private DateOnly _currentDay;

    public RollingFileLoggerProvider(RollingFileLoggerOptions options, TimeProvider? clock = null)
    {
        _options = options;
        _clock = clock ?? TimeProvider.System;
        System.IO.Directory.CreateDirectory(options.Directory);
        _writer = Task.Run(WriteLoopAsync);
    }

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(name, this));

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        try
        {
            _writer.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Never let logging shutdown take the process down.
        }

        _stream?.Dispose();
    }

    /// <summary>Completes once everything enqueued before the call has been written and flushed.</summary>
    internal Task FlushAsync()
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _queue.Writer.TryWrite(marker) ? marker.Task : Task.CompletedTask;
    }

    private void Enqueue(string line) => _queue.Writer.TryWrite(line);

    private async Task WriteLoopAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            if (item is TaskCompletionSource marker)
            {
                if (_stream is not null)
                {
                    await _stream.FlushAsync();
                }

                marker.TrySetResult();
                continue;
            }

            var line = (string)item;
            try
            {
                var bytes = Encoding.UTF8.GetByteCount(line);
                EnsureFile(bytes);
                await _stream!.WriteAsync(line);
                _currentSize += bytes;
                if (_queue.Reader.Count == 0)
                {
                    await _stream.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Disk full, file locked by a scanner, etc. Drop the line and reopen on the next one.
                _stream?.Dispose();
                _stream = null;
            }
        }
    }

    private void EnsureFile(int nextLineBytes)
    {
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        if (_stream is not null && today == _currentDay
            && (_currentSize == 0 || _currentSize + nextLineBytes <= _options.MaxFileSizeBytes))
        {
            return;
        }

        var rollingBecauseFull = _stream is not null && today == _currentDay;
        _stream?.Dispose();
        _stream = null;

        var path = NextPath(today, nextLineBytes, forceNew: rollingBecauseFull);
        var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _currentSize = file.Length;
        _stream = new StreamWriter(file, new UTF8Encoding(false));
        _currentDay = today;
        PruneOldFiles();
    }

    /// <summary>
    /// Sequence numbers only move forward within a day: reuse the newest file if it has room
    /// (e.g. after a restart), otherwise start the next number.
    /// </summary>
    private string NextPath(DateOnly day, int nextLineBytes, bool forceNew)
    {
        var stem = $"{_options.FilePrefix}-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";
        var newest = -1;
        for (var seq = 0; File.Exists(PathFor(seq)); seq++)
        {
            newest = seq;
        }

        if (newest >= 0 && !forceNew)
        {
            var length = new FileInfo(PathFor(newest)).Length;
            if (length == 0 || length + nextLineBytes <= _options.MaxFileSizeBytes)
            {
                return PathFor(newest);
            }
        }

        return PathFor(newest + 1);

        string PathFor(int seq) => Path.Combine(_options.Directory, seq == 0 ? $"{stem}.log" : $"{stem}.{seq}.log");
    }

    private void PruneOldFiles()
    {
        var files = new DirectoryInfo(_options.Directory)
            .GetFiles($"{_options.FilePrefix}-*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(Math.Max(1, _options.RetainedFileCount));
        foreach (var file in files)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // In use; try again on the next roll.
            }
        }
    }

    private sealed class FileLogger(string category, RollingFileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder(256);
            sb.Append(provider._clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            sb.Append(' ').Append(Level(logLevel)).Append(' ').Append(category).Append(": ");
            sb.Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            sb.AppendLine();
            provider.Enqueue(sb.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRCE",
            LogLevel.Debug => "DBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "FAIL",
            LogLevel.Critical => "CRIT",
            _ => "????",
        };
    }
}
