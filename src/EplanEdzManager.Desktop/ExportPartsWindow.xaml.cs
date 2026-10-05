using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.ViewModels;
using EplanEdzManager.EplanBridge.Protocol;

namespace EplanEdzManager.Desktop;

public partial class ExportPartsWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SelectedPartsExportPreparation _preparation;
    private readonly string _outputPath;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _running = true;
    private ExportResult? _result;

    public ExportPartsWindow(MainViewModel viewModel, SelectedPartsExportPreparation preparation, string outputPath)
    {
        _viewModel = viewModel;
        _preparation = preparation;
        _outputPath = outputPath;
        InitializeComponent();
        SelectedCountText.Text = preparation.Parts.Count.ToString("N0");
        SourceCountText.Text = preparation.SourceEdzCount.ToString("N0");
        OutputPathText.Text = outputPath;
        Loaded += OnLoaded;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            _cancellation.Cancel();
            StageText.Text = "Cancelling…";
            e.Cancel = true;
        }
        base.OnClosing(e);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        var progress = new Progress<ExportProgress>(ApplyProgress);
        try
        {
            _result = await _viewModel.ExportSelectedPartsAsync(_preparation, _outputPath, progress, _cancellation.Token);
            if (_result.Success) ShowSuccess(_result);
            else ShowFailure(_result.ErrorCode, _result.UserMessage, _result.TechnicalDetails, _result.LogPath);
        }
        catch (Exception exception)
        {
            ShowFailure("BRIDGE-CLIENT", "The EPLAN Bridge operation did not complete.", exception.ToString(), string.Empty);
        }
        finally
        {
            _running = false;
            CancelButton.Visibility = Visibility.Collapsed;
            CloseButton.Visibility = Visibility.Visible;
        }
    }

    private void ApplyProgress(ExportProgress progress)
    {
        StageText.Text = progress.Stage;
        ProgressBar.IsIndeterminate = progress.Total <= 0;
        if (progress.Total > 0)
        {
            ProgressBar.Maximum = progress.Total;
            ProgressBar.Value = Math.Min(progress.Current, progress.Total);
        }
        var source = string.IsNullOrWhiteSpace(progress.CurrentSource) ? string.Empty : Environment.NewLine + "Source: " + Path.GetFileName(progress.CurrentSource);
        ProgressDetailText.Text = progress.Message + source;
        ElapsedText.Text = "Elapsed: " + TimeSpan.FromMilliseconds(progress.ElapsedMilliseconds).ToString(@"hh\:mm\:ss");
    }

    private void ShowSuccess(ExportResult result)
    {
        StageText.Text = "Completed";
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Maximum = 1;
        ProgressBar.Value = 1;
        ResultBorder.Visibility = Visibility.Visible;
        ResultText.Text = $"Export completed{Environment.NewLine}{Environment.NewLine}" +
            $"Requested: {result.RequestedPartCount:N0}{Environment.NewLine}" +
            $"Exported: {result.ExportedRequestedPartCount:N0}{Environment.NewLine}" +
            $"Referenced: {result.ReferencedPartCount:N0}{Environment.NewLine}" +
            $"Sources: {result.SourceEdzCount:N0}{Environment.NewLine}" +
            $"Size: {FormatBytes(result.OutputSize)}{Environment.NewLine}" +
            $"Validation: {result.ValidationSummary.Verdict}";
        DiagnosticsText.Text = BuildDiagnostics(result);
        OpenFolderButton.Visibility = Visibility.Visible;
        DiagnosticsButton.Visibility = Visibility.Visible;
    }

    private void ShowFailure(string code, string message, string details, string logPath)
    {
        StageText.Text = code == BridgeProtocol.ErrorCodes.Cancelled ? "Cancelled" : "Failed";
        ProgressBar.IsIndeterminate = false;
        ResultBorder.Visibility = Visibility.Visible;
        ResultText.Text = code + Environment.NewLine + message;
        DiagnosticsText.Text = details + (string.IsNullOrWhiteSpace(logPath) ? string.Empty : Environment.NewLine + "Log: " + logPath);
        DiagnosticsButton.Visibility = Visibility.Visible;
    }

    private static string BuildDiagnostics(ExportResult result) =>
        string.Join(Environment.NewLine, result.Warnings.Select(value => "Warning: " + value)
            .Concat(result.Diagnostics.Select(value => value.Code + " " + value.Severity + ": " + value.Message + " " + value.Detail))
            .Append("Log: " + result.LogPath));

    private static string FormatBytes(long value) => value < 1024 * 1024
        ? (value / 1024d).ToString("0.0") + " KiB"
        : (value / 1024d / 1024d).ToString("0.0") + " MiB";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellation.Cancel();
        StageText.Text = "Cancelling…";
        CancelButton.IsEnabled = false;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(_outputPath);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    private void Diagnostics_Click(object sender, RoutedEventArgs e) =>
        DiagnosticsText.Visibility = DiagnosticsText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
