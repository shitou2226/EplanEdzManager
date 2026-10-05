using System.IO.Compression;
using System.IO;
using System.Text.Json;
using EplanEdzManager.Application;
using Xunit;

namespace EplanEdzManager.Application.Tests;

public sealed class Phase7ProductizationTests
{
    [Fact]
    public void Central_version_and_eplan_compatibility_are_explicit()
    {
        Assert.StartsWith("1.0.0-beta.3", ProductInfo.Version, StringComparison.Ordinal);
        Assert.Equal(EplanCompatibility.Verified, EplanEnvironmentDetector.ClassifyCompatibility("2.9.4.14642"));
        Assert.Equal(EplanCompatibility.DetectedButUnverified, EplanEnvironmentDetector.ClassifyCompatibility("2.9.3.12345"));
        Assert.True(EplanEnvironmentDetector.IsBridgeAllowed(EplanCompatibility.Verified));
        Assert.False(EplanEnvironmentDetector.IsBridgeAllowed(EplanCompatibility.DetectedButUnverified));
        Assert.Equal(EplanCompatibility.Unsupported, EplanEnvironmentDetector.ClassifyCompatibility("2022.0.3"));
    }

    [Theory]
    [InlineData("Authorization: Bearer secret-token", "secret-token")]
    [InlineData("{\"api_key\":\"sk-example123456\"}", "sk-example123456")]
    [InlineData("password = value with spaces", "value with spaces")]
    public void Diagnostic_secret_redaction_removes_entire_sensitive_value(string input, string forbidden)
    {
        var redacted = EdzManagerApplication.RedactSecrets(input);
        Assert.DoesNotContain(forbidden, redacted, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Explicit_invalid_eplan_paths_are_not_masked_by_machine_registry()
    {
        using var workspace = new TestWorkspace();
        var settings = new ApplicationSettings
        {
            EplanPlatformBinDirectory = Path.Combine(workspace.DirectoryPath, "missing-platform"),
            EplanVariantBinDirectory = Path.Combine(workspace.DirectoryPath, "missing-variant")
        };

        var detected = new EplanEnvironmentDetector().Detect(settings);

        Assert.False(detected.Detected);
        Assert.Equal("Application settings", detected.DetectionSource);
        Assert.Equal(EplanCompatibility.InvalidConfiguration, detected.Compatibility);
    }

    [Fact]
    public void No_eplan_machine_detection_returns_offline_without_registry_or_environment_discovery()
    {
        var detected = new EplanEnvironmentDetector().Detect(new ApplicationSettings(), includeMachineDiscovery: false);

        Assert.False(detected.Detected);
        Assert.Equal(EplanCompatibility.Offline, detected.Compatibility);
        Assert.Contains("Offline Mode", detected.Messages.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_self_check_reports_install_runtime_localappdata_temp_and_eplan_capability()
    {
        using var workspace = new TestWorkspace();
        var settings = new ApplicationSettings
        {
            DatabasePath = Path.Combine(workspace.DirectoryPath, "index.db"),
            EplanPlatformBinDirectory = Path.Combine(workspace.DirectoryPath, "missing platform"),
            EplanVariantBinDirectory = Path.Combine(workspace.DirectoryPath, "missing variant")
        };
        var application = new EdzManagerApplication(settings.DatabasePath, runtimeSettings: settings);

        var status = application.CheckPrerequisites(settings);

        Assert.True(status.InstallRootReadable);
        Assert.True(status.WindowsX64);
        Assert.True(status.DesktopRuntimeReady);
        Assert.True(status.LocalAppDataWritable);
        Assert.True(status.TempWritable);
        Assert.False(status.EplanDetected);
        Assert.True(status.DesktopStartupReady);
    }

    [Fact]
    public async Task Diagnostic_bundle_is_sanitized_and_never_contains_database_file()
    {
        using var workspace = new TestWorkspace();
        var database = Path.Combine(workspace.DirectoryPath, "index.db");
        var settings = new ApplicationSettings
        {
            DatabasePath = database,
            FirstRunCompleted = true,
            BackupFolder = Path.Combine(workspace.DirectoryPath, "backups")
        };
        var application = new EdzManagerApplication(database, runtimeSettings: settings);
        await application.InitializeAsync();
        var report = await application.RunDiagnosticsAsync(settings, "Not detected", includeBridgeProbe: false, outputDirectory: workspace.DirectoryPath);
        var bundlePath = Path.Combine(workspace.DirectoryPath, "DiagnosticBundle.zip");
        await application.ExportDiagnosticBundleAsync(bundlePath, settings, "Not detected", includeBridgeProbe: false);

        Assert.True(File.Exists(report.ReportPath));
        Assert.True(File.Exists(bundlePath));
        using var archive = ZipFile.OpenRead(bundlePath);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith(".db", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(archive.Entries, entry => entry.FullName == "config/settings.sanitized.json");
        Assert.Contains(archive.Entries, entry => entry.FullName == "environment/manifest.json");
    }

    [Fact]
    public void Orphan_cleaner_deletes_only_valid_old_inactive_marker_sessions()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.DirectoryPath, "Bridge");
        Directory.CreateDirectory(root);
        var oldOrphan = CreateSession(root, Guid.NewGuid(), 900, DateTimeOffset.UtcNow.AddDays(-2));
        var active = CreateSession(root, Guid.NewGuid(), 42, DateTimeOffset.UtcNow.AddDays(-2));
        var recent = CreateSession(root, Guid.NewGuid(), 901, DateTimeOffset.UtcNow.AddHours(-2));
        var noMarker = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(noMarker);
        var random = Path.Combine(root, "do-not-delete");
        Directory.CreateDirectory(random);

        var cleaner = new OrphanBridgeSessionCleaner(root, isMatchingBridgeProcess: pid => pid == 42);
        var scan = cleaner.Scan();

        Assert.Equal(1, scan.OrphanCount);
        Assert.Equal(oldOrphan, Assert.Single(scan.Sessions, item => item.CanDelete).DirectoryPath);
        var result = cleaner.Clean(scan);
        Assert.Equal(1, result.Deleted);
        Assert.False(Directory.Exists(oldOrphan));
        Assert.True(Directory.Exists(active));
        Assert.True(Directory.Exists(recent));
        Assert.True(Directory.Exists(noMarker));
        Assert.True(Directory.Exists(random));
    }

    [Fact]
    public void Orphan_cleaner_rejects_reparse_session_when_supported()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.DirectoryPath, "Bridge");
        Directory.CreateDirectory(root);
        var session = CreateSession(root, Guid.NewGuid(), 900, DateTimeOffset.UtcNow.AddDays(-2));
        var outside = Path.Combine(workspace.DirectoryPath, "outside");
        Directory.CreateDirectory(outside);
        try { Directory.CreateSymbolicLink(Path.Combine(session, "escape"), outside); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

        var cleaner = new OrphanBridgeSessionCleaner(root, isMatchingBridgeProcess: _ => false);
        var item = Assert.Single(cleaner.Scan().Sessions);
        Assert.False(item.CanDelete);
        Assert.Contains("reparse", item.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(outside));
    }

    [Fact]
    public void Orphan_cleaner_handles_a_missing_temp_root_without_creating_or_throwing()
    {
        using var workspace = new TestWorkspace();
        var root = Path.Combine(workspace.DirectoryPath, "missing Temp", "EplanEdzManager", "Bridge");

        var scan = new OrphanBridgeSessionCleaner(root).Scan();

        Assert.Empty(scan.Sessions);
        Assert.False(Directory.Exists(root));
    }

    private static string CreateSession(string root, Guid id, int pid, DateTimeOffset createdUtc)
    {
        var path = Path.Combine(root, id.ToString("N"));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, OrphanBridgeSessionCleaner.SessionMarkerFileName), JsonSerializer.Serialize(new
        {
            sessionId = id.ToString("N"),
            pid,
            createdUtc,
            appVersion = "1.0.0-beta.3",
            component = "EplanBridge"
        }));
        File.WriteAllText(Path.Combine(path, "payload.txt"), "test");
        return path;
    }
}
