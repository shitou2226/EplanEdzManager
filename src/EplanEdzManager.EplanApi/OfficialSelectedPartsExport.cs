using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Eplan.EplApi.Base;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;
using EplanApplication = Eplan.EplApi.System.EplApplication;

namespace EplanEdzManager.EplanApi;

public sealed class OfficialSelectedPart
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

public sealed class OfficialExportOptions
{
    public string RequestId { get; set; } = string.Empty;
    public string EplanVariantBinDirectory { get; set; } = string.Empty;
    public string EplanPlatformBinDirectory { get; set; } = string.Empty;
    public string TemporaryRoot { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string SystemConfigurationScheme { get; set; } = "EplanEdzManagerTemp";
    public List<OfficialSelectedPart> Parts { get; } = new List<OfficialSelectedPart>();
}

public sealed class OfficialExportProgress
{
    public string Stage { get; set; } = string.Empty;
    public int Current { get; set; }
    public int Total { get; set; }
    public string CurrentSource { get; set; } = string.Empty;
    public string CurrentPart { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
}

public sealed class OfficialCapabilities
{
    public bool RuntimeFound { get; set; }
    public string EplanVersion { get; set; } = string.Empty;
    public string ApiVersion { get; set; } = string.Empty;
    public string LicenseState { get; set; } = string.Empty;
    public bool PartsServiceAvailable { get; set; }
    public bool EdzConverterAvailable { get; set; }
    public bool TemporaryDatabaseCapability { get; set; }
}

public sealed class OfficialValidationSummary
{
    public int RequestedPartCount { get; set; }
    public int ImportedPartCount { get; set; }
    public int ReferencedPartCount { get; set; }
    public int UnexpectedPartCount { get; set; }
    public List<string> MissingRequestedParts { get; } = new List<string>();
    public List<string> UnexpectedUnknownParts { get; } = new List<string>();
    public bool PictureAndMacroResourcesValid { get; set; }
}

public sealed class OfficialExportResult
{
    public string TemporaryOutputPath { get; set; } = string.Empty;
    public string PublishedOutputPath { get; set; } = string.Empty;
    public int RequestedPartCount { get; set; }
    public int ExportedRequestedPartCount { get; set; }
    public int SourceEdzCount { get; set; }
    public long OutputSize { get; set; }
    public string RuntimeVersion { get; set; } = string.Empty;
    public double ElapsedMilliseconds { get; set; }
    public long TemporaryPeakBytes { get; set; }
    public bool OfflineArchiveReadable { get; set; }
    public bool OfflineManifestValid { get; set; }
    public int OfflineMissingReferenceCount { get; set; }
    public OfficialValidationSummary Validation { get; set; } = new OfficialValidationSummary();
    public List<string> Warnings { get; } = new List<string>();
    public List<string> Diagnostics { get; } = new List<string>();
}

public sealed class OfficialOfflineValidation
{
    public bool ArchiveReadable { get; set; }
    public bool ManifestValid { get; set; }
    public int MissingReferenceCount { get; set; }
    public List<string> MissingRequestedParts { get; } = new List<string>();
    public List<string> Diagnostics { get; } = new List<string>();
    public bool Passed => ArchiveReadable && ManifestValid && MissingReferenceCount == 0 && MissingRequestedParts.Count == 0;
}

public sealed class OfficialExportException : Exception
{
    public OfficialExportException(string errorCode, string userMessage, string technicalDetails, Exception? exception = null)
        : base(userMessage, exception)
    {
        ErrorCode = errorCode;
        UserMessage = userMessage;
        TechnicalDetails = technicalDetails;
    }

    public string ErrorCode { get; }
    public string UserMessage { get; }
    public string TechnicalDetails { get; }
}

public sealed class EplanOfficialSelectedPartsExporter : IDisposable
{
    private const string EdzConverter = "IXPartsImportExportEdz";
    private static readonly string[] IsolationVariables =
    {
        "EPLAN_DATA", "CFG_USER", "CFG_STATION", "CFG_COMPANY", "MD_PARTS",
        "MD_IMG", "MD_MACROS", "MD_DOCUMENTS", "MD_MECHANICALMODELS"
    };

    private readonly string _variantBinDirectory;
    private readonly string _platformBinDirectory;
    private readonly string _temporaryRoot;
    private readonly string _systemConfigurationScheme;
    private EplanApplication? _application;
    private PartsService? _partsService;
    private MDPartsManagement? _partsManagement;
    private bool _disposed;

    public EplanOfficialSelectedPartsExporter(
        string variantBinDirectory,
        string platformBinDirectory,
        string temporaryRoot,
        string systemConfigurationScheme)
    {
        _variantBinDirectory = Path.GetFullPath(variantBinDirectory ?? throw new ArgumentNullException(nameof(variantBinDirectory)));
        _platformBinDirectory = Path.GetFullPath(platformBinDirectory ?? throw new ArgumentNullException(nameof(platformBinDirectory)));
        _temporaryRoot = Path.GetFullPath(temporaryRoot ?? throw new ArgumentNullException(nameof(temporaryRoot)));
        _systemConfigurationScheme = systemConfigurationScheme ?? throw new ArgumentNullException(nameof(systemConfigurationScheme));
    }

    public OfficialCapabilities GetCapabilities()
    {
        EnsureRuntime();
        VerifySupportedRuntimeVersion();
        var probePath = Path.Combine(_temporaryRoot, "Capability", "Probe.mdb");
        Directory.CreateDirectory(Path.GetDirectoryName(probePath));
        CreateAndCloseDatabase(probePath);
        TryDeleteDatabaseFiles(probePath);
        return new OfficialCapabilities
        {
            RuntimeFound = true,
            EplanVersion = SafeRead(() => _application!.Version),
            ApiVersion = FileVersionInfo.GetVersionInfo(typeof(PartsService).Assembly.Location).FileVersion ?? string.Empty,
            LicenseState = SafeRead(() => _application!.License),
            PartsServiceAvailable = true,
            EdzConverterAvailable = VerifyConverterRegistration(),
            TemporaryDatabaseCapability = true
        };
    }

    public OfficialExportResult Export(
        OfficialExportOptions options,
        Action<OfficialExportProgress> reportProgress,
        Func<string, OfficialOfflineValidation> offlineValidator,
        Func<bool> isCancellationRequested)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        if (reportProgress == null) throw new ArgumentNullException(nameof(reportProgress));
        if (offlineValidator == null) throw new ArgumentNullException(nameof(offlineValidator));
        if (isCancellationRequested == null) throw new ArgumentNullException(nameof(isCancellationRequested));
        var timer = Stopwatch.StartNew();
        var normalized = ValidateAndNormalize(options);
        Report(reportProgress, timer, "CreatingTemporaryEnvironment", 0, normalized.Count, string.Empty, string.Empty,
            "Validated the isolated operation workspace and selected part request.");
        ThrowIfCancelled(isCancellationRequested);

        try
        {
            Report(reportProgress, timer, "StartingRuntime", 0, normalized.Count, string.Empty, string.Empty,
                "Initializing the isolated EPLAN 2.9 runtime.");
            EnsureRuntime();
            VerifySupportedRuntimeVersion();
            VerifyPathIsolation();
        }
        catch (OfficialExportException) { throw; }
        catch (Exception exception)
        {
            var details = FlattenException(exception);
            var licenseFailure = details.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0;
            throw new OfficialExportException(
                licenseFailure ? "BRIDGE003" : "BRIDGE001",
                licenseFailure ? "An EPLAN license is not available to the isolated Bridge runtime." : "EPLAN 2.9 Runtime could not be initialized.",
                details,
                exception);
        }

        var operationDirectory = Path.Combine(_temporaryRoot, "Operations", MakeSafeName(options.RequestId));
        if (Directory.Exists(operationDirectory))
            throw new OfficialExportException("BRIDGE103", "The operation identifier has already been used.", operationDirectory);
        Directory.CreateDirectory(operationDirectory);

        var result = new OfficialExportResult
        {
            RequestedPartCount = normalized.Count,
            SourceEdzCount = normalized.Select(part => part.PreferredSourceEdz).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            RuntimeVersion = SafeRead(() => _application!.Version),
            PublishedOutputPath = Path.GetFullPath(options.OutputPath)
        };
        result.Warnings.Add("Variant is verified after import, but the documented PartsService part filter is limited to tblPart columns; the filter therefore uses Manufacturer + PartNumber and does not guess a tblVariant join.");
        result.Warnings.Add("PDF, construction, mechanical-model, and accessory resource samples are Not Yet Covered; picture and EMA macro references are verified.");

        string cleanupWarning = string.Empty;
        try
        {
            ExecuteOfficialPipeline(normalized, operationDirectory, result, reportProgress, timer, offlineValidator, isCancellationRequested);
            result.ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
            result.TemporaryPeakBytes = MeasureDirectoryBytes(_temporaryRoot);
        }
        catch (OperationCanceledException exception)
        {
            throw new OfficialExportException("BRIDGE301", "The EPLAN export was cancelled.", "Cancellation was observed at a safe point after the active official API call returned.", exception);
        }
        finally
        {
            try
            {
                Report(reportProgress, timer, "CleaningUp", 0, normalized.Count, string.Empty, string.Empty,
                    "Closing operation handles and deleting the isolated operation workspace.");
            }
            finally
            {
                cleanupWarning = TryDeleteDirectory(operationDirectory);
            }
        }
        if (!string.IsNullOrWhiteSpace(cleanupWarning))
            throw new OfficialExportException("BRIDGE401", "The export output was validated, but isolated workspace cleanup did not complete.", cleanupWarning);
        return result;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeValue(_partsManagement);
        DisposeValue(_partsService);
        if (_application != null)
        {
            try { _application.Exit(); } catch { }
            _application = null;
        }
    }

    private void ExecuteOfficialPipeline(
        IReadOnlyList<OfficialSelectedPart> parts,
        string operationDirectory,
        OfficialExportResult result,
        Action<OfficialExportProgress> reportProgress,
        Stopwatch timer,
        Func<string, OfficialOfflineValidation> offlineValidator,
        Func<bool> isCancellationRequested)
    {
        var databasesDirectory = Path.Combine(operationDirectory, "Databases");
        var batchesDirectory = Path.Combine(operationDirectory, "Batches");
        var outputDirectory = Path.Combine(operationDirectory, "Output");
        Directory.CreateDirectory(databasesDirectory);
        Directory.CreateDirectory(batchesDirectory);
        Directory.CreateDirectory(outputDirectory);
        var combinedDatabase = Path.Combine(databasesDirectory, "SelectedParts.mdb");
        CreateAndCloseDatabase(combinedDatabase);

        var sourceGroups = parts
            .GroupBy(part => part.PreferredSourceEdz, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var filterBuilder = new EplanPartFilterBuilder();
        var sourceIndex = 0;
        foreach (var sourceGroup in sourceGroups)
        {
            sourceIndex++;
            ThrowIfCancelled(isCancellationRequested);
            var stagingDatabase = Path.Combine(databasesDirectory, "Source-" + sourceIndex.ToString("D4", CultureInfo.InvariantCulture) + ".mdb");
            CreateAndCloseDatabase(stagingDatabase);
            _partsService!.PartsDatabase = stagingDatabase;
            VerifySourceEvidence(sourceGroup.First());
            Report(reportProgress, timer, "ImportingSource", sourceIndex, sourceGroups.Length, sourceGroup.Key, string.Empty,
                "Officially importing the preferred source EDZ into an isolated source database.");
            try
            {
                _partsService.ImportPartsListToSystem(sourceGroup.Key, EdzConverter);
            }
            catch (Exception exception)
            {
                throw new OfficialExportException("BRIDGE201", "EPLAN could not import a selected source EDZ.", sourceGroup.Key + Environment.NewLine + FlattenException(exception), exception);
            }

            ThrowIfCancelled(isCancellationRequested);
            Report(reportProgress, timer, "VerifyingImportedParts", sourceIndex, sourceGroups.Length, sourceGroup.Key, string.Empty,
                "Checking requested Manufacturer / PartNumber / Variant identities in the isolated source database.");
            var importedRows = ReadPartRows(stagingDatabase);
            var missing = sourceGroup.Where(request => !importedRows.Any(row => Matches(row, request))).ToArray();
            if (missing.Length > 0)
            {
                throw new OfficialExportException("BRIDGE102", "One or more requested parts were not found after official source import.",
                    string.Join(Environment.NewLine, missing.Select(DescribePart)));
            }

            var filters = filterBuilder.BuildBatches(sourceGroup.Select(part => new EplanPartFilterTerm
            {
                Manufacturer = part.Manufacturer,
                PartNumber = part.PartNumber,
                Variant = part.Variant
            }));
            var batchIndex = 0;
            foreach (var filter in filters)
            {
                batchIndex++;
                ThrowIfCancelled(isCancellationRequested);
                Report(reportProgress, timer, "PreparingFilter", batchIndex, filters.Count, sourceGroup.Key, string.Empty,
                    "Prepared a deterministic documented SQL-WHERE filter batch.");
                var batchEdz = Path.Combine(batchesDirectory,
                    "Source-" + sourceIndex.ToString("D4", CultureInfo.InvariantCulture) + "-Batch-" + batchIndex.ToString("D4", CultureInfo.InvariantCulture) + ".edz");
                try
                {
                    Report(reportProgress, timer, "Exporting", batchIndex, filters.Count, sourceGroup.Key, string.Empty,
                        "Officially exporting the selected subset from this source.");
                    _partsService!.PartsDatabase = stagingDatabase;
                    _partsService.ExportPartsListFromSystem(batchEdz, EdzConverter, filter);
                    EnsureNonEmptyFile(batchEdz, "BRIDGE202", "EPLAN did not create a non-empty source subset EDZ.");
                    ThrowIfCancelled(isCancellationRequested);
                    _partsService.PartsDatabase = combinedDatabase;
                    _partsService.ImportPartsListToSystem(batchEdz, EdzConverter);
                }
                catch (OfficialExportException) { throw; }
                catch (Exception exception)
                {
                    throw new OfficialExportException("BRIDGE202", "EPLAN could not prepare the selected source subset.",
                        "Filter: " + filter + Environment.NewLine + FlattenException(exception), exception);
                }
            }
        }

        ThrowIfCancelled(isCancellationRequested);
        var combinedRows = ReadPartRows(combinedDatabase);
        var missingCombined = parts.Where(request => !combinedRows.Any(row => Matches(row, request))).ToArray();
        if (missingCombined.Length > 0)
            throw new OfficialExportException("BRIDGE204", "The combined isolated database is missing requested parts.", string.Join(Environment.NewLine, missingCombined.Select(DescribePart)));

        var temporaryOutput = Path.Combine(outputDirectory, "SelectedParts.edz");
        Report(reportProgress, timer, "Exporting", parts.Count, parts.Count, string.Empty, string.Empty,
            "Officially exporting one final EDZ from the isolated selected-parts database.");
        try
        {
            _partsService!.PartsDatabase = combinedDatabase;
            _partsService.ExportPartsListFromSystem(temporaryOutput, EdzConverter);
            EnsureNonEmptyFile(temporaryOutput, "BRIDGE202", "EPLAN returned without creating a non-empty final EDZ.");
        }
        catch (OfficialExportException) { throw; }
        catch (Exception exception)
        {
            throw new OfficialExportException("BRIDGE202", "EPLAN could not export the selected parts EDZ.", FlattenException(exception), exception);
        }

        ThrowIfCancelled(isCancellationRequested);
        Report(reportProgress, timer, "RoundTripImport", 0, parts.Count, string.Empty, string.Empty,
            "Officially re-importing the generated EDZ into a fresh isolated database.");
        var roundTripDatabase = Path.Combine(databasesDirectory, "RoundTrip.mdb");
        CreateAndCloseDatabase(roundTripDatabase);
        try
        {
            _partsService!.PartsDatabase = roundTripDatabase;
            _partsService.ImportPartsListToSystem(temporaryOutput, EdzConverter);
        }
        catch (Exception exception)
        {
            throw new OfficialExportException("BRIDGE203", "The generated EDZ failed official round-trip import.", FlattenException(exception), exception);
        }

        ThrowIfCancelled(isCancellationRequested);
        Report(reportProgress, timer, "VerifyingParts", parts.Count, parts.Count, string.Empty, string.Empty,
            "Comparing the requested set with the official round-trip database.");
        var roundTripRows = ReadPartRows(roundTripDatabase);
        var validation = new OfficialValidationSummary
        {
            RequestedPartCount = parts.Count,
            ImportedPartCount = roundTripRows.Count
        };
        foreach (var request in parts.Where(request => !roundTripRows.Any(row => Matches(row, request))))
            validation.MissingRequestedParts.Add(DescribePart(request));
        var requestedRows = roundTripRows.Where(row => parts.Any(request => Matches(row, request))).ToArray();
        var additionalRows = roundTripRows.Where(row => !parts.Any(request => Matches(row, request))).ToArray();
        validation.ReferencedPartCount = additionalRows.Length;
        validation.UnexpectedPartCount = 0;
        validation.PictureAndMacroResourcesValid = VerifyPictureAndMacroResources(requestedRows, result.Diagnostics);
        result.Validation = validation;
        result.ExportedRequestedPartCount = parts.Count - validation.MissingRequestedParts.Count;
        if (validation.MissingRequestedParts.Count > 0 || !validation.PictureAndMacroResourcesValid)
            throw new OfficialExportException("BRIDGE204", "The generated EDZ failed requested-part or resource validation.",
                string.Join(Environment.NewLine, validation.MissingRequestedParts.Concat(result.Diagnostics)));

        ThrowIfCancelled(isCancellationRequested);
        Report(reportProgress, timer, "ValidatingArchive", parts.Count, parts.Count, string.Empty, string.Empty,
            "Validating archive readability, manifest integrity, references, and requested packages with the offline EDZ reader.");
        OfficialOfflineValidation offline;
        try { offline = offlineValidator(temporaryOutput); }
        catch (Exception exception)
        {
            throw new OfficialExportException("BRIDGE204", "The generated EDZ failed offline validation.", FlattenException(exception), exception);
        }
        result.OfflineArchiveReadable = offline.ArchiveReadable;
        result.OfflineManifestValid = offline.ManifestValid;
        result.OfflineMissingReferenceCount = offline.MissingReferenceCount;
        result.Diagnostics.AddRange(offline.Diagnostics);
        if (!offline.Passed)
            throw new OfficialExportException("BRIDGE204", "The generated EDZ failed offline validation.",
                string.Join(Environment.NewLine, offline.MissingRequestedParts.Concat(offline.Diagnostics)));

        ThrowIfCancelled(isCancellationRequested);
        PublishAtomically(temporaryOutput, result.PublishedOutputPath);
        result.TemporaryOutputPath = temporaryOutput;
        result.OutputSize = new FileInfo(result.PublishedOutputPath).Length;
    }

    private IReadOnlyList<OfficialSelectedPart> ValidateAndNormalize(OfficialExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.RequestId)) throw Invalid("requestId is required.");
        if (options.Parts.Count == 0) throw Invalid("At least one selected part is required.");
        if (string.IsNullOrWhiteSpace(options.OutputPath) || !string.Equals(Path.GetExtension(options.OutputPath), ".edz", StringComparison.OrdinalIgnoreCase))
            throw Invalid("A .edz output path is required.");
        var output = Path.GetFullPath(options.OutputPath);
        if (File.Exists(output)) throw Invalid("The output file already exists; overwrite must be confirmed by the Desktop before starting the Bridge.");
        if (!IsPathWithin(Path.Combine(Path.GetTempPath(), "EplanEdzManager", "Bridge"), _temporaryRoot))
            throw Invalid("The Bridge temporary root is outside the approved local temp base.");

        var normalized = new List<OfficialSelectedPart>();
        foreach (var sourceGroup in options.Parts.GroupBy(part => part.StablePartIdentity, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(sourceGroup.Key)) throw Invalid("StablePartIdentity is required.");
            var distinctSources = sourceGroup.Select(part => Path.GetFullPath(part.PreferredSourceEdz ?? string.Empty)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinctSources.Length > 1)
                throw Invalid("The same StablePartIdentity was supplied with multiple preferred sources: " + sourceGroup.Key);
            var part = sourceGroup.First();
            if (string.IsNullOrWhiteSpace(part.PartNumber)) throw Invalid("PartNumber is required for " + sourceGroup.Key);
            var source = distinctSources[0];
            if (!File.Exists(source))
                throw new OfficialExportException("BRIDGE101", "A selected source EDZ no longer exists.", source);
            if (new FileInfo(source).Length == 0)
                throw new OfficialExportException("BRIDGE101", "A selected source EDZ is empty.", source);
            if (part.SourceFileSize <= 0 || part.SourceLastWriteTimeUtcTicks <= 0)
                throw new OfficialExportException("BRIDGE101", "Source EDZ evidence is missing. Refresh the selection and try again.", source);
            if (sourceGroup.Any(item => item.SourceFileSize != part.SourceFileSize || item.SourceLastWriteTimeUtcTicks != part.SourceLastWriteTimeUtcTicks))
                throw Invalid("The same source EDZ was supplied with inconsistent file evidence: " + source);
            normalized.Add(new OfficialSelectedPart
            {
                StablePartIdentity = part.StablePartIdentity,
                PreferredSourceEdz = source,
                Manufacturer = (part.Manufacturer ?? string.Empty).Trim(),
                PackageKey = (part.PackageKey ?? string.Empty).Trim(),
                PartNumber = part.PartNumber.Trim(),
                Variant = NormalizeVariant(part.Variant),
                SourceFileSize = part.SourceFileSize,
                SourceLastWriteTimeUtcTicks = part.SourceLastWriteTimeUtcTicks
            });
        }
        normalized.Sort((left, right) => string.Compare(left.StablePartIdentity, right.StablePartIdentity, StringComparison.Ordinal));
        return normalized;
    }

    private static void VerifySourceEvidence(OfficialSelectedPart part)
    {
        FileInfo source;
        try { source = new FileInfo(part.PreferredSourceEdz); }
        catch (Exception exception)
        {
            throw new OfficialExportException("BRIDGE101", "A selected source EDZ is unavailable.", part.PreferredSourceEdz, exception);
        }
        if (!source.Exists || source.Length != part.SourceFileSize || source.LastWriteTimeUtc.Ticks != part.SourceLastWriteTimeUtcTicks)
            throw new OfficialExportException("BRIDGE101", "A selected source EDZ changed after selection. Refresh the selection and try again.",
                part.PreferredSourceEdz + Environment.NewLine +
                "Expected=" + part.SourceFileSize + "/" + part.SourceLastWriteTimeUtcTicks + Environment.NewLine +
                "Actual=" + (source.Exists ? source.Length.ToString(CultureInfo.InvariantCulture) + "/" + source.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) : "missing"));
    }

    private void EnsureRuntime()
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        if (_application != null) return;
        if (!Directory.Exists(_variantBinDirectory)) throw new DirectoryNotFoundException(_variantBinDirectory);
        if (!Directory.Exists(_platformBinDirectory)) throw new DirectoryNotFoundException(_platformBinDirectory);
        _application = new EplanApplication
        {
            EplanBinFolder = _variantBinDirectory,
            SystemConfiguration = _systemConfigurationScheme
        };
        try
        {
            _application.Init(string.Empty, false, false);
            _partsService = new PartsService();
            _partsManagement = new MDPartsManagement();
        }
        catch
        {
            try { _application.Exit(); } catch { }
            _application = null;
            throw;
        }
    }

    private void VerifyPathIsolation()
    {
        foreach (var variable in IsolationVariables)
        {
            var resolved = PathMap.SubstitutePath("$(" + variable + ")");
            if (!IsPathWithin(_temporaryRoot, resolved))
                throw new OfficialExportException("BRIDGE103", "The isolated EPLAN path safety gate rejected the operation.", variable + "=" + resolved);
        }
    }

    private void VerifySupportedRuntimeVersion()
    {
        var runtimeVersion = SafeRead(() => _application!.Version);
        var apiVersion = FileVersionInfo.GetVersionInfo(typeof(PartsService).Assembly.Location).FileVersion ?? string.Empty;
        if (!IsVerifiedEplanVersion(runtimeVersion) || !IsVerifiedEplanVersion(apiVersion))
            throw new OfficialExportException("BRIDGE002", "The EPLAN Bridge requires the verified EPLAN 2.9 runtime and API.",
                "Runtime=" + runtimeVersion + "; API=" + apiVersion);
    }

    private bool VerifyConverterRegistration()
    {
        var platformRoot = Directory.GetParent(_platformBinDirectory)?.FullName;
        var registrationFile = platformRoot == null ? string.Empty : Path.Combine(platformRoot, "Cfg", "regPlatform.xml");
        if (!File.Exists(registrationFile)) return false;
        return File.ReadAllText(registrationFile).IndexOf("IXPartsImportExportEdz:XPamExport,XPamImport", StringComparison.Ordinal) >= 0;
    }

    private string CreateAndCloseDatabase(string databasePath)
    {
        if (File.Exists(databasePath)) throw new InvalidOperationException("Refusing to overwrite a temporary database: " + databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
        MDPartsDatabase? database = null;
        try
        {
            database = _partsManagement!.CreateDatabase(databasePath);
            if (database == null || !database.IsOpen) throw new InvalidOperationException("MDPartsManagement.CreateDatabase did not return an open database.");
            return databasePath;
        }
        finally { CloseAndDispose(database); }
    }

    private List<PartRow> ReadPartRows(string databasePath)
    {
        MDPartsDatabase? database = null;
        MDPart[] parts = new MDPart[0];
        try
        {
            database = _partsManagement!.OpenDatabase(databasePath);
            parts = database.Parts;
            return parts.Select(part => new PartRow
            {
                Manufacturer = ReadProperty(part.Properties.ARTICLE_MANUFACTURER),
                PartNumber = part.PartNr ?? string.Empty,
                Variant = NormalizeVariant(part.Variant),
                PictureReferences = ReadProperty(part.Properties.ARTICLE_PICTUREFILE),
                MacroReferences = ReadProperty(part.Properties.ARTICLE_GROUPSYMBOLMACRO)
            }).ToList();
        }
        finally
        {
            foreach (var part in parts) part.Dispose();
            CloseAndDispose(database);
        }
    }

    private static bool Matches(PartRow row, OfficialSelectedPart request) =>
        string.Equals(row.PartNumber.Trim(), request.PartNumber.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(NormalizeVariant(row.Variant), NormalizeVariant(request.Variant), StringComparison.OrdinalIgnoreCase) &&
        (string.IsNullOrWhiteSpace(request.Manufacturer) || string.Equals(row.Manufacturer.Trim(), request.Manufacturer.Trim(), StringComparison.OrdinalIgnoreCase));

    private bool VerifyPictureAndMacroResources(IEnumerable<PartRow> rows, ICollection<string> diagnostics)
    {
        foreach (var row in rows)
        {
            foreach (var item in SplitReferences(row.PictureReferences).Select(value => new { Variable = "MD_IMG", Value = value })
                .Concat(SplitReferences(row.MacroReferences).Select(value => new { Variable = "MD_MACROS", Value = value })))
            {
                var resolved = PathMap.SubstitutePath(item.Value);
                if (!Path.IsPathRooted(resolved)) resolved = Path.Combine(PathMap.SubstitutePath("$(" + item.Variable + ")"), resolved);
                if (!IsPathWithin(_temporaryRoot, resolved) || !File.Exists(resolved))
                    diagnostics.Add("Missing or non-isolated referenced resource: " + resolved);
            }
        }
        return diagnostics.Count == 0;
    }

    private static IEnumerable<string> SplitReferences(string value) => string.IsNullOrWhiteSpace(value)
        ? Enumerable.Empty<string>()
        : value.Split(new[] { " || " }, StringSplitOptions.RemoveEmptyEntries).Select(item => item.Trim()).Where(item => item.Length > 0);

    private static string ReadProperty(MDPropertyValue value)
    {
        if (value == null || value.IsEmpty) return string.Empty;
        if (!value.Definition.IsIndexed) return value.ToString() ?? string.Empty;
        return string.Join(" || ", value.Indexes.Select(index => value[index]).Where(item => !item.IsEmpty).Select(item => item.ToString()));
    }

    private static void PublishAtomically(string temporaryOutput, string optionsOutputPath)
    {
        if (string.IsNullOrWhiteSpace(optionsOutputPath)) return;
        var destination = Path.GetFullPath(optionsOutputPath);
        if (File.Exists(destination)) throw Invalid("Refusing to overwrite an existing output file.");
        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) throw Invalid("The selected output directory does not exist.");
        var sibling = Path.Combine(directory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.Copy(temporaryOutput, sibling, false);
            using (var stream = new FileStream(sibling, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) stream.Flush(true);
            File.Move(sibling, destination);
        }
        finally
        {
            if (File.Exists(sibling)) File.Delete(sibling);
        }
    }

    private static void Report(Action<OfficialExportProgress> callback, Stopwatch timer, string stage, int current, int total, string source, string part, string message) =>
        callback(new OfficialExportProgress { Stage = stage, Current = current, Total = total, CurrentSource = source, CurrentPart = part, Message = message, ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds });

    private static void ThrowIfCancelled(Func<bool> isCancellationRequested)
    {
        if (isCancellationRequested()) throw new OperationCanceledException();
    }

    private static void EnsureNonEmptyFile(string path, string code, string message)
    {
        if (!File.Exists(path) || new FileInfo(path).Length <= 0) throw new OfficialExportException(code, message, path);
    }

    private static string NormalizeVariant(string value) => string.IsNullOrWhiteSpace(value) ? "1" : value.Trim();
    private static bool IsVerifiedEplanVersion(string value) =>
        string.Equals((value ?? string.Empty).Trim().Replace(',', '.').Split(' ')[0], "2.9.4.14642", StringComparison.Ordinal);
    private static string DescribePart(OfficialSelectedPart part) => part.Manufacturer + " | " + part.PartNumber + " | " + NormalizeVariant(part.Variant) + " | " + part.StablePartIdentity;
    private static OfficialExportException Invalid(string detail) => new OfficialExportException("BRIDGE103", "The selected-parts export request is invalid.", detail);
    private static string MakeSafeName(string value) => new string((value ?? string.Empty).Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character).ToArray());
    private static string SafeRead(Func<string> getter) { try { return getter() ?? string.Empty; } catch (Exception exception) { return "<unavailable: " + exception.Message + ">"; } }
    private static string FlattenException(Exception exception) { var values = new List<string>(); for (var current = exception; current != null; current = current.InnerException) values.Add(current.GetType().Name + ": " + current.Message); return string.Join(" --> ", values); }

    private static bool IsPathWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static long MeasureDirectoryBytes(string path)
    {
        try { return Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length); }
        catch { return -1; }
    }

    private static string TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return string.Empty;
            var reparse = Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)
                .Select(item => new FileInfo(item)).FirstOrDefault(item => (item.Attributes & FileAttributes.ReparsePoint) != 0);
            if (reparse != null) throw new IOException("Cleanup rejected a reparse point: " + reparse.FullName);
            Directory.Delete(path, true);
            return Directory.Exists(path) ? "The operation workspace still exists after cleanup." : string.Empty;
        }
        catch (Exception exception) { return "BRIDGE401 CleanupWarning: " + FlattenException(exception); }
    }

    private static void TryDeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, Path.ChangeExtension(databasePath, ".ldb") })
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    private static void CloseAndDispose(MDPartsDatabase? database)
    {
        if (database == null) return;
        try { if (database.IsOpen) database.Close(); }
        finally { database.Dispose(); }
    }

    private static void DisposeValue(object? value)
    {
        var disposable = value as IDisposable;
        if (disposable != null) disposable.Dispose();
    }

    private sealed class PartRow
    {
        public string Manufacturer { get; set; } = string.Empty;
        public string PartNumber { get; set; } = string.Empty;
        public string Variant { get; set; } = string.Empty;
        public string PictureReferences { get; set; } = string.Empty;
        public string MacroReferences { get; set; } = string.Empty;
    }
}
