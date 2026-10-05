using EplanEdzManager.Application;
using EplanEdzManager.EplanBridge.Protocol;
using System.IO;

namespace EplanEdzManager.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly Dictionary<long, bool> _selectedPartExportReadiness = new();

    public bool CanExportMyLibrarySelection =>
        SelectedSavedPartCount > 0 &&
        _selectedPartExportReadiness.Count == SelectedSavedPartCount &&
        _selectedPartExportReadiness.Values.All(value => value);

    public bool IsEplanBridgeExecutableAvailable => _application?.IsEplanBridgeExecutableAvailable == true;

    public async Task<SelectedPartsExportPreparation> PrepareMyLibraryExportAsync(CancellationToken cancellationToken = default)
    {
        if (_application is null)
            return new SelectedPartsExportPreparation(Array.Empty<PartExportRequest>(), new[] { new ExportValidationIssue("EXPORT-APP", string.Empty, "应用程序尚未初始化。") });
        var ids = _application.Selection.Snapshot().Select(item => item.SavedPartId).ToArray();
        if (ids.Length == 0)
            return new SelectedPartsExportPreparation(Array.Empty<PartExportRequest>(), new[] { new ExportValidationIssue("EXPORT-EMPTY", string.Empty, "请至少选择一个我的部件库中的部件。") });
        return await _application.PrepareSelectedPartsExportAsync(ids, cancellationToken);
    }

    public static SelectedPartsExportPreparation PrepareCatalogExport(IReadOnlyCollection<PartSummary> parts) =>
        EdzManagerApplication.PrepareCatalogPartsExport(parts);

    private void TrackExportReadiness(SavedPartRow row)
    {
        if (row.IsSelected)
        {
            var summary = row.Summary;
            _selectedPartExportReadiness[row.Id] =
                !string.IsNullOrWhiteSpace(summary.PartNumber) &&
                !string.IsNullOrWhiteSpace(summary.PreferredEdzPath) &&
                File.Exists(summary.PreferredEdzPath) &&
                summary.PreferredCurrentPartId is not null &&
                summary.SourceStatus is not (MyLibrarySourceStatus.SourceMissing or MyLibrarySourceStatus.Unresolved);
        }
        else
        {
            _selectedPartExportReadiness.Remove(row.Id);
        }
        OnPropertyChanged(nameof(CanExportMyLibrarySelection));
    }

    private void ClearExportReadiness()
    {
        _selectedPartExportReadiness.Clear();
        OnPropertyChanged(nameof(CanExportMyLibrarySelection));
    }

    public Task<ExportResult> ExportSelectedPartsAsync(
        SelectedPartsExportPreparation preparation,
        string outputPath,
        IProgress<ExportProgress> progress,
        CancellationToken cancellationToken)
    {
        if (_application is null) throw new InvalidOperationException("The application is not initialized.");
        return _application.ExportSelectedPartsAsync(preparation, outputPath, progress, cancellationToken);
    }

    public Task<PartsDatabaseImportSession> StartPartsDatabaseImportSessionAsync(
        string targetMasterDataRoot,
        CancellationToken cancellationToken = default)
    {
        if (_application is null) throw new InvalidOperationException("The application is not initialized.");
        return _application.StartPartsDatabaseImportSessionAsync(targetMasterDataRoot, cancellationToken);
    }
}
