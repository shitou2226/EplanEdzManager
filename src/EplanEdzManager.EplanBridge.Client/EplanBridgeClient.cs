using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using EplanEdzManager.EplanBridge.Protocol;

namespace EplanEdzManager.EplanBridge.Client;

public sealed class EplanBridgeClient : IAsyncDisposable
{
    private readonly EplanBridgeClientOptions _options;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly StringBuilder _processDiagnostics = new();
    private Process? _process;
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private bool _disposed;
    private bool _operationActive;

    public EplanBridgeClient(EplanBridgeClientOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string PipeName { get; private set; } = string.Empty;
    public int? BridgeProcessId => _process is null ? null : _process.Id;
    public bool IsConnected => _pipe?.IsConnected == true && _process?.HasExited == false;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (IsConnected) return;
        _options.Validate();
        PipeName = EplanBridgeClientOptions.CreatePipeName();
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(_options.BridgeExecutablePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.BridgeExecutablePath))!
        };
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(PipeName);
        startInfo.ArgumentList.Add("--session-id");
        startInfo.ArgumentList.Add(SessionId);
        startInfo.ArgumentList.Add("--variant-bin");
        startInfo.ArgumentList.Add(Path.GetFullPath(_options.EplanVariantBinDirectory));
        startInfo.ArgumentList.Add("--platform-bin");
        startInfo.ArgumentList.Add(Path.GetFullPath(_options.EplanPlatformBinDirectory));
        if (!string.IsNullOrWhiteSpace(_options.TargetMasterDataRoot))
        {
            startInfo.ArgumentList.Add("--target-master-data-root");
            startInfo.ArgumentList.Add(Path.GetFullPath(_options.TargetMasterDataRoot));
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += CaptureProcessOutput;
        _process.ErrorDataReceived += CaptureProcessOutput;
        if (!_process.Start()) throw new EplanBridgeException("BRIDGE-START", "Unable to start EPLAN Bridge.");
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        _pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        connect.CancelAfter(_options.ConnectionTimeout);
        try
        {
            var pipeConnection = _pipe.ConnectAsync(connect.Token);
            var processExit = _process.WaitForExitAsync(connect.Token);
            var completed = await Task.WhenAny(pipeConnection, processExit).ConfigureAwait(false);
            if (completed == processExit && !_pipe.IsConnected)
            {
                await processExit.ConfigureAwait(false);
                connect.Cancel();
                try { await pipeConnection.ConfigureAwait(false); } catch (OperationCanceledException) { }
                throw CreateExitedException(new InvalidOperationException("The EPLAN Bridge exited before opening its named pipe."));
            }
            await pipeConnection.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out connecting to the EPLAN Bridge named pipe.", exception);
        }
        catch (EplanBridgeProcessExitedException)
        {
            throw;
        }
        catch (Exception exception) when (_process.HasExited)
        {
            throw CreateExitedException(exception);
        }

        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 64 * 1024, leaveOpen: true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    public async Task<PingResult> PingAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var request = new PingRequest { RequestId = NewRequestId() };
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadExpectedAsync<PingResult>(request.RequestId, _options.CapabilityTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CapabilitiesResult> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        var request = new GetCapabilitiesRequest { RequestId = NewRequestId() };
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadExpectedAsync<CapabilitiesResult>(request.RequestId, _options.CapabilityTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExportResult> ExportPartsAsync(
        string outputPath,
        IReadOnlyCollection<ExportPartDto> parts,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_operationActive) throw new InvalidOperationException("Only one bridge export can run at a time.");
        ValidateExport(outputPath, parts);
        var request = new ExportPartsRequest
        {
            RequestId = NewRequestId(),
            OutputPath = Path.GetFullPath(outputPath),
            Parts = parts.Select(ClonePart).ToList()
        };
        _operationActive = true;
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => _ = TrySendCancelAsync(request.RequestId));
            while (true)
            {
                var message = await ReadMessageAsync(_options.OperationIdleTimeout, CancellationToken.None).ConfigureAwait(false);
                EnsureMatchingRequest(request.RequestId, message);
                if (message is ExportProgress update)
                {
                    progress?.Report(update);
                    continue;
                }
                if (message is CancelAcknowledged) continue;
                if (message is ExportResult result) return result;
                if (message is ErrorResponse error) throw ToException(error);
                throw new EplanBridgeException(BridgeProtocol.ErrorCodes.InvalidRequest, "Unexpected bridge response: " + message.MessageType);
            }
        }
        finally
        {
            _operationActive = false;
        }
    }

    public async Task<InspectPartsDatabaseResult> InspectPartsDatabaseAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A target database path is required.", nameof(databasePath));
        var request = new InspectPartsDatabaseRequest
        {
            RequestId = NewRequestId(),
            DatabasePath = Path.GetFullPath(databasePath)
        };
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadExpectedAsync<InspectPartsDatabaseResult>(request.RequestId, _options.CapabilityTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PreviewPartsImportResult> PreviewPartsImportAsync(
        string targetDatabase,
        string validatedEdzPath,
        IReadOnlyCollection<ExportPartDto> parts,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_operationActive) throw new InvalidOperationException("Only one Bridge operation can run at a time.");
        ValidateImportInputs(targetDatabase, validatedEdzPath, parts);
        var request = new PreviewPartsImportRequest
        {
            RequestId = NewRequestId(),
            TargetDatabase = Path.GetFullPath(targetDatabase),
            ValidatedEdzPath = Path.GetFullPath(validatedEdzPath),
            Parts = parts.Select(ClonePart).ToList()
        };
        _operationActive = true;
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => _ = TrySendCancelAsync(request.RequestId));
            while (true)
            {
                var message = await ReadMessageAsync(_options.OperationIdleTimeout, CancellationToken.None).ConfigureAwait(false);
                EnsureMatchingRequest(request.RequestId, message);
                if (message is CancelAcknowledged) continue;
                if (message is PreviewPartsImportResult result) return result;
                if (message is ErrorResponse error) throw ToException(error);
                throw new EplanBridgeException(BridgeProtocol.ErrorCodes.InvalidRequest, "Unexpected Bridge response: " + message.MessageType);
            }
        }
        finally { _operationActive = false; }
    }

    public async Task<ImportPartsResult> ImportPartsAsync(
        PreviewPartsImportResult preview,
        IReadOnlyCollection<ImportDecisionDto> decisions,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        if (_operationActive) throw new InvalidOperationException("Only one Bridge operation can run at a time.");
        if (preview is null || !preview.Success || string.IsNullOrWhiteSpace(preview.PreviewId) || string.IsNullOrWhiteSpace(preview.ConfirmationToken))
            throw new ArgumentException("A successful, unexpired Preview is required.", nameof(preview));
        if (decisions is null || decisions.Count != preview.Items.Count)
            throw new ArgumentException("Every Preview item requires one explicit decision.", nameof(decisions));
        var request = new ImportPartsRequest
        {
            RequestId = NewRequestId(),
            PreviewId = preview.PreviewId,
            ConfirmationToken = preview.ConfirmationToken,
            TargetDatabase = preview.TargetDatabase,
            DatabaseFingerprint = preview.DatabaseFingerprint,
            Decisions = decisions.Select(item => new ImportDecisionDto { StableIdentity = item.StableIdentity, Action = item.Action }).ToList()
        };
        _operationActive = true;
        try
        {
            await WriteAsync(request, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => _ = TrySendCancelAsync(request.RequestId));
            while (true)
            {
                var message = await ReadMessageAsync(_options.OperationIdleTimeout, CancellationToken.None).ConfigureAwait(false);
                EnsureMatchingRequest(request.RequestId, message);
                if (message is ImportProgress update) { progress?.Report(update); continue; }
                if (message is CancelAcknowledged) continue;
                if (message is ImportPartsResult result) return result;
                if (message is ErrorResponse error) throw ToException(error);
                throw new EplanBridgeException(BridgeProtocol.ErrorCodes.InvalidRequest, "Unexpected Bridge response: " + message.MessageType);
            }
        }
        finally { _operationActive = false; }
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected || _writer is null) return;
        var request = new ShutdownRequest { RequestId = NewRequestId() };
        await WriteAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            await ReadExpectedAsync<ShutdownAcknowledged>(request.RequestId, _options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            // A clean operation-scoped bridge may close immediately after acknowledging shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
            await ShutdownAsync(timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            // Disposal still closes the private pipe and the child process below.
        }
        _lifetime.Cancel();
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        if (_process is not null)
        {
            if (!_process.HasExited)
            {
                try
                {
                    if (!_process.WaitForExit((int)Math.Min(int.MaxValue, _options.ShutdownTimeout.TotalMilliseconds)))
                        _process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException) { }
            }
            _process.Dispose();
        }
        _writeGate.Dispose();
        _lifetime.Dispose();
    }

    private async Task<T> ReadExpectedAsync<T>(string requestId, TimeSpan timeout, CancellationToken cancellationToken) where T : BridgeMessage
    {
        var message = await ReadMessageAsync(timeout, cancellationToken).ConfigureAwait(false);
        EnsureMatchingRequest(requestId, message);
        if (message is ErrorResponse error) throw ToException(error);
        if (message is T expected) return expected;
        throw new EplanBridgeException(BridgeProtocol.ErrorCodes.InvalidRequest, $"Expected {typeof(T).Name}, received {message.MessageType}.");
    }

    private async Task<BridgeMessage> ReadMessageAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_reader is null) throw new InvalidOperationException("Bridge client is not connected.");
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        readTimeout.CancelAfter(timeout);
        string? line;
        try
        {
            line = await _reader.ReadLineAsync(readTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("The EPLAN Bridge produced no protocol messages before the idle timeout.", exception);
        }
        catch (IOException exception) when (_process?.HasExited == true)
        {
            throw CreateExitedException(exception);
        }
        if (line is null)
        {
            if (_process?.HasExited == true) throw CreateExitedException();
            throw new EndOfStreamException("The EPLAN Bridge closed the named pipe.");
        }
        var message = BridgeJson.DeserializeKnown(line);
        if (!string.Equals(message.ProtocolVersion, BridgeProtocol.Version, StringComparison.Ordinal))
            throw new EplanBridgeException(BridgeProtocol.ErrorCodes.VersionMismatch, $"Protocol mismatch. Client={BridgeProtocol.Version}, Bridge={message.ProtocolVersion}.");
        return message;
    }

    private async Task WriteAsync(BridgeMessage message, CancellationToken cancellationToken)
    {
        if (_writer is null) throw new InvalidOperationException("Bridge client is not connected.");
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(BridgeJson.Serialize(message).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        catch (IOException exception) when (_process?.HasExited == true)
        {
            throw CreateExitedException(exception);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task TrySendCancelAsync(string targetRequestId)
    {
        try
        {
            await WriteAsync(new CancelOperationRequest { RequestId = NewRequestId(), TargetRequestId = targetRequestId }, _lifetime.Token).ConfigureAwait(false);
        }
        catch
        {
            // The primary operation read reports pipe/process failure with better context.
        }
    }

    private void CaptureProcessOutput(object sender, DataReceivedEventArgs args)
    {
        if (string.IsNullOrWhiteSpace(args.Data)) return;
        lock (_processDiagnostics)
        {
            if (_processDiagnostics.Length < 32 * 1024) _processDiagnostics.AppendLine(args.Data);
        }
    }

    private EplanBridgeProcessExitedException CreateExitedException(Exception? inner = null)
    {
        int? exitCode = null;
        try { if (_process?.HasExited == true) exitCode = _process.ExitCode; } catch (InvalidOperationException) { }
        string diagnostics;
        lock (_processDiagnostics) diagnostics = _processDiagnostics.ToString();
        if (inner is not null) diagnostics += inner + Environment.NewLine;
        return new EplanBridgeProcessExitedException(exitCode, diagnostics);
    }

    private static EplanBridgeException ToException(ErrorResponse error) =>
        new(error.ErrorCode, error.UserMessage, error.TechnicalDetails, error.LogPath);

    private static ExportPartDto ClonePart(ExportPartDto part) => new()
    {
        StablePartIdentity = part.StablePartIdentity,
        PreferredSourceEdz = Path.GetFullPath(part.PreferredSourceEdz),
        Manufacturer = part.Manufacturer,
        PackageKey = part.PackageKey,
        PartNumber = part.PartNumber,
        Variant = part.Variant,
        SourceFileSize = part.SourceFileSize,
        SourceLastWriteTimeUtcTicks = part.SourceLastWriteTimeUtcTicks
    };

    private static void ValidateExport(string outputPath, IReadOnlyCollection<ExportPartDto> parts)
    {
        if (parts is null || parts.Count == 0) throw new ArgumentException("At least one part is required.", nameof(parts));
        if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path is required.", nameof(outputPath));
        if (!string.Equals(Path.GetExtension(outputPath), ".edz", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output path must use the .edz extension.", nameof(outputPath));
        if (File.Exists(outputPath)) throw new IOException("Refusing to overwrite an existing output file: " + outputPath);
        foreach (var part in parts)
        {
            if (part is null || string.IsNullOrWhiteSpace(part.StablePartIdentity) || string.IsNullOrWhiteSpace(part.PartNumber))
                throw new ArgumentException("Each selected part requires StablePartIdentity and PartNumber.", nameof(parts));
            if (string.IsNullOrWhiteSpace(part.PreferredSourceEdz) || !File.Exists(part.PreferredSourceEdz))
                throw new FileNotFoundException("A preferred source EDZ is missing.", part.PreferredSourceEdz);
        }
    }

    private static void ValidateImportInputs(string targetDatabase, string validatedEdzPath, IReadOnlyCollection<ExportPartDto> parts)
    {
        if (string.IsNullOrWhiteSpace(targetDatabase) || !File.Exists(targetDatabase))
            throw new FileNotFoundException("The explicitly selected target MDB does not exist.", targetDatabase);
        if (!string.Equals(Path.GetExtension(targetDatabase), ".mdb", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Phase 5 supports only EPLAN 2.9 Access MDB parts databases.", nameof(targetDatabase));
        if (string.IsNullOrWhiteSpace(validatedEdzPath) || !File.Exists(validatedEdzPath))
            throw new FileNotFoundException("The validated SelectedParts.edz does not exist.", validatedEdzPath);
        if (parts is null || parts.Count == 0) throw new ArgumentException("At least one part is required.", nameof(parts));
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.StablePartIdentity) || string.IsNullOrWhiteSpace(part.PartNumber))
                throw new ArgumentException("Each import part requires StablePartIdentity and PartNumber.", nameof(parts));
        }
    }

    private static void EnsureMatchingRequest(string requestId, BridgeMessage message)
    {
        if (!string.Equals(requestId, message.RequestId, StringComparison.Ordinal))
            throw new EplanBridgeException(BridgeProtocol.ErrorCodes.InvalidRequest, $"Bridge response requestId mismatch. Expected {requestId}, received {message.RequestId}.");
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private void EnsureConnected()
    {
        ThrowIfDisposed();
        if (!IsConnected)
        {
            if (_process?.HasExited == true) throw CreateExitedException();
            throw new InvalidOperationException("Call StartAsync before using the bridge client.");
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
