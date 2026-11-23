using Lasteast.Core.Abstractions;

namespace Lasteast.Core.Logging;

/// <summary>Append-only text log, one file per day, under %LOCALAPPDATA%\Lasteast\Partition\logs.</summary>
public sealed class FileLog : IAppLog
{
    private readonly string _directory;
    private readonly object _gate = new();

    public FileLog(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public static string DefaultDirectory => Lasteast.Core.AppPaths.Logs;

    public string CurrentFile => Path.Combine(_directory, $"lasteast-{DateTime.Now:yyyy-MM-dd}.log");

    public void Info(string message) => Write("INFO ", message);
    public void Warn(string message) => Write("WARN ", message);
    public void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}{Environment.NewLine}{ex}");

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{Environment.NewLine}";
        lock (_gate)
        {
            try { File.AppendAllText(CurrentFile, line); }
            catch (IOException) { /* logging must never crash the app */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class NullLog : IAppLog
{
    public static readonly NullLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}
