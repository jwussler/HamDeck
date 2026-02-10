using System.IO;

namespace HamDeck.Services;

public enum LogLevel { Debug, Info, Warn, Error }

public static class Logger
{
    private static LogLevel _level = LogLevel.Info;
    private static StreamWriter? _fileWriter;
    private static readonly object Lock = new();

    public static event Action<string>? OnLog;

    public static void Init(LogLevel level, bool logToFile)
    {
        _level = level;
        if (logToFile)
        {
            try
            {
                var logDir = Models.Config.ConfigDir;
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, $"hamdeck_{DateTime.Now:yyyyMMdd}.log");
                _fileWriter = new StreamWriter(logPath, append: true) { AutoFlush = true };
            }
            catch { /* ignore file logging errors */ }
        }
    }

    public static void Close() { _fileWriter?.Dispose(); _fileWriter = null; }

    public static void Debug(string tag, string msg, params object[] args) => Log(LogLevel.Debug, tag, msg, args);
    public static void Info(string tag, string msg, params object[] args) => Log(LogLevel.Info, tag, msg, args);
    public static void Warn(string tag, string msg, params object[] args) => Log(LogLevel.Warn, tag, msg, args);
    public static void Error(string tag, string msg, params object[] args) => Log(LogLevel.Error, tag, msg, args);

    private static void Log(LogLevel level, string tag, string fmt, params object[] args)
    {
        if (level < _level) return;
        var message = args.Length > 0 ? string.Format(fmt, args) : fmt;
        var line = $"[{DateTime.Now:HH:mm:ss}] [{level}] [{tag}] {message}";

        lock (Lock)
        {
            Console.WriteLine(line);
            _fileWriter?.WriteLine(line);
            OnLog?.Invoke(line);
        }
    }
}
