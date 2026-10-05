using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EplanEdzManager.AddIn.Protocol;

public sealed class DesktopEndpoint
{
    private DesktopEndpoint(string pipeName, string mutexName)
    {
        PipeName = pipeName;
        MutexName = mutexName;
    }

    public string PipeName { get; }
    public string MutexName { get; }

    public static DesktopEndpoint ForCurrentUserSession()
    {
        var sessionId = Process.GetCurrentProcess().SessionId;
        var identity = Environment.UserDomainName + "\\" + Environment.UserName;
        string identityHash;
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(identity));
            var builder = new StringBuilder(24);
            for (var index = 0; index < 12; index++) builder.Append(hash[index].ToString("x2"));
            identityHash = builder.ToString();
        }

        var suffix = identityHash + "." + sessionId;
        return new DesktopEndpoint(
            "EplanEdzManager.Desktop." + suffix,
            "Local\\EplanEdzManager.Desktop." + suffix);
    }
}

public static class LocalPipeClient
{
    public static async Task<AddInMessage> SendAsync(
        string pipeName,
        AddInMessage message,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName)) throw new ArgumentException("A pipe name is required.", nameof(pipeName));
        if (message == null) throw new ArgumentNullException(nameof(message));
        if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using (var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            operationTimeout.CancelAfter(timeout);
            var connected = false;
            using (operationTimeout.Token.Register(() =>
            {
                try { pipe.Dispose(); }
                catch (Exception) { }
            }))
            {
                try
                {
                    try
                    {
                        await Task.Run(() => pipe.Connect((int)timeout.TotalMilliseconds), operationTimeout.Token)
                            .ConfigureAwait(false);
                        connected = true;
                    }
                    catch (TimeoutException exception)
                    {
                        throw new TimeoutException("Could not connect to the local Desktop pipe within the configured timeout.", exception);
                    }

                    operationTimeout.Token.ThrowIfCancellationRequested();
                    using (var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true))
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                    {
                        await writer.WriteLineAsync(AddInJson.Serialize(message)).ConfigureAwait(false);
                        var response = await LocalPipeFraming.ReadLineAsync(
                            reader,
                            LocalPipeFraming.MaximumFrameCharacters,
                            operationTimeout.Token).ConfigureAwait(false);
                        operationTimeout.Token.ThrowIfCancellationRequested();
                        if (response == null || string.IsNullOrWhiteSpace(response))
                            throw new IOException("The local Desktop pipe closed without a response.");
                        return AddInJson.DeserializeKnown(response);
                    }
                }
                catch (Exception exception) when (operationTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    var timeoutMessage = connected
                        ? "The local Desktop pipe did not respond within the configured timeout."
                        : "Could not connect to the local Desktop pipe within the configured timeout.";
                    throw new TimeoutException(timeoutMessage, exception);
                }
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
            }
        }
    }
}

public static class LocalPipeFraming
{
    public const int MaximumFrameCharacters = 1024 * 1024;

    public static async Task<string?> ReadLineAsync(TextReader reader, int maximumCharacters, CancellationToken cancellationToken)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));

        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        var buffer = new char[1];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer, 0, 1).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0) return builder.Length == 0 ? null : builder.ToString();
            if (buffer[0] == '\n') return builder.ToString();
            if (buffer[0] == '\r') continue;
            if (builder.Length >= maximumCharacters)
                throw new InvalidDataException("The local pipe frame exceeded the 1 MiB safety limit.");
            builder.Append(buffer[0]);
        }
    }
}

public sealed class DesktopInstanceLease : IDisposable
{
    private static readonly object ProcessLeaseSync = new object();
    private static readonly HashSet<string> ProcessLeases = new HashSet<string>(StringComparer.Ordinal);
    private readonly string _name;
    private Mutex? _mutex;
    private bool _processLeaseHeld;

    private DesktopInstanceLease(string name, Mutex? mutex, bool isPrimary, bool processLeaseHeld)
    {
        _name = name;
        _mutex = mutex;
        IsPrimary = isPrimary;
        _processLeaseHeld = processLeaseHeld;
    }

    public bool IsPrimary { get; }

    public static DesktopInstanceLease TryAcquire(string mutexName)
    {
        if (string.IsNullOrWhiteSpace(mutexName)) throw new ArgumentException("A mutex name is required.", nameof(mutexName));

        lock (ProcessLeaseSync)
        {
            if (ProcessLeases.Contains(mutexName))
                return new DesktopInstanceLease(mutexName, null, false, false);
            ProcessLeases.Add(mutexName);
        }

        var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0, false); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                mutex.Dispose();
                lock (ProcessLeaseSync) ProcessLeases.Remove(mutexName);
                return new DesktopInstanceLease(mutexName, null, false, false);
            }
            return new DesktopInstanceLease(mutexName, mutex, true, true);
        }
        catch
        {
            mutex.Dispose();
            lock (ProcessLeaseSync) ProcessLeases.Remove(mutexName);
            throw;
        }
    }

    public void Dispose()
    {
        var mutex = Interlocked.Exchange(ref _mutex, null);
        if (mutex != null)
        {
            try { mutex.ReleaseMutex(); }
            finally { mutex.Dispose(); }
        }
        if (_processLeaseHeld)
        {
            lock (ProcessLeaseSync) ProcessLeases.Remove(_name);
            _processLeaseHeld = false;
        }
    }
}
