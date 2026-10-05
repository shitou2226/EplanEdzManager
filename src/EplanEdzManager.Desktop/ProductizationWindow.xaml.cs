using System.Diagnostics;
using System.IO;
using System.Windows;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.ViewModels;
using Microsoft.Win32;

namespace EplanEdzManager.Desktop;

public partial class ProductizationWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly EdzManagerApplication _application;
    private readonly OrphanBridgeSessionCleaner _cleaner = new();
    private OrphanSessionScan? _orphanScan;

    public ProductizationWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _application = viewModel.ProductizationApplication;
    }

    public void SelectAbout() => MainTabs.SelectedIndex = MainTabs.Items.Count - 1;

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AddInStatusText.Text = "Add-In: " + _viewModel.AddInStatusText;
        AddInPathText.Text = InstallationLayout.FindAddInAssembly() ?? "Add-In DLL not found in this build layout.";
        BackupText.Text = _viewModel.Settings.LastBackupPath is null
            ? "Recent Backup: none"
            : $"Recent Backup: {_viewModel.Settings.LastBackupPath}\nLast Backup: {_viewModel.Settings.LastBackupUtc:u}";
        AboutText.Text = $"Version: {ProductInfo.Version}\nBuild / Git commit: {ProductInfo.Build}\nSupported EPLAN: {ProductInfo.SupportedEplanDisplay}\nSigning: Unsigned Beta\nProtocol: Bridge 1.1 / Add-In 1.0\nLogs: {Path.Combine(ApplicationPaths.LocalDataRoot, "Logs")}\nLicense notices: THIRD_PARTY_NOTICES.md\n\nNo telemetry. No cloud service is required.";
        await RefreshCapabilitiesAsync(false);
        await RefreshDatabaseAsync();
    }

    private async void RefreshCapabilities_Click(object sender, RoutedEventArgs e) => await RefreshCapabilitiesAsync(false);

    private async Task RefreshCapabilitiesAsync(bool probeBridge)
    {
        await RunBusyAsync(async () =>
        {
            var environment = new EplanEnvironmentDetector().Detect(_viewModel.Settings);
            var prerequisites = _application.CheckPrerequisites(_viewModel.Settings);
            CapabilityGrid.ItemsSource = await _application.RunCapabilityCheckAsync(_viewModel.Settings, _viewModel.AddInStatusText, probeBridge);
            EnvironmentText.Text = $"Install Root: {prerequisites.InstallRoot}\nInstall Root readable: {prerequisites.InstallRootReadable}; Windows/process x64: {prerequisites.WindowsX64}; bundled .NET 8: {prerequisites.DesktopRuntimeReady}\nEPLAN: {environment.CompatibilityText}\nVersion: {Show(environment.EplanVersion)}\nPlatform Bin: {Show(environment.PlatformBinDirectory)}\nVariant Bin: {Show(environment.VariantBinDirectory)}\nAPI: {Show(environment.ApiVersion)}\n.NET Framework 4.7.2+: {prerequisites.NetFramework472OrLater}; LocalAppData writable: {prerequisites.LocalAppDataWritable}; Temp writable: {prerequisites.TempWritable}\n{string.Join(Environment.NewLine, environment.Messages)}";
        }, probeBridge ? "Running isolated EPLAN capability probe…" : "Refreshing local environment…");
    }

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _application.RunDiagnosticsAsync(_viewModel.Settings, _viewModel.AddInStatusText, includeBridgeProbe: true);
            StatusText.Text = "Diagnostic report: " + result.ReportPath;
        }, "Running diagnostics…");
        await RefreshCapabilitiesAsync(false);
    }

    private async void ExportBundle_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "导出诊断包", Filter = "ZIP archive|*.zip", FileName = $"DiagnosticBundle-{DateTime.Now:yyyyMMdd-HHmmss}.zip", InitialDirectory = ApplicationPaths.DiagnosticDirectory };
        if (dialog.ShowDialog(this) != true) return;
        await RunBusyAsync(async () =>
        {
            var result = await _application.ExportDiagnosticBundleAsync(dialog.FileName, _viewModel.Settings, _viewModel.AddInStatusText, includeBridgeProbe: true);
            StatusText.Text = "Diagnostic bundle: " + result.BundlePath;
        }, "Exporting sanitized diagnostic bundle…");
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => OpenDirectory(Path.Combine(ApplicationPaths.LocalDataRoot, "Logs"));
    private async void RefreshDatabase_Click(object sender, RoutedEventArgs e) => await RefreshDatabaseAsync();
    private async Task RefreshDatabaseAsync()
    {
        var info = await _application.GetDatabaseMaintenanceInfoAsync();
        DatabaseText.Text = $"Path: {info.Path}\nSize: {FormatBytes(info.SizeBytes)} · Schema: {info.SchemaVersion} · Parts: {info.Parts:N0} · Resources: {info.Resources:N0} · Saved Parts: {info.SavedParts:N0} · Collections: {info.Collections:N0} · Tags: {info.Tags:N0}";
    }

    private async void Vacuum_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "仅在用户显式操作时执行 VACUUM。继续？", "SQLite VACUUM", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunBusyAsync(async () => { await _application.VacuumDatabaseAsync(); await RefreshDatabaseAsync(); }, "Running SQLite VACUUM…");
    }

    private async void Optimize_Click(object sender, RoutedEventArgs e) => await RunBusyAsync(async () => { await _application.OptimizeDatabaseAsync(); await RefreshDatabaseAsync(); }, "Running SQLite optimize…");

    private async void Health_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var report = await _application.CheckLibraryHealthAsync();
            HealthText.Text = $"Registered folders: {report.RegisteredFolders}; Indexed EDZ: {report.IndexedEdz}; Missing folders: {report.MissingFolders}; Source Missing: {report.MissingSources}; Index stale: {report.StaleSources}; Broken EDZ: {report.BrokenEdz}; Missing resources: {report.MissingResources}; Orphan Saved Source: {report.OrphanSavedSources}\n\n" +
                string.Join(Environment.NewLine, report.Issues.Select(item => $"[{item.Severity}] {item.Code}: {item.Message} {item.Path}"));
        }, "Checking library health…");
    }

    private async void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将清空并重建 Catalog 索引。My Library、Favorite、Collection、Tag、Note 与 Backup 不会删除。继续？", "Rebuild Catalog Index", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunBusyAsync(async () => { await _viewModel.RebuildCatalogIndexAsync(); await RefreshDatabaseAsync(); }, "Rebuilding Catalog index…");
    }

    private async void ResetSettings_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "重置应用设置？不会删除 index.db 或 My Library；下次启动将重新显示 First Run Setup。", "Reset Application Settings", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await _viewModel.ResetApplicationSettingsAsync();
        StatusText.Text = "Settings reset. Restart to run First Run Setup.";
    }

    private void ScanOrphans_Click(object sender, RoutedEventArgs e)
    {
        _orphanScan = _cleaner.Scan();
        OrphanGrid.ItemsSource = _orphanScan.Sessions;
        CleanOrphansButton.IsEnabled = _orphanScan.OrphanCount > 0;
        StatusText.Text = $"Found: {_orphanScan.OrphanCount} orphan sessions; Total: {FormatBytes(_orphanScan.OrphanBytes)}. No files were deleted.";
    }

    private void CleanOrphans_Click(object sender, RoutedEventArgs e)
    {
        if (_orphanScan is null || _orphanScan.OrphanCount == 0) return;
        if (MessageBox.Show(this, $"删除 {_orphanScan.OrphanCount} 个已验证 orphan session（{FormatBytes(_orphanScan.OrphanBytes)}）？", "Clean Temporary Sessions", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        var result = _cleaner.Clean(_orphanScan);
        StatusText.Text = $"Deleted: {result.Deleted}; {FormatBytes(result.DeletedBytes)}; skipped: {result.Skipped.Count}; errors: {result.Errors.Count}.";
        ScanOrphans_Click(sender, e);
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var folder = _viewModel.Settings.BackupFolder;
        try { Directory.CreateDirectory(folder); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "默认备份目录不可写。请在保存对话框中选择普通用户可写目录。\n\n" + exception.Message,
                "Backup My Library", MessageBoxButton.OK, MessageBoxImage.Warning);
            folder = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
        var dialog = new SaveFileDialog { Title = "Backup My Library", Filter = "JSON backup|*.json", FileName = $"my-library-{DateTime.Now:yyyyMMdd-HHmmss}.json", InitialDirectory = folder };
        if (dialog.ShowDialog(this) != true) return;
        var result = await _viewModel.ExportMyLibraryAsync(dialog.FileName);
        if (result is null) return;
        await _viewModel.RecordBackupAsync(result);
        BackupText.Text = $"Recent Backup: {result}\nLast Backup: {_viewModel.Settings.LastBackupUtc:u}";
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Restore My Library", Filter = "JSON backup|*.json", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        var preview = await _viewModel.PreviewImportAsync(dialog.FileName);
        if (preview is null) return;
        var text = $"Schema: {preview.SchemaVersion}\nNew: {preview.NewParts}\nExisting: {preview.ExistingParts}\nDuplicate: {preview.DuplicateParts}\nInvalid: {preview.InvalidParts}";
        if (!preview.CanApply) { MessageBox.Show(this, text + "\n\nBackup cannot be applied.", "Restore Preview", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        if (MessageBox.Show(this, text + "\n\nApply transactional metadata restore?", "Restore Preview", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await _viewModel.RestoreMyLibraryAsync(preview);
    }

    private void CopyAddInPath_Click(object sender, RoutedEventArgs e) { if (File.Exists(AddInPathText.Text)) Clipboard.SetText(AddInPathText.Text); }
    private void OpenAddInFolder_Click(object sender, RoutedEventArgs e) { var path = Path.GetDirectoryName(AddInPathText.Text); if (path is not null) OpenDirectory(path); }
    private void OpenNotices_Click(object sender, RoutedEventArgs e) => OpenInstalledDocument("THIRD_PARTY_NOTICES.md");
    private void OpenPrivacy_Click(object sender, RoutedEventArgs e) => OpenInstalledDocument(Path.Combine("Docs", "PRIVACY_AND_DATA.md"));

    private async Task RunBusyAsync(Func<Task> action, string message)
    {
        IsEnabled = false;
        StatusText.Text = message;
        try { await action(); if (StatusText.Text == message) StatusText.Text = "Completed."; }
        catch (Exception exception) { StatusText.Text = "Failed: " + exception.Message; MessageBox.Show(this, exception.Message, "EPLAN EDZ Manager", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { IsEnabled = true; }
    }

    private static void OpenInstalledDocument(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", relativePath));
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static void OpenDirectory(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
    private static string Show(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : value;
    private static string FormatBytes(long value) => value < 1024 ? value + " B" : value < 1024 * 1024 ? (value / 1024d).ToString("0.0") + " KiB" : (value / 1024d / 1024d).ToString("0.0") + " MiB";
}
