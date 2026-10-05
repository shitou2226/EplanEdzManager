using System.Reflection;

namespace EplanEdzManager.Application;

public static class ProductInfo
{
    public const string Name = "EPLAN EDZ Manager";
    public const string VerifiedEplanVersion = "2.9.4.14642";
    public const string SupportedEplanDisplay = "Verified 2.9.4.14642; other 2.9.x detected but not verified";

    public static string Version => GetInformationalVersion(typeof(ProductInfo).Assembly);
    public static string Build => GetBuild(typeof(ProductInfo).Assembly);

    public static string GetInformationalVersion(Assembly assembly)
    {
        var value = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return string.IsNullOrWhiteSpace(value) ? assembly.GetName().Version?.ToString() ?? "unknown" : value;
    }

    private static string GetBuild(Assembly assembly)
    {
        var value = GetInformationalVersion(assembly);
        var separator = value.IndexOf('+');
        return separator >= 0 && separator + 1 < value.Length ? value[(separator + 1)..] : "unsigned-local-build";
    }
}

public static class ProductSession
{
    public static string Id { get; } = Guid.NewGuid().ToString("N");
}

public static class OperationContext
{
    private static string _lastOperation = "Startup";
    public static string LastOperation => Volatile.Read(ref _lastOperation);
    public static void Set(string? operation) => Volatile.Write(ref _lastOperation, string.IsNullOrWhiteSpace(operation) ? "Unknown" : operation);
}

public enum EplanCompatibility
{
    Offline,
    Verified,
    DetectedButUnverified,
    Unsupported,
    InvalidConfiguration
}

public sealed record EplanEnvironmentInfo(
    bool Detected,
    string DetectionSource,
    string EplanVersion,
    string PlatformBinDirectory,
    string VariantBinDirectory,
    string ApiVersion,
    bool IsX64,
    EplanCompatibility Compatibility,
    IReadOnlyList<string> Messages)
{
    public string CompatibilityText => Compatibility switch
    {
        EplanCompatibility.Verified => "Compatible · Verified 2.9.4.14642",
        EplanCompatibility.DetectedButUnverified => "Detected but not verified",
        EplanCompatibility.Unsupported => "Unsupported by this build",
        EplanCompatibility.InvalidConfiguration => "Invalid configuration",
        _ => "Offline Mode · EPLAN features unavailable"
    };
}

public enum CapabilityLevel
{
    Green,
    Yellow,
    Red
}

public sealed record CapabilityStatus(string Name, CapabilityLevel Level, string Status, string Detail);

public sealed record PrerequisiteStatus(
    string InstallRoot,
    bool InstallRootReadable,
    bool WindowsX64,
    bool DesktopRuntimeReady,
    bool NetFramework472OrLater,
    bool LocalAppDataWritable,
    bool TempWritable,
    bool EplanDetected,
    bool OfflineModeAvailable,
    IReadOnlyList<string> Messages)
{
    public bool DesktopStartupReady =>
        InstallRootReadable && WindowsX64 && DesktopRuntimeReady && LocalAppDataWritable && TempWritable;
}

public sealed record DatabaseMaintenanceInfo(
    string Path,
    long SizeBytes,
    int SchemaVersion,
    int Parts,
    int Resources,
    int SavedParts,
    int Collections,
    int Tags);

public sealed record LibraryHealthIssue(string Severity, string Code, string Message, string? Path);

public sealed record LibraryHealthReport(
    int RegisteredFolders,
    int IndexedEdz,
    int MissingFolders,
    int MissingSources,
    int StaleSources,
    int BrokenEdz,
    int MissingResources,
    int OrphanSavedSources,
    IReadOnlyList<LibraryHealthIssue> Issues);

public sealed record OrphanSessionInfo(
    string SessionId,
    string DirectoryPath,
    int ProcessId,
    DateTimeOffset CreatedUtc,
    string AppVersion,
    TimeSpan Age,
    long SizeBytes,
    bool CanDelete,
    string Reason);

public sealed record OrphanSessionScan(
    string RootPath,
    DateTimeOffset ScannedUtc,
    TimeSpan MinimumAge,
    IReadOnlyList<OrphanSessionInfo> Sessions)
{
    public int OrphanCount => Sessions.Count(item => item.CanDelete);
    public long OrphanBytes => Sessions.Where(item => item.CanDelete).Sum(item => item.SizeBytes);
}

public sealed record OrphanCleanupResult(int Deleted, long DeletedBytes, IReadOnlyList<string> Skipped, IReadOnlyList<string> Errors);

public sealed record DiagnosticReportLocation(string ReportPath, DateTimeOffset CreatedUtc);

public sealed record DiagnosticBundleLocation(string BundlePath, DateTimeOffset CreatedUtc);
