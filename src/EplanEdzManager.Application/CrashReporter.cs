using System.Text.Json;

namespace EplanEdzManager.Application;

public static class CrashReporter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Write(string component, Exception exception, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var targetDirectory = Path.GetFullPath(directory ?? ApplicationPaths.CrashReportDirectory);
        Directory.CreateDirectory(targetDirectory);
        var timestamp = DateTimeOffset.UtcNow;
        var path = Path.Combine(targetDirectory, $"CrashReport-{timestamp:yyyyMMdd-HHmmss}-{ProductSession.Id[..8]}.json");
        var payload = new
        {
            version = ProductInfo.Version,
            component,
            timestampUtc = timestamp,
            exceptionType = exception.GetType().FullName ?? exception.GetType().Name,
            message = Sanitize(exception.Message),
            stack = Sanitize(exception.StackTrace ?? string.Empty),
            lastOperation = OperationContext.LastOperation,
            sessionId = ProductSession.Id,
            privacy = "No user document contents are collected."
        };
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(payload, Options));
        File.Move(temporaryPath, path, overwrite: true);
        return path;
    }

    private static string Sanitize(string value)
    {
        if (value.Length <= 32_000) return value;
        return value[..32_000] + "\n[truncated]";
    }
}
