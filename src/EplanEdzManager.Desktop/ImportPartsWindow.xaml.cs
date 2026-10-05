using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.ViewModels;
using EplanEdzManager.EplanBridge.Protocol;
using Microsoft.Win32;

namespace EplanEdzManager.Desktop;

public partial class ImportPartsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SelectedPartsExportPreparation _preparation;
    private CancellationTokenSource? _operationCancellation;
    private PartsDatabaseImportSession? _session;
    private PreviewPartsImportResult? _preview;
    private ImportPartsResult? _result;
    private bool _running;

    public ImportPartsWindow(MainViewModel viewModel, SelectedPartsExportPreparation preparation)
    {
        _viewModel = viewModel;
        _preparation = preparation;
        InitializeComponent();
        SelectedCountText.Text = preparation.Parts.Count.ToString("N0") + " parts from " + preparation.SourceEdzCount.ToString("N0") + " preferred source EDZ files";
        ActionColumn.ItemsSource = new[] { ImportDecisionAction.Skip, ImportDecisionAction.ImportNew };
        LoadRecentDatabases();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            _operationCancellation?.Cancel();
            StageText.Text = "Cancelling… The active official API call will be allowed to return.";
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void BrowseEdz_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the Phase 4 validated EDZ", Filter = "EPLAN Data Archive|*.edz", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) EdzPathText.Text = dialog.FileName;
    }

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Choose the explicit target EPLAN 2.9 MDB", Filter = "EPLAN Parts Database|*.mdb", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) DatabasePathText.Text = dialog.FileName;
    }

    private void RecentDatabase_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentDatabasesCombo.SelectedItem is string path && File.Exists(path)) DatabasePathText.Text = path;
    }

    private void BrowseMasterData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the target EPLAN data root (contains picture/macro master data)", Multiselect = false };
        if (dialog.ShowDialog(this) == true) MasterDataRootText.Text = dialog.FolderName;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidatePaths()) return;
        SetRunning(true);
        var cancellation = BeginOperationCancellation();
        try
        {
            StageText.Text = "Step 2/8 — Inspecting Database";
            _session ??= await _viewModel.StartPartsDatabaseImportSessionAsync(MasterDataRootText.Text, cancellation.Token);
            var inspection = await _session.InspectAsync(DatabasePathText.Text, cancellation.Token);
            if (!inspection.Success || !inspection.CanImport)
            {
                ShowFailure(inspection.ErrorCode, inspection.UserMessage, inspection.TechnicalDetails);
                return;
            }
            RememberDatabase(inspection.DatabasePath);
            StageText.Text = "Step 3/8 — Preview and conflict analysis";
            _preview = await _session.PreviewAsync(_preparation, DatabasePathText.Text, EdzPathText.Text, cancellation.Token);
            var rows = _preview.Items.Select(item => new PreviewRow(item)).ToList();
            PreviewGrid.ItemsSource = rows;
            SummaryText.Text = $"Target: {_preview.TargetDatabase}{Environment.NewLine}" +
                $"Database type: {inspection.DatabaseType}; Exists: {inspection.Exists}; Readable: {inspection.Readable}; Writable: {inspection.Writable}; EPLAN compatible: {inspection.EplanCompatible}{Environment.NewLine}" +
                $"Existing parts: {inspection.PartCount:N0}; Last modified UTC: {new DateTime(inspection.LastModifiedUtcTicks, DateTimeKind.Utc):u}; Detected: {inspection.DetectedVersion}{Environment.NewLine}" +
                $"Preview: {_preview.NewCount:N0} New · {_preview.ExistingCount:N0} Existing · {_preview.ConflictCount:N0} Conflict · {_preview.AmbiguousCount:N0} Ambiguous · {_preview.InvalidCount:N0} Invalid{Environment.NewLine}" +
                "Default: import New only; all existing/conflicts are Skip. Selective Update is deliberately unavailable in this build.";
            DiagnosticsText.Text = string.Join(Environment.NewLine, inspection.ResourceMappings.Select(mapping => mapping.Variable + " = " + mapping.ResolvedPath)
                .Concat(_preview.Diagnostics));
            DiagnosticsText.Visibility = Visibility.Visible;
            StageText.Text = "Step 6/8 — Review the exact target, decisions, and automatic verified backup; then press IMPORT.";
            ImportButton.IsEnabled = _preview.InvalidCount == 0 && _preview.AmbiguousCount == 0;
            SkipExistingButton.Visibility = Visibility.Visible;
        }
        catch (Exception exception) { ShowFailure("DB101", "Preview did not complete.", exception.ToString()); }
        finally
        {
            CompleteOperationCancellation(cancellation);
            SetRunning(false);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _preview is null) return;
        var rows = PreviewGrid.ItemsSource.Cast<PreviewRow>().ToArray();
        var added = rows.Count(row => row.Action == ImportDecisionAction.ImportNew);
        var skipped = rows.Count(row => row.Action == ImportDecisionAction.Skip);
        var newRows = rows.Where(row => row.Status == ImportPreviewStatus.New).ToArray();
        var selectedNew = newRows.Count(row => row.Action == ImportDecisionAction.ImportNew);
        if (selectedNew > 0 && selectedNew != newRows.Length)
        {
            MessageBox.Show(this,
                "EPLAN 2.9 AppendNewRecords applies to the whole validated EDZ. To import only a subset of New rows, return to Phase 4 and export that subset as a new validated EDZ.",
                "Partial New selection is not safe", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmation = MessageBox.Show(this,
            $"Target:{Environment.NewLine}{_preview.TargetDatabase}{Environment.NewLine}{Environment.NewLine}" +
            $"Selected: {rows.Length:N0}{Environment.NewLine}New to import: {added:N0}{Environment.NewLine}Skipped: {skipped:N0}{Environment.NewLine}" +
            "Backup: a unique MDB copy will be created and reopened before import.",
            "Final confirmation — official EPLAN import", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        SetRunning(true);
        var cancellation = BeginOperationCancellation();
        ImportButton.IsEnabled = false;
        try
        {
            StageText.Text = "Step 7/8 — Preparing Backup";
            var progress = new Progress<ImportProgress>(ApplyProgress);
            _result = await _session.ImportAsync(_preview, rows.Select(row => new ImportDecisionDto
            {
                StableIdentity = row.Item.StableIdentity,
                Action = row.Action
            }).ToArray(), progress, cancellation.Token);
            StageText.Text = "Step 8/8 — " + (_result.Success ? "Completed" : _result.PartialSuccess ? "Partial Success" : "Failed");
            SummaryText.Text = $"Requested: {_result.Requested:N0} · Added: {_result.Added:N0} · Updated: {_result.Updated:N0} · Skipped: {_result.Skipped:N0} · Failed: {_result.Failed:N0}{Environment.NewLine}" +
                $"Target: {_result.TargetDatabase}{Environment.NewLine}Backup: {_result.BackupPath}{Environment.NewLine}Audit: {_result.AuditReportPath}{Environment.NewLine}" +
                "Elapsed: " + TimeSpan.FromMilliseconds(_result.ElapsedMilliseconds).ToString(@"hh\:mm\:ss");
            DiagnosticsText.Text = string.Join(Environment.NewLine, _result.Warnings.Concat(_result.Diagnostics)
                .Concat(_result.Outcomes.Select(outcome => outcome.PartNumber + " / " + outcome.Variant + ": " + outcome.Outcome + " — " + outcome.Diagnostic)));
            OpenBackupButton.Visibility = File.Exists(_result.BackupPath) ? Visibility.Visible : Visibility.Collapsed;
            ExportReportButton.Visibility = File.Exists(_result.AuditReportPath) ? Visibility.Visible : Visibility.Collapsed;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 1;
        }
        catch (Exception exception) { ShowFailure("DB201", "Import did not complete successfully.", exception.ToString()); }
        finally
        {
            CompleteOperationCancellation(cancellation);
            SetRunning(false);
        }
    }

    private void ApplyProgress(ImportProgress progress)
    {
        StageText.Text = "Step 7/8 — " + progress.Stage;
        ProgressBar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0) { ProgressBar.Maximum = progress.Total; ProgressBar.Value = Math.Min(progress.Current, progress.Total); }
        if (!string.IsNullOrWhiteSpace(progress.Message)) SummaryText.Text = progress.Message;
    }

    private bool ValidatePaths()
    {
        if (!File.Exists(EdzPathText.Text) || !File.Exists(DatabasePathText.Text) || !Directory.Exists(MasterDataRootText.Text))
        {
            MessageBox.Show(this, "Choose an existing validated EDZ, target MDB, and target EPLAN data root.", "Missing input", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    private void SetRunning(bool value)
    {
        _running = value;
        PreviewButton.IsEnabled = !value;
        CancelButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsEnabled = !value;
        if (value) ProgressBar.IsIndeterminate = true;
    }

    private void ShowFailure(string code, string message, string details)
    {
        StageText.Text = "Failed — " + code;
        SummaryText.Text = message;
        DiagnosticsText.Text = details;
        DiagnosticsText.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        StageText.Text = "Cancelling… waiting for the current official API call to return.";
        CancelButton.IsEnabled = false;
    }

    private void OpenBackup_Click(object sender, RoutedEventArgs e)
    {
        var folder = _result is null ? null : Path.GetDirectoryName(_result.BackupPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (_result is null || !File.Exists(_result.AuditReportPath)) return;
        var dialog = new SaveFileDialog
        {
            Title = "Export ImportReport.json",
            Filter = "JSON report|*.json",
            FileName = "ImportReport.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true) File.Copy(_result.AuditReportPath, dialog.FileName, true);
    }

    private void SkipExisting_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewGrid.ItemsSource is not IEnumerable<PreviewRow> rows) return;
        foreach (var row in rows.Where(row => row.Status != ImportPreviewStatus.New)) row.Action = ImportDecisionAction.Skip;
        PreviewGrid.Items.Refresh();
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        var session = _session;
        _session = null;
        if (session is not null) await session.DisposeAsync();
        Close();
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        var session = _session;
        _session = null;
        if (session is null) return;
        try { await session.DisposeAsync(); }
        catch
        {
            // The window is already closed; client disposal still closes its private pipe/process handles.
        }
    }

    private CancellationTokenSource BeginOperationCancellation()
    {
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        CancelButton.IsEnabled = true;
        return _operationCancellation;
    }

    private void CompleteOperationCancellation(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_operationCancellation, cancellation)) return;
        _operationCancellation = null;
        cancellation.Dispose();
    }

    private static string RecentDatabasesFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EplanEdzManager", "recent-parts-databases.json");

    private void LoadRecentDatabases()
    {
        try
        {
            var paths = File.Exists(RecentDatabasesFile)
                ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(RecentDatabasesFile)) ?? Array.Empty<string>()
                : Array.Empty<string>();
            RecentDatabasesCombo.ItemsSource = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        }
        catch (IOException) { RecentDatabasesCombo.ItemsSource = Array.Empty<string>(); }
        catch (JsonException) { RecentDatabasesCombo.ItemsSource = Array.Empty<string>(); }
    }

    private void RememberDatabase(string path)
    {
        try
        {
            var existing = RecentDatabasesCombo.ItemsSource?.Cast<string>() ?? Enumerable.Empty<string>();
            var paths = new[] { Path.GetFullPath(path) }.Concat(existing)
                .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(RecentDatabasesFile)!);
            File.WriteAllText(RecentDatabasesFile, JsonSerializer.Serialize(paths));
            RecentDatabasesCombo.ItemsSource = paths;
            RecentDatabasesCombo.SelectedIndex = -1;
        }
        catch (IOException)
        {
            // Recent paths are a local convenience only and never block the safety workflow.
        }
    }

    private sealed class PreviewRow
    {
        public PreviewRow(ImportPreviewItemDto item)
        {
            Item = item;
            Action = item.Status == ImportPreviewStatus.New ? ImportDecisionAction.ImportNew : ImportDecisionAction.Skip;
            DifferenceSummary = string.Join("; ", item.Differences.Select(value => value.Field + ": " + value.Existing + " → " + value.Incoming));
        }

        public ImportPreviewItemDto Item { get; }
        public string Manufacturer => Item.Manufacturer;
        public string PartNumber => Item.PartNumber;
        public string Variant => Item.Variant;
        public string Status => Item.Status;
        public string DifferenceSummary { get; }
        public string Action { get; set; }
    }
}
