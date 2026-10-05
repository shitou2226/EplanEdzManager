using System.Diagnostics;
using EplanEdzManager.Core.Identity;
using EplanEdzManager.Core.Model;
using EplanEdzManager.Edz.Documents;
using EplanEdzManager.EplanBridge.Client;
using EplanEdzManager.EplanBridge.Protocol;
using Xunit;

namespace EplanEdzManager.EplanBridge.IntegrationTests;

public sealed class Phase4IntegrationTests
{
    private static readonly string Root = FindRoot();
    private static readonly string OutputRoot = Path.Combine(Root, "output", "phase4", "integration");

    [Fact]
    public async Task Official_single_multi_and_mixed_source_exports_round_trip()
    {
        Directory.CreateDirectory(OutputRoot);
        var meanWell = EplanIntegrationEnvironment.Fixture("明纬.edz");
        var secondSource = EplanIntegrationEnvironment.Fixture("德力西.edz");
        var secondPart = ReadFirstPart(secondSource);
        var options = CreateOptions();
        await using var client = new EplanBridgeClient(options);
        await client.StartAsync();
        var ping = await client.PingAsync();
        Assert.False(string.IsNullOrWhiteSpace(ping.BridgeVersion));
        var capabilities = await client.GetCapabilitiesAsync();
        Assert.True(capabilities.EplanRuntimeFound);
        Assert.True(capabilities.PartsServiceAvailable);
        Assert.True(capabilities.EdzConverterAvailable);
        Assert.True(capabilities.TemporaryDatabaseCapability);

        var onePath = FreshOutput("SelectedOne.edz");
        var one = await client.ExportPartsAsync(onePath, new[] { Part(meanWell, "明纬", "DR-100-24", "1", "DR-100-24") });
        AssertSuccess(one, 1, 1);

        var multiPath = FreshOutput("SelectedMulti.edz");
        var multiRequests = new[] { "DR-100-24", "DR-120-24", "DR-120-48", "DR-15-24", "DR-30-24", "DR-45-24", "DR-60-24", "DR-75-24", "DRP-240-24", "DRP-480-24" }
            .Select(number => Part(meanWell, "明纬", number, "1", number)).ToArray();
        var multi = await client.ExportPartsAsync(multiPath, multiRequests);
        AssertSuccess(multi, multiRequests.Length, 1);

        var mixedPath = FreshOutput("MixedParts.edz");
        var mixedRequests = new[]
        {
            Part(meanWell, "明纬", "DR-100-24", "1", "DR-100-24"),
            Part(secondSource, secondPart.Manufacturer ?? string.Empty, secondPart.PartNumber ?? string.Empty, secondPart.Variant ?? "1", secondPart.PackageKey ?? string.Empty)
        };
        var mixed = await client.ExportPartsAsync(mixedPath, mixedRequests);
        AssertSuccess(mixed, mixedRequests.Length, 2);

        var sequentialPath = FreshOutput("Sequential.edz");
        var sequential = await client.ExportPartsAsync(sequentialPath, new[] { Part(meanWell, "明纬", "DR-120-24", "1", "DR-120-24") });
        AssertSuccess(sequential, 1, 1);
    }

    [Fact]
    public async Task Duplicate_preferred_sources_and_missing_source_fail_safely()
    {
        Directory.CreateDirectory(OutputRoot);
        var options = CreateOptions();
        await using var client = new EplanBridgeClient(options);
        await client.StartAsync();
        var identity = "LP1:duplicate";
        var duplicates = new[]
        {
            new ExportPartDto { StablePartIdentity = identity, PreferredSourceEdz = EplanIntegrationEnvironment.Fixture("明纬.edz"), Manufacturer = "明纬", PartNumber = "DR-100-24", Variant = "1", PackageKey = "DR-100-24" },
            new ExportPartDto { StablePartIdentity = identity, PreferredSourceEdz = EplanIntegrationEnvironment.Fixture("欧姆龙.edz"), Manufacturer = "明纬", PartNumber = "DR-100-24", Variant = "1", PackageKey = "DR-100-24" }
        };
        var duplicateResult = await client.ExportPartsAsync(FreshOutput("Duplicate.edz"), duplicates);
        Assert.False(duplicateResult.Success);
        Assert.Equal(BridgeProtocol.ErrorCodes.InvalidRequest, duplicateResult.ErrorCode);
        Assert.False(File.Exists(duplicateResult.OutputPath));

        var missing = Path.Combine(OutputRoot, "missing-" + Guid.NewGuid().ToString("N") + ".edz");
        var exception = await Assert.ThrowsAsync<FileNotFoundException>(() => client.ExportPartsAsync(
            FreshOutput("MissingSource.edz"),
            new[] { new ExportPartDto { StablePartIdentity = "LP1:missing", PreferredSourceEdz = missing, Manufacturer = "M", PartNumber = "P", Variant = "1" } }));
        Assert.Equal(missing, exception.FileName);
    }

    [Fact]
    public async Task Source_changed_after_selection_is_rejected_before_official_import()
    {
        Directory.CreateDirectory(OutputRoot);
        var source = Path.Combine(OutputRoot, "ChangedAfterSelection.edz");
        File.Copy(EplanIntegrationEnvironment.Fixture("明纬.edz"), source, true);
        var request = Part(source, "明纬", "DR-100-24", "1", "DR-100-24");
        File.SetLastWriteTimeUtc(source, File.GetLastWriteTimeUtc(source).AddSeconds(2));

        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var result = await client.ExportPartsAsync(FreshOutput("ChangedAfterSelectionOutput.edz"), new[] { request });

        Assert.False(result.Success);
        Assert.Equal(BridgeProtocol.ErrorCodes.SourceMissing, result.ErrorCode);
        Assert.Contains("changed after selection", result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Bridge_crash_does_not_crash_client_and_next_bridge_can_start()
    {
        var options = CreateOptions();
        var session = string.Empty;
        await using (var client = new EplanBridgeClient(options))
        {
            await client.StartAsync();
            session = client.SessionId;
            await client.PingAsync();
            var process = Process.GetProcessById(client.BridgeProcessId!.Value);
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            await Assert.ThrowsAsync<EplanBridgeProcessExitedException>(() => client.PingAsync());
        }
        CleanupCrashedSession(session);
        await using var recovery = new EplanBridgeClient(options);
        await recovery.StartAsync();
        Assert.Equal(recovery.SessionId, (await recovery.PingAsync()).SessionId);
    }

    [Fact]
    public async Task Cancel_waits_for_an_official_api_safe_point_and_does_not_publish_output()
    {
        Directory.CreateDirectory(OutputRoot);
        var source = EplanIntegrationEnvironment.Fixture("德力西.edz");
        var selected = ReadFirstPart(source);
        var output = FreshOutput("Cancelled.edz");
        using var cancellation = new CancellationTokenSource();
        var progress = new CancellingProgress(cancellation, BridgeProtocol.ProgressStages.ImportingSource);
        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();

        var result = await client.ExportPartsAsync(
            output,
            new[] { Part(source, selected.Manufacturer ?? string.Empty, selected.PartNumber ?? string.Empty, selected.Variant ?? "1", selected.PackageKey ?? string.Empty) },
            progress,
            cancellation.Token);

        Assert.True(progress.CancellationRequested);
        Assert.False(result.Success);
        Assert.Equal(BridgeProtocol.ErrorCodes.Cancelled, result.ErrorCode);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Connection_timeout_is_distinct_and_the_client_remains_safe()
    {
        var options = CreateOptions();
        options.ConnectionTimeout = TimeSpan.FromTicks(1);
        var session = string.Empty;
        await using (var client = new EplanBridgeClient(options))
        {
            session = client.SessionId;
            await Assert.ThrowsAsync<TimeoutException>(() => client.StartAsync());
        }
        CleanupCrashedSession(session);
    }

    private static void AssertSuccess(ExportResult result, int requested, int sources)
    {
        Assert.True(result.Success, result.ErrorCode + ": " + result.UserMessage + Environment.NewLine + result.TechnicalDetails);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(requested, result.RequestedPartCount);
        Assert.Equal(requested, result.ExportedRequestedPartCount);
        Assert.Equal(sources, result.SourceEdzCount);
        Assert.True(result.ValidationSummary.ArchiveReadable);
        Assert.True(result.ValidationSummary.ManifestValid);
        Assert.Equal(0, result.ValidationSummary.MissingReferenceCount);
        Assert.Equal("PASS", result.ValidationSummary.Verdict);
    }

    private static ExportPartDto Part(string source, string manufacturer, string partNumber, string variant, string packageKey) => new()
    {
        StablePartIdentity = StablePartIdentity.Create(manufacturer, partNumber, variant, packageKey).Value,
        PreferredSourceEdz = source,
        Manufacturer = manufacturer,
        PackageKey = packageKey,
        PartNumber = partNumber,
        Variant = variant,
        SourceFileSize = new FileInfo(source).Length,
        SourceLastWriteTimeUtcTicks = new FileInfo(source).LastWriteTimeUtc.Ticks
    };

    private static PartRecord ReadFirstPart(string source)
    {
        var open = new EdzDocumentReader().Open(source, CancellationToken.None);
        Assert.NotNull(open.Document);
        using var document = open.Document!;
        return document.Parts.Take(1, CancellationToken.None).Single();
    }

    private static EplanBridgeClientOptions CreateOptions() => new()
    {
        BridgeExecutablePath = Path.Combine(Root, "src", "EplanEdzManager.EplanBridge", "bin", "Release", "net472", "EplanEdzManager.EplanBridge.exe"),
        EplanVariantBinDirectory = EplanIntegrationEnvironment.VariantBinDirectory,
        EplanPlatformBinDirectory = EplanIntegrationEnvironment.PlatformBinDirectory,
        ConnectionTimeout = TimeSpan.FromMinutes(2),
        CapabilityTimeout = TimeSpan.FromMinutes(3),
        OperationIdleTimeout = TimeSpan.FromMinutes(30)
    };

    private static string FreshOutput(string name)
    {
        var path = Path.Combine(OutputRoot, name);
        if (File.Exists(path)) File.Delete(path);
        return path;
    }

    private static void CleanupCrashedSession(string session)
    {
        var path = Path.Combine(Path.GetTempPath(), "EplanEdzManager", "Bridge", session);
        if (!Directory.Exists(path)) return;
        var approved = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "EplanEdzManager", "Bridge")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(path).StartsWith(approved, StringComparison.OrdinalIgnoreCase)) Directory.Delete(path, true);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EplanEdzManager.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("EplanEdzManager.sln was not found.");
    }

    private sealed class CancellingProgress : IProgress<ExportProgress>
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly string _stage;

        public CancellingProgress(CancellationTokenSource cancellation, string stage)
        {
            _cancellation = cancellation;
            _stage = stage;
        }

        public bool CancellationRequested { get; private set; }

        public void Report(ExportProgress value)
        {
            if (CancellationRequested || !string.Equals(value.Stage, _stage, StringComparison.Ordinal)) return;
            CancellationRequested = true;
            _cancellation.Cancel();
        }
    }
}
