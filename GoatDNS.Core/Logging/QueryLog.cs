using System.Collections.Concurrent;
using GoatDNS.Core.Config;

namespace GoatDNS.Core.Logging;

public sealed record LogEntry(DateTimeOffset Time, LogVerbosity Level, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss.fff} [{Level}] {Message}";
}

/// <summary>
/// Bounded in-memory ring for the live UI view plus an optional file sink,
/// each with its own verbosity threshold (an entry passes when its level &lt;= the threshold).
/// </summary>
public sealed class QueryLog : IDisposable
{
    private const int RingCapacity = 2000;

    private readonly ConcurrentQueue<LogEntry> _ring = new();
    private readonly Lock _fileLock = new();
    private StreamWriter? _fileWriter;

    public LogVerbosity ScreenVerbosity { get; set; } = LogVerbosity.Normal;
    public LogVerbosity FileVerbosity { get; set; } = LogVerbosity.ErrorsOnly;

    public event Action<LogEntry>? EntryAdded;

    public void Configure(LoggingOptions options)
    {
        ScreenVerbosity = options.ScreenVerbosity;
        FileVerbosity = options.FileVerbosity;
        lock (_fileLock)
        {
            _fileWriter?.Dispose();
            _fileWriter = null;
            if (!string.IsNullOrWhiteSpace(options.FilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.FilePath))!);
                _fileWriter = new StreamWriter(File.Open(options.FilePath, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    AutoFlush = true,
                };
            }
        }
    }

    public void Error(string message) => Add(LogVerbosity.ErrorsOnly, message);
    public void Info(string message) => Add(LogVerbosity.Normal, message);
    public void Verbose(string message) => Add(LogVerbosity.Verbose, message);
    public void Debug(string message) => Add(LogVerbosity.Debug, message);

    private void Add(LogVerbosity level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        if (level <= ScreenVerbosity)
        {
            _ring.Enqueue(entry);
            while (_ring.Count > RingCapacity) _ring.TryDequeue(out _);
            EntryAdded?.Invoke(entry);
        }

        if (level <= FileVerbosity)
        {
            lock (_fileLock) _fileWriter?.WriteLine(entry.ToString());
        }
    }

    public LogEntry[] Snapshot() => [.. _ring];

    public void Dispose()
    {
        lock (_fileLock) _fileWriter?.Dispose();
    }
}
