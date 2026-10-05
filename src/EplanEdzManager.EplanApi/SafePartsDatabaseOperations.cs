using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Eplan.EplApi.Base;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;

namespace EplanEdzManager.EplanApi;

public sealed class OfficialDatabaseFingerprint
{
    public string FullPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public long LastWriteTimeUtcTicks { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class OfficialResourceMapping
{
    public string Variable { get; set; } = string.Empty;
    public string ResolvedPath { get; set; } = string.Empty;
}

public sealed class OfficialDatabaseInspection
{
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
    public OfficialDatabaseFingerprint Fingerprint { get; set; } = new OfficialDatabaseFingerprint();
    public List<OfficialResourceMapping> ResourceMappings { get; } = new List<OfficialResourceMapping>();
    public List<string> Diagnostics { get; } = new List<string>();
    public string ErrorCode { get; set; } = string.Empty;
    public string UserMessage { get; set; } = string.Empty;
    public string TechnicalDetails { get; set; } = string.Empty;
}

public sealed class OfficialImportMetadata
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

public sealed class OfficialImportPart
{
    public string StableIdentity { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
}

public sealed class OfficialImportDifference
{
    public string Field { get; set; } = string.Empty;
    public string Existing { get; set; } = string.Empty;
    public string Incoming { get; set; } = string.Empty;
}

public sealed class OfficialImportPreviewItem
{
    public string StableIdentity { get; set; } = string.Empty;
    public string Manufacturer { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string Status { get; set; } = "Invalid";
    public string SuggestedAction { get; set; } = "Skip";
    public OfficialImportMetadata ExistingMetadata { get; set; } = new OfficialImportMetadata();
    public OfficialImportMetadata IncomingMetadata { get; set; } = new OfficialImportMetadata();
    public List<OfficialImportDifference> Differences { get; } = new List<OfficialImportDifference>();
    public string Diagnostic { get; set; } = string.Empty;
}

public sealed class OfficialImportPreview
{
    public string TargetDatabase { get; set; } = string.Empty;
    public string ValidatedEdzPath { get; set; } = string.Empty;
    public OfficialDatabaseFingerprint Fingerprint { get; set; } = new OfficialDatabaseFingerprint();
    public OfficialDatabaseFingerprint ValidatedEdzFingerprint { get; set; } = new OfficialDatabaseFingerprint();
    public List<OfficialResourceMapping> ResourceMappings { get; } = new List<OfficialResourceMapping>();
    public List<OfficialImportPreviewItem> Items { get; } = new List<OfficialImportPreviewItem>();
    public List<string> Diagnostics { get; } = new List<string>();
}

public sealed class OfficialImportDecision
{
    public string StableIdentity { get; set; } = string.Empty;
    public string Action { get; set; } = "Skip";
}

public sealed class OfficialImportPartOutcome
{
    public string StableIdentity { get; set; } = string.Empty;
    public string PartNumber { get; set; } = string.Empty;
    public string Variant { get; set; } = string.Empty;
    public string RequestedAction { get; set; } = string.Empty;
    public string Outcome { get; set; } = "Failed";
    public string Diagnostic { get; set; } = string.Empty;
}

public sealed class OfficialImportResult
{
    public bool Success { get; set; }
    public bool PartialSuccess { get; set; }
    public string TargetDatabase { get; set; } = string.Empty;
    public string BackupPath { get; set; } = string.Empty;
    public string BackupSha256 { get; set; } = string.Empty;
    public string RuntimeVersion { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
    public double BackupElapsedMilliseconds { get; set; }
    public double OfficialImportElapsedMilliseconds { get; set; }
    public double VerificationElapsedMilliseconds { get; set; }
    public OfficialDatabaseFingerprint FingerprintBefore { get; set; } = new OfficialDatabaseFingerprint();
    public OfficialDatabaseFingerprint FingerprintAfter { get; set; } = new OfficialDatabaseFingerprint();
    public List<OfficialImportPartOutcome> Outcomes { get; } = new List<OfficialImportPartOutcome>();
    public List<string> Warnings { get; } = new List<string>();
    public List<string> Diagnostics { get; } = new List<string>();
}

public sealed class OfficialImportException : Exception
{
    public OfficialImportException(
        string errorCode,
        string userMessage,
        string technicalDetails,
        Exception? inner = null,
        OfficialImportResult? result = null)
        : base(userMessage, inner)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
        TechnicalDetails = technicalDetails;
        Result = result;
    }

    public string ErrorCode { get; }
    public string UserMessage { get; }
    public string TechnicalDetails { get; }
    public OfficialImportResult? Result { get; }
}

public sealed partial class SafePartsDatabaseImporter
{
    public OfficialDatabaseInspection InspectPartsDatabase(string databasePath)
    {
        var result = new OfficialDatabaseInspection();
        try
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                return Failure(result, "DB001", "Select an EPLAN parts database.", "The database path is empty.");
            var path = Path.GetFullPath(databasePath);
            result.DatabasePath = path;
            result.Exists = File.Exists(path);
            if (!result.Exists) return Failure(result, "DB001", "The selected parts database does not exist.", path);
            if (!string.Equals(Path.GetExtension(path), ".mdb", StringComparison.OrdinalIgnoreCase))
                return Failure(result, "DB002", "This Phase 5 build supports only EPLAN 2.9 Access MDB parts databases.", path);

            result.Writable = ProbeExclusiveWritable(path, out var writeCode, out var writeDiagnostic);
            if (!result.Writable)
                return Failure(result, writeCode, writeCode == "DB004" ? "The selected parts database is read-only." : "The selected parts database is locked. Close EPLAN Parts Management or switch it away from this database.", writeDiagnostic);
            result.Readable = ProbeReadable(path, out var readDiagnostic);
            if (!result.Readable) return Failure(result, "DB002", "The selected parts database cannot be read.", readDiagnostic);

            result.Fingerprint = CreateFingerprint(path);
            result.LastModifiedUtcTicks = result.Fingerprint.LastWriteTimeUtcTicks;
            EnsureRuntime(_systemConfigurationScheme);
            MDPartsDatabase? database = null;
            MDPart[] parts = new MDPart[0];
            try
            {
                database = _partsManagement!.OpenDatabase(path);
                if (database == null || !database.IsOpen)
                    return Failure(result, "DB002", "EPLAN 2.9 could not open the selected parts database.", path);
                result.OpenSuccess = true;
                result.DatabaseType = database.Type.ToString();
                result.IsSchemeUpToDate = database.IsSchemeUpToDate;
                parts = database.Parts;
                result.PartCount = parts.Length;
                result.EplanCompatible = true;
                result.DetectedVersion = "EPLAN 2.9 API compatible schema";
            }
            finally
            {
                foreach (var part in parts) part.Dispose();
                CloseAndDispose(database);
            }
            if (!string.IsNullOrWhiteSpace(_targetSystemConfigurationScheme))
            {
                EnsureRuntime(_targetSystemConfigurationScheme);
                result.ResourceMappings.AddRange(ReadMappings());
                EnsureRuntime(_systemConfigurationScheme);
            }
            else
            {
                result.ResourceMappings.AddRange(ReadMappings());
            }
            result.CanImport = result.OpenSuccess && result.Writable && result.EplanCompatible && result.IsSchemeUpToDate && !string.IsNullOrWhiteSpace(_targetSystemConfigurationScheme);
            if (string.IsNullOrWhiteSpace(_targetSystemConfigurationScheme))
                result.Diagnostics.Add("A target master-data root was not supplied when this Bridge session started; Preview is allowed, Import is not.");
            if (!result.IsSchemeUpToDate)
                return Failure(result, "DB002", "The database schema is not current for EPLAN 2.9. Automatic schema updates are not performed.", path);
            result.Success = true;
            return result;
        }
        catch (Exception exception)
        {
            return Failure(result, ClassifyDatabaseException(exception), "EPLAN 2.9 could not safely inspect the selected parts database.", Flatten(exception));
        }
    }

    public OfficialImportPreview PreviewPartsImport(
        string targetDatabase,
        string validatedEdzPath,
        IReadOnlyCollection<OfficialImportPart> requestedParts,
        IReadOnlyCollection<OfficialImportMetadata> incomingRows,
        Func<bool> isCancellationRequested)
    {
        if (requestedParts == null || requestedParts.Count == 0)
            throw new OfficialImportException("DB101", "At least one part is required for import Preview.", string.Empty);
        if (!File.Exists(validatedEdzPath) || new FileInfo(validatedEdzPath).Length == 0)
            throw new OfficialImportException("DB101", "The validated SelectedParts.edz is missing or empty.", validatedEdzPath);
        if (incomingRows == null || incomingRows.Count == 0)
            throw new OfficialImportException("DB101", "The validated SelectedParts.edz contains no readable incoming part metadata.", validatedEdzPath);
        var inspection = InspectPartsDatabase(targetDatabase);
        if (!inspection.Success)
            throw new OfficialImportException(inspection.ErrorCode, inspection.UserMessage, inspection.TechnicalDetails);
        ThrowIfImportCancelled(isCancellationRequested);

        try
        {
            ThrowIfImportCancelled(isCancellationRequested);
            var existingRows = ReadMetadataRows(Path.GetFullPath(targetDatabase));
            var preview = new OfficialImportPreview
            {
                TargetDatabase = Path.GetFullPath(targetDatabase),
                ValidatedEdzPath = Path.GetFullPath(validatedEdzPath),
                Fingerprint = inspection.Fingerprint,
                ValidatedEdzFingerprint = CreateFingerprint(validatedEdzPath)
            };
            preview.ResourceMappings.AddRange(inspection.ResourceMappings);
            foreach (var request in requestedParts.OrderBy(item => item.StableIdentity, StringComparer.Ordinal))
                preview.Items.Add(ClassifyPreviewItem(request, incomingRows, existingRows));
            var additionalIncomingRows = incomingRows.Count(row => !requestedParts.Any(request =>
                IdentityMatches(row, request.Manufacturer, request.PartNumber, request.Variant)));
            if (additionalIncomingRows > 0)
                preview.Diagnostics.Add(additionalIncomingRows.ToString(CultureInfo.InvariantCulture) +
                    " additional EDZ part rows are not requested logical parts. They may be official referenced parts and the whole-EDZ importer may add them.");
            var missing = preview.Items.Where(item => item.Status == "Invalid").ToArray();
            if (missing.Length > 0)
                preview.Diagnostics.Add(missing.Length.ToString(CultureInfo.InvariantCulture) + " requested parts were not uniquely resolved in the validated EDZ.");
            return preview;
        }
        catch (OfficialImportException) { throw; }
        catch (Exception exception)
        {
            throw new OfficialImportException("DB101", "EPLAN could not build the import Preview.", Flatten(exception), exception);
        }
    }

    public OfficialImportResult ImportParts(
        OfficialImportPreview preview,
        IReadOnlyCollection<OfficialImportDecision> decisions,
        Action<string, int, int, string> reportProgress,
        Func<bool> isCancellationRequested)
    {
        if (preview == null) throw new ArgumentNullException(nameof(preview));
        if (decisions == null) throw new ArgumentNullException(nameof(decisions));
        if (string.IsNullOrWhiteSpace(_targetSystemConfigurationScheme) || string.IsNullOrWhiteSpace(_targetMasterDataRoot))
            throw new OfficialImportException("DB002", "A target master-data root must be explicitly selected before Import.", "This Bridge session has no target System Configuration scheme.");
        var decisionGroups = decisions.GroupBy(item => item.StableIdentity, StringComparer.Ordinal).ToArray();
        if (decisionGroups.Any(group => group.Count() != 1))
            throw new OfficialImportException("DB102", "Every Preview item requires exactly one decision.", "Duplicate StableIdentity decision detected.");
        var decisionMap = decisionGroups.ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        if (decisionMap.Count != preview.Items.Count || preview.Items.Any(item => !decisionMap.ContainsKey(item.StableIdentity)))
            throw new OfficialImportException("DB102", "Every Preview item requires one explicit import decision.", string.Empty);
        if (preview.Items.Any(item => item.Status == "Ambiguous" || item.Status == "Invalid"))
            throw new OfficialImportException("DB102", "Resolve every Ambiguous or Invalid Preview item before Import.", string.Empty);
        if (decisions.Any(item => string.Equals(item.Action, "Update", StringComparison.Ordinal)))
            throw new OfficialImportException("DB102", "Selective Update is not enabled in this safety-first build.", "EPLAN 2.9 accepts whole-file Update modes, but a validated way to limit the update to only individually selected items has not been established.");
        foreach (var item in preview.Items)
        {
            var action = decisionMap[item.StableIdentity].Action;
            if (item.Status == "New" && action != "ImportNew" && action != "Skip")
                throw new OfficialImportException("DB102", "A New part must be explicitly imported or skipped.", item.StableIdentity);
            if (item.Status != "New" && action != "Skip")
                throw new OfficialImportException("DB102", "Existing, conflict, ambiguous, and invalid parts default to Skip.", item.StableIdentity);
        }
        var newItems = preview.Items.Where(item => item.Status == "New").ToArray();
        var selectedNewCount = newItems.Count(item => decisionMap[item.StableIdentity].Action == "ImportNew");
        if (selectedNewCount > 0 && selectedNewCount != newItems.Length)
            throw new OfficialImportException(
                "DB102",
                "A validated EDZ cannot safely import only some of its New rows.",
                "EPLAN 2.9 AppendNewRecords applies to the whole EDZ. Export the intended subset as a new Phase 4 validated EDZ, then run Preview again.");

        var timer = Stopwatch.StartNew();
        var result = new OfficialImportResult { TargetDatabase = preview.TargetDatabase, FingerprintBefore = preview.Fingerprint };
        var importItems = preview.Items.Where(item => decisionMap[item.StableIdentity].Action == "ImportNew").ToArray();
        foreach (var item in preview.Items.Where(item => decisionMap[item.StableIdentity].Action == "Skip"))
            result.Outcomes.Add(Outcome(item, "Skip", "Skipped", "Skipped by explicit user decision."));
        reportProgress("PreparingBackup", 0, preview.Items.Count, "Rechecking the database fingerprint and preparing a unique verified MDB backup.");
        ThrowIfImportCancelled(isCancellationRequested);
        var current = CreateFingerprint(preview.TargetDatabase);
        if (!FingerprintEquals(preview.Fingerprint, current))
            throw new OfficialImportException("DB005", "The target database changed after Preview. Run Preview again.", DescribeFingerprintChange(preview.Fingerprint, current));
        VerifyValidatedEdzFingerprint(preview);
        if (!ProbeExclusiveWritable(preview.TargetDatabase, out var lockCode, out var lockDiagnostic))
            throw new OfficialImportException(lockCode, "The target database is no longer exclusively writable.", lockDiagnostic);
        var phaseTimer = Stopwatch.StartNew();
        CreateVerifiedBackup(preview.TargetDatabase, result);
        result.BackupElapsedMilliseconds = phaseTimer.Elapsed.TotalMilliseconds;
        ThrowIfImportCancelled(isCancellationRequested);

        if (importItems.Length == 0)
        {
            result.Success = true;
            result.FingerprintAfter = CreateFingerprint(preview.TargetDatabase);
            result.ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
            return result;
        }

        try
        {
            reportProgress("OpeningTargetDatabase", 0, importItems.Length, "Initializing the isolated EPLAN 2.9 runtime with the explicit target master-data mapping.");
            EnsureRuntime(_targetSystemConfigurationScheme);
            result.RuntimeVersion = _application?.Version ?? string.Empty;
            VerifyTargetMasterDataMappings();
            ThrowIfImportCancelled(isCancellationRequested);
            reportProgress("Importing", 0, importItems.Length, "Officially importing the validated EDZ in AppendNewRecords mode.");
            _partsService!.PartsDatabase = preview.TargetDatabase;
            VerifyValidatedEdzFingerprint(preview);
            phaseTimer.Restart();
            _partsService.ImportPartsListToSystem(preview.ValidatedEdzPath, EdzConverter, string.Empty, PartsService.ImportMode.AppendNewRecords);
            result.OfficialImportElapsedMilliseconds = phaseTimer.Elapsed.TotalMilliseconds;
            var cancellationObservedAfterImport = isCancellationRequested();
            reportProgress("VerifyingParts", 0, importItems.Length, "Closing and reopening the target database to verify each requested part.");
            phaseTimer.Restart();
            var afterRows = ReadMetadataRows(preview.TargetDatabase);
            var verified = 0;
            foreach (var item in importItems)
            {
                var matches = afterRows.Where(row => IdentityMatches(row, item.Manufacturer, item.PartNumber, item.Variant)).ToArray();
                if (matches.Length == 1)
                {
                    verified++;
                    var resourceFailures = VerifyTargetResources(matches[0]);
                    if (resourceFailures.Count == 0)
                        result.Outcomes.Add(Outcome(item, "ImportNew", "Added", "Verified after database reopen."));
                    else
                        result.Outcomes.Add(Outcome(item, "ImportNew", "Failed", string.Join("; ", resourceFailures)));
                }
                else
                {
                    result.Outcomes.Add(Outcome(item, "ImportNew", "Failed", "Post-import identity match count=" + matches.Length.ToString(CultureInfo.InvariantCulture)));
                }
                reportProgress("VerifyingParts", verified, importItems.Length, item.PartNumber);
            }
            result.VerificationElapsedMilliseconds = phaseTimer.Elapsed.TotalMilliseconds;
            if (cancellationObservedAfterImport)
                result.Warnings.Add("Cancellation was requested while the official import call was active. The call could not be aborted; actual post-import outcomes are reported above.");
            result.Success = result.Outcomes.All(item => item.Outcome != "Failed");
            result.PartialSuccess = result.Outcomes.Any(item => item.Outcome == "Failed") && result.Outcomes.Any(item => item.Outcome == "Added");
            if (!result.Success && !result.PartialSuccess)
                throw new OfficialImportException("DB203", "Post-import verification failed.", string.Join(Environment.NewLine, result.Outcomes.Where(item => item.Outcome == "Failed").Select(item => item.StableIdentity + ": " + item.Diagnostic)));
        }
        catch (OfficialImportException exception)
        {
            CaptureFailureState(result, timer, preview.TargetDatabase);
            throw new OfficialImportException(exception.ErrorCode, exception.UserMessage, exception.TechnicalDetails, exception, result);
        }
        catch (Exception exception)
        {
            CaptureFailureState(result, timer, preview.TargetDatabase);
            throw new OfficialImportException("DB201", "EPLAN could not import the validated EDZ into the selected database.", Flatten(exception), exception, result);
        }
        finally
        {
            reportProgress("ClosingDatabase", preview.Items.Count, preview.Items.Count, "Closing EPLAN runtime handles for the target database.");
        }
        result.FingerprintAfter = CreateFingerprint(preview.TargetDatabase);
        result.ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
        return result;
    }

    private static void CaptureFailureState(OfficialImportResult result, Stopwatch timer, string databasePath)
    {
        result.Success = false;
        result.ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
        try { result.FingerprintAfter = CreateFingerprint(databasePath); }
        catch (Exception exception) { result.Diagnostics.Add("Could not fingerprint the target after failure: " + Flatten(exception)); }
    }

    private List<OfficialImportMetadata> ReadMetadataRows(string databasePath)
    {
        MDPartsDatabase? database = null;
        MDPart[] parts = new MDPart[0];
        try
        {
            database = _partsManagement!.OpenDatabase(databasePath);
            parts = database.Parts;
            return parts.Select(ToMetadata).ToList();
        }
        finally
        {
            foreach (var part in parts) part.Dispose();
            CloseAndDispose(database);
        }
    }

    private static OfficialImportMetadata ToMetadata(MDPart part) => new OfficialImportMetadata
    {
        Manufacturer = ReadProperty(part.Properties.ARTICLE_MANUFACTURER),
        PartNumber = part.PartNr ?? string.Empty,
        Variant = NormalizeVariant(part.Variant),
        TypeNumber = ReadProperty(part.Properties.ARTICLE_TYPENR),
        OrderNumber = ReadProperty(part.Properties.ARTICLE_ORDERNR),
        Description = ReadProperty(part.Properties.ARTICLE_DESCR1),
        Picture = ReadProperty(part.Properties.ARTICLE_PICTUREFILE),
        Macro = ReadProperty(part.Properties.ARTICLE_GROUPSYMBOLMACRO)
    };

    private static OfficialImportPreviewItem ClassifyPreviewItem(
        OfficialImportPart request,
        IReadOnlyCollection<OfficialImportMetadata> incomingRows,
        IReadOnlyCollection<OfficialImportMetadata> existingRows)
    {
        var item = new OfficialImportPreviewItem
        {
            StableIdentity = request.StableIdentity,
            Manufacturer = request.Manufacturer,
            PartNumber = request.PartNumber,
            Variant = NormalizeVariant(request.Variant)
        };
        if (string.IsNullOrWhiteSpace(request.StableIdentity) || string.IsNullOrWhiteSpace(request.PartNumber))
        {
            item.Status = "Invalid";
            item.Diagnostic = "StableIdentity and PartNumber are required.";
            return item;
        }
        var incoming = incomingRows.Where(row => IdentityMatches(row, request.Manufacturer, request.PartNumber, request.Variant)).ToArray();
        if (incoming.Length != 1)
        {
            item.Status = incoming.Length > 1 ? "Ambiguous" : "Invalid";
            item.Diagnostic = "Validated EDZ identity match count=" + incoming.Length.ToString(CultureInfo.InvariantCulture);
            return item;
        }
        item.IncomingMetadata = incoming[0];
        var existing = existingRows.Where(row => IdentityMatches(row, request.Manufacturer, request.PartNumber, request.Variant)).ToArray();
        if (existing.Length == 0)
        {
            item.Status = "New";
            item.SuggestedAction = "ImportNew";
            return item;
        }
        if (existing.Length > 1)
        {
            item.Status = "Ambiguous";
            item.Diagnostic = "Target database identity match count=" + existing.Length.ToString(CultureInfo.InvariantCulture);
            return item;
        }
        item.ExistingMetadata = existing[0];
        Compare(item, "TypeNumber", existing[0].TypeNumber, incoming[0].TypeNumber);
        Compare(item, "OrderNumber", existing[0].OrderNumber, incoming[0].OrderNumber);
        Compare(item, "Description", existing[0].Description, incoming[0].Description);
        Compare(item, "Picture", existing[0].Picture, incoming[0].Picture);
        Compare(item, "Macro", existing[0].Macro, incoming[0].Macro);
        item.Status = item.Differences.Count == 0 ? "ExistingSame" : "ExistingDifferent";
        item.SuggestedAction = "Skip";
        return item;
    }

    private static void Compare(OfficialImportPreviewItem item, string field, string existing, string incoming)
    {
        if (!Equivalent(existing, incoming))
            item.Differences.Add(new OfficialImportDifference { Field = field, Existing = existing, Incoming = incoming });
    }

    private static bool Equivalent(string left, string right) =>
        string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.Ordinal);

    private static bool IdentityMatches(OfficialImportMetadata row, string manufacturer, string partNumber, string variant) =>
        string.Equals(row.Manufacturer.Trim(), (manufacturer ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.PartNumber.Trim(), (partNumber ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizeVariant(row.Variant), NormalizeVariant(variant), StringComparison.OrdinalIgnoreCase);

    private List<OfficialResourceMapping> ReadMappings()
    {
        var result = new List<OfficialResourceMapping>();
        foreach (var variable in new[] { "MD_IMG", "MD_MACROS", "MD_DOCUMENTS", "MD_MECHANICALMODELS" })
            result.Add(new OfficialResourceMapping { Variable = variable, ResolvedPath = Path.GetFullPath(PathMap.SubstitutePath("$(" + variable + ")")) });
        return result;
    }

    private void VerifyTargetMasterDataMappings()
    {
        foreach (var mapping in ReadMappings())
        {
            if (!IsPathWithin(_targetMasterDataRoot, mapping.ResolvedPath))
                throw new OfficialImportException("DB002", "The target master-data path safety check failed.", mapping.Variable + "=" + mapping.ResolvedPath + "; TargetRoot=" + _targetMasterDataRoot);
            Directory.CreateDirectory(mapping.ResolvedPath);
        }
        foreach (var variable in new[] { "CFG_USER", "CFG_STATION", "CFG_COMPANY" })
        {
            var resolved = PathMap.SubstitutePath("$(" + variable + ")");
            if (!IsPathWithin(_temporaryRoot, resolved))
                throw new OfficialImportException("DB002", "The isolated settings path safety check failed.", variable + "=" + resolved);
        }
    }

    private List<string> VerifyTargetResources(OfficialImportMetadata metadata)
    {
        var failures = new List<string>();
        VerifyReferences("MD_IMG", metadata.Picture, failures);
        VerifyReferences("MD_MACROS", metadata.Macro, failures);
        return failures;
    }

    private void VerifyReferences(string variable, string references, ICollection<string> failures)
    {
        foreach (var reference in SplitReferences(references))
        {
            var path = PathMap.SubstitutePath(reference);
            if (!Path.IsPathRooted(path)) path = Path.Combine(PathMap.SubstitutePath("$(" + variable + ")"), path);
            if (!IsPathWithin(_targetMasterDataRoot, path) || !File.Exists(path)) failures.Add(variable + " missing: " + path);
        }
    }

    private void CreateVerifiedBackup(string databasePath, OfficialImportResult result)
    {
        try
        {
            var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "backup");
            Directory.CreateDirectory(directory);
            var backupPath = Path.Combine(directory,
                Path.GetFileNameWithoutExtension(databasePath) + ".before-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".mdb");
            File.Copy(databasePath, backupPath, false);
            using (var stream = new FileStream(backupPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            var originalCount = CountRows(databasePath);
            var backupCount = CountRows(backupPath);
            if (originalCount != backupCount) throw new InvalidDataException("Backup part count differs from source. Source=" + originalCount + "; Backup=" + backupCount);
            result.BackupPath = backupPath;
            result.BackupSha256 = ComputeSha256(backupPath);
        }
        catch (Exception exception)
        {
            throw new OfficialImportException("DB006", "A verified backup could not be created; Import did not start.", Flatten(exception), exception);
        }
    }

    private int CountRows(string path) => ReadMetadataRows(path).Count;

    private static OfficialDatabaseFingerprint CreateFingerprint(string path)
    {
        var info = new FileInfo(path);
        return new OfficialDatabaseFingerprint
        {
            FullPath = Path.GetFullPath(path),
            FileSize = info.Length,
            LastWriteTimeUtcTicks = info.LastWriteTimeUtc.Ticks,
            Sha256 = ComputeSha256(path)
        };
    }

    private static string ComputeSha256(string path)
    {
        using (var algorithm = SHA256.Create())
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty);
    }

    private static bool FingerprintEquals(OfficialDatabaseFingerprint left, OfficialDatabaseFingerprint right) =>
        string.Equals(Path.GetFullPath(left.FullPath), Path.GetFullPath(right.FullPath), StringComparison.OrdinalIgnoreCase) &&
        left.FileSize == right.FileSize && left.LastWriteTimeUtcTicks == right.LastWriteTimeUtcTicks &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.OrdinalIgnoreCase);

    private static string DescribeFingerprintChange(OfficialDatabaseFingerprint before, OfficialDatabaseFingerprint after) =>
        "Before=" + before.FileSize + "/" + before.LastWriteTimeUtcTicks + "/" + before.Sha256 + Environment.NewLine +
        "After=" + after.FileSize + "/" + after.LastWriteTimeUtcTicks + "/" + after.Sha256;

    private static void VerifyValidatedEdzFingerprint(OfficialImportPreview preview)
    {
        OfficialDatabaseFingerprint current;
        try { current = CreateFingerprint(preview.ValidatedEdzPath); }
        catch (Exception exception)
        {
            throw new OfficialImportException("DB005", "The validated EDZ changed or disappeared after Preview. Run Preview again.", Flatten(exception), exception);
        }
        if (!FingerprintEquals(preview.ValidatedEdzFingerprint, current))
            throw new OfficialImportException("DB005", "The validated EDZ changed after Preview. Run Preview again.",
                DescribeFingerprintChange(preview.ValidatedEdzFingerprint, current));
    }

    private static bool ProbeReadable(string path, out string diagnostic)
    {
        try
        {
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) { }
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception exception) { diagnostic = Flatten(exception); return false; }
    }

    private static bool ProbeExclusiveWritable(string path, out string errorCode, out string diagnostic)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            {
                errorCode = "DB004";
                diagnostic = "The MDB has the read-only file attribute.";
                return false;
            }
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            errorCode = string.Empty;
            diagnostic = string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException exception) { errorCode = "DB004"; diagnostic = Flatten(exception); return false; }
        catch (IOException exception) { errorCode = "DB003"; diagnostic = Flatten(exception); return false; }
    }

    private static OfficialDatabaseInspection Failure(OfficialDatabaseInspection result, string code, string message, string details)
    {
        result.Success = false;
        result.CanImport = false;
        result.ErrorCode = code;
        result.UserMessage = message;
        result.TechnicalDetails = details;
        return result;
    }

    private static string ClassifyDatabaseException(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return "DB004";
        if (exception is IOException) return "DB003";
        return "DB002";
    }

    private static string Flatten(Exception exception)
    {
        var values = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
            values.Add(current.GetType().Name + ": " + current.Message);
        return string.Join(" --> ", values);
    }

    private static void ThrowIfImportCancelled(Func<bool> predicate)
    {
        if (predicate()) throw new OfficialImportException("DB301", "The database import was cancelled at a safe point.", string.Empty);
    }

    private static OfficialImportPartOutcome Outcome(OfficialImportPreviewItem item, string action, string outcome, string diagnostic) => new OfficialImportPartOutcome
    {
        StableIdentity = item.StableIdentity,
        PartNumber = item.PartNumber,
        Variant = item.Variant,
        RequestedAction = action,
        Outcome = outcome,
        Diagnostic = diagnostic
    };

    private static string TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
            return string.Empty;
        }
        catch (Exception exception) { return Flatten(exception); }
    }
}
