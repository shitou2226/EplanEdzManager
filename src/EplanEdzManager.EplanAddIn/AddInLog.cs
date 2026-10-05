using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using Newtonsoft.Json;

namespace EplanEdzManager.EplanAddIn;

internal static class AddInLog
{
    private static readonly object Sync = new object();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EplanEdzManager", "Logs", "AddIn");
    private static readonly string AppVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
        ?? "unknown";

    public static string CurrentLogPath => Path.Combine(LogDirectory, DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");

    public static void Information(string sessionId, string eventName, string detail = "") =>
        Write("INFO", sessionId, eventName, detail);

    public static void Warning(string sessionId, string eventName, string detail = "") =>
        Write("WARN", sessionId, eventName, detail);

    public static void Error(string sessionId, string eventName, Exception exception) =>
        Write("ERROR", sessionId, eventName, exception.GetType().FullName + ": " + exception.Message);

    private static void Write(string level, string sessionId, string eventName, string detail)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            DeleteExpiredLogs();
            var safeDetail = (detail ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
            var line = JsonConvert.SerializeObject(new
            {
                sessionId = sessionId ?? string.Empty,
                appVersion = AppVersion,
                component = "AddIn",
                timestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                severity = level == "INFO" ? "Information" : level == "WARN" ? "Warning" : "Error",
                eventId = eventName ?? string.Empty,
                message = safeDetail
            }) + Environment.NewLine;
            lock (Sync) File.AppendAllText(CurrentLogPath, line, new UTF8Encoding(false));
        }
        catch (Exception)
        {
            // Logging must never destabilize EPLAN.
        }
    }

    private static void DeleteExpiredLogs()
    {
        foreach (var path in Directory.GetFiles(LogDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddDays(-14)) continue;
            try { File.Delete(path); } catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) { }
        }
    }
}
