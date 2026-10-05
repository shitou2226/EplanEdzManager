using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EplanEdzManager.EplanBridge.Client;
using Microsoft.Win32;

namespace EplanEdzManager.Application;

public sealed partial class EdzManagerApplication
{
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public PrerequisiteStatus CheckPrerequisites(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var messages = new List<string>();
        var eplan = new EplanEnvironmentDetector().Detect(settings);
        var installRoot = Path.GetFullPath(AppContext.BaseDirectory);
        var installRootReadable = IsDirectoryReadable(installRoot, messages, "Install root");
        var x64 = OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem && Environment.Is64BitProcess;
        var desktopRuntime = Environment.Version.Major >= 8;
        var framework = IsNetFramework472OrLater();
        var localWritable = IsDirectoryWritable(ApplicationPaths.LocalDataRoot, messages, "LocalAppData");
        var tempWritable = IsDirectoryWritable(Path.GetTempPath(), messages, "Temp");
        if (!x64) messages.Add("Windows x64 and an x64 process are required.");
        if (!desktopRuntime) messages.Add("The bundled .NET 8 Desktop runtime is unavailable or failed to initialize.");
        if (!framework) messages.Add(".NET Framework 4.7.2 or later is required for Bridge/Add-In.");
        if (!eplan.Detected) messages.Add("EPLAN is unavailable; Desktop will continue in Offline Mode.");
        if (messages.Count == 0) messages.Add("All local prerequisites passed.");
        return new PrerequisiteStatus(installRoot, installRootReadable, x64, desktopRuntime, framework,
            localWritable, tempWritable, eplan.Detected, true, messages);
    }

    public async Task<IReadOnlyList<CapabilityStatus>> RunCapabilityCheckAsync(
        ApplicationSettings settings,
        string addInStatus,
        bool includeBridgeProbe,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var result = new List<CapabilityStatus>
        {
            new("Desktop", CapabilityLevel.Green, "Ready", $"{ProductInfo.Name} {ProductInfo.Version}"),
            new("EDZ Reader", CapabilityLevel.Green, "Ready", "Read-only parser; source EDZ files are never modified."),
            new("SharpCompress", Type.GetType("SharpCompress.Archives.IArchive, SharpCompress") is null ? CapabilityLevel.Red : CapabilityLevel.Green,
                Type.GetType("SharpCompress.Archives.IArchive, SharpCompress") is null ? "Unavailable" : "Ready", "Archive reader dependency")
        };

        try
        {
            var database = await GetDatabaseMaintenanceInfoAsync(cancellationToken).ConfigureAwait(false);
            result.Add(new CapabilityStatus("SQLite", CapabilityLevel.Green, "Ready", $"Schema {database.SchemaVersion}; {database.Parts:N0} parts"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            result.Add(new CapabilityStatus("SQLite", CapabilityLevel.Red, "Unavailable", exception.GetType().Name + ": " + exception.Message));
        }

        var environment = new EplanEnvironmentDetector().Detect(settings);
        var eplanLevel = environment.Compatibility switch
        {
            EplanCompatibility.Verified => CapabilityLevel.Green,
            EplanCompatibility.DetectedButUnverified => CapabilityLevel.Yellow,
            EplanCompatibility.Offline => CapabilityLevel.Yellow,
            _ => CapabilityLevel.Red
        };
        result.Add(new CapabilityStatus("EPLAN Detected", eplanLevel, environment.CompatibilityText,
            environment.Detected ? environment.EplanVersion + " · " + environment.DetectionSource : environment.Messages.FirstOrDefault() ?? "Not detected"));
        result.Add(new CapabilityStatus("EPLAN API", string.IsNullOrWhiteSpace(environment.ApiVersion) ? CapabilityLevel.Red : eplanLevel,
            string.IsNullOrWhiteSpace(environment.ApiVersion) ? "Unavailable" : environment.CompatibilityText,
            string.IsNullOrWhiteSpace(environment.ApiVersion) ? "API assemblies were not found." : environment.ApiVersion));

        var bridgeOptions = CreateConfiguredBridgeOptions();
        var bridgePresent = File.Exists(bridgeOptions.BridgeExecutablePath);
        var bridgeAllowed = bridgePresent && EplanEnvironmentDetector.IsBridgeAllowed(environment.Compatibility);
        result.Add(new CapabilityStatus("Bridge", bridgeAllowed ? CapabilityLevel.Green : bridgePresent ? CapabilityLevel.Yellow : CapabilityLevel.Red,
            bridgeAllowed ? "Executable enabled" : bridgePresent ? "Disabled for unverified EPLAN version" : "Unavailable",
            bridgePresent ? bridgeOptions.BridgeExecutablePath : "Bridge executable was not found."));
        var addInLevel = addInStatus.StartsWith("Connected", StringComparison.OrdinalIgnoreCase) ? CapabilityLevel.Green
            : addInStatus.StartsWith("Recently", StringComparison.OrdinalIgnoreCase) ? CapabilityLevel.Yellow : CapabilityLevel.Yellow;
        result.Add(new CapabilityStatus("Add-In", addInLevel, addInStatus, "Status is based on a real local IPC connection, not DLL presence."));

        if (!includeBridgeProbe || !bridgeAllowed)
        {
            var level = environment.Compatibility == EplanCompatibility.DetectedButUnverified ? CapabilityLevel.Yellow : CapabilityLevel.Red;
            var status = includeBridgeProbe ? "Unavailable" : "Not probed";
            foreach (var name in new[] { "PartsService", "Converter", "MDB Import Capability" })
                result.Add(new CapabilityStatus(name, level, status, includeBridgeProbe ? environment.CompatibilityText : "Run Diagnostics to perform the isolated EPLAN runtime probe."));
            return result;
        }

        try
        {
            await using var client = new EplanBridgeClient(bridgeOptions);
            await client.StartAsync(cancellationToken).ConfigureAwait(false);
            var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            AddRuntimeCapability(result, "PartsService", capabilities.PartsServiceAvailable, "Official EPLAN PartsService");
            AddRuntimeCapability(result, "Converter", capabilities.EdzConverterAvailable, "Official EDZ converter");
            AddRuntimeCapability(result, "MDB Import Capability",
                capabilities.PartsServiceAvailable && capabilities.EdzConverterAvailable && capabilities.TemporaryDatabaseCapability,
                "Safe preview/backup/import boundary");
        }
        catch (Exception exception)
        {
            foreach (var name in new[] { "PartsService", "Converter", "MDB Import Capability" })
                result.Add(new CapabilityStatus(name, CapabilityLevel.Red, "Probe failed", exception.GetType().Name + ": " + exception.Message));
        }
        return result;
    }

    public async Task<DiagnosticReportLocation> RunDiagnosticsAsync(
        ApplicationSettings settings,
        string addInStatus,
        bool includeBridgeProbe = true,
        string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        OperationContext.Set("DIAG-RUN");
        var created = DateTimeOffset.UtcNow;
        var directory = Path.GetFullPath(outputDirectory ?? ApplicationPaths.DiagnosticDirectory);
        Directory.CreateDirectory(directory);
        var environment = new EplanEnvironmentDetector().Detect(settings);
        var prerequisites = CheckPrerequisites(settings);
        var capabilities = await RunCapabilityCheckAsync(settings, addInStatus, includeBridgeProbe, cancellationToken).ConfigureAwait(false);
        var database = await GetDatabaseMaintenanceInfoAsync(cancellationToken).ConfigureAwait(false);
        var libraries = await GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        var logSummary = ReadRecentLogSummary();
        var report = new
        {
            reportSchemaVersion = 1,
            createdUtc = created,
            app = new { name = ProductInfo.Name, version = ProductInfo.Version, build = ProductInfo.Build, component = "Desktop", sessionId = ProductSession.Id },
            runtime = new
            {
                os = RuntimeInformation.OSDescription,
                osArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                framework = RuntimeInformation.FrameworkDescription,
                is64BitOperatingSystem = Environment.Is64BitOperatingSystem,
                is64BitProcess = Environment.Is64BitProcess
            },
            eplan = environment,
            prerequisites,
            capabilities,
            config = CreateSanitizedConfig(settings),
            database,
            registeredEdzFolders = libraries.Select(item => new { item.Path, item.Recursive, item.LastScanUtc, item.FileCount, item.PartCount, item.FailedFileCount }).ToArray(),
            bridgeCapability = capabilities.FirstOrDefault(item => item.Name == "Bridge"),
            addInStatus,
            recentLogs = logSummary,
            lastOperation = OperationContext.LastOperation,
            exclusions = new[] { "EDZ contents", "user parts database", "index.db", "passwords", "tokens", "project file contents" }
        };
        var path = Path.Combine(directory, $"DiagnosticReport-{created:yyyyMMdd-HHmmss}.json");
        await WriteJsonAtomicallyAsync(path, report, cancellationToken).ConfigureAwait(false);
        await _logger.WriteAsync("Information", "DIAG-COMPLETE", "Diagnostic report generated.",
            new Dictionary<string, object?> { ["path"] = path, ["bridgeProbe"] = includeBridgeProbe }, cancellationToken).ConfigureAwait(false);
        return new DiagnosticReportLocation(path, created);
    }

    public async Task<DiagnosticBundleLocation> ExportDiagnosticBundleAsync(
        string destinationPath,
        ApplicationSettings settings,
        string addInStatus,
        bool includeBridgeProbe = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("A bundle path is required.", nameof(destinationPath));
        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var report = await RunDiagnosticsAsync(settings, addInStatus, includeBridgeProbe, cancellationToken: cancellationToken).ConfigureAwait(false);
        var temporaryPath = fullPath + ".tmp";
        if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        using (var archive = ZipFile.Open(temporaryPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(report.ReportPath, "diagnostics/" + Path.GetFileName(report.ReportPath), CompressionLevel.Optimal);
            await WriteZipJsonAsync(archive, "config/settings.sanitized.json", CreateSanitizedConfig(settings), cancellationToken).ConfigureAwait(false);
            await WriteZipJsonAsync(archive, "environment/manifest.json", new
            {
                product = ProductInfo.Name,
                version = ProductInfo.Version,
                createdUtc = DateTimeOffset.UtcNow,
                unsignedBeta = true,
                databaseIncluded = false,
                userEdzIncluded = false,
                lastOperation = OperationContext.LastOperation
            }, cancellationToken).ConfigureAwait(false);
            AddSanitizedLogs(archive);
        }
        File.Move(temporaryPath, fullPath, overwrite: true);
        await _logger.WriteAsync("Information", "DIAG-BUNDLE", "Diagnostic bundle exported.",
            new Dictionary<string, object?> { ["path"] = fullPath, ["databaseIncluded"] = false }, cancellationToken).ConfigureAwait(false);
        return new DiagnosticBundleLocation(fullPath, DateTimeOffset.UtcNow);
    }

    private static object CreateSanitizedConfig(ApplicationSettings settings) => new
    {
        settings.SettingsSchemaVersion,
        settings.FirstRunCompleted,
        settings.DatabasePath,
        settings.DefaultExportFolder,
        settings.BackupFolder,
        settings.EplanPlatformBinDirectory,
        settings.EplanVariantBinDirectory,
        settings.LogRetentionDays,
        settings.LastBackupPath,
        settings.LastBackupUtc,
        telemetry = false,
        cloudServiceRequired = false
    };

    private static IReadOnlyDictionary<string, object> ReadRecentLogSummary()
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var root = Path.Combine(ApplicationPaths.LocalDataRoot, "Logs");
        foreach (var component in new[] { "Desktop", "Bridge", "AddIn" })
        {
            var directory = Path.Combine(root, component);
            var files = Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory).OrderByDescending(File.GetLastWriteTimeUtc).Take(5).ToArray()
                : Array.Empty<string>();
            result[component] = new
            {
                fileCount = files.Length,
                newestUtc = files.Length == 0 ? (DateTimeOffset?)null : File.GetLastWriteTimeUtc(files[0]),
                newestFile = files.Length == 0 ? null : Path.GetFileName(files[0])
            };
        }
        return result;
    }

    private static void AddSanitizedLogs(ZipArchive archive)
    {
        var root = Path.Combine(ApplicationPaths.LocalDataRoot, "Logs");
        foreach (var component in new[] { "Desktop", "Bridge", "AddIn" })
        {
            var directory = Path.Combine(root, component);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory).OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
            {
                var entry = archive.CreateEntry("logs/" + component + "/" + Path.GetFileName(path), CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                foreach (var line in File.ReadLines(path).TakeLast(500)) writer.WriteLine(RedactSecrets(line));
            }
        }
    }

    internal static string RedactSecrets(string value)
    {
        value = Regex.Replace(value,
            "(?i)(\\\"(?:password|token|api[_-]?key|authorization)\\\"\\s*:\\s*)\\\"[^\\\"]*\\\"",
            "$1\\\"[REDACTED]\\\"");
        value = Regex.Replace(value,
            "(?i)\\b(password|token|api[_-]?key|authorization)\\b(\\s*[:=]\\s*)([^\\r\\n,;}]+)",
            "$1$2[REDACTED]");
        value = Regex.Replace(value, "(?i)\\bBearer\\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]");
        return Regex.Replace(value, "(?i)\\bsk-[A-Za-z0-9_-]{8,}\\b", "[REDACTED]");
    }

    private static async Task WriteZipJsonAsync(ZipArchive archive, string entryName, object value, CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await JsonSerializer.SerializeAsync(stream, value, DiagnosticJsonOptions, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteJsonAtomicallyAsync(string path, object value, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, value, DiagnosticJsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static bool IsNetFramework472OrLater()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
                .OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            return key?.GetValue("Release") is int release && release >= 461_808;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }

    private static bool IsDirectoryWritable(string directory, ICollection<string> messages, string label)
    {
        string? path = null;
        try
        {
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (File.Create(path, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            messages.Add(label + " is not writable: " + exception.GetType().Name + ".");
            return false;
        }
        finally
        {
            if (path is not null && File.Exists(path))
            {
                try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static bool IsDirectoryReadable(string directory, ICollection<string> messages, string label)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                messages.Add(label + " does not exist.");
                return false;
            }
            _ = Directory.EnumerateFileSystemEntries(directory).Take(1).ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            messages.Add(label + " is not readable: " + exception.GetType().Name + ".");
            return false;
        }
    }

    private static void AddRuntimeCapability(ICollection<CapabilityStatus> target, string name, bool available, string detail) =>
        target.Add(new CapabilityStatus(name, available ? CapabilityLevel.Green : CapabilityLevel.Red,
            available ? "Available" : "Unavailable", detail));
}
