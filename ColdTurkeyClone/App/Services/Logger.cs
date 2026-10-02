using System.IO;

namespace ColdTurkeyClone.Services;

public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ColdTurkeyClone", "app.log");

    public static string LogFilePath => LogPath;

    // Identifica qual processo está logando (App.exe ou Watchdog.exe) e o PID,
    // pra dar pra distinguir no log quem fez o quê quando os dois rodam juntos.
    private static readonly string ProcessTag =
        $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName}:{Environment.ProcessId}";

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{ProcessTag}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public static void LogException(string context, Exception ex) =>
        Log($"ERRO em {context}: {ex.GetType().Name}: {ex.Message}");
}
