using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace KobraCache.Desktop;

public static partial class AppLogger
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KobraCache",
        "Logs");

    public static string CurrentLogPath { get; private set; } = Path.Combine(
        LogDirectory,
        $"kobracache-{DateTime.Now:yyyyMMdd}.log");

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(LogDirectory);
            CurrentLogPath = Path.Combine(LogDirectory, $"kobracache-{DateTime.Now:yyyyMMdd}.log");
            _initialized = true;
            Info("Logging initialized.");
            Info($"Process: {Environment.ProcessPath}");
            Info($"Version: {typeof(AppLogger).Assembly.GetName().Version}");
            Info($".NET: {Environment.Version}");
            Info($"OS: {Environment.OSVersion}");
        }
    }

    public static void Info(string message)
    {
        Write("INFO", message, null);
    }

    public static void Warn(string message)
    {
        Write("WARN", message, null);
    }

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message, exception);
    }

    public static void OpenLogDirectory()
    {
        Initialize();
        Process.Start(new ProcessStartInfo
        {
            FileName = LogDirectory,
            UseShellExecute = true
        });
    }

    private static void Write(string level, string message, Exception? exception)
    {
        InitializeIfNeeded();

        lock (Sync)
        {
            var line = $"{DateTimeOffset.Now:O} [{level}] {Redact(message)}";
            File.AppendAllText(CurrentLogPath, line + Environment.NewLine);
            if (exception is not null)
            {
                File.AppendAllText(CurrentLogPath, Redact(exception.ToString()) + Environment.NewLine);
            }
        }
    }

    private static void InitializeIfNeeded()
    {
        if (_initialized)
        {
            return;
        }

        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(LogDirectory);
            CurrentLogPath = Path.Combine(LogDirectory, $"kobracache-{DateTime.Now:yyyyMMdd}.log");
            _initialized = true;
        }
    }

    private static string Redact(string value)
    {
        var redacted = TokenJsonRegex().Replace(value, "$1[REDACTED]$3");
        redacted = BearerRegex().Replace(redacted, "$1[REDACTED]");
        return redacted;
    }

    [GeneratedRegex("(\"(?:access_token|token|CloudAccessToken)\"\\s*:\\s*\")([^\"]+)(\")", RegexOptions.IgnoreCase)]
    private static partial Regex TokenJsonRegex();

    [GeneratedRegex("(Bearer\\s+)[A-Za-z0-9._\\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();
}
