using System.Text.Json;

namespace EplanEdzManager.Application;

public interface IApplicationLogger
{
    Task WriteAsync(string level, string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default);
}

public sealed class JsonLineLogger : IApplicationLogger
{
    private readonly string _directory;
    private readonly string _component;
    private readonly string _sessionId;
    private readonly string _appVersion;
    private readonly int _retentionDays;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public JsonLineLogger(string? directory = null, string component = "Desktop", string? sessionId = null, int retentionDays = 14)
    {
        _directory = Path.GetFullPath(directory ?? ApplicationPaths.DefaultLogDirectory);
        _component = component;
        _sessionId = sessionId ?? ProductSession.Id;
        _appVersion = ProductInfo.Version;
        _retentionDays = Math.Clamp(retentionDays, 1, 365);
    }

    public async Task WriteAsync(string level, string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default)
    {
        OperationContext.Set(eventName);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            DeleteExpiredLogs();
            var path = Path.Combine(_directory, "eplan-edz-manager-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".jsonl");
            var payload = new
            {
                sessionId = _sessionId,
                appVersion = _appVersion,
                component = _component,
                timestampUtc = DateTimeOffset.UtcNow,
                severity = level,
                eventId = eventName,
                message,
                properties
            };
            await File.AppendAllTextAsync(path, JsonSerializer.Serialize(payload) + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void DeleteExpiredLogs()
    {
        foreach (var path in Directory.EnumerateFiles(_directory, "eplan-edz-manager-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-_retentionDays))
            {
                try { File.Delete(path); } catch (IOException) { }
            }
        }
    }
}

internal sealed class NullApplicationLogger : IApplicationLogger
{
    public Task WriteAsync(string level, string eventName, string message, IReadOnlyDictionary<string, object?>? properties = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
