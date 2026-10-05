using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EplanEdzManager.Application;
using Microsoft.Win32;

namespace EplanEdzManager.Desktop;

public partial class FirstRunWizardWindow : Window
{
    private readonly ApplicationSettings _settings;
    private readonly SettingsStore _store = new();
    private EplanEnvironmentInfo? _environment;

    public FirstRunWizardWindow(ApplicationSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        PlatformPathBox.Text = settings.EplanPlatformBinDirectory;
        VariantPathBox.Text = settings.EplanVariantBinDirectory;
        EdzFolderBox.Text = settings.InitialEdzLibraryFolder ?? string.Empty;
        DatabasePathBox.Text = settings.DatabasePath;
        AddInPathBox.Text = InstallationLayout.FindAddInAssembly() ?? "Add-In DLL not found in this build layout.";
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => Detect();
    private void Detect_Click(object sender, RoutedEventArgs e) => Detect();

    private void Detect()
    {
        _settings.EplanPlatformBinDirectory = PlatformPathBox.Text.Trim();
        _settings.EplanVariantBinDirectory = VariantPathBox.Text.Trim();
        _environment = new EplanEnvironmentDetector().Detect(_settings);
        if (_environment.Detected)
        {
            PlatformPathBox.Text = _environment.PlatformBinDirectory;
            VariantPathBox.Text = _environment.VariantBinDirectory;
        }
        DetectionText.Text = $"Status: {_environment.CompatibilityText}\nSource: {_environment.DetectionSource}\nEPLAN Version: {Display(_environment.EplanVersion)}\nPlatform Bin: {Display(_environment.PlatformBinDirectory)}\nVariant Bin: {Display(_environment.VariantBinDirectory)}\nx64: {_environment.IsX64}\n\n{string.Join(Environment.NewLine, _environment.Messages)}";
        ApiText.Text = $"API Version: {Display(_environment.ApiVersion)}\nCompatibility: {_environment.CompatibilityText}\n\nEPLAN API DLL 仅从本机已安装 EPLAN 目录加载，不会复制进安装包。";
        UpdateSummary();
    }

    private void Back_Click(object sender, RoutedEventArgs e) { if (Steps.SelectedIndex > 0) Steps.SelectedIndex--; }
    private void Next_Click(object sender, RoutedEventArgs e) { if (Steps.SelectedIndex < Steps.Items.Count - 1) Steps.SelectedIndex++; }
    private void Steps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        BackButton.IsEnabled = Steps.SelectedIndex > 0;
        NextButton.Visibility = Steps.SelectedIndex == Steps.Items.Count - 1 ? Visibility.Collapsed : Visibility.Visible;
        FinishButton.Visibility = Steps.SelectedIndex == Steps.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
        StepText.Text = $"步骤 {Steps.SelectedIndex + 1} / {Steps.Items.Count}";
        if (Steps.SelectedIndex == Steps.Items.Count - 1) UpdateSummary();
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.DatabasePath = Path.GetFullPath(DatabasePathBox.Text.Trim());
            _settings.InitialEdzLibraryFolder = string.IsNullOrWhiteSpace(EdzFolderBox.Text) ? null : Path.GetFullPath(EdzFolderBox.Text.Trim());
            _settings.EplanPlatformBinDirectory = PlatformPathBox.Text.Trim();
            _settings.EplanVariantBinDirectory = VariantPathBox.Text.Trim();
            _environment = new EplanEnvironmentDetector().Detect(_settings);
            if (_environment.Compatibility == EplanCompatibility.InvalidConfiguration)
            {
                var answer = MessageBox.Show(this, "配置的 EPLAN 路径无效。是否清空这些路径并以 Offline Mode 继续？", "First Run Setup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { Steps.SelectedIndex = 1; return; }
                _settings.EplanPlatformBinDirectory = string.Empty;
                _settings.EplanVariantBinDirectory = string.Empty;
            }
            _settings.FirstRunCompleted = true;
            await _store.SaveAsync(_settings);
            if (!string.IsNullOrWhiteSpace(_settings.InitialEdzLibraryFolder) && Directory.Exists(_settings.InitialEdzLibraryFolder))
            {
                var application = new EdzManagerApplication(_settings.DatabasePath, runtimeSettings: _settings);
                await application.InitializeAsync();
                await application.AddLibraryAsync(_settings.InitialEdzLibraryFolder);
            }
            DialogResult = true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, "无法保存首次运行设置。\n\n" + exception.Message, "First Run Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BrowsePlatform_Click(object sender, RoutedEventArgs e) => BrowseFolder(PlatformPathBox);
    private void BrowseVariant_Click(object sender, RoutedEventArgs e) => BrowseFolder(VariantPathBox);
    private void BrowseEdz_Click(object sender, RoutedEventArgs e) => BrowseFolder(EdzFolderBox);
    private void BrowseFolder(TextBox target)
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹", InitialDirectory = Directory.Exists(target.Text) ? target.Text : null };
        if (dialog.ShowDialog(this) == true) target.Text = dialog.FolderName;
    }

    private void BrowseDatabase_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Title = "选择 SQLite 数据库位置", Filter = "SQLite database|*.db", FileName = "index.db", InitialDirectory = Path.GetDirectoryName(DatabasePathBox.Text) };
        if (dialog.ShowDialog(this) == true) DatabasePathBox.Text = dialog.FileName;
    }

    private void CopyAddInPath_Click(object sender, RoutedEventArgs e) { if (File.Exists(AddInPathBox.Text)) Clipboard.SetText(AddInPathBox.Text); }
    private void OpenAddInFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(AddInPathBox.Text);
        if (directory is not null && Directory.Exists(directory)) Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void UpdateSummary()
    {
        SummaryText.Text = $"Version: {ProductInfo.Version}\nEPLAN: {_environment?.CompatibilityText ?? "Not checked"}\nEDZ Library: {Display(EdzFolderBox.Text)}\nDatabase: {Display(DatabasePathBox.Text)}\nAdd-In: Not detected (interactive registration required)\nCloud/Telemetry: None";
    }

    private static string Display(string? value) => string.IsNullOrWhiteSpace(value) ? "Not configured" : value;
}
