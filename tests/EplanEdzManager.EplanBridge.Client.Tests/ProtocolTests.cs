using EplanEdzManager.EplanBridge.Protocol;
using Newtonsoft.Json;
using Xunit;

namespace EplanEdzManager.EplanBridge.Client.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void Export_request_round_trips_all_transport_identity_fields()
    {
        var source = new ExportPartsRequest
        {
            RequestId = "request-1",
            OutputPath = TestPath("Exports", "SelectedParts.edz"),
            Parts = new List<ExportPartDto>
            {
                new()
                {
                    StablePartIdentity = "LP1:abc",
                    PreferredSourceEdz = TestPath("Parts", "source.edz"),
                    Manufacturer = "MFR",
                    PackageKey = "PKG",
                    PartNumber = "P-1",
                    Variant = "1"
                }
            }
        };

        var json = BridgeJson.Serialize(source);
        var result = BridgeJson.Deserialize<ExportPartsRequest>(json);

        Assert.Equal(BridgeProtocol.Version, result.ProtocolVersion);
        Assert.Equal(BridgeProtocol.MessageTypes.ExportParts, result.MessageType);
        Assert.Equal("LP1:abc", result.Parts.Single().StablePartIdentity);
        Assert.Equal("MFR", result.Parts.Single().Manufacturer);
    }

    [Fact]
    public void Unknown_fields_are_forward_compatible()
    {
        const string json = "{\"protocolVersion\":\"1.0\",\"requestId\":\"r\",\"messageType\":\"Ping\",\"futureField\":42}";
        var result = BridgeJson.Deserialize<PingRequest>(json);
        Assert.Equal("r", result.RequestId);
    }

    [Fact]
    public void Protocol_mismatch_is_visible_in_header()
    {
        const string json = "{\"protocolVersion\":\"9.0\",\"requestId\":\"r\",\"messageType\":\"Ping\"}";
        var header = BridgeJson.ReadHeader(json);
        Assert.NotEqual(BridgeProtocol.Version, header.ProtocolVersion);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"protocolVersion\":\"1.0\"}")]
    [InlineData("")]
    public void Invalid_envelope_is_rejected(string json)
    {
        Assert.ThrowsAny<JsonException>(() => BridgeJson.ReadHeader(json));
    }

    [Fact]
    public void Cancel_progress_and_result_messages_parse()
    {
        BridgeMessage[] messages =
        {
            new CancelOperationRequest { RequestId = "cancel", TargetRequestId = "export" },
            new ExportProgress { RequestId = "export", Stage = BridgeProtocol.ProgressStages.ImportingSource, Current = 1, Total = 2 },
            new ExportResult { RequestId = "export", Success = true, RequestedPartCount = 2 }
        };
        foreach (var message in messages)
        {
            var parsed = BridgeJson.DeserializeKnown(BridgeJson.Serialize(message));
            Assert.Equal(message.MessageType, parsed.MessageType);
        }
    }

    [Fact]
    public void Error_codes_are_stable()
    {
        Assert.Equal("BRIDGE001", BridgeProtocol.ErrorCodes.RuntimeNotFound);
        Assert.Equal("BRIDGE301", BridgeProtocol.ErrorCodes.Cancelled);
        Assert.Equal("BRIDGE401", BridgeProtocol.ErrorCodes.CleanupWarning);
    }

    [Fact]
    public void Phase5_preview_and_import_contracts_round_trip_confirmation_boundary()
    {
        var preview = new PreviewPartsImportResult
        {
            RequestId = "preview-request",
            Success = true,
            PreviewId = "preview-id",
            ConfirmationToken = "synthetic-confirmation-value",
            TargetDatabase = TestPath("Parts", "Target.mdb"),
            RequestedCount = 1,
            NewCount = 1,
            DatabaseFingerprint = new DatabaseFingerprintDto
            {
                FullPath = TestPath("Parts", "Target.mdb"),
                FileSize = 42,
                LastWriteTimeUtcTicks = 123,
                Sha256 = "ABC"
            },
            Items = new List<ImportPreviewItemDto>
            {
                new()
                {
                    StableIdentity = "LP1:part",
                    PartNumber = "P-1",
                    Variant = "1",
                    Status = ImportPreviewStatus.New,
                    SuggestedAction = ImportDecisionAction.ImportNew
                }
            }
        };

        var parsedPreview = Assert.IsType<PreviewPartsImportResult>(BridgeJson.DeserializeKnown(BridgeJson.Serialize(preview)));
        Assert.Equal("preview-id", parsedPreview.PreviewId);
        Assert.Equal("ABC", parsedPreview.DatabaseFingerprint.Sha256);

        var import = new ImportPartsRequest
        {
            RequestId = "import-request",
            PreviewId = parsedPreview.PreviewId,
            ConfirmationToken = parsedPreview.ConfirmationToken,
            TargetDatabase = parsedPreview.TargetDatabase,
            DatabaseFingerprint = parsedPreview.DatabaseFingerprint,
            Decisions = new List<ImportDecisionDto>
            {
                new() { StableIdentity = "LP1:part", Action = ImportDecisionAction.ImportNew }
            }
        };
        var parsedImport = Assert.IsType<ImportPartsRequest>(BridgeJson.DeserializeKnown(BridgeJson.Serialize(import)));
        Assert.Equal(ImportDecisionAction.ImportNew, parsedImport.Decisions.Single().Action);
    }

    [Fact]
    public void Phase5_error_codes_are_stable()
    {
        Assert.Equal("DB001", BridgeProtocol.ErrorCodes.DatabaseNotFound);
        Assert.Equal("DB005", BridgeProtocol.ErrorCodes.DatabaseChangedSincePreview);
        Assert.Equal("DB102", BridgeProtocol.ErrorCodes.ConflictRequiresDecision);
        Assert.Equal("DB202", BridgeProtocol.ErrorCodes.PartialImport);
        Assert.Equal("DB301", BridgeProtocol.ErrorCodes.DatabaseImportCancelled);
    }

    [Fact]
    public void Phase5_progress_and_result_round_trip_outcomes_and_timings()
    {
        var progress = new ImportProgress
        {
            RequestId = "import-request",
            Stage = BridgeProtocol.ProgressStages.VerifyingParts,
            Current = 1,
            Total = 2,
            CurrentPart = "P-1",
            ElapsedMilliseconds = 1250
        };
        var parsedProgress = Assert.IsType<ImportProgress>(BridgeJson.DeserializeKnown(BridgeJson.Serialize(progress)));
        Assert.Equal(BridgeProtocol.ProgressStages.VerifyingParts, parsedProgress.Stage);
        Assert.Equal("P-1", parsedProgress.CurrentPart);

        var result = new ImportPartsResult
        {
            RequestId = "import-request",
            Success = true,
            Requested = 2,
            Added = 1,
            Skipped = 1,
            BackupPath = TestPath("Parts", "backup", "target.before.mdb"),
            AuditReportPath = TestPath("Reports", "ImportReport.json"),
            ElapsedMilliseconds = 4200,
            BackupElapsedMilliseconds = 700,
            OfficialImportElapsedMilliseconds = 2000,
            VerificationElapsedMilliseconds = 500,
            Outcomes = new List<ImportPartOutcomeDto>
            {
                new() { StableIdentity = "LP1:new", PartNumber = "P-1", Outcome = ImportOutcomeStatus.Added },
                new() { StableIdentity = "LP1:old", PartNumber = "P-2", Outcome = ImportOutcomeStatus.Skipped }
            }
        };
        var parsedResult = Assert.IsType<ImportPartsResult>(BridgeJson.DeserializeKnown(BridgeJson.Serialize(result)));
        Assert.Equal(1, parsedResult.Added);
        Assert.Equal(1, parsedResult.Skipped);
        Assert.Equal(2000, parsedResult.OfficialImportElapsedMilliseconds);
        Assert.Equal(ImportOutcomeStatus.Added, parsedResult.Outcomes[0].Outcome);
    }

    private static string TestPath(params string[] parts) =>
        Path.Combine(new[] { Path.GetTempPath(), "EPLAN Parts Library 中文 (test)-_" }.Concat(parts).ToArray());
}
