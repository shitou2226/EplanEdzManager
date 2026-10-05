using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Eplan.EplApi.Base;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;
using EplanApplication = Eplan.EplApi.System.EplApplication;

namespace EplanEdzManager.EplanApi;

public sealed class Phase5SmokeReport
{
    public string TargetDatabase { get; set; } = string.Empty;
    public string SourceEdz { get; set; } = string.Empty;
    public int BeforeCount { get; set; }
    public int AfterCount { get; set; }
    public int RepeatedAppendCount { get; set; }
    public bool ReopenedPartFound { get; set; }
    public bool MetadataVerified { get; set; }
    public bool PictureReferencesVerified { get; set; }
    public bool MacroReferencesVerified { get; set; }
    public List<string> VerifiedImportModes { get; } = new List<string>();
    public List<string> Diagnostics { get; } = new List<string>();
}

/// <summary>
/// Owns the EPLAN 2.9 official API boundary for explicit parts-database work.
/// It never executes SQL and never reads or changes the user's active database setting.
/// </summary>
public sealed partial class SafePartsDatabaseImporter : IDisposable
{
    private const string EdzConverter = "IXPartsImportExportEdz";
    private readonly string _variantBinDirectory;
    private readonly string _platformBinDirectory;
    private readonly string _temporaryRoot;
    private readonly string _systemConfigurationScheme;
    private readonly string _targetSystemConfigurationScheme;
    private readonly string _targetMasterDataRoot;
    private EplanApplication? _application;
    private PartsService? _partsService;
    private MDPartsManagement? _partsManagement;
    private bool _disposed;
    private string _activeScheme = string.Empty;

    public SafePartsDatabaseImporter(
        string variantBinDirectory,
        string platformBinDirectory,
        string temporaryRoot,
        string systemConfigurationScheme,
        string? targetSystemConfigurationScheme = null,
        string? targetMasterDataRoot = null)
    {
        _variantBinDirectory = Path.GetFullPath(variantBinDirectory ?? throw new ArgumentNullException(nameof(variantBinDirectory)));
        _platformBinDirectory = Path.GetFullPath(platformBinDirectory ?? throw new ArgumentNullException(nameof(platformBinDirectory)));
        _temporaryRoot = Path.GetFullPath(temporaryRoot ?? throw new ArgumentNullException(nameof(temporaryRoot)));
        _systemConfigurationScheme = systemConfigurationScheme ?? throw new ArgumentNullException(nameof(systemConfigurationScheme));
        _targetSystemConfigurationScheme = targetSystemConfigurationScheme ?? string.Empty;
        _targetMasterDataRoot = string.IsNullOrWhiteSpace(targetMasterDataRoot) ? string.Empty : Path.GetFullPath(targetMasterDataRoot);
    }

    public Phase5SmokeReport RunSmokeInvestigation(
        string targetDatabase,
        string selectedEdz,
        string expectedPartNumber,
        string expectedVariant)
    {
        if (!IsPathWithin(_temporaryRoot, targetDatabase))
            throw new InvalidOperationException("The smoke-test database must remain inside the unique temporary root.");
        if (!File.Exists(selectedEdz) || new FileInfo(selectedEdz).Length == 0)
            throw new FileNotFoundException("A non-empty validated SelectedParts.edz is required.", selectedEdz);

        EnsureRuntime();
        VerifyTemporaryMasterDataMappings();
        CreateAndCloseDatabase(targetDatabase);
        var report = new Phase5SmokeReport
        {
            TargetDatabase = Path.GetFullPath(targetDatabase),
            SourceEdz = Path.GetFullPath(selectedEdz),
            BeforeCount = CountParts(targetDatabase)
        };

        Import(targetDatabase, selectedEdz, PartsService.ImportMode.AppendNewRecords);
        report.VerifiedImportModes.Add(nameof(PartsService.ImportMode.AppendNewRecords));
        report.AfterCount = CountParts(targetDatabase);
        var snapshot = ReadPart(targetDatabase, expectedPartNumber, expectedVariant);
        report.ReopenedPartFound = snapshot != null;
        if (snapshot != null)
        {
            report.MetadataVerified = !string.IsNullOrWhiteSpace(snapshot.PartNumber) &&
                !string.IsNullOrWhiteSpace(snapshot.Manufacturer) &&
                string.Equals(NormalizeVariant(snapshot.Variant), NormalizeVariant(expectedVariant), StringComparison.OrdinalIgnoreCase);
            report.PictureReferencesVerified = VerifyResources("MD_IMG", snapshot.PictureReferences, report.Diagnostics);
            report.MacroReferencesVerified = VerifyResources("MD_MACROS", snapshot.MacroReferences, report.Diagnostics);
        }

        Import(targetDatabase, selectedEdz, PartsService.ImportMode.AppendNewRecords);
        report.RepeatedAppendCount = CountParts(targetDatabase);

        Import(targetDatabase, selectedEdz, PartsService.ImportMode.UpdateExistingRecords);
        report.VerifiedImportModes.Add(nameof(PartsService.ImportMode.UpdateExistingRecords));

        var combinedModeDatabase = Path.Combine(_temporaryRoot, "ImportMode-UpdateAndAppend.mdb");
        CreateAndCloseDatabase(combinedModeDatabase);
        Import(combinedModeDatabase, selectedEdz, PartsService.ImportMode.UpdateAndAppend);
        if (CountParts(combinedModeDatabase) == 0)
            throw new InvalidOperationException("UpdateAndAppend returned without adding the new part to a fresh database.");
        report.VerifiedImportModes.Add(nameof(PartsService.ImportMode.UpdateAndAppend));
        return report;
    }

    public void CreateEmptyIntegrationTestDatabase(string databasePath)
    {
        if (!IsPathWithin(_temporaryRoot, databasePath))
            throw new InvalidOperationException("Integration-test database creation is restricted to the unique temporary root.");
        EnsureRuntime();
        CreateAndCloseDatabase(databasePath);
    }

    public void CreateConflictIntegrationTestDatabase(string databasePath, string selectedEdz, string partNumber)
    {
        if (!IsPathWithin(_temporaryRoot, databasePath))
            throw new InvalidOperationException("Integration-test database creation is restricted to the unique temporary root.");
        EnsureRuntime();
        CreateAndCloseDatabase(databasePath);
        Import(databasePath, selectedEdz, PartsService.ImportMode.AppendNewRecords);
        MDPartsDatabase? database = null;
        MDPart? part = null;
        MDPartsDatabaseTransaction? transaction = null;
        try
        {
            database = _partsManagement!.OpenDatabase(databasePath);
            transaction = database.CreateTransaction();
            part = database.GetPart(partNumber) ?? throw new InvalidOperationException("Conflict fixture part was not found: " + partNumber);
            part.Properties.ARTICLE_TYPENR.Set("PHASE5-CONTROLLED-CONFLICT");
            transaction.Commit();
        }
        finally
        {
            part?.Dispose();
            transaction?.Dispose();
            CloseAndDispose(database);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseRuntime();
    }

    private void CloseRuntime()
    {
        (_partsManagement as IDisposable)?.Dispose();
        _partsManagement = null;
        (_partsService as IDisposable)?.Dispose();
        _partsService = null;
        if (_application != null)
        {
            try { _application.Exit(); } catch { }
            _application = null;
        }
        _activeScheme = string.Empty;
    }

    private void EnsureRuntime() => EnsureRuntime(_systemConfigurationScheme);

    private void EnsureRuntime(string scheme)
    {
        if (_disposed) throw new ObjectDisposedException(GetType().FullName);
        if (_application != null && string.Equals(_activeScheme, scheme, StringComparison.Ordinal)) return;
        if (_application != null) CloseRuntime();
        if (!Directory.Exists(_variantBinDirectory)) throw new DirectoryNotFoundException(_variantBinDirectory);
        if (!Directory.Exists(_platformBinDirectory)) throw new DirectoryNotFoundException(_platformBinDirectory);
        _application = new EplanApplication
        {
            EplanBinFolder = _variantBinDirectory,
            SystemConfiguration = scheme
        };
        try
        {
            _application.Init(string.Empty, false, false);
            _partsService = new PartsService();
            _partsManagement = new MDPartsManagement();
            _activeScheme = scheme;
        }
        catch
        {
            try { _application.Exit(); } catch { }
            _application = null;
            throw;
        }
    }

    private void VerifyTemporaryMasterDataMappings()
    {
        foreach (var variable in new[] { "EPLAN_DATA", "CFG_USER", "CFG_STATION", "CFG_COMPANY", "MD_PARTS", "MD_IMG", "MD_MACROS", "MD_DOCUMENTS", "MD_MECHANICALMODELS" })
        {
            var resolved = PathMap.SubstitutePath("$(" + variable + ")");
            if (!IsPathWithin(_temporaryRoot, resolved))
                throw new InvalidOperationException("Smoke-test isolation rejected " + variable + "=" + resolved);
        }
    }

    private void Import(string targetDatabase, string selectedEdz, PartsService.ImportMode mode)
    {
        _partsService!.PartsDatabase = Path.GetFullPath(targetDatabase);
        _partsService.ImportPartsListToSystem(Path.GetFullPath(selectedEdz), EdzConverter, string.Empty, mode);
    }

    private void CreateAndCloseDatabase(string databasePath)
    {
        if (File.Exists(databasePath)) throw new InvalidOperationException("Refusing to overwrite a temporary database: " + databasePath);
        MDPartsDatabase? database = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath));
            database = _partsManagement!.CreateDatabase(databasePath);
            if (database == null || !database.IsOpen)
                throw new InvalidOperationException("MDPartsManagement.CreateDatabase did not return an open database.");
        }
        finally { CloseAndDispose(database); }
    }

    private int CountParts(string databasePath)
    {
        MDPartsDatabase? database = null;
        MDPart[] parts = new MDPart[0];
        try
        {
            database = _partsManagement!.OpenDatabase(databasePath);
            parts = database.Parts;
            return parts.Length;
        }
        finally
        {
            foreach (var part in parts) part.Dispose();
            CloseAndDispose(database);
        }
    }

    private PartSnapshot? ReadPart(string databasePath, string partNumber, string variant)
    {
        MDPartsDatabase? database = null;
        MDPart[] parts = new MDPart[0];
        try
        {
            database = _partsManagement!.OpenDatabase(databasePath);
            parts = database.Parts;
            var match = parts.FirstOrDefault(part =>
                string.Equals((part.PartNr ?? string.Empty).Trim(), partNumber.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeVariant(part.Variant), NormalizeVariant(variant), StringComparison.OrdinalIgnoreCase));
            if (match == null) return null;
            return new PartSnapshot
            {
                Manufacturer = ReadProperty(match.Properties.ARTICLE_MANUFACTURER),
                PartNumber = match.PartNr ?? string.Empty,
                Variant = match.Variant ?? string.Empty,
                Description = ReadProperty(match.Properties.ARTICLE_DESCR1),
                TypeNumber = ReadProperty(match.Properties.ARTICLE_TYPENR),
                OrderNumber = ReadProperty(match.Properties.ARTICLE_ORDERNR),
                PictureReferences = ReadProperty(match.Properties.ARTICLE_PICTUREFILE),
                MacroReferences = ReadProperty(match.Properties.ARTICLE_GROUPSYMBOLMACRO)
            };
        }
        finally
        {
            foreach (var part in parts) part.Dispose();
            CloseAndDispose(database);
        }
    }

    private bool VerifyResources(string variable, string references, ICollection<string> diagnostics)
    {
        var values = SplitReferences(references).ToArray();
        if (values.Length == 0)
        {
            diagnostics.Add(variable + " reference was empty in the selected sample.");
            return false;
        }
        var passed = true;
        foreach (var value in values)
        {
            var resolved = PathMap.SubstitutePath(value);
            if (!Path.IsPathRooted(resolved)) resolved = Path.Combine(PathMap.SubstitutePath("$(" + variable + ")"), resolved);
            if (!IsPathWithin(_temporaryRoot, resolved) || !File.Exists(resolved))
            {
                passed = false;
                diagnostics.Add("Missing or non-isolated " + variable + " resource: " + resolved);
            }
        }
        return passed;
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

    private static string NormalizeVariant(string value) => string.IsNullOrWhiteSpace(value) ? "1" : value.Trim();

    private static bool IsPathWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate)) return false;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void CloseAndDispose(MDPartsDatabase? database)
    {
        if (database == null) return;
        try { if (database.IsOpen) database.Close(); }
        finally { database.Dispose(); }
    }

    private sealed class PartSnapshot
    {
        public string Manufacturer { get; set; } = string.Empty;
        public string PartNumber { get; set; } = string.Empty;
        public string Variant { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string TypeNumber { get; set; } = string.Empty;
        public string OrderNumber { get; set; } = string.Empty;
        public string PictureReferences { get; set; } = string.Empty;
        public string MacroReferences { get; set; } = string.Empty;
    }
}
