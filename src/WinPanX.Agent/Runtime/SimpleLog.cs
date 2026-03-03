using System.Globalization;

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

    // Removes log entries older than the configured retention period.
    // Set retentionDays to 0 to disable pruning.
    public static void PruneOlderThanDays(int retentionDays)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        lock (Sync)
        {
            try
            {
                EnsureDirectoryExists(_logPath);
                if (!File.Exists(_logPath))
                {
                    File.WriteAllText(_logPath, string.Empty);
                    return;
                }

                var cutoffUtc = DateTime.UtcNow.AddDays(-retentionDays);
                var keptLines = new List<string>();

                foreach (var line in File.ReadLines(_logPath))
                {
                    if (!TryParseLineTimestampUtc(line, out var timestampUtc) || timestampUtc >= cutoffUtc)
                    {
                        keptLines.Add(line);
                    }
                }

                File.WriteAllLines(_logPath, keptLines);
            }
            catch
            {
                // Logging must never crash the agent.
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

    private static bool TryParseLineTimestampUtc(string line, out DateTime timestampUtc)
    {
        timestampUtc = DateTime.MinValue;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var separatorIndex = line.IndexOf(' ');
        if (separatorIndex <= 0)
        {
            return false;
        }

        var token = line[..separatorIndex];
        if (!DateTime.TryParse(
                token,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            return false;
        }

        timestampUtc = parsed.ToUniversalTime();
        return true;
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
