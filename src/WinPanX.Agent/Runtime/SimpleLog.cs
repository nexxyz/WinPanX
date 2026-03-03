namespace WinPanX.Agent.Runtime;

public static class SimpleLog
{
    private static readonly object Sync = new();
    private static string _logPath = Path.Combine(AppContext.BaseDirectory, "winpanx.log");

    public static string LogPath
    {
        get
        {
            lock (Sync)
            {
                return _logPath;
            }
        }
    }

    public static void Initialize(string logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        lock (Sync)
        {
            _logPath = logPath;
            EnsureDirectoryExists(_logPath);
            if (!File.Exists(_logPath))
            {
                File.WriteAllText(_logPath, string.Empty);
            }
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.UtcNow:O} [{level}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                EnsureDirectoryExists(_logPath);
                File.AppendAllText(_logPath, line);
            }
            catch
            {
                // Logging must never crash the agent.
            }
        }
    }

    private static void EnsureDirectoryExists(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}

