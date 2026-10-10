namespace XControl.Core;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>Small rolling file logger. Never records account data: only engine, driver and input-state events.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string? _path;
    public static LogLevel Level { get; set; } = LogLevel.Info;
    public static string? Directory { get; private set; }

    public static void Init(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        Directory = dir;
        _path = Path.Combine(dir, "xcontrol.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                File.Move(_path, Path.Combine(dir, "xcontrol.old.log"), true);
        }
        catch (IOException) { }
    }

    public static void SetLevel(string name) =>
        Level = Enum.TryParse<LogLevel>(name, true, out var l) ? l : LogLevel.Info;

    public static void Write(LogLevel level, string message)
    {
        if (level < Level || _path == null) return;
        try
        {
            lock (Gate) File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch (IOException) { }
    }

    public static void Debug(string m) => Write(LogLevel.Debug, m);
    public static void Info(string m) => Write(LogLevel.Info, m);
    public static void Warn(string m) => Write(LogLevel.Warn, m);
    public static void Error(string m) => Write(LogLevel.Error, m);
}
