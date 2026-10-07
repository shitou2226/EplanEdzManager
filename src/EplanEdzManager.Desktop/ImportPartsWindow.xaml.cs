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
    private static readonly ImportActionOption[] ImportActionOptions =
    {
        new(ImportDecisionAction.Skip, "跳过"),
        new(ImportDecisionAction.ImportNew, "导入新部件")
    };

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
        SelectedCountText.Text = $"{preparation.Parts.Count:N0} 个部件，来自 {preparation.SourceEdzCount:N0} 个首选 EDZ 源文件";
        ActionColumn.ItemsSource = ImportActionOptions;
        LoadRecentDatabases();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            _operationCancellation?.Cancel();
            StageText.Text = "正在取消…当前官方 API 调用完成后将安全退出。";
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private void BrowseEdz_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择第 4 阶段已验证的 EDZ", Filter = "EPLAN 数据归档 (*.edz)|*.edz", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) EdzPathText.Text = dialog.FileName;
    }

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择明确的目标 EPLAN 2.9 MDB", Filter = "EPLAN 部件数据库 (*.mdb)|*.mdb", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) DatabasePathText.Text = dialog.FileName;
    }

    private void RecentDatabase_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentDatabasesCombo.SelectedItem is string path && File.Exists(path)) DatabasePathText.Text = path;
    }

    private void BrowseMasterData_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择目标 EPLAN 数据根目录（包含图片/宏主数据）", Multiselect = false };
        if (dialog.ShowDialog(this) == true) MasterDataRootText.Text = dialog.FolderName;
    }

    private async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidatePaths()) return;
        SetRunning(true);
        var cancellation = BeginOperationCancellation();
        try
        {
            StageText.Text = "第 2/8 步 — 检查数据库";
            _session ??= await _viewModel.StartPartsDatabaseImportSessionAsync(MasterDataRootText.Text, cancellation.Token);
            var inspection = await _session.InspectAsync(DatabasePathText.Text, cancellation.Token);
            if (!inspection.Success || !inspection.CanImport)
            {
                ShowFailure(inspection.ErrorCode, inspection.UserMessage, inspection.TechnicalDetails);
                return;
            }
            RememberDatabase(inspection.DatabasePath);
            StageText.Text = "第 3/8 步 — 预览并分析冲突";
            _preview = await _session.PreviewAsync(_preparation, DatabasePathText.Text, EdzPathText.Text, cancellation.Token);
            var rows = _preview.Items.Select(item => new PreviewRow(item)).ToList();
            PreviewGrid.ItemsSource = rows;
            SummaryText.Text = $"目标数据库：{_preview.TargetDatabase}{Environment.NewLine}" +
                $"数据库类型：{inspection.DatabaseType}；存在：{YesNo(inspection.Exists)}；可读：{YesNo(inspection.Readable)}；可写：{YesNo(inspection.Writable)}；EPLAN 兼容：{YesNo(inspection.EplanCompatible)}{Environment.NewLine}" +
                $"已有部件：{inspection.PartCount:N0}；最后修改时间（UTC）：{new DateTime(inspection.LastModifiedUtcTicks, DateTimeKind.Utc):u}；检测版本：{inspection.DetectedVersion}{Environment.NewLine}" +
                $"预览：新部件 {_preview.NewCount:N0} · 已存在 {_preview.ExistingCount:N0} · 冲突 {_preview.ConflictCount:N0} · 匹配不明确 {_preview.AmbiguousCount:N0} · 无效 {_preview.InvalidCount:N0}{Environment.NewLine}" +
                "默认仅导入新部件；已有部件与冲突均跳过。本版本有意不提供选择性更新。";
            DiagnosticsText.Text = string.Join(Environment.NewLine, inspection.ResourceMappings.Select(mapping => mapping.Variable + " = " + mapping.ResolvedPath)
                .Concat(_preview.Diagnostics));
            DiagnosticsText.Visibility = Visibility.Visible;
            StageText.Text = "第 6/8 步 — 核对目标、处理决定和自动验证备份，然后点击“确认导入”。";
            ImportButton.IsEnabled = _preview.InvalidCount == 0 && _preview.AmbiguousCount == 0;
            SkipExistingButton.Visibility = Visibility.Visible;
        }
        catch (Exception exception) { ShowFailure("DB101", "预览未完成。", exception.ToString()); }
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
        var newRows = rows.Where(row => row.StatusCode == ImportPreviewStatus.New).ToArray();
        var selectedNew = newRows.Count(row => row.Action == ImportDecisionAction.ImportNew);
        if (selectedNew > 0 && selectedNew != newRows.Length)
        {
            MessageBox.Show(this,
                "EPLAN 2.9 的 AppendNewRecords 会作用于整个已验证 EDZ。若只想导入部分新部件，请返回第 4 阶段，将所需子集重新导出为新的已验证 EDZ。",
                "不能安全地只导入部分新部件", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var confirmation = MessageBox.Show(this,
            $"目标数据库：{Environment.NewLine}{_preview.TargetDatabase}{Environment.NewLine}{Environment.NewLine}" +
            $"所选部件：{rows.Length:N0}{Environment.NewLine}将导入的新部件：{added:N0}{Environment.NewLine}将跳过：{skipped:N0}{Environment.NewLine}" +
            "备份：导入前会创建一个唯一的 MDB 副本，并重新打开验证。",
            "最终确认 — 官方 EPLAN 导入", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK) return;
        SetRunning(true);
        var cancellation = BeginOperationCancellation();
        ImportButton.IsEnabled = false;
        try
        {
            StageText.Text = "第 7/8 步 — 准备备份";
            var progress = new Progress<ImportProgress>(ApplyProgress);
            _result = await _session.ImportAsync(_preview, rows.Select(row => new ImportDecisionDto
            {
                StableIdentity = row.Item.StableIdentity,
                Action = row.Action
            }).ToArray(), progress, cancellation.Token);
            StageText.Text = "第 8/8 步 — " + (_result.Success ? "已完成" : _result.PartialSuccess ? "部分成功" : "失败");
            SummaryText.Text = $"请求：{_result.Requested:N0} · 已添加：{_result.Added:N0} · 已更新：{_result.Updated:N0} · 已跳过：{_result.Skipped:N0} · 失败：{_result.Failed:N0}{Environment.NewLine}" +
                $"目标数据库：{_result.TargetDatabase}{Environment.NewLine}备份：{_result.BackupPath}{Environment.NewLine}审计报告：{_result.AuditReportPath}{Environment.NewLine}" +
                "耗时：" + TimeSpan.FromMilliseconds(_result.ElapsedMilliseconds).ToString(@"hh\:mm\:ss");
            DiagnosticsText.Text = string.Join(Environment.NewLine, _result.Warnings.Concat(_result.Diagnostics)
                .Concat(_result.Outcomes.Select(outcome => outcome.PartNumber + " / " + outcome.Variant + "：" + LocalizeOutcome(outcome.Outcome) + " — " + outcome.Diagnostic)));
            OpenBackupButton.Visibility = File.Exists(_result.BackupPath) ? Visibility.Visible : Visibility.Collapsed;
            ExportReportButton.Visibility = File.Exists(_result.AuditReportPath) ? Visibility.Visible : Visibility.Collapsed;
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 1;
        }
        catch (Exception exception) { ShowFailure("DB201", "导入未成功完成。", exception.ToString()); }
        finally
        {
            CompleteOperationCancellation(cancellation);
            SetRunning(false);
        }
    }

    private void ApplyProgress(ImportProgress progress)
    {
        StageText.Text = "第 7/8 步 — " + LocalizeProgressStage(progress.Stage);
        ProgressBar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0) { ProgressBar.Maximum = progress.Total; ProgressBar.Value = Math.Min(progress.Current, progress.Total); }
        if (!string.IsNullOrWhiteSpace(progress.Message)) SummaryText.Text = LocalizeProgressMessage(progress.Message);
    }

    private bool ValidatePaths()
    {
        if (!File.Exists(EdzPathText.Text) || !File.Exists(DatabasePathText.Text) || !Directory.Exists(MasterDataRootText.Text))
        {
            MessageBox.Show(this, "请选择确实存在的已验证 EDZ、目标 MDB 和目标 EPLAN 数据根目录。", "缺少输入", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        StageText.Text = "失败 — " + code;
        SummaryText.Text = LocalizeFailureMessage(code, message);
        DiagnosticsText.Text = details;
        DiagnosticsText.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = false;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCancellation?.Cancel();
        StageText.Text = "正在取消…等待当前官方 API 调用返回。";
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
            Title = "导出 ImportReport.json",
            Filter = "JSON 报告 (*.json)|*.json",
            FileName = "ImportReport.json",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) == true) File.Copy(_result.AuditReportPath, dialog.FileName, true);
    }

    private void SkipExisting_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewGrid.ItemsSource is not IEnumerable<PreviewRow> rows) return;
        foreach (var row in rows.Where(row => row.StatusCode != ImportPreviewStatus.New)) row.Action = ImportDecisionAction.Skip;
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

    private static string YesNo(bool value) => value ? "是" : "否";

    private static string LocalizeStatus(string value) => value switch
    {
        ImportPreviewStatus.New => "新部件",
        ImportPreviewStatus.ExistingSame => "已存在（相同）",
        ImportPreviewStatus.ExistingDifferent => "已存在（有差异）",
        ImportPreviewStatus.Ambiguous => "匹配不明确",
        ImportPreviewStatus.Invalid => "无效",
        _ => value
    };

    private static string LocalizeDifferenceField(string value) => value switch
    {
        "Manufacturer" => "厂商",
        "PartNumber" => "部件编号",
        "Variant" => "变体",
        "TypeNumber" => "型号",
        "OrderNumber" => "订货号",
        "Description" => "描述",
        "Picture" => "图片",
        "Macro" => "宏",
        _ => value
    };

    private static string LocalizeOutcome(string value) => value switch
    {
        ImportOutcomeStatus.Added => "已添加",
        ImportOutcomeStatus.Updated => "已更新",
        ImportOutcomeStatus.Skipped => "已跳过",
        ImportOutcomeStatus.Failed => "失败",
        _ => value
    };

    private static string LocalizeProgressStage(string value) => value switch
    {
        BridgeProtocol.ProgressStages.StartingRuntime => "启动 EPLAN 运行时",
        BridgeProtocol.ProgressStages.InspectingDatabase => "检查数据库",
        BridgeProtocol.ProgressStages.CheckingConflicts => "检查冲突",
        BridgeProtocol.ProgressStages.PreparingBackup => "准备备份",
        BridgeProtocol.ProgressStages.GeneratingValidatedEdz => "生成已验证 EDZ",
        BridgeProtocol.ProgressStages.OpeningTargetDatabase => "打开目标数据库",
        BridgeProtocol.ProgressStages.Importing => "导入部件",
        BridgeProtocol.ProgressStages.VerifyingParts => "验证部件",
        BridgeProtocol.ProgressStages.VerifyingResources => "验证资源",
        BridgeProtocol.ProgressStages.ClosingDatabase => "关闭数据库",
        BridgeProtocol.ProgressStages.CleaningUp => "清理临时资源",
        BridgeProtocol.ProgressStages.Completed => "已完成",
        _ => value
    };

    private static string LocalizeProgressMessage(string value) => value switch
    {
        "Rechecking the database fingerprint and preparing a unique verified MDB backup." => "正在重新检查数据库指纹，并准备唯一且已验证的 MDB 备份。",
        "Initializing the isolated EPLAN 2.9 runtime with the explicit target master-data mapping." => "正在使用明确的目标主数据映射初始化隔离的 EPLAN 2.9 运行时。",
        "Officially importing the validated EDZ in AppendNewRecords mode." => "正在使用 AppendNewRecords 模式通过官方 API 导入已验证的 EDZ。",
        "Closing and reopening the target database to verify each requested part." => "正在关闭并重新打开目标数据库，逐项验证请求的部件。",
        "Closing EPLAN runtime handles for the target database." => "正在关闭目标数据库的 EPLAN 运行时句柄。",
        "Import and post-import verification completed." => "导入及导入后验证已完成。",
        "Import completed with verification failures." => "导入已完成，但部分验证失败。",
        _ => value
    };

    private static string LocalizeFailureMessage(string code, string fallback) => code switch
    {
        BridgeProtocol.ErrorCodes.DatabaseNotFound => "找不到目标部件数据库。",
        BridgeProtocol.ErrorCodes.UnsupportedDatabase => "目标数据库不受支持，或未通过安全检查。",
        BridgeProtocol.ErrorCodes.DatabaseLocked => "目标数据库正被占用，无法获得独占写入权限。",
        BridgeProtocol.ErrorCodes.DatabaseReadOnly => "目标数据库为只读，无法导入。",
        BridgeProtocol.ErrorCodes.DatabaseChangedSincePreview => "目标数据库或已验证 EDZ 在预览后发生变化，请重新预览。",
        BridgeProtocol.ErrorCodes.BackupFailed => "无法创建并验证备份，导入尚未开始。",
        BridgeProtocol.ErrorCodes.PreviewFailed => "无法完成导入预览。",
        BridgeProtocol.ErrorCodes.ConflictRequiresDecision => "仍有冲突或无效项目需要处理。",
        BridgeProtocol.ErrorCodes.DatabaseImportFailed => "EPLAN 未能把已验证的 EDZ 导入所选数据库。",
        BridgeProtocol.ErrorCodes.PartialImport => "部分部件导入或验证失败。",
        BridgeProtocol.ErrorCodes.ImportVerificationFailed => "导入后的部件验证失败。",
        BridgeProtocol.ErrorCodes.DatabaseImportCancelled => "已在安全检查点取消数据库导入。",
        _ => fallback
    };

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
            DifferenceSummary = string.Join("；", item.Differences.Select(value => LocalizeDifferenceField(value.Field) + "：" + value.Existing + " → " + value.Incoming));
        }

        public ImportPreviewItemDto Item { get; }
        public string Manufacturer => Item.Manufacturer;
        public string PartNumber => Item.PartNumber;
        public string Variant => Item.Variant;
        public string StatusCode => Item.Status;
        public string Status => LocalizeStatus(Item.Status);
        public string DifferenceSummary { get; }
        public string Action { get; set; }
    }

    private sealed record ImportActionOption(string Value, string DisplayName);
}
