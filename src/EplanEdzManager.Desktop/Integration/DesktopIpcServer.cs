using System.IO;
using System.IO.Pipes;
using System.Text;
using EplanEdzManager.AddIn.Protocol;

namespace EplanEdzManager.Desktop.Integration;

public sealed class DesktopIpcServer : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly Func<AddInMessage, Task<DesktopStatusMessage>> _handler;
    private readonly TimeSpan _clientReadTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _pipeSync = new();
    private Task? _listenTask;
    private NamedPipeServerStream? _activePipe;

    public DesktopIpcServer(
        string pipeName,
        Func<AddInMessage, Task<DesktopStatusMessage>> handler,
        TimeSpan? clientReadTimeout = null)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName) ? throw new ArgumentException("A pipe name is required.", nameof(pipeName)) : pipeName;
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _clientReadTimeout = clientReadTimeout ?? TimeSpan.FromSeconds(5);
        if (_clientReadTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(clientReadTimeout));
    }

    public void Start()
    {
        if (_listenTask is not null) throw new InvalidOperationException("The Desktop IPC server is already running.");
        _listenTask = ListenAsync(_stop.Token);
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await AcceptOneAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                if (cancellationToken.IsCancellationRequested) return;
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task AcceptOneAsync(CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            4,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        lock (_pipeSync) _activePipe = pipe;
        try
        {
            await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };

            AddInMessage response;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(_clientReadTimeout);
                using var cancellationRegistration = readTimeout.Token.Register(() =>
                {
                    try { pipe.Dispose(); }
                    catch (Exception) { }
                });
                var line = await LocalPipeFraming.ReadLineAsync(
                    reader,
                    LocalPipeFraming.MaximumFrameCharacters,
                    readTimeout.Token).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    throw new InvalidDataException("The Add-In IPC request is empty.");
                var request = AddInJson.DeserializeKnown(line);
                response = await _handler(request).ConfigureAwait(false);
            }
            catch (AddInProtocolException exception)
            {
                response = CreateError(exception.ErrorCode, exception.Message);
            }
            catch (Exception exception)
            {
                response = CreateError(AddInProtocol.ErrorCodes.InvalidMessage, exception.Message);
            }

            await writer.WriteLineAsync(AddInJson.Serialize(response)).ConfigureAwait(false);
        }
        finally
        {
            lock (_pipeSync)
            {
                if (ReferenceEquals(_activePipe, pipe)) _activePipe = null;
            }
            await pipe.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ErrorMessage CreateError(string code, string message) => new()
    {
        InstanceId = "desktop-primary",
        ErrorCode = code,
        UserMessage = "The Desktop could not process the EPLAN Add-In message.",
        TechnicalDetails = message
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        lock (_pipeSync) _activePipe?.Dispose();
        if (_listenTask is not null)
        {
            try { await _listenTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
    }
}
