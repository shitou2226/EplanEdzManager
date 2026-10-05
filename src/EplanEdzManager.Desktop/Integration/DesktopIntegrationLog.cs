using System.IO;
using System.Text.Json;
using EplanEdzManager.Application;

namespace EplanEdzManager.Desktop.Integration;

internal static class DesktopIntegrationLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EplanEdzManager", "Logs", "Desktop", "desktop-integration-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");

    public static void Write(string eventName, string detail = "")
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            DeleteExpiredLogs();
            var payload = new
            {
                sessionId = ProductSession.Id,
                appVersion = ProductInfo.Version,
                component = "Desktop",
                timestampUtc = DateTimeOffset.UtcNow,
                severity = "Information",
                eventId = eventName,
                message = detail.Replace("\r", " ").Replace("\n", " ")
            };
            var line = JsonSerializer.Serialize(payload) + Environment.NewLine;
            lock (Sync) File.AppendAllText(LogPath, line);
        }
        catch (Exception)
        {
            // Diagnostics must never block Desktop startup or shutdown.
        }
    }

    private static void DeleteExpiredLogs()
    {
        var directory = Path.GetDirectoryName(LogPath)!;
        foreach (var path in Directory.EnumerateFiles(directory, "desktop-integration-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddDays(-14)) continue;
            try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
