using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EplanEdzManager.Core.Diagnostics;
using EplanEdzManager.Edz.Documents;
using EplanEdzManager.EplanApi;
using EplanEdzManager.EplanBridge.Protocol;
using Newtonsoft.Json;

namespace EplanEdzManager.EplanBridge;

internal sealed class BridgeServer : IDisposable
{
    private readonly BridgeBootstrapOptions _options;
    private readonly string _temporaryRoot;
    private readonly string _temporaryVariantBin;
    private readonly BridgeLogger _logger;
    private readonly BlockingCollection<BridgeMessage> _commands = new BlockingCollection<BridgeMessage>();
    private readonly object _writeGate = new object();
    private readonly object _operationGate = new object();
    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private EplanOfficialSelectedPartsExporter? _exporter;
    private SafePartsDatabaseImporter? _importer;
    private readonly Dictionary<string, StoredImportPreview> _importPreviews = new Dictionary<string, StoredImportPreview>(StringComparer.Ordinal);
    private CancellationTokenSource? _activeOperation;
    private string _activeRequestId = string.Empty;
    private bool _disposed;

    public BridgeServer(BridgeBootstrapOptions options, string temporaryRoot, string temporaryVariantBin, BridgeLogger logger)
    {
        _options = options;
        _temporaryRoot = temporaryRoot;
        _temporaryVariantBin = temporaryVariantBin;
        _logger = logger;
    }

    public int Run()
    {
        _pipe = CreateCurrentUserPipe(_options.PipeName);
        _logger.Write(_options.SessionId, string.Empty, "WaitingForPipeClient");
        _pipe.WaitForConnection();
        VerifyConnectedIdentity(_pipe);
        _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 64 * 1024, true);
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 64 * 1024, true) { AutoFlush = true, NewLine = "\n" };
        var readerTask = Task.Run(ReadLoop);
        var cleanShutdown = false;
        try
        {
            foreach (var command in _commands.GetConsumingEnumerable())
            {
                if (command is ShutdownRequest)
                {
                    Write(new ShutdownAcknowledged { RequestId = command.RequestId });
                    cleanShutdown = true;
                    break;
                }
                Dispatch(command);
            }
        }
        finally
        {
            _pipe.Disconnect();
            try { readerTask.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
        }
        return cleanShutdown ? 0 : 1;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_operationGate)
        {
            _activeOperation?.Cancel();
            _activeOperation?.Dispose();
        }
        _exporter?.Dispose();
        _importer?.Dispose();
        _writer?.Dispose();
        _reader?.Dispose();
        _pipe?.Dispose();
        _commands.Dispose();
    }

    private void Dispatch(BridgeMessage command)
    {
        try
        {
            switch (command)
            {
                case PingRequest ping:
                    Write(new PingResult { RequestId = ping.RequestId, BridgeVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty, SessionId = _options.SessionId });
                    break;
                case GetCapabilitiesRequest capabilities:
                    HandleCapabilities(capabilities);
                    break;
                case ExportPartsRequest export:
                    HandleExport(export);
                    break;
                case InspectPartsDatabaseRequest inspect:
                    HandleInspectDatabase(inspect);
                    break;
                case PreviewPartsImportRequest preview:
                    HandleImportPreview(preview);
                    break;
                case ImportPartsRequest import:
                    HandleImport(import);
                    break;
                default:
                    WriteError(command.RequestId, BridgeProtocol.ErrorCodes.InvalidRequest, "Unsupported Bridge command.", command.MessageType);
                    break;
            }
        }
        catch (OfficialExportException exception)
        {
            _logger.Write(_options.SessionId, command.RequestId, exception.ErrorCode, exception.TechnicalDetails);
            WriteError(command.RequestId, exception.ErrorCode, exception.UserMessage, exception.TechnicalDetails);
        }
        catch (OfficialImportException exception)
        {
            _logger.Write(_options.SessionId, command.RequestId, exception.ErrorCode, exception.TechnicalDetails);
            WriteError(command.RequestId, exception.ErrorCode, exception.UserMessage, exception.TechnicalDetails);
        }
        catch (Exception exception)
        {
            _logger.Write(_options.SessionId, command.RequestId, "UnhandledCommandError", exception.GetType().Name + ": " + exception.Message);
            WriteError(command.RequestId, Classify(exception), "The EPLAN Bridge could not complete the command.", exception.ToString());
        }
    }

    private void HandleInspectDatabase(InspectPartsDatabaseRequest request)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        _logger.Write(_options.SessionId, request.RequestId, "InspectingDatabase", request.DatabasePath);
        var inspection = Importer.InspectPartsDatabase(request.DatabasePath);
        Write(new InspectPartsDatabaseResult
        {
            RequestId = request.RequestId,
            Success = inspection.Success,
            DatabasePath = inspection.DatabasePath,
            DatabaseType = inspection.DatabaseType,
            Exists = inspection.Exists,
            OpenSuccess = inspection.OpenSuccess,
            Readable = inspection.Readable,
            Writable = inspection.Writable,
            EplanCompatible = inspection.EplanCompatible,
            IsSchemeUpToDate = inspection.IsSchemeUpToDate,
            PartCount = inspection.PartCount,
            LastModifiedUtcTicks = inspection.LastModifiedUtcTicks,
            CanImport = inspection.CanImport,
            DetectedVersion = inspection.DetectedVersion,
            DatabaseFingerprint = Map(inspection.Fingerprint),
            ResourceMappings = inspection.ResourceMappings.Select(Map).ToList(),
            Diagnostics = inspection.Diagnostics,
            ErrorCode = inspection.ErrorCode,
            UserMessage = inspection.UserMessage,
            TechnicalDetails = inspection.TechnicalDetails,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
        });
    }

    private void HandleImportPreview(PreviewPartsImportRequest request)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var cancellation = BeginOperation(request.RequestId);
        try
        {
            _logger.Write(_options.SessionId, request.RequestId, "CheckingConflicts", "Parts=" + request.Parts.Count.ToString());
            var preview = Importer.PreviewPartsImport(
                request.TargetDatabase,
                request.ValidatedEdzPath,
                request.Parts.Select(part => new OfficialImportPart
                {
                    StableIdentity = part.StablePartIdentity,
                    Manufacturer = part.Manufacturer,
                    PartNumber = part.PartNumber,
                    Variant = part.Variant
                }).ToArray(),
                ReadValidatedIncomingMetadata(request.ValidatedEdzPath),
                () => cancellation.IsCancellationRequested);
            var previewId = Guid.NewGuid().ToString("N");
            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            _importPreviews.Clear();
            _importPreviews.Add(previewId, new StoredImportPreview(token, preview));
            var items = preview.Items.Select(Map).ToList();
            Write(new PreviewPartsImportResult
            {
                RequestId = request.RequestId,
                Success = true,
                PreviewId = previewId,
                ConfirmationToken = token,
                TargetDatabase = preview.TargetDatabase,
                ValidatedEdzPath = preview.ValidatedEdzPath,
                RequestedCount = items.Count,
                NewCount = items.Count(item => item.Status == ImportPreviewStatus.New),
                ExistingCount = items.Count(item => item.Status == ImportPreviewStatus.ExistingSame),
                ConflictCount = items.Count(item => item.Status == ImportPreviewStatus.ExistingDifferent),
                AmbiguousCount = items.Count(item => item.Status == ImportPreviewStatus.Ambiguous),
                InvalidCount = items.Count(item => item.Status == ImportPreviewStatus.Invalid),
                DatabaseFingerprint = Map(preview.Fingerprint),
                ValidatedEdzFingerprint = Map(preview.ValidatedEdzFingerprint),
                ResourceMappings = preview.ResourceMappings.Select(Map).ToList(),
                Items = items,
                SupportedImportModes = new List<string> { "AddNewOnly" },
                Diagnostics = preview.Diagnostics,
                ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds
            });
        }
        finally { EndOperation(); }
    }

    private void HandleImport(ImportPartsRequest request)
    {
        var cancellation = BeginOperation(request.RequestId);
        try
        {
            if (!_importPreviews.TryGetValue(request.PreviewId, out var stored))
                throw new OfficialImportException("DB005", "The import Preview is missing or expired. Run Preview again.", request.PreviewId);
            if (!FixedTimeEquals(stored.ConfirmationToken, request.ConfirmationToken))
                throw new OfficialImportException("DB005", "The explicit confirmation token is invalid. Run Preview again.", string.Empty);
            if (!string.Equals(Path.GetFullPath(stored.Preview.TargetDatabase), Path.GetFullPath(request.TargetDatabase), StringComparison.OrdinalIgnoreCase) ||
                !FingerprintEquals(stored.Preview.Fingerprint, request.DatabaseFingerprint))
                throw new OfficialImportException("DB005", "The Import request does not match its Preview.", request.TargetDatabase);

            var result = Importer.ImportParts(
                stored.Preview,
                request.Decisions.Select(item => new OfficialImportDecision { StableIdentity = item.StableIdentity, Action = item.Action }).ToArray(),
                (stage, current, total, message) => Write(new ImportProgress
                {
                    RequestId = request.RequestId,
                    Stage = stage,
                    Current = current,
                    Total = total,
                    CurrentPart = message,
                    Message = message
                }),
                () => cancellation.IsCancellationRequested);
            var auditPath = WriteImportAudit(request, result);
            Write(new ImportProgress
            {
                RequestId = request.RequestId,
                Stage = BridgeProtocol.ProgressStages.Completed,
                Current = result.Outcomes.Count,
                Total = result.Outcomes.Count,
                Message = result.Success ? "Import and post-import verification completed." : "Import completed with verification failures.",
                ElapsedMilliseconds = result.ElapsedMilliseconds
            });
            Write(MapImportResult(request.RequestId, result, auditPath));
            _importPreviews.Remove(request.PreviewId);
        }
        catch (OfficialImportException exception) when (exception.Result != null)
        {
            var auditPath = WriteImportAudit(request, exception.Result, exception);
            throw new OfficialImportException(
                exception.ErrorCode,
                exception.UserMessage,
                exception.TechnicalDetails + Environment.NewLine + "AuditReport=" + auditPath,
                exception,
                exception.Result);
        }
        finally { EndOperation(); }
    }

    private void HandleCapabilities(GetCapabilitiesRequest request)
    {
        _logger.Write(_options.SessionId, request.RequestId, "StartingRuntime");
        var capabilities = Exporter.GetCapabilities();
        Write(new CapabilitiesResult
        {
            RequestId = request.RequestId,
            EplanRuntimeFound = capabilities.RuntimeFound,
            EplanVersion = capabilities.EplanVersion,
            ApiVersion = capabilities.ApiVersion,
            LicenseState = capabilities.LicenseState,
            PartsServiceAvailable = capabilities.PartsServiceAvailable,
            EdzConverterAvailable = capabilities.EdzConverterAvailable,
            TemporaryDatabaseCapability = capabilities.TemporaryDatabaseCapability,
            BridgeVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            ProtocolVersionSupported = BridgeProtocol.Version,
            LogPath = _logger.LogPath
        });
    }

    private void HandleExport(ExportPartsRequest request)
    {
        CancellationTokenSource cancellation;
        lock (_operationGate)
        {
            if (_activeOperation != null) throw new OfficialExportException("BRIDGE103", "Another Bridge operation is already active.", _activeRequestId);
            _activeRequestId = request.RequestId;
            _activeOperation = cancellation = new CancellationTokenSource();
        }
        try
        {
            var options = new OfficialExportOptions
            {
                RequestId = request.RequestId,
                EplanVariantBinDirectory = _temporaryVariantBin,
                EplanPlatformBinDirectory = _options.PlatformBinDirectory,
                TemporaryRoot = _temporaryRoot,
                OutputPath = request.OutputPath,
                SystemConfigurationScheme = BridgeBootstrap.SchemeName
            };
            foreach (var part in request.Parts) options.Parts.Add(new OfficialSelectedPart
            {
                StablePartIdentity = part.StablePartIdentity,
                PreferredSourceEdz = part.PreferredSourceEdz,
                Manufacturer = part.Manufacturer,
                PackageKey = part.PackageKey,
                PartNumber = part.PartNumber,
                Variant = part.Variant,
                SourceFileSize = part.SourceFileSize,
                SourceLastWriteTimeUtcTicks = part.SourceLastWriteTimeUtcTicks
            });
            _logger.Write(_options.SessionId, request.RequestId, "ExportStarted", "Parts=" + request.Parts.Count.ToString());
            var result = Exporter.Export(
                options,
                progress =>
                {
                    _logger.Write(_options.SessionId, request.RequestId, progress.Stage, Path.GetFileName(progress.CurrentSource));
                    Write(new ExportProgress
                    {
                        RequestId = request.RequestId,
                        Stage = progress.Stage,
                        Current = progress.Current,
                        Total = progress.Total,
                        CurrentSource = progress.CurrentSource,
                        CurrentPart = progress.CurrentPart,
                        Message = progress.Message,
                        ElapsedMilliseconds = progress.ElapsedMilliseconds
                    });
                },
                path => ValidateOffline(path, request.Parts),
                () => cancellation.IsCancellationRequested);

            Write(new ExportProgress
            {
                RequestId = request.RequestId,
                Stage = BridgeProtocol.ProgressStages.Completed,
                Current = result.RequestedPartCount,
                Total = result.RequestedPartCount,
                Message = "Official export and dual validation completed.",
                ElapsedMilliseconds = result.ElapsedMilliseconds
            });
            Write(new ExportResult
            {
                RequestId = request.RequestId,
                Success = true,
                OutputPath = result.PublishedOutputPath,
                RequestedPartCount = result.RequestedPartCount,
                ExportedRequestedPartCount = result.ExportedRequestedPartCount,
                ReferencedPartCount = result.Validation.ReferencedPartCount,
                OutputSize = result.OutputSize,
                SourceEdzCount = result.SourceEdzCount,
                RuntimeVersion = result.RuntimeVersion,
                ElapsedMilliseconds = result.ElapsedMilliseconds,
                TemporaryPeakBytes = result.TemporaryPeakBytes,
                Warnings = result.Warnings,
                Diagnostics = result.Diagnostics.Select(detail => new ExportDiagnosticDto { Code = "BRIDGE-DIAGNOSTIC", Severity = "Info", Message = detail }).ToList(),
                ValidationSummary = new ValidationSummaryDto
                {
                    ArchiveReadable = result.OfflineArchiveReadable,
                    ManifestValid = result.OfflineManifestValid,
                    MissingReferenceCount = result.OfflineMissingReferenceCount,
                    RequestedPartCount = result.Validation.RequestedPartCount,
                    ImportedPartCount = result.Validation.ImportedPartCount,
                    ReferencedPartCount = result.Validation.ReferencedPartCount,
                    UnexpectedPartCount = result.Validation.UnexpectedPartCount,
                    MissingRequestedParts = result.Validation.MissingRequestedParts,
                    UnexpectedUnknownParts = result.Validation.UnexpectedUnknownParts,
                    Verdict = "PASS"
                },
                LogPath = _logger.LogPath
            });
            _logger.Write(
                _options.SessionId,
                request.RequestId,
                "ExportCompleted",
                "OutputBytes=" + result.OutputSize.ToString() +
                "; TemporaryPeakBytes=" + result.TemporaryPeakBytes.ToString() +
                "; ElapsedMilliseconds=" + result.ElapsedMilliseconds.ToString());
        }
        catch (OfficialExportException exception)
        {
            Write(new ExportResult
            {
                RequestId = request.RequestId,
                Success = false,
                OutputPath = request.OutputPath,
                RequestedPartCount = request.Parts.Count,
                ErrorCode = exception.ErrorCode,
                UserMessage = exception.UserMessage,
                TechnicalDetails = exception.TechnicalDetails,
                LogPath = _logger.LogPath,
                ValidationSummary = new ValidationSummaryDto { RequestedPartCount = request.Parts.Count, Verdict = "FAIL" }
            });
            _logger.Write(_options.SessionId, request.RequestId, exception.ErrorCode, exception.TechnicalDetails);
        }
        finally
        {
            lock (_operationGate)
            {
                _activeOperation?.Dispose();
                _activeOperation = null;
                _activeRequestId = string.Empty;
            }
        }
    }

    private OfficialOfflineValidation ValidateOffline(string path, IReadOnlyCollection<ExportPartDto> requested)
    {
        var validation = new OfficialOfflineValidation();
        var open = new EdzDocumentReader().Open(path, CancellationToken.None);
        foreach (var diagnostic in open.Diagnostics.Where(item => item.Severity >= DiagnosticSeverity.Warning))
            validation.Diagnostics.Add(diagnostic.Code + ": " + diagnostic.Message);
        if (open.Document == null) return validation;
        using (var document = open.Document)
        {
            validation.ArchiveReadable = true;
            validation.ManifestValid = document.Manifest != null;
            validation.MissingReferenceCount = document.MissingReferenceCount;
            var parts = document.Parts.Take(100000, CancellationToken.None);
            foreach (var request in requested)
            {
                var found = parts.Any(part =>
                    string.Equals((part.PartNumber ?? string.Empty).Trim(), request.PartNumber.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(NormalizeVariant(part.Variant), NormalizeVariant(request.Variant), StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(request.Manufacturer) || string.Equals((part.Manufacturer ?? string.Empty).Trim(), request.Manufacturer.Trim(), StringComparison.OrdinalIgnoreCase)) &&
                    (string.IsNullOrWhiteSpace(request.PackageKey) || string.Equals(part.PackageKey, request.PackageKey, StringComparison.OrdinalIgnoreCase)));
                if (!found) validation.MissingRequestedParts.Add(request.StablePartIdentity + " | " + request.PartNumber + " | " + NormalizeVariant(request.Variant));
            }
        }
        return validation;
    }

    private void ReadLoop()
    {
        try
        {
            while (_reader != null)
            {
                string? line;
                try { line = BridgeFraming.ReadLine(_reader); }
                catch (InvalidDataException exception)
                {
                    WriteError(Guid.NewGuid().ToString("N"), BridgeProtocol.ErrorCodes.InvalidRequest,
                        "The Bridge protocol request exceeded the 1 MiB safety limit.", exception.Message);
                    break;
                }
                if (line == null) break;
                MessageHeader header;
                try { header = BridgeJson.ReadHeader(line); }
                catch (Exception exception)
                {
                    WriteError(Guid.NewGuid().ToString("N"), BridgeProtocol.ErrorCodes.InvalidRequest, "Malformed Bridge protocol message.", exception.Message);
                    continue;
                }
                if (!string.Equals(header.ProtocolVersion, BridgeProtocol.Version, StringComparison.Ordinal))
                {
                    WriteError(header.RequestId, BridgeProtocol.ErrorCodes.VersionMismatch,
                        "Client / Bridge protocol version mismatch.", "Client=" + header.ProtocolVersion + "; Bridge=" + BridgeProtocol.Version);
                    continue;
                }
                BridgeMessage message;
                try { message = BridgeJson.DeserializeKnown(line); }
                catch (JsonException exception)
                {
                    WriteError(header.RequestId, BridgeProtocol.ErrorCodes.InvalidRequest, "Unknown or invalid Bridge message.", exception.Message);
                    continue;
                }
                if (message is CancelOperationRequest cancel)
                {
                    HandleCancellation(cancel);
                    continue;
                }
                _commands.Add(message);
            }
        }
        catch (Exception exception) when (exception is IOException || exception is ObjectDisposedException)
        {
            _logger.Write(_options.SessionId, string.Empty, "PipeReaderStopped", exception.Message);
        }
        finally { _commands.CompleteAdding(); }
    }

    private void HandleCancellation(CancelOperationRequest cancel)
    {
        lock (_operationGate)
        {
            if (_activeOperation != null && string.Equals(_activeRequestId, cancel.TargetRequestId, StringComparison.Ordinal))
            {
                _activeOperation.Cancel();
                Write(new CancelAcknowledged { RequestId = cancel.TargetRequestId, TargetRequestId = cancel.TargetRequestId });
                _logger.Write(_options.SessionId, cancel.TargetRequestId, "Cancelling");
            }
        }
    }

    private EplanOfficialSelectedPartsExporter Exporter => _exporter ??= new EplanOfficialSelectedPartsExporter(
        _temporaryVariantBin, _options.PlatformBinDirectory, _temporaryRoot, BridgeBootstrap.SchemeName);

    private SafePartsDatabaseImporter Importer
    {
        get
        {
            if (_importer != null) return _importer;
            var phase5 = !string.IsNullOrWhiteSpace(_options.TargetMasterDataRoot);
            var scheme = phase5 ? BridgeBootstrap.TargetSchemeName : BridgeBootstrap.SchemeName;
            _importer = new SafePartsDatabaseImporter(
                _temporaryVariantBin,
                _options.PlatformBinDirectory,
                _temporaryRoot,
                scheme,
                phase5 ? scheme : null,
                _options.TargetMasterDataRoot);
            return _importer;
        }
    }

    private static IReadOnlyList<OfficialImportMetadata> ReadValidatedIncomingMetadata(string edzPath)
    {
        var open = new EdzDocumentReader().Open(edzPath, CancellationToken.None);
        if (open.Document == null)
            throw new OfficialImportException("DB101", "The selected EDZ failed offline validation.",
                string.Join(Environment.NewLine, open.Diagnostics.Select(item => item.Code + ": " + item.Message)));
        using (var document = open.Document)
        {
            if (document.MissingReferenceCount != 0)
                throw new OfficialImportException("DB101", "The selected EDZ has missing archive references.", "MissingReferenceCount=" + document.MissingReferenceCount.ToString());
            return document.Parts.Take(100000, CancellationToken.None).Select(part => new OfficialImportMetadata
            {
                Manufacturer = part.Manufacturer ?? string.Empty,
                PartNumber = part.PartNumber ?? string.Empty,
                Variant = part.Variant ?? "1",
                TypeNumber = part.TypeNumber ?? string.Empty,
                OrderNumber = part.OrderNumber ?? string.Empty,
                Description = part.Description ?? string.Empty,
                Picture = ReadUnknown(part.UnknownAttributes, "P_ARTICLE_PICTUREFILE"),
                Macro = FirstNotEmpty(
                    ReadUnknown(part.UnknownAttributes, "P_ARTICLE_GROUPSYMBOLMACRO"),
                    ReadUnknown(part.UnknownAttributes, "P_ARTICLE_MACRO"))
            }).ToArray();
        }
    }

    private static string ReadUnknown(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) ? value ?? string.Empty : string.Empty;

    private static string FirstNotEmpty(string first, string second) => string.IsNullOrWhiteSpace(first) ? second : first;

    private CancellationTokenSource BeginOperation(string requestId)
    {
        lock (_operationGate)
        {
            if (_activeOperation != null)
                throw new OfficialImportException(BridgeProtocol.ErrorCodes.InvalidRequest, "Another Bridge operation is already active.", _activeRequestId);
            _activeRequestId = requestId;
            _activeOperation = new CancellationTokenSource();
            return _activeOperation;
        }
    }

    private void EndOperation()
    {
        lock (_operationGate)
        {
            _activeOperation?.Dispose();
            _activeOperation = null;
            _activeRequestId = string.Empty;
        }
    }

    private string WriteImportAudit(ImportPartsRequest request, OfficialImportResult result, OfficialImportException? failure = null)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EplanEdzManager", "Logs", "Bridge", "ImportReports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory,
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + request.RequestId + "-ImportReport.json");
        var payload = new
        {
            schemaVersion = "1.0",
            operationId = request.RequestId,
            timeUtc = DateTimeOffset.UtcNow,
            applicationVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? string.Empty,
            eplanVersion = result.RuntimeVersion,
            targetDatabase = result.TargetDatabase,
            fingerprintBefore = Map(result.FingerprintBefore),
            fingerprintAfter = Map(result.FingerprintAfter),
            userDecisions = request.Decisions,
            error = failure == null ? null : new
            {
                failure.ErrorCode,
                failure.UserMessage,
                failure.TechnicalDetails
            },
            result = new
            {
                result.Success,
                result.PartialSuccess,
                outcomes = result.Outcomes,
                backupPath = result.BackupPath,
                backupSha256 = result.BackupSha256,
                result.Warnings,
                result.Diagnostics,
                result.ElapsedMilliseconds,
                result.BackupElapsedMilliseconds,
                result.OfficialImportElapsedMilliseconds,
                result.VerificationElapsedMilliseconds
            }
        };
        File.WriteAllText(path, JsonConvert.SerializeObject(payload, Formatting.Indented), new UTF8Encoding(false));
        return path;
    }

    private ImportPartsResult MapImportResult(string requestId, OfficialImportResult result, string auditPath)
    {
        return new ImportPartsResult
        {
            RequestId = requestId,
            Success = result.Success,
            PartialSuccess = result.PartialSuccess,
            TargetDatabase = result.TargetDatabase,
            Requested = result.Outcomes.Count,
            Added = result.Outcomes.Count(item => item.Outcome == ImportOutcomeStatus.Added),
            Updated = result.Outcomes.Count(item => item.Outcome == ImportOutcomeStatus.Updated),
            Skipped = result.Outcomes.Count(item => item.Outcome == ImportOutcomeStatus.Skipped),
            Failed = result.Outcomes.Count(item => item.Outcome == ImportOutcomeStatus.Failed),
            BackupPath = result.BackupPath,
            BackupSha256 = result.BackupSha256,
            AuditReportPath = auditPath,
            RuntimeVersion = result.RuntimeVersion,
            ElapsedMilliseconds = result.ElapsedMilliseconds,
            BackupElapsedMilliseconds = result.BackupElapsedMilliseconds,
            OfficialImportElapsedMilliseconds = result.OfficialImportElapsedMilliseconds,
            VerificationElapsedMilliseconds = result.VerificationElapsedMilliseconds,
            FingerprintBefore = Map(result.FingerprintBefore),
            FingerprintAfter = Map(result.FingerprintAfter),
            Outcomes = result.Outcomes.Select(item => new ImportPartOutcomeDto
            {
                StableIdentity = item.StableIdentity,
                PartNumber = item.PartNumber,
                Variant = item.Variant,
                RequestedAction = item.RequestedAction,
                Outcome = item.Outcome,
                Diagnostic = item.Diagnostic
            }).ToList(),
            Warnings = result.Warnings,
            Diagnostics = result.Diagnostics,
            ErrorCode = result.PartialSuccess ? BridgeProtocol.ErrorCodes.PartialImport : string.Empty,
            UserMessage = result.PartialSuccess ? "Some requested parts failed post-import verification." : string.Empty,
            LogPath = _logger.LogPath
        };
    }

    private static ImportPreviewItemDto Map(OfficialImportPreviewItem item) => new ImportPreviewItemDto
    {
        StableIdentity = item.StableIdentity,
        Manufacturer = item.Manufacturer,
        PartNumber = item.PartNumber,
        Variant = item.Variant,
        Status = item.Status,
        ExistingMetadata = Map(item.ExistingMetadata),
        IncomingMetadata = Map(item.IncomingMetadata),
        Differences = item.Differences.Select(difference => new ImportDifferenceDto
        {
            Field = difference.Field,
            Existing = difference.Existing,
            Incoming = difference.Incoming
        }).ToList(),
        SuggestedAction = item.SuggestedAction,
        Diagnostic = item.Diagnostic
    };

    private static ImportMetadataDto Map(OfficialImportMetadata metadata) => new ImportMetadataDto
    {
        Manufacturer = metadata.Manufacturer,
        PartNumber = metadata.PartNumber,
        Variant = metadata.Variant,
        TypeNumber = metadata.TypeNumber,
        OrderNumber = metadata.OrderNumber,
        Description = metadata.Description,
        Picture = metadata.Picture,
        Macro = metadata.Macro
    };

    private static DatabaseFingerprintDto Map(OfficialDatabaseFingerprint fingerprint) => new DatabaseFingerprintDto
    {
        FullPath = fingerprint.FullPath,
        FileSize = fingerprint.FileSize,
        LastWriteTimeUtcTicks = fingerprint.LastWriteTimeUtcTicks,
        Sha256 = fingerprint.Sha256
    };

    private static ResourcePathMappingDto Map(OfficialResourceMapping mapping) => new ResourcePathMappingDto
    {
        Variable = mapping.Variable,
        ResolvedPath = mapping.ResolvedPath
    };

    private static bool FingerprintEquals(OfficialDatabaseFingerprint left, DatabaseFingerprintDto right) =>
        string.Equals(Path.GetFullPath(left.FullPath), Path.GetFullPath(right.FullPath), StringComparison.OrdinalIgnoreCase) &&
        left.FileSize == right.FileSize &&
        left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        var difference = leftBytes.Length ^ rightBytes.Length;
        var length = Math.Max(leftBytes.Length, rightBytes.Length);
        for (var index = 0; index < length; index++)
        {
            var leftValue = index < leftBytes.Length ? leftBytes[index] : (byte)0;
            var rightValue = index < rightBytes.Length ? rightBytes[index] : (byte)0;
            difference |= leftValue ^ rightValue;
        }
        return difference == 0;
    }

    private sealed class StoredImportPreview
    {
        public StoredImportPreview(string confirmationToken, OfficialImportPreview preview)
        {
            ConfirmationToken = confirmationToken;
            Preview = preview;
        }

        public string ConfirmationToken { get; }
        public OfficialImportPreview Preview { get; }
    }

    private void Write(BridgeMessage message)
    {
        lock (_writeGate)
        {
            if (_writer == null) return;
            _writer.WriteLine(BridgeJson.Serialize(message));
        }
    }

    private void WriteError(string requestId, string code, string userMessage, string technicalDetails) => Write(new ErrorResponse
    {
        RequestId = requestId,
        ErrorCode = code,
        UserMessage = userMessage,
        TechnicalDetails = technicalDetails,
        LogPath = _logger.LogPath
    });

    private static NamedPipeServerStream CreateCurrentUserPipe(string pipeName)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("Current Windows user SID is unavailable.");
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 64 * 1024, 64 * 1024, security);
    }

    private static void VerifyConnectedIdentity(NamedPipeServerStream pipe)
    {
        var serverSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty;
        var clientSid = string.Empty;
        pipe.RunAsClient(() => clientSid = WindowsIdentity.GetCurrent().User?.Value ?? string.Empty);
        if (!string.Equals(serverSid, clientSid, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("The named-pipe client is not the current Windows user.");
    }

    private static string NormalizeVariant(string? value) => string.IsNullOrWhiteSpace(value) ? "1" : value!.Trim();
    private static string Classify(Exception exception)
    {
        var text = exception.ToString();
        if (text.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0) return BridgeProtocol.ErrorCodes.LicenseUnavailable;
        if (exception is FileNotFoundException || exception is DirectoryNotFoundException) return BridgeProtocol.ErrorCodes.RuntimeNotFound;
        return BridgeProtocol.ErrorCodes.InvalidRequest;
    }
}
