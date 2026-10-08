using System.Collections.Concurrent;

namespace AeroGatePilot.Infrastructure;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
}

public sealed record LogEntry(DateTime Time, LogLevel Level, string Message);

public sealed class AppLog
{
    private const int MaxEntries = 500;
    private readonly string _directory;
    private readonly ConcurrentQueue<LogEntry> _entries = new();
    private readonly object _fileGate = new();

    public AppLog(string directory) => _directory = directory;

    public event Action<LogEntry>? Written;

    public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

    public void Info(string message) => Write(LogLevel.Info, message);
    public void Success(string message) => Write(LogLevel.Success, message);
    public void Warn(string message) => Write(LogLevel.Warning, message);
    public void Error(string message) => Write(LogLevel.Error, message);
    public void Error(string message, Exception ex) => Write(LogLevel.Error, $"{message}: {ex.Message}");

    public void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        _entries.Enqueue(entry);
        while (_entries.Count > MaxEntries && _entries.TryDequeue(out _))
        {
        }

        try
        {
            lock (_fileGate)
            {
                var file = Path.Combine(_directory, $"{entry.Time:yyyy-MM-dd}.log");
                File.AppendAllText(file, $"{entry.Time:HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }

        Written?.Invoke(entry);
    }
}
