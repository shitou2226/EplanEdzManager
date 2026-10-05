using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Reflection;
using Newtonsoft.Json;

namespace EplanEdzManager.EplanBridge;

internal sealed class BridgeLogger : IDisposable
{
    private readonly object _gate = new object();
    private readonly StreamWriter _writer;
    private readonly string _appVersion;

    public BridgeLogger(string sessionId)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EplanEdzManager", "Logs", "Bridge");
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + sessionId + ".jsonl");
        _writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        _appVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        DeleteExpiredLogs(directory);
    }

    public string LogPath { get; }

    public void Write(string sessionId, string requestId, string eventName, string detail = "")
    {
        var record = new
        {
            sessionId,
            appVersion = _appVersion,
            component = "Bridge",
            timestampUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            severity = SeverityFor(eventName),
            eventId = eventName,
            message = detail,
            properties = new { requestId }
        };
        lock (_gate) _writer.WriteLine(JsonConvert.SerializeObject(record));
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    private static string SeverityFor(string eventName) =>
        eventName.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0
            || eventName.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) >= 0 ? "Error" :
        eventName.IndexOf("Warning", StringComparison.OrdinalIgnoreCase) >= 0
            || eventName.IndexOf("Cleanup", StringComparison.OrdinalIgnoreCase) >= 0 ? "Warning" : "Information";

    private static void DeleteExpiredLogs(string directory)
    {
        foreach (var path in Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly))
        {
            if (File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddDays(-14)) continue;
            try { File.Delete(path); } catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException) { }
        }
    }
}
