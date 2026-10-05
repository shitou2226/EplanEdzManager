using EplanEdzManager.EplanBridge.Client;
using EplanEdzManager.EplanBridge.Protocol;

namespace EplanEdzManager.Application;

public sealed partial class EdzManagerApplication
{
    public bool IsEplanBridgeExecutableAvailable
    {
        get
        {
            var environment = new EplanEnvironmentDetector().Detect(_runtimeSettings);
            return File.Exists(CreateConfiguredBridgeOptions().BridgeExecutablePath)
                && environment.Detected
                && EplanEnvironmentDetector.IsBridgeAllowed(environment.Compatibility);
        }
    }

    public async Task<PartsDatabaseImportSession> StartPartsDatabaseImportSessionAsync(
        string targetMasterDataRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetMasterDataRoot) || !Directory.Exists(targetMasterDataRoot))
            throw new DirectoryNotFoundException("Select the target EPLAN master-data root before starting the Bridge: " + targetMasterDataRoot);
        var options = CreateConfiguredBridgeOptions();
        options.TargetMasterDataRoot = Path.GetFullPath(targetMasterDataRoot);
        var session = new PartsDatabaseImportSession(new EplanBridgeClient(options));
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

public sealed class PartsDatabaseImportSession : IAsyncDisposable
{
    private readonly EplanBridgeClient _client;

    internal PartsDatabaseImportSession(EplanBridgeClient client) => _client = client;

    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        await _client.StartAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = await _client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.EplanRuntimeFound || !capabilities.PartsServiceAvailable || !capabilities.EdzConverterAvailable)
            throw new EplanBridgeException(BridgeProtocol.ErrorCodes.RuntimeNotFound, "The EPLAN 2.9 Bridge capability check did not pass.");
    }

    public Task<InspectPartsDatabaseResult> InspectAsync(string databasePath, CancellationToken cancellationToken = default) =>
        _client.InspectPartsDatabaseAsync(databasePath, cancellationToken);

    public Task<PreviewPartsImportResult> PreviewAsync(
        SelectedPartsExportPreparation preparation,
        string targetDatabase,
        string validatedEdzPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.CanExport) throw new SelectedPartsExportValidationException(preparation.Issues);
        return _client.PreviewPartsImportAsync(
            targetDatabase,
            validatedEdzPath,
            preparation.Parts.Select(part => new ExportPartDto
            {
                StablePartIdentity = part.StablePartIdentity,
                PreferredSourceEdz = part.PreferredSourceEdz,
                Manufacturer = part.Manufacturer ?? string.Empty,
                PackageKey = part.PackageKey ?? string.Empty,
                PartNumber = part.PartNumber ?? string.Empty,
                Variant = part.Variant ?? string.Empty,
                SourceFileSize = part.SourceFileSize,
                SourceLastWriteTimeUtcTicks = part.SourceLastWriteTimeUtcTicks
            }).ToArray(),
            cancellationToken);
    }

    public Task<ImportPartsResult> ImportAsync(
        PreviewPartsImportResult preview,
        IReadOnlyCollection<ImportDecisionDto> decisions,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _client.ImportPartsAsync(preview, decisions, progress, cancellationToken);

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
