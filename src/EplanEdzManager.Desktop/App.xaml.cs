using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using EplanEdzManager.AddIn.Protocol;
using EplanEdzManager.Application;
using EplanEdzManager.Desktop.Integration;

namespace EplanEdzManager.Desktop;

public partial class App : System.Windows.Application
{
    private DesktopInstanceLease? _instanceLease;
    private DesktopIpcServer? _ipcServer;
    private MainWindow? _mainWindow;
    private readonly DesktopContextRegistry _contexts = new();
    private int _fatalErrorShown;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (TryGetSmokeResultPath(e.Args, out var smokeResultPath))
        {
            await RunSmokeTestAsync(smokeResultPath!);
            return;
        }
        if (!RunStartupSelfCheck()) return;
        DesktopIntegrationLog.Write("OnStartup");
        var endpoint = DesktopEndpoint.ForCurrentUserSession();
        _instanceLease = DesktopInstanceLease.TryAcquire(endpoint.MutexName);
        if (!_instanceLease.IsPrimary)
        {
            try
            {
                await LocalPipeClient.SendAsync(endpoint.PipeName, new OpenManagerMessage
                {
                    InstanceId = "desktop-secondary-" + Process.GetCurrentProcess().Id,
                    Reason = "SecondDesktopInstance"
                }, TimeSpan.FromSeconds(2), CancellationToken.None);
            }
            catch (Exception exception)
            {
                MessageBox.Show(
                    "EPLAN EDZ Manager is already running, but its local activation channel did not respond.\n\n" + exception.Message,
                    "EPLAN EDZ Manager",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        var settingsStore = new SettingsStore();
        var settings = await settingsStore.LoadAsync();
        if (!settings.FirstRunCompleted)
        {
            var wizard = new FirstRunWizardWindow(settings);
            if (wizard.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _ipcServer = new DesktopIpcServer(endpoint.PipeName, HandleIntegrationMessageAsync);
        _ipcServer.Start();
        _mainWindow.Show();
        DesktopIntegrationLog.Write("PrimaryWindowShown");
    }

    private static bool TryGetSmokeResultPath(IReadOnlyList<string> args, out string? resultPath)
    {
        resultPath = null;
        for (var index = 0; index < args.Count; index++)
        {
            if (!string.Equals(args[index], "--smoke-test", StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 >= args.Count) return true;
            resultPath = Path.GetFullPath(args[index + 1]);
            return true;
        }
        return false;
    }

    private async Task RunSmokeTestAsync(string resultPath)
    {
        var exitCode = 1;
        try
        {
            if (string.IsNullOrWhiteSpace(resultPath)) throw new ArgumentException("--smoke-test requires a result path.");
            var resultDirectory = Path.GetDirectoryName(resultPath)!;
            Directory.CreateDirectory(resultDirectory);
            var settings = new ApplicationSettings
            {
                DatabasePath = Path.Combine(resultDirectory, "smoke-index.db"),
                FirstRunCompleted = true
            };
            var application = new EdzManagerApplication(settings.DatabasePath, runtimeSettings: settings);
            await application.InitializeAsync();
            var database = await application.GetDatabaseMaintenanceInfoAsync();
            var environment = new EplanEnvironmentDetector().Detect(settings, includeMachineDiscovery: false);
            var prerequisites = application.CheckPrerequisites(settings);
            var payload = new
            {
                passed = database.SchemaVersion >= 2
                    && environment.Compatibility == EplanCompatibility.Offline
                    && prerequisites.DesktopStartupReady,
                version = ProductInfo.Version,
                database.SchemaVersion,
                noEplanMode = environment.Compatibility.ToString(),
                prerequisites.InstallRoot,
                prerequisites.InstallRootReadable,
                prerequisites.WindowsX64,
                prerequisites.DesktopRuntimeReady,
                prerequisites.LocalAppDataWritable,
                prerequisites.TempWritable,
                timestampUtc = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(payload));
            exitCode = payload.passed ? 0 : 2;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(resultPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
                    await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(new { passed = false, error = exception.GetType().Name + ": " + exception.Message }));
                }
                catch (Exception) { }
            }
        }
        Shutdown(exitCode);
    }

    private bool RunStartupSelfCheck()
    {
        try
        {
            var settings = new ApplicationSettings();
            var application = new EdzManagerApplication(settings.DatabasePath, runtimeSettings: settings);
            var status = application.CheckPrerequisites(settings);
            if (status.DesktopStartupReady) return true;
            MessageBox.Show(
                "EPLAN EDZ Manager 无法安全启动。\n\n" + string.Join(Environment.NewLine, status.Messages)
                + "\n\nInstall Root: " + status.InstallRoot,
                "Startup environment check failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
            return false;
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "EPLAN EDZ Manager 无法检查当前用户环境。\n\n" + exception.GetType().Name + ": " + exception.Message,
                "Startup environment check failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(3);
            return false;
        }
    }

    private async Task<DesktopStatusMessage> HandleIntegrationMessageAsync(AddInMessage message)
    {
        DesktopIntegrationLog.Write("IntegrationMessageReceived", DesktopMessageLogFormatter.Format(message));
        var state = _contexts.Apply(message);
        var displayState = _contexts.Current ?? state;
        await Dispatcher.InvokeAsync(() =>
        {
            _mainWindow?.ApplyEplanConnection(displayState, _contexts.Connections.Values.ToArray());
            if (message is OpenManagerMessage) _mainWindow?.ActivateFromIntegration();
        });
        return new DesktopStatusMessage
        {
            InstanceId = "desktop-primary",
            Connected = true,
            Status = "Ready",
            DesktopVersion = typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown"
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DesktopIntegrationLog.Write("OnExitStarted");
        if (_ipcServer is not null)
        {
            try { _ipcServer.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception) { }
        }
        DesktopIntegrationLog.Write("IpcServerDisposed");
        _instanceLease?.Dispose();
        DesktopIntegrationLog.Write("InstanceLeaseDisposed");
        base.OnExit(e);
        DesktopIntegrationLog.Write("OnExitCompleted");
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        HandleFatalException("DispatcherUnhandledException", e.Exception, requestShutdown: true);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        HandleFatalException("TaskScheduler.UnobservedTaskException", e.Exception, requestShutdown: false);
    }

    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception ?? new InvalidOperationException("Unknown unhandled AppDomain exception.");
        HandleFatalException("AppDomain.UnhandledException", exception, requestShutdown: false);
    }

    private void HandleFatalException(string boundary, Exception exception, bool requestShutdown)
    {
        string reportPath;
        try
        {
            OperationContext.Set(boundary);
            reportPath = CrashReporter.Write("Desktop", exception);
            DesktopIntegrationLog.Write("CRASH-001", boundary + "; report=" + reportPath);
        }
        catch (Exception reportingException)
        {
            reportPath = ApplicationPaths.CrashReportDirectory + " (report write failed: " + reportingException.GetType().Name + ")";
        }

        if (Interlocked.Exchange(ref _fatalErrorShown, 1) == 0)
        {
            try
            {
                Dispatcher.Invoke(() => MessageBox.Show(
                    "EPLAN EDZ Manager encountered a serious error and recorded a crash report.\n\n" + reportPath,
                    "EPLAN EDZ Manager Beta",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error));
            }
            catch (Exception) { }
        }
        if (requestShutdown) Shutdown(-1);
    }
}
