using System.Diagnostics;

namespace Sox.App.Services;

internal static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Warning(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                System.Diagnostics.Debug.WriteLine(line);
                Core.Logger.Log($"[App] {message}", level switch
                {
                    "ERROR" => Core.LogLevel.Error,
                    "WARN" => Core.LogLevel.Warn,
                    _ => Core.LogLevel.Info,
                });
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
