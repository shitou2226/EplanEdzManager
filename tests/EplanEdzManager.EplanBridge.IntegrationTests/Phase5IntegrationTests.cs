using System.Diagnostics;
using System.Text.Json;
using EplanEdzManager.Core.Identity;
using EplanEdzManager.Core.Model;
using EplanEdzManager.Edz.Documents;
using EplanEdzManager.EplanBridge.Client;
using EplanEdzManager.EplanBridge.Protocol;
using Xunit;

namespace EplanEdzManager.EplanBridge.IntegrationTests;

public sealed class Phase5IntegrationTests
{
    private static readonly string Root = FindRoot();
    private static readonly string OutputRoot = Path.Combine(Root, "output", "phase5", "integration");
    private static readonly string TargetMasterDataRoot = Path.Combine(OutputRoot, "TargetMasterData");

    [Fact]
    public async Task Bridge_starts_from_clean_output_without_redistributed_eplan_api_dlls()
    {
        Directory.CreateDirectory(TargetMasterDataRoot);
        var options = CreateOptions();
        options.ConnectionTimeout = TimeSpan.FromSeconds(30);
        var bridgeDirectory = Path.GetDirectoryName(options.BridgeExecutablePath)!;
        Assert.Empty(Directory.EnumerateFiles(bridgeDirectory, "Eplan.EplApi.*.dll", SearchOption.TopDirectoryOnly));

        await using var client = new EplanBridgeClient(options);
        await client.StartAsync();
        var ping = await client.PingAsync();

        Assert.False(string.IsNullOrWhiteSpace(ping.BridgeVersion));
        Assert.Equal(client.SessionId, ping.SessionId);
    }

    [Fact]
    public async Task Official_import_is_previewed_backed_up_and_verified_for_one_multi_and_mixed_edz()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var selectedOne = EnsureSelectedEdz("SelectedOne.edz");
        var selectedMulti = EnsureSelectedEdz("SelectedMulti.edz");
        var mixed = EnsureSelectedEdz("MixedParts.edz");
        var target = FreshTarget("SafeImportTarget.mdb", selectedOne);

        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var inspection = await client.InspectPartsDatabaseAsync(target);
        Assert.True(inspection.Success, inspection.ErrorCode + ": " + inspection.TechnicalDetails);
        Assert.True(inspection.CanImport);
        Assert.Equal(0, inspection.PartCount);
        Assert.All(inspection.ResourceMappings, mapping => Assert.StartsWith(TargetMasterDataRoot, mapping.ResolvedPath, StringComparison.OrdinalIgnoreCase));

        var oneParts = ReadParts(selectedOne);
        var onePreview = await client.PreviewPartsImportAsync(target, selectedOne, oneParts);
        Assert.Equal(1, onePreview.NewCount);
        var oneResult = await client.ImportPartsAsync(onePreview, DefaultDecisions(onePreview));
        Assert.True(oneResult.Success, oneResult.ErrorCode + ": " + oneResult.TechnicalDetails);
        Assert.Equal(1, oneResult.Added);
        Assert.True(File.Exists(oneResult.BackupPath));
        Assert.Equal(64, oneResult.BackupSha256.Length);
        Assert.True(File.Exists(oneResult.AuditReportPath));
        Assert.Equal(0, (await client.InspectPartsDatabaseAsync(oneResult.BackupPath)).PartCount);

        var duplicatePreview = await client.PreviewPartsImportAsync(target, selectedOne, oneParts);
        Assert.Equal(1, duplicatePreview.ExistingCount);
        Assert.Equal(0, duplicatePreview.ConflictCount);
        var duplicateResult = await client.ImportPartsAsync(duplicatePreview, DefaultDecisions(duplicatePreview));
        Assert.True(duplicateResult.Success);
        Assert.Equal(1, duplicateResult.Skipped);
        Assert.Equal(0, duplicateResult.Added);

        var multiParts = ReadParts(selectedMulti);
        var multiPreview = await client.PreviewPartsImportAsync(target, selectedMulti, multiParts);
        Assert.Equal(9, multiPreview.NewCount);
        Assert.Equal(1, multiPreview.ExistingCount);
        var multiResult = await client.ImportPartsAsync(multiPreview, DefaultDecisions(multiPreview));
        Assert.True(multiResult.Success, multiResult.ErrorCode + ": " + multiResult.TechnicalDetails);
        Assert.Equal(9, multiResult.Added);
        Assert.Equal(1, multiResult.Skipped);

        var mixedParts = ReadParts(mixed);
        var mixedPreview = await client.PreviewPartsImportAsync(target, mixed, mixedParts);
        Assert.Equal(1, mixedPreview.NewCount);
        Assert.Equal(1, mixedPreview.ExistingCount);
        var mixedResult = await client.ImportPartsAsync(mixedPreview, DefaultDecisions(mixedPreview));
        Assert.True(mixedResult.Success, mixedResult.ErrorCode + ": " + mixedResult.TechnicalDetails);
        Assert.Equal(1, mixedResult.Added);
        Assert.Equal(1, mixedResult.Skipped);

        var finalInspection = await client.InspectPartsDatabaseAsync(target);
        Assert.Equal(11, finalInspection.PartCount);
    }

    [Fact]
    public async Task Stale_preview_and_locked_database_fail_before_official_import()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var selectedOne = EnsureSelectedEdz("SelectedOne.edz");
        var parts = ReadParts(selectedOne);
        var staleTarget = FreshTarget("StalePreviewTarget.mdb", selectedOne);
        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var preview = await client.PreviewPartsImportAsync(staleTarget, selectedOne, parts);
        File.SetLastWriteTimeUtc(staleTarget, File.GetLastWriteTimeUtc(staleTarget).AddSeconds(5));
        var stale = await Assert.ThrowsAsync<EplanBridgeException>(() => client.ImportPartsAsync(preview, DefaultDecisions(preview)));
        Assert.Equal(BridgeProtocol.ErrorCodes.DatabaseChangedSincePreview, stale.ErrorCode);

        var lockedTarget = FreshTarget("LockedTarget.mdb", selectedOne);
        using var lockStream = new FileStream(lockedTarget, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var locked = await client.InspectPartsDatabaseAsync(lockedTarget);
        Assert.False(locked.Success);
        Assert.Equal(BridgeProtocol.ErrorCodes.DatabaseLocked, locked.ErrorCode);
    }

    [Fact]
    public async Task Controlled_metadata_conflict_is_visible_and_never_silently_updated()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var selectedOne = EnsureSelectedEdz("SelectedOne.edz");
        var fixtures = EnsureFixtures(selectedOne);
        var target = Path.Combine(OutputRoot, "ConflictTarget.mdb");
        File.Copy(fixtures.Conflict, target, true);
        File.SetAttributes(target, FileAttributes.Normal);
        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var preview = await client.PreviewPartsImportAsync(target, selectedOne, ReadParts(selectedOne));
        Assert.Equal(1, preview.ConflictCount);
        var conflict = Assert.Single(preview.Items);
        Assert.Equal(ImportPreviewStatus.ExistingDifferent, conflict.Status);
        Assert.Equal(ImportDecisionAction.Skip, conflict.SuggestedAction);
        Assert.Contains(conflict.Differences, difference => difference.Field == "TypeNumber");

        var skipped = await client.ImportPartsAsync(preview, DefaultDecisions(preview));
        Assert.True(skipped.Success);
        Assert.Equal(1, skipped.Skipped);
        Assert.Equal(0, skipped.Updated);

        var freshPreview = await client.PreviewPartsImportAsync(target, selectedOne, ReadParts(selectedOne));
        var update = new[] { new ImportDecisionDto { StableIdentity = conflict.StableIdentity, Action = ImportDecisionAction.Update } };
        var rejected = await Assert.ThrowsAsync<EplanBridgeException>(() => client.ImportPartsAsync(freshPreview, update));
        Assert.Equal(BridgeProtocol.ErrorCodes.ConflictRequiresDecision, rejected.ErrorCode);

        var selectedMulti = EnsureSelectedEdz("SelectedMulti.edz");
        var mixedConflictPreview = await client.PreviewPartsImportAsync(target, selectedMulti, ReadParts(selectedMulti));
        Assert.Equal(1, mixedConflictPreview.ConflictCount);
        Assert.Equal(9, mixedConflictPreview.NewCount);
        var mixedConflictResult = await client.ImportPartsAsync(mixedConflictPreview, DefaultDecisions(mixedConflictPreview));
        Assert.True(mixedConflictResult.Success, mixedConflictResult.TechnicalDetails);
        Assert.Equal(9, mixedConflictResult.Added);
        Assert.Equal(1, mixedConflictResult.Skipped);
        var preservedConflict = await client.PreviewPartsImportAsync(target, selectedOne, ReadParts(selectedOne));
        Assert.Equal(1, preservedConflict.ConflictCount);
        Assert.Contains(Assert.Single(preservedConflict.Items).Differences, difference => difference.Field == "TypeNumber");
    }

    [Fact]
    public async Task Changed_validated_edz_is_rejected_before_backup_or_official_write()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var source = EnsureSelectedEdz("SelectedOne.edz");
        var workingEdz = Path.Combine(OutputRoot, "FailureAfterPreview.edz");
        File.Copy(source, workingEdz, true);
        var target = FreshTarget("FailureTarget.mdb", source);
        var backupDirectory = Path.Combine(Path.GetDirectoryName(target)!, "backup");
        var backupsBefore = Directory.Exists(backupDirectory)
            ? Directory.GetFiles(backupDirectory, "FailureTarget.before-*.mdb").ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var preview = await client.PreviewPartsImportAsync(target, workingEdz, ReadParts(source));
        File.WriteAllBytes(workingEdz, new byte[] { 0x45, 0x44, 0x5A, 0x00 });
        var failure = await Assert.ThrowsAsync<EplanBridgeException>(() => client.ImportPartsAsync(preview, DefaultDecisions(preview)));
        Assert.Equal(BridgeProtocol.ErrorCodes.DatabaseChangedSincePreview, failure.ErrorCode);
        Assert.Contains("validated EDZ changed", failure.Message, StringComparison.OrdinalIgnoreCase);

        var newBackups = Directory.GetFiles(backupDirectory, "FailureTarget.before-*.mdb")
            .Where(path => !backupsBefore.Contains(path)).ToArray();
        Assert.Empty(newBackups);
        var targetInspection = await client.InspectPartsDatabaseAsync(target);
        Assert.True(targetInspection.Success, targetInspection.TechnicalDetails);
        Assert.Equal(0, targetInspection.PartCount);
    }

    [Fact]
    public async Task Partial_selection_of_new_rows_is_rejected_before_official_write()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var selectedMulti = EnsureSelectedEdz("SelectedMulti.edz");
        var target = FreshTarget("PartialNewSelectionTarget.mdb", EnsureSelectedEdz("SelectedOne.edz"));
        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var preview = await client.PreviewPartsImportAsync(target, selectedMulti, ReadParts(selectedMulti));
        Assert.Equal(10, preview.NewCount);
        var decisions = DefaultDecisions(preview).ToArray();
        decisions[0].Action = ImportDecisionAction.Skip;

        var rejected = await Assert.ThrowsAsync<EplanBridgeException>(() => client.ImportPartsAsync(preview, decisions));
        Assert.Equal(BridgeProtocol.ErrorCodes.ConflictRequiresDecision, rejected.ErrorCode);
        Assert.Contains("whole EDZ", rejected.TechnicalDetails, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, (await client.InspectPartsDatabaseAsync(target)).PartCount);
    }

    [Fact]
    public async Task Cancellation_during_official_import_reports_the_verified_final_database_state()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var selectedMulti = EnsureSelectedEdz("SelectedMulti.edz");
        var target = FreshTarget("CancelledDuringImportTarget.mdb", EnsureSelectedEdz("SelectedOne.edz"));
        await using var client = new EplanBridgeClient(CreateOptions());
        await client.StartAsync();
        var preview = await client.PreviewPartsImportAsync(target, selectedMulti, ReadParts(selectedMulti));
        using var cancellation = new CancellationTokenSource();
        var progress = new CancelAtImportProgress(cancellation);
        var result = await client.ImportPartsAsync(preview, DefaultDecisions(preview), progress, cancellation.Token);

        Assert.True(progress.Requested);
        Assert.Equal(10, result.Added);
        Assert.Equal(0, result.Failed);
        Assert.Contains(result.Warnings, warning => warning.Contains("could not be aborted", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(10, (await client.InspectPartsDatabaseAsync(target)).PartCount);
    }

    [Fact]
    public async Task Performance_baseline_records_inspect_preview_backup_import_verification_and_cleanup()
    {
        Directory.CreateDirectory(OutputRoot);
        Directory.CreateDirectory(TargetMasterDataRoot);
        var selectedOne = EnsureSelectedEdz("SelectedOne.edz");
        var selectedMulti = EnsureSelectedEdz("SelectedMulti.edz");
        var oneParts = ReadParts(selectedOne);
        var tenParts = ReadParts(selectedMulti);
        var oneTarget = FreshTarget("PerformanceOneTarget.mdb", selectedOne);
        var tenTarget = FreshTarget("PerformanceTenTarget.mdb", selectedOne);
        var previewTarget = FreshTarget("PerformancePreview100Target.mdb", selectedOne);
        var hundredPreviewRows = Enumerable.Range(0, 100).Select(index =>
        {
            var part = tenParts[index % tenParts.Count];
            return new ExportPartDto
            {
                StablePartIdentity = part.StablePartIdentity + ":preview:" + index.ToString("D3"),
                PreferredSourceEdz = part.PreferredSourceEdz,
                Manufacturer = part.Manufacturer,
                PackageKey = part.PackageKey,
                PartNumber = part.PartNumber,
                Variant = part.Variant
            };
        }).ToArray();

        var client = new EplanBridgeClient(CreateOptions());
        var startup = Stopwatch.StartNew();
        await client.StartAsync();
        startup.Stop();
        var inspection = await client.InspectPartsDatabaseAsync(oneTarget);
        var onePreview = await client.PreviewPartsImportAsync(oneTarget, selectedOne, oneParts);
        var oneResult = await client.ImportPartsAsync(onePreview, DefaultDecisions(onePreview));
        var tenPreview = await client.PreviewPartsImportAsync(tenTarget, selectedMulti, tenParts);
        var tenResult = await client.ImportPartsAsync(tenPreview, DefaultDecisions(tenPreview));
        var hundredPreview = await client.PreviewPartsImportAsync(previewTarget, selectedMulti, hundredPreviewRows);
        var cleanup = Stopwatch.StartNew();
        await client.DisposeAsync();
        cleanup.Stop();

        Assert.True(inspection.Success);
        Assert.Equal(1, oneResult.Added);
        Assert.Equal(10, tenResult.Added);
        Assert.Equal(100, hundredPreview.NewCount);

        var report = new
        {
            measuredUtc = DateTimeOffset.UtcNow,
            environment = new { configuration = "Release", eplan = oneResult.RuntimeVersion, database = "EPLAN 2.9 MDB" },
            bridgeStartupMilliseconds = startup.Elapsed.TotalMilliseconds,
            databaseInspectMilliseconds = inspection.ElapsedMilliseconds,
            preview = new
            {
                onePartMilliseconds = onePreview.ElapsedMilliseconds,
                tenPartMilliseconds = tenPreview.ElapsedMilliseconds,
                hundredRowsMilliseconds = hundredPreview.ElapsedMilliseconds,
                hundredRowsNote = "Preview-only load using 100 application request rows repeated across the 10 real parts in SelectedMulti.edz; not a 100-distinct-part import claim."
            },
            onePartImport = Timings(oneResult),
            tenPartImport = Timings(tenResult),
            bridgeCleanupMilliseconds = cleanup.Elapsed.TotalMilliseconds
        };
        File.WriteAllText(
            Path.Combine(OutputRoot, "Phase5Performance.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static IReadOnlyList<ImportDecisionDto> DefaultDecisions(PreviewPartsImportResult preview) =>
        preview.Items.Select(item => new ImportDecisionDto
        {
            StableIdentity = item.StableIdentity,
            Action = item.Status == ImportPreviewStatus.New ? ImportDecisionAction.ImportNew : ImportDecisionAction.Skip
        }).ToArray();

    private static object Timings(ImportPartsResult result) => new
    {
        result.ElapsedMilliseconds,
        result.BackupElapsedMilliseconds,
        result.OfficialImportElapsedMilliseconds,
        result.VerificationElapsedMilliseconds,
        result.Added,
        result.Skipped,
        result.Failed
    };

    private static IReadOnlyList<ExportPartDto> ReadParts(string edz)
    {
        var open = new EdzDocumentReader().Open(edz, CancellationToken.None);
        Assert.NotNull(open.Document);
        using var document = open.Document!;
        return document.Parts.Take(1000, CancellationToken.None).Select(Map).ToArray();
    }

    private static ExportPartDto Map(PartRecord part) => new()
    {
        StablePartIdentity = StablePartIdentity.Create(part.Manufacturer, part.PartNumber, part.Variant, part.PackageKey).Value,
        PreferredSourceEdz = part.SourceEdz,
        Manufacturer = part.Manufacturer ?? string.Empty,
        PackageKey = part.PackageKey ?? string.Empty,
        PartNumber = part.PartNumber ?? string.Empty,
        Variant = part.Variant ?? "1",
        SourceFileSize = new FileInfo(part.SourceEdz).Length,
        SourceLastWriteTimeUtcTicks = new FileInfo(part.SourceEdz).LastWriteTimeUtc.Ticks
    };

    private static string FreshTarget(string fileName, string selectedOne)
    {
        var fixture = EnsureEmptyFixture(selectedOne);
        var target = Path.Combine(OutputRoot, fileName);
        File.Copy(fixture, target, true);
        File.SetAttributes(target, FileAttributes.Normal);
        return target;
    }

    private static string EnsureEmptyFixture(string selectedOne)
    {
        return EnsureFixtures(selectedOne).Empty;
    }

    private static (string Empty, string Conflict) EnsureFixtures(string selectedOne)
    {
        var fixtureDirectory = Path.Combine(Root, "output", "phase5", "smoke");
        var fixture = Path.Combine(fixtureDirectory, "EmptyTargetTemplate.mdb");
        var conflict = Path.Combine(fixtureDirectory, "ConflictTargetTemplate.mdb");
        if (File.Exists(fixture) && File.Exists(conflict)) return (fixture, conflict);
        Directory.CreateDirectory(fixtureDirectory);
        var executable = Path.Combine(Root, "tests", "EplanApi.IntegrationTests", "bin", "Release", "net472", "EplanApi.IntegrationTests.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("Build the net472 EPLAN API integration runner first.", executable);
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--phase5");
        start.ArgumentList.Add("--execute");
        start.ArgumentList.Add("--variant-bin");
        start.ArgumentList.Add(EplanIntegrationEnvironment.VariantBinDirectory);
        start.ArgumentList.Add("--platform-bin");
        start.ArgumentList.Add(EplanIntegrationEnvironment.PlatformBinDirectory);
        start.ArgumentList.Add("--sample");
        start.ArgumentList.Add(selectedOne);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(fixtureDirectory);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Phase 5 smoke runner.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True(File.Exists(fixture));
        Assert.True(File.Exists(conflict));
        return (fixture, conflict);
    }

    private static string EnsureSelectedEdz(string name)
    {
        var existing = Path.Combine(Root, "output", "phase4", "integration", name);
        if (File.Exists(existing)) return existing;
        throw new FileNotFoundException("Run the Phase 4 official integration test to generate the validated EDZ prerequisite.", existing);
    }

    private static EplanBridgeClientOptions CreateOptions() => new()
    {
        BridgeExecutablePath = Path.Combine(Root, "src", "EplanEdzManager.EplanBridge", "bin", "Release", "net472", "EplanEdzManager.EplanBridge.exe"),
        EplanVariantBinDirectory = EplanIntegrationEnvironment.VariantBinDirectory,
        EplanPlatformBinDirectory = EplanIntegrationEnvironment.PlatformBinDirectory,
        TargetMasterDataRoot = TargetMasterDataRoot,
        ConnectionTimeout = TimeSpan.FromMinutes(2),
        CapabilityTimeout = TimeSpan.FromMinutes(3),
        OperationIdleTimeout = TimeSpan.FromMinutes(30)
    };

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

    private sealed class CancelAtImportProgress : IProgress<ImportProgress>
    {
        private readonly CancellationTokenSource _source;

        public CancelAtImportProgress(CancellationTokenSource source) => _source = source;

        public bool Requested { get; private set; }

        public void Report(ImportProgress value)
        {
            if (Requested || value.Stage != BridgeProtocol.ProgressStages.Importing) return;
            Requested = true;
            _source.Cancel();
        }
    }
}
