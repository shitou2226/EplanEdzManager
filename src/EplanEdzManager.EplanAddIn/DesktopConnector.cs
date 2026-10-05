using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using EplanEdzManager.AddIn.Protocol;

namespace EplanEdzManager.EplanAddIn;

internal static class DesktopConnector
{
    private static readonly object Sync = new object();

    public static void OpenManager()
    {
        lock (Sync)
        {
            var stopwatch = Stopwatch.StartNew();
            var config = AddInRuntime.LoadConfiguration();
            var endpoint = DesktopEndpoint.ForCurrentUserSession();
            var hello = new HelloMessage
            {
                InstanceId = AddInRuntime.SessionId,
                EplanVersion = AddInRuntime.EplanVersion,
                EplanProcessId = Process.GetCurrentProcess().Id,
                AddInVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"
            };

            if (!TrySend(endpoint.PipeName, hello, TimeSpan.FromMilliseconds(250)))
            {
                StartDesktop(config.DesktopExecutablePath);
                WaitForDesktop(endpoint.PipeName, hello, TimeSpan.FromMilliseconds(config.ConnectTimeoutMilliseconds));
            }

            var context = EplanContextCollector.Collect();
            Send(endpoint.PipeName, context, TimeSpan.FromSeconds(2));
            Send(endpoint.PipeName, new OpenManagerMessage
            {
                InstanceId = AddInRuntime.SessionId,
                Reason = "EplanMenu"
            }, TimeSpan.FromSeconds(2));
            AddInLog.Information(AddInRuntime.SessionId, "OpenManagerCompleted", "elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
    }

    public static void NotifyDisconnect(string reason)
    {
        try
        {
            var endpoint = DesktopEndpoint.ForCurrentUserSession();
            TrySend(endpoint.PipeName, new DisconnectMessage
            {
                InstanceId = AddInRuntime.SessionId,
                Reason = reason
            }, TimeSpan.FromMilliseconds(250));
        }
        catch (Exception exception)
        {
            AddInLog.Error(AddInRuntime.SessionId, "DisconnectNotificationFailed", exception);
        }
    }

    private static void StartDesktop(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = false
        };
        Process.Start(startInfo);
        AddInLog.Information(AddInRuntime.SessionId, "DesktopStarted", Path.GetFileName(executablePath));
    }

    private static void WaitForDesktop(string pipeName, AddInMessage hello, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        Exception? lastError = null;
        while (stopwatch.Elapsed < timeout)
        {
            try
            {
                Send(pipeName, hello, TimeSpan.FromMilliseconds(250));
                return;
            }
            catch (Exception exception)
            {
                lastError = exception;
                Thread.Sleep(100);
            }
        }
        throw new TimeoutException("The Desktop did not open its local IPC endpoint within the configured startup timeout.", lastError);
    }

    private static bool TrySend(string pipeName, AddInMessage message, TimeSpan timeout)
    {
        try
        {
            Send(pipeName, message, timeout);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Send(string pipeName, AddInMessage message, TimeSpan timeout)
    {
        var response = LocalPipeClient.SendAsync(pipeName, message, timeout, CancellationToken.None).GetAwaiter().GetResult();
        if (response is ErrorMessage error)
            throw new InvalidOperationException(error.ErrorCode + ": " + error.UserMessage + " " + error.TechnicalDetails);
    }
}
