using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace EplanEdzManager.EplanBridge.Protocol;

public static class BridgeProtocol
{
    public const string Version = "1.1";
    public const int MaximumFrameCharacters = 1024 * 1024;

    public static class MessageTypes
    {
        public const string Ping = "Ping";
        public const string PingResult = "PingResult";
        public const string GetCapabilities = "GetCapabilities";
        public const string CapabilitiesResult = "CapabilitiesResult";
        public const string ExportParts = "ExportParts";
        public const string ExportProgress = "ExportProgress";
        public const string ExportResult = "ExportResult";
        public const string InspectPartsDatabase = "InspectPartsDatabase";
        public const string InspectPartsDatabaseResult = "InspectPartsDatabaseResult";
        public const string PreviewPartsImport = "PreviewPartsImport";
        public const string PreviewPartsImportResult = "PreviewPartsImportResult";
        public const string ImportParts = "ImportParts";
        public const string ImportProgress = "ImportProgress";
        public const string ImportPartsResult = "ImportPartsResult";
        public const string CancelOperation = "CancelOperation";
        public const string CancelAcknowledged = "CancelAcknowledged";
        public const string Shutdown = "Shutdown";
        public const string ShutdownAcknowledged = "ShutdownAcknowledged";
        public const string Error = "Error";
    }

    public static class ErrorCodes
    {
        public const string RuntimeNotFound = "BRIDGE001";
        public const string VersionMismatch = "BRIDGE002";
        public const string LicenseUnavailable = "BRIDGE003";
        public const string SourceMissing = "BRIDGE101";
        public const string RequestedPartMissing = "BRIDGE102";
        public const string InvalidRequest = "BRIDGE103";
        public const string ImportFailed = "BRIDGE201";
        public const string ExportFailed = "BRIDGE202";
        public const string ReimportFailed = "BRIDGE203";
        public const string ValidationFailed = "BRIDGE204";
        public const string Cancelled = "BRIDGE301";
        public const string CleanupWarning = "BRIDGE401";
        public const string DatabaseNotFound = "DB001";
        public const string UnsupportedDatabase = "DB002";
        public const string DatabaseLocked = "DB003";
        public const string DatabaseReadOnly = "DB004";
        public const string DatabaseChangedSincePreview = "DB005";
        public const string BackupFailed = "DB006";
        public const string PreviewFailed = "DB101";
        public const string ConflictRequiresDecision = "DB102";
        public const string DatabaseImportFailed = "DB201";
        public const string PartialImport = "DB202";
        public const string ImportVerificationFailed = "DB203";
        public const string DatabaseImportCancelled = "DB301";
    }

    public static class ProgressStages
    {
        public const string StartingRuntime = "StartingRuntime";
        public const string CreatingTemporaryEnvironment = "CreatingTemporaryEnvironment";
        public const string ImportingSource = "ImportingSource";
        public const string VerifyingImportedParts = "VerifyingImportedParts";
        public const string PreparingFilter = "PreparingFilter";
        public const string Exporting = "Exporting";
        public const string ValidatingArchive = "ValidatingArchive";
        public const string RoundTripImport = "RoundTripImport";
        public const string VerifyingParts = "VerifyingParts";
        public const string CleaningUp = "CleaningUp";
        public const string Completed = "Completed";
        public const string InspectingDatabase = "InspectingDatabase";
        public const string CheckingConflicts = "CheckingConflicts";
        public const string PreparingBackup = "PreparingBackup";
        public const string GeneratingValidatedEdz = "GeneratingValidatedEdz";
        public const string OpeningTargetDatabase = "OpeningTargetDatabase";
        public const string Importing = "Importing";
        public const string VerifyingResources = "VerifyingResources";
        public const string ClosingDatabase = "ClosingDatabase";
    }
}

public static class BridgeFraming
{
    public static string? ReadLine(TextReader reader, int maximumCharacters = BridgeProtocol.MaximumFrameCharacters)
    {
        if (reader == null) throw new ArgumentNullException(nameof(reader));
        if (maximumCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
        var builder = new StringBuilder(Math.Min(maximumCharacters, 4096));
        while (true)
        {
            var value = reader.Read();
            if (value < 0) return builder.Length == 0 ? null : builder.ToString();
            var character = (char)value;
            if (character == '\n') return builder.ToString();
            if (character == '\r') continue;
            if (builder.Length >= maximumCharacters)
                throw new InvalidDataException("The Bridge protocol frame exceeded the 1 MiB safety limit.");
            builder.Append(character);
        }
    }
}

public static class ImportPreviewStatus
{
    public const string New = "New";
    public const string ExistingSame = "ExistingSame";
    public const string ExistingDifferent = "ExistingDifferent";
    public const string Ambiguous = "Ambiguous";
    public const string Invalid = "Invalid";
}

public static class ImportDecisionAction
{
    public const string Skip = "Skip";
    public const string ImportNew = "ImportNew";
    public const string Update = "Update";
}

public static class ImportOutcomeStatus
{
    public const string Added = "Added";
    public const string Updated = "Updated";
    public const string Skipped = "Skipped";
    public const string Failed = "Failed";
}

public abstract class BridgeMessage
{
    public string ProtocolVersion { get; set; } = BridgeProtocol.Version;
    public string RequestId { get; set; } = string.Empty;
    public string MessageType { get; set; } = string.Empty;
}

public sealed class MessageHeader : BridgeMessage
{
}

public sealed class PingRequest : BridgeMessage
{
    public PingRequest() => MessageType = BridgeProtocol.MessageTypes.Ping;
}

public sealed class PingResult : BridgeMessage
{
    public PingResult() => MessageType = BridgeProtocol.MessageTypes.PingResult;
    public string BridgeVersion { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
}

public sealed class GetCapabilitiesRequest : BridgeMessage
{
    public GetCapabilitiesRequest() => MessageType = BridgeProtocol.MessageTypes.GetCapabilities;
}

public sealed class CapabilitiesResult : BridgeMessage
{
    public CapabilitiesResult() => MessageType = BridgeProtocol.MessageTypes.CapabilitiesResult;
    public bool EplanRuntimeFound { get; set; }
    public string EplanVersion { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string LicenseState { get; set; } = string.Empty;
    public bool PartsServiceAvailable { get; set; }
    public bool EdzConverterAvailable { get; set; }
    public bool TemporaryDatabaseCapability { get; set; }
    public string BridgeVersion { get; set; } = string.Empty;
    public string ProtocolVersionSupported { get; set; } = BridgeProtocol.Version;
    public string LogPath { get; set; } = string.Empty;
}

public sealed class ExportPartDto
{
    public string StablePartIdentity { get; set; } = string.Empty;
    public string PreferredSourceEdz { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string PackageKey { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public long SourceFileSize { get; set; }
    public long SourceLastWriteTimeUtcTicks { get; set; }
}

public sealed class ExportPartsRequest : BridgeMessage
{
    public ExportPartsRequest() => MessageType = BridgeProtocol.MessageTypes.ExportParts;
    public string OutputPath { get; set; } = string.Empty;
    public List<ExportPartDto> Parts { get; set; } = new List<ExportPartDto>();
}

public sealed class DatabaseFingerprintDto
{
    public string FullPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class ResourcePathMappingDto
{
    public string Variable { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
}

public sealed class InspectPartsDatabaseRequest : BridgeMessage
{
    public InspectPartsDatabaseRequest() => MessageType = BridgeProtocol.MessageTypes.InspectPartsDatabase;
    public string DatabasePath { get; set; } = string.Empty;
}

public sealed class InspectPartsDatabaseResult : BridgeMessage
{
    public InspectPartsDatabaseResult() => MessageType = BridgeProtocol.MessageTypes.InspectPartsDatabaseResult;
    public bool Success { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public string DatabaseType { get; set; } = string.Empty;
    public bool Exists { get; set; }
    public bool OpenSuccess { get; set; }
    public bool Readable { get; set; }
    public bool Writable { get; set; }
    public bool EplanCompatible { get; set; }
    public bool IsSchemeUpToDate { get; set; }
    public int PartCount { get; set; }
    public long LastModifiedUtcTicks { get; set; }
    public bool CanImport { get; set; }
    public string DetectedVersion { get; set; } = string.Empty;
    public DatabaseFingerprintDto DatabaseFingerprint { get; set; } = new DatabaseFingerprintDto();
    public List<ResourcePathMappingDto> ResourceMappings { get; set; } = new List<ResourcePathMappingDto>();
    public List<string> Diagnostics { get; set; } = new List<string>();
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
}

public sealed class ImportMetadataDto
{
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string TypeNumber { get; set; } = string.Empty;
    public string OrderNumber { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Picture { get; set; } = string.Empty;
    public string Macro { get; set; } = string.Empty;
}

public sealed class ImportDifferenceDto
{
    public string Field { get; set; } = string.Empty;
    public string Existing { get; set; } = string.Empty;
    public string Incoming { get; set; } = string.Empty;
}

public sealed class ImportPreviewItemDto
{
    public string StableIdentity { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string Status { get; set; } = ImportPreviewStatus.Invalid;
    public ImportMetadataDto ExistingMetadata { get; set; } = new ImportMetadataDto();
    public ImportMetadataDto IncomingMetadata { get; set; } = new ImportMetadataDto();
    public List<ImportDifferenceDto> Differences { get; set; } = new List<ImportDifferenceDto>();
    public string SuggestedAction { get; set; } = ImportDecisionAction.Skip;
    public string Diagnostic { get; set; } = string.Empty;
}

public sealed class PreviewPartsImportRequest : BridgeMessage
{
    public PreviewPartsImportRequest() => MessageType = BridgeProtocol.MessageTypes.PreviewPartsImport;
    public string TargetDatabase { get; set; } = string.Empty;
    public string ValidatedEdzPath { get; set; } = string.Empty;
    public List<ExportPartDto> Parts { get; set; } = new List<ExportPartDto>();
}

public sealed class PreviewPartsImportResult : BridgeMessage
{
    public PreviewPartsImportResult() => MessageType = BridgeProtocol.MessageTypes.PreviewPartsImportResult;
    public bool Success { get; set; }
    public string PreviewId { get; set; } = string.Empty;
    public string ConfirmationToken { get; set; } = string.Empty;
    public string TargetDatabase { get; set; } = string.Empty;
    public string ValidatedEdzPath { get; set; } = string.Empty;
    public int RequestedCount { get; set; }
    public int NewCount { get; set; }
    public int ExistingCount { get; set; }
    public int ConflictCount { get; set; }
    public int AmbiguousCount { get; set; }
    public int InvalidCount { get; set; }
    public DatabaseFingerprintDto DatabaseFingerprint { get; set; } = new DatabaseFingerprintDto();
    public DatabaseFingerprintDto ValidatedEdzFingerprint { get; set; } = new DatabaseFingerprintDto();
    public List<ResourcePathMappingDto> ResourceMappings { get; set; } = new List<ResourcePathMappingDto>();
    public List<ImportPreviewItemDto> Items { get; set; } = new List<ImportPreviewItemDto>();
    public List<string> SupportedImportModes { get; set; } = new List<string>();
    public List<string> Diagnostics { get; set; } = new List<string>();
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
}

public sealed class ImportDecisionDto
{
    public string StableIdentity { get; set; } = string.Empty;
    public string Action { get; set; } = ImportDecisionAction.Skip;
}

public sealed class ImportPartsRequest : BridgeMessage
{
    public ImportPartsRequest() => MessageType = BridgeProtocol.MessageTypes.ImportParts;
    public string PreviewId { get; set; } = string.Empty;
    public string ConfirmationToken { get; set; } = string.Empty;
    public string TargetDatabase { get; set; } = string.Empty;
    public DatabaseFingerprintDto DatabaseFingerprint { get; set; } = new DatabaseFingerprintDto();
    public List<ImportDecisionDto> Decisions { get; set; } = new List<ImportDecisionDto>();
}

public sealed class ImportProgress : BridgeMessage
{
    public ImportProgress() => MessageType = BridgeProtocol.MessageTypes.ImportProgress;
    public string Stage { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentPart { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
}

public sealed class ImportPartOutcomeDto
{
    public string StableIdentity { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string RequestedAction { get; set; } = string.Empty;
    public string Outcome { get; set; } = ImportOutcomeStatus.Failed;
    public string Diagnostic { get; set; } = string.Empty;
}

public sealed class ImportPartsResult : BridgeMessage
{
    public ImportPartsResult() => MessageType = BridgeProtocol.MessageTypes.ImportPartsResult;
    public bool Success { get; set; }
    public bool PartialSuccess { get; set; }
    public string TargetDatabase { get; set; } = string.Empty;
    public int Requested { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string BackupPath { get; set; } = string.Empty;
    public string BackupSha256 { get; set; } = string.Empty;
    public string AuditReportPath { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
    public double BackupElapsedMilliseconds { get; set; }
    public double OfficialImportElapsedMilliseconds { get; set; }
    public double VerificationElapsedMilliseconds { get; set; }
    public DatabaseFingerprintDto FingerprintBefore { get; set; } = new DatabaseFingerprintDto();
    public DatabaseFingerprintDto FingerprintAfter { get; set; } = new DatabaseFingerprintDto();
    public List<ImportPartOutcomeDto> Outcomes { get; set; } = new List<ImportPartOutcomeDto>();
    public List<string> Warnings { get; set; } = new List<string>();
    public List<string> Diagnostics { get; set; } = new List<string>();
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
}

public sealed class CancelOperationRequest : BridgeMessage
{
    public CancelOperationRequest() => MessageType = BridgeProtocol.MessageTypes.CancelOperation;
    public string TargetRequestId { get; set; } = string.Empty;
}

public sealed class CancelAcknowledged : BridgeMessage
{
    public CancelAcknowledged() => MessageType = BridgeProtocol.MessageTypes.CancelAcknowledged;
    public string TargetRequestId { get; set; } = string.Empty;
}

public sealed class ShutdownRequest : BridgeMessage
{
    public ShutdownRequest() => MessageType = BridgeProtocol.MessageTypes.Shutdown;
}

public sealed class ShutdownAcknowledged : BridgeMessage
{
    public ShutdownAcknowledged() => MessageType = BridgeProtocol.MessageTypes.ShutdownAcknowledged;
}

public sealed class ExportProgress : BridgeMessage
{
    public ExportProgress() => MessageType = BridgeProtocol.MessageTypes.ExportProgress;
    public string Stage { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentSource { get; set; } = string.Empty;
    public string CurrentPart { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
}

public sealed class ValidationSummaryDto
{
    public bool ArchiveReadable { get; set; }
    public bool ManifestValid { get; set; }
    public int MissingReferenceCount { get; set; }
    public int RequestedPartCount { get; set; }
    public int ImportedPartCount { get; set; }
    public int ReferencedPartCount { get; set; }
    public int UnexpectedPartCount { get; set; }
    public List<string> MissingRequestedParts { get; set; } = new List<string>();
    public List<string> UnexpectedUnknownParts { get; set; } = new List<string>();
    public string Verdict { get; set; } = "FAIL";
}

public sealed class ExportDiagnosticDto
{
    public string Code { get; set; } = string.Empty;
    public string Severity { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}

public sealed class ExportResult : BridgeMessage
{
    public ExportResult() => MessageType = BridgeProtocol.MessageTypes.ExportResult;
    public bool Success { get; set; }
    public string OutputPath { get; set; } = string.Empty;
    public int RequestedPartCount { get; set; }
    public int ExportedRequestedPartCount { get; set; }
    public int ReferencedPartCount { get; set; }
    public long OutputSize { get; set; }
    public int SourceEdzCount { get; set; }
    public string RuntimeVersion { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
    public long TemporaryPeakBytes { get; set; }
    public List<string> Warnings { get; set; } = new List<string>();
    public List<ExportDiagnosticDto> Diagnostics { get; set; } = new List<ExportDiagnosticDto>();
    public ValidationSummaryDto ValidationSummary { get; set; } = new ValidationSummaryDto();
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
}

public sealed class ErrorResponse : BridgeMessage
{
    public ErrorResponse() => MessageType = BridgeProtocol.MessageTypes.Error;
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
    public string LogPath { get; set; } = string.Empty;
}

public static class BridgeJson
{
    private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Include,
        MissingMemberHandling = MissingMemberHandling.Ignore,
        TypeNameHandling = TypeNameHandling.None,
        Formatting = Formatting.None
    };

    public static string Serialize(BridgeMessage message)
    {
        if (message == null) throw new ArgumentNullException(nameof(message));
        ValidateEnvelope(message);
        return JsonConvert.SerializeObject(message, Settings);
    }

    public static MessageHeader ReadHeader(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) throw new JsonException("The bridge message is empty.");
        var value = JObject.Parse(json);
        var header = new MessageHeader
        {
            ProtocolVersion = value.Value<string>("protocolVersion") ?? string.Empty,
            RequestId = value.Value<string>("requestId") ?? string.Empty,
            MessageType = value.Value<string>("messageType") ?? string.Empty
        };
        ValidateEnvelope(header);
        return header;
    }

    public static T Deserialize<T>(string json) where T : BridgeMessage
    {
        var message = JsonConvert.DeserializeObject<T>(json, Settings)
            ?? throw new JsonException("The bridge message deserialized to null.");
        ValidateEnvelope(message);
        return message;
    }

    public static BridgeMessage DeserializeKnown(string json)
    {
        var header = ReadHeader(json);
        switch (header.MessageType)
        {
            case BridgeProtocol.MessageTypes.Ping: return Deserialize<PingRequest>(json);
            case BridgeProtocol.MessageTypes.PingResult: return Deserialize<PingResult>(json);
            case BridgeProtocol.MessageTypes.GetCapabilities: return Deserialize<GetCapabilitiesRequest>(json);
            case BridgeProtocol.MessageTypes.CapabilitiesResult: return Deserialize<CapabilitiesResult>(json);
            case BridgeProtocol.MessageTypes.ExportParts: return Deserialize<ExportPartsRequest>(json);
            case BridgeProtocol.MessageTypes.ExportProgress: return Deserialize<ExportProgress>(json);
            case BridgeProtocol.MessageTypes.ExportResult: return Deserialize<ExportResult>(json);
            case BridgeProtocol.MessageTypes.InspectPartsDatabase: return Deserialize<InspectPartsDatabaseRequest>(json);
            case BridgeProtocol.MessageTypes.InspectPartsDatabaseResult: return Deserialize<InspectPartsDatabaseResult>(json);
            case BridgeProtocol.MessageTypes.PreviewPartsImport: return Deserialize<PreviewPartsImportRequest>(json);
            case BridgeProtocol.MessageTypes.PreviewPartsImportResult: return Deserialize<PreviewPartsImportResult>(json);
            case BridgeProtocol.MessageTypes.ImportParts: return Deserialize<ImportPartsRequest>(json);
            case BridgeProtocol.MessageTypes.ImportProgress: return Deserialize<ImportProgress>(json);
            case BridgeProtocol.MessageTypes.ImportPartsResult: return Deserialize<ImportPartsResult>(json);
            case BridgeProtocol.MessageTypes.CancelOperation: return Deserialize<CancelOperationRequest>(json);
            case BridgeProtocol.MessageTypes.CancelAcknowledged: return Deserialize<CancelAcknowledged>(json);
            case BridgeProtocol.MessageTypes.Shutdown: return Deserialize<ShutdownRequest>(json);
            case BridgeProtocol.MessageTypes.ShutdownAcknowledged: return Deserialize<ShutdownAcknowledged>(json);
            case BridgeProtocol.MessageTypes.Error: return Deserialize<ErrorResponse>(json);
            default: throw new JsonException("Unknown bridge message type: " + header.MessageType);
        }
    }

    private static void ValidateEnvelope(BridgeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ProtocolVersion)) throw new JsonException("protocolVersion is required.");
        if (string.IsNullOrWhiteSpace(message.RequestId)) throw new JsonException("requestId is required.");
        if (string.IsNullOrWhiteSpace(message.MessageType)) throw new JsonException("messageType is required.");
    }
}
