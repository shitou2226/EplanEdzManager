using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Eplan.EplApi.Base;
using Eplan.EplApi.HEServices;
using Eplan.EplApi.MasterData;
using CommandLineInterpreter = Eplan.EplApi.ApplicationFramework.CommandLineInterpreter;
using EplanApplication = Eplan.EplApi.System.EplApplication;

namespace EplanEdzManager.EplanApi;

public static class Phase15SmokeRunner
{
    private const string EdzConverter = "IXPartsImportExportEdz";
    private static readonly string[] IsolationVariables =
    {
        "EPLAN_DATA",
        "CFG_USER",
        "CFG_STATION",
        "CFG_COMPANY",
        "MD_PARTS",
        "MD_IMG",
        "MD_MACROS",
        "MD_DOCUMENTS",
        "MD_MECHANICALMODELS"
    };

    public static int Execute(Phase15Options options)
    {
        ValidateOptions(options);

        var report = new EnvironmentReport
        {
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ExecuteRequested = options.ExecuteMutatingSmokeTest,
            TemporaryRoot = options.TemporaryRoot,
            SourceEdzPath = options.SourceEdzPath,
            ProcessArchitecture = Environment.Is64BitProcess ? "x64" : "x86",
            Framework = "net472; CLR " + Environment.Version
        };

        Directory.CreateDirectory(options.OutputDirectory);
        EplanApplication? application = null;
        PartsService? partsService = null;
        MDPartsManagement? partsManagement = null;
        string? previousPartsDatabase = null;

        try
        {
            report.SourceSha256Before = RunRequiredStep(
                report,
                "Source EDZ SHA-256 before",
                () => ComputeSha256(options.SourceEdzPath),
                hash => hash);

            application = RunRequiredStep(
                report,
                "Initialize EPLAN 2.9 offline runtime",
                () => InitializeRuntime(options),
                app => "Version=" + SafeRead(() => app.Version) + "; Variant=" + SafeRead(() => app.Variant));
            report.RuntimeInitialized = true;
            report.Version = SafeRead(() => application.Version);
            report.Variant = SafeRead(() => application.Variant);
            report.License = SafeRead(() => application.License);

            RunRequiredStep(
                report,
                "Inventory loaded API assemblies",
                () => InventoryAssemblies(report),
                count => count + " assemblies recorded");

            RunRequiredStep(
                report,
                "Verify isolated EPLAN path mappings",
                () => VerifyPathIsolation(options, report),
                _ => "All mutable settings and master-data paths resolve under the unique temporary root.");

            partsService = RunRequiredStep(
                report,
                "Construct PartsService",
                () => new PartsService(),
                _ => typeof(PartsService).FullName ?? "PartsService");
            report.AvailableApi.Add("Eplan.EplApi.HEServices.PartsService");

            partsManagement = RunRequiredStep(
                report,
                "Construct MDPartsManagement",
                () => new MDPartsManagement(),
                _ => typeof(MDPartsManagement).FullName ?? "MDPartsManagement");
            report.AvailableApi.Add("Eplan.EplApi.MasterData.MDPartsManagement");
            report.AvailableApi.Add("Eplan.EplApi.MasterData.MDPartsDatabase");

            var partsManagementActionAvailable = RunRequiredStep(
                report,
                "Probe partsmanagementapi action",
                VerifyPartsManagementAction,
                available => available ? "partsmanagementapi is executable." : "partsmanagementapi is not executable.");
            if (partsManagementActionAvailable)
            {
                report.AvailableApi.Add("partsmanagementapi action");
            }
            else
            {
                AddMissingCapability(
                    report,
                    "partsmanagementapi could not be confirmed executable: IsExecutable(\"partsmanagementapi\") returned false and no EPLAN 2.9 parameter contract was found; the smoke test continues through the documented PartsService API.");
            }

            RunRequiredStep(
                report,
                "Verify IXPartsImportExportEdz registration",
                () => VerifyConverterRegistration(options.EplanPlatformBinDirectory),
                available => available
                    ? "IXPartsImportExportEdz is registered for XPamImport and XPamExport."
                    : "IXPartsImportExportEdz registration was not found.");
            report.AvailableApi.Add("IXPartsImportExportEdz (XPamImport, XPamExport)");

            if (!options.ExecuteMutatingSmokeTest)
            {
                throw new InvalidOperationException(
                    "Import/export was not executed because --execute was not supplied. Capability-only mode is intentionally FAIL for Phase 1.5.");
            }

            previousPartsDatabase = SafeRead(() => partsService.PartsDatabase);
            ExecuteRoundTrips(options, report, partsService, partsManagement);

            if (report.RoundTrips.Count != options.ExportRequests.Count ||
                report.RoundTrips.Any(roundTrip =>
                    !roundTrip.ExportCreated ||
                    !roundTrip.OnlyRequestedPartNumbersPresent ||
                    !roundTrip.MetadataMatches ||
                    !roundTrip.ResourceReferencesMatch ||
                    !roundTrip.ReferencedResourceFilesExist))
            {
                throw new InvalidOperationException("One or more required EDZ round-trip assertions failed.");
            }

            report.OverallVerdict = "PASS";
        }
        catch (Exception exception)
        {
            report.OverallVerdict = "FAIL";
            AddMissingCapability(report, ClassifyFailure(exception));
            AddFailedStepIfNeeded(report, "Uncaught Phase 1.5 failure", exception);
        }
        finally
        {
            if (partsService != null && previousPartsDatabase != null)
            {
                AddSkippedStep(
                    report,
                    "Restore PartsService database setting",
                    "The entire EPLAN system configuration is disposable and every writable path passed the temporary-root safety gate; restoring a database setting would only reopen an unnecessary temporary database.");
            }

            DisposeIfPossible(partsManagement, report, "Dispose MDPartsManagement");
            DisposeIfPossible(partsService, report, "Dispose PartsService");

            if (application != null)
            {
                TryStep(report, "Exit EPLAN offline runtime", application.Exit, "EplApplication.Exit completed on the STA main thread.");
            }

            TryCaptureSourceIntegrity(options, report);
            TryCleanup(options, report);
            report.FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            WriteReport(options.OutputDirectory, report);
        }

        return string.Equals(report.OverallVerdict, "PASS", StringComparison.Ordinal) ? 0 : 1;
    }

    private static EplanApplication InitializeRuntime(Phase15Options options)
    {
        var application = new EplanApplication
        {
            EplanBinFolder = options.EplanVariantBinDirectory,
            SystemConfiguration = options.SystemConfigurationScheme
        };

        try
        {
            application.Init(string.Empty, false, false);
            return application;
        }
        catch
        {
            try
            {
                application.Exit();
            }
            catch
            {
                // Preserve the initialization exception. Cleanup is best effort here.
            }

            throw;
        }
    }

    private static int InventoryAssemblies(EnvironmentReport report)
    {
        var assemblies = new[]
        {
            typeof(CommandLineInterpreter).Assembly,
            typeof(PathMap).Assembly,
            typeof(PartsService).Assembly,
            typeof(MDPartsManagement).Assembly,
            typeof(EplanApplication).Assembly
        }
        .GroupBy(assembly => assembly.FullName, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
        .ToArray();

        foreach (var assembly in assemblies)
        {
            var location = assembly.Location;
            var versionInfo = FileVersionInfo.GetVersionInfo(location);
            report.Assemblies.Add(new AssemblyRecord
            {
                Assembly = assembly.GetName().Name ?? string.Empty,
                Location = location,
                AssemblyVersion = assembly.GetName().Version?.ToString() ?? string.Empty,
                FileVersion = versionInfo.FileVersion ?? string.Empty,
                ProductVersion = versionInfo.ProductVersion ?? string.Empty
            });
        }

        report.AvailableApi.Add("Eplan.EplApi.System.EplApplication");
        report.AvailableApi.Add("Eplan.EplApi.Base.PathMap");
        return assemblies.Length;
    }

    private static bool VerifyPathIsolation(Phase15Options options, EnvironmentReport report)
    {
        var allIsolated = true;
        foreach (var variable in IsolationVariables)
        {
            var record = new PathIsolationRecord { Variable = variable };
            try
            {
                record.ResolvedPath = PathMap.SubstitutePath("$(" + variable + ")");
                record.IsUnderTemporaryRoot = IsPathWithin(options.TemporaryRoot, record.ResolvedPath);
                allIsolated &= record.IsUnderTemporaryRoot;
            }
            catch (Exception exception)
            {
                record.Error = FlattenException(exception);
                allIsolated = false;
            }

            report.PathIsolation.Add(record);
        }

        if (!allIsolated)
        {
            throw new InvalidOperationException(
                "Safety gate rejected the run because at least one EPLAN settings/master-data path is outside the temporary root.");
        }

        return true;
    }

    private static bool VerifyPartsManagementAction()
    {
        var interpreter = new CommandLineInterpreter(true);
        return interpreter.IsExecutable("partsmanagementapi");
    }

    private static bool VerifyConverterRegistration(string platformBinDirectory)
    {
        var platformRoot = Directory.GetParent(platformBinDirectory)?.FullName;
        var registrationFile = platformRoot == null
            ? string.Empty
            : Path.Combine(platformRoot, "Cfg", "regPlatform.xml");
        if (!File.Exists(registrationFile))
        {
            throw new FileNotFoundException("EPLAN converter registration file was not found.", registrationFile);
        }

        var registration = File.ReadAllText(registrationFile);
        if (registration.IndexOf("IXPartsImportExportEdz:XPamExport,XPamImport", StringComparison.Ordinal) < 0)
        {
            throw new InvalidOperationException("IXPartsImportExportEdz was not registered for XPamExport and XPamImport.");
        }

        return true;
    }

    private static void ExecuteRoundTrips(
        Phase15Options options,
        EnvironmentReport report,
        PartsService partsService,
        MDPartsManagement partsManagement)
    {
        var sourceDatabasePath = Path.Combine(options.TemporaryRoot, "Databases", "ImportedSource.mdb");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceDatabasePath) ?? options.TemporaryRoot);

        RunRequiredStep(
            report,
            "Create temporary source parts database",
            () => CreateAndCloseDatabase(partsManagement, sourceDatabasePath),
            path => path);

        RunRequiredStep(
            report,
            "Select temporary source parts database",
            () => partsService.PartsDatabase = sourceDatabasePath,
            "PartsService now targets only the temporary database.");

        RunRequiredStep(
            report,
            "Import source EDZ with IXPartsImportExportEdz",
            () => partsService.ImportPartsListToSystem(options.SourceEdzPath, EdzConverter),
            "Imported into " + sourceDatabasePath);

        var sourcePartCount = RunRequiredStep(
            report,
            "Query imported source parts",
            () => CountParts(partsManagement, sourceDatabasePath),
            count => count + " parts found");

        foreach (var request in options.ExportRequests)
        {
            ExecuteSingleRoundTrip(
                options,
                report,
                partsService,
                partsManagement,
                sourceDatabasePath,
                sourcePartCount,
                request);
        }
    }

    private static void ExecuteSingleRoundTrip(
        Phase15Options options,
        EnvironmentReport report,
        PartsService partsService,
        MDPartsManagement partsManagement,
        string sourceDatabasePath,
        int sourcePartCount,
        PartExportRequest request)
    {
        var record = new PartRoundTripRecord
        {
            TestName = request.Name,
            PartNumber = request.PartNumber,
            Filter = "partnr='" + EscapeSqlLiteral(request.PartNumber) + "'",
            OutputEdzPath = Path.Combine(options.OutputDirectory, request.OutputFileName),
            SourceDatabasePartCount = sourcePartCount
        };
        report.RoundTrips.Add(record);

        partsService.PartsDatabase = sourceDatabasePath;
        record.BeforeExport = RunRequiredStep(
            report,
            request.Name + ": query requested part",
            () => ReadPart(partsManagement, sourceDatabasePath, request.PartNumber),
            snapshot => snapshot.PartNumber + "/" + snapshot.Variant);

        if (File.Exists(record.OutputEdzPath))
        {
            File.Delete(record.OutputEdzPath);
        }

        RunRequiredStep(
            report,
            request.Name + ": export filtered EDZ",
            () => partsService.ExportPartsListFromSystem(record.OutputEdzPath, EdzConverter, record.Filter),
            "Documented PartsService filter used: " + record.Filter);

        record.ExportCreated = File.Exists(record.OutputEdzPath);
        record.ExportSizeBytes = record.ExportCreated ? new FileInfo(record.OutputEdzPath).Length : 0;
        if (!record.ExportCreated || record.ExportSizeBytes == 0)
        {
            throw new InvalidOperationException("EPLAN returned from export without creating a non-empty EDZ: " + record.OutputEdzPath);
        }

        var roundTripDatabasePath = Path.Combine(
            options.TemporaryRoot,
            "Databases",
            "RoundTrip-" + MakeSafeFileName(request.PartNumber) + ".mdb");
        CreateAndCloseDatabase(partsManagement, roundTripDatabasePath);
        partsService.PartsDatabase = roundTripDatabasePath;

        RunRequiredStep(
            report,
            request.Name + ": re-import generated EDZ",
            () => partsService.ImportPartsListToSystem(record.OutputEdzPath, EdzConverter),
            "Re-imported into a second temporary database.");

        var importedKeys = ReadPartKeys(partsManagement, roundTripDatabasePath);
        record.RoundTripDatabasePartCount = importedKeys.Count;
        record.ImportedPartKeys.AddRange(importedKeys);
        record.OnlyRequestedPartNumbersPresent = importedKeys.Count > 0 &&
            importedKeys.All(key => key.StartsWith(request.PartNumber + "/", StringComparison.Ordinal));
        if (!record.OnlyRequestedPartNumbersPresent)
        {
            record.Differences.Add("Generated EDZ contains a part number other than the requested value, or contains no parts.");
        }

        record.AfterImport = ReadPart(partsManagement, roundTripDatabasePath, request.PartNumber);
        CompareSnapshots(record);
        VerifyReferencedResources(record, options.TemporaryRoot);

        var assertionFailures = new List<string>();
        if (!record.OnlyRequestedPartNumbersPresent)
        {
            assertionFailures.Add("part-number filter");
        }

        if (!record.MetadataMatches)
        {
            assertionFailures.Add("metadata comparison");
        }

        if (!record.ResourceReferencesMatch || !record.ReferencedResourceFilesExist)
        {
            assertionFailures.Add("resource reference verification");
        }

        RunRequiredStep(
            report,
            request.Name + ": validate round trip",
            () =>
            {
                if (assertionFailures.Count > 0)
                {
                    throw new InvalidOperationException("Failed assertions: " + string.Join(", ", assertionFailures));
                }

                return true;
            },
            _ => "Part, metadata, variant, and referenced resources passed round-trip checks.");
    }

    private static string CreateAndCloseDatabase(MDPartsManagement partsManagement, string databasePath)
    {
        if (File.Exists(databasePath))
        {
            throw new InvalidOperationException("Refusing to overwrite an existing temporary database: " + databasePath);
        }

        MDPartsDatabase? database = null;
        try
        {
            database = partsManagement.CreateDatabase(databasePath);
            if (database == null || !database.IsOpen)
            {
                throw new InvalidOperationException("MDPartsManagement.CreateDatabase did not return an open database.");
            }

            return databasePath;
        }
        finally
        {
            CloseAndDispose(database);
        }
    }

    private static int CountParts(MDPartsManagement partsManagement, string databasePath)
    {
        MDPartsDatabase? database = null;
        MDPart[] parts = Array.Empty<MDPart>();
        try
        {
            database = partsManagement.OpenDatabase(databasePath);
            parts = database.Parts;
            return parts.Length;
        }
        finally
        {
            DisposeParts(parts);
            CloseAndDispose(database);
        }
    }

    private static PartSnapshot ReadPart(MDPartsManagement partsManagement, string databasePath, string partNumber)
    {
        MDPartsDatabase? database = null;
        MDPart? part = null;
        try
        {
            database = partsManagement.OpenDatabase(databasePath);
            part = database.GetPart(partNumber);
            if (part == null)
            {
                throw new InvalidOperationException("Part was not found in temporary database: " + partNumber);
            }

            return new PartSnapshot
            {
                Manufacturer = ReadProperty(part.Properties.ARTICLE_MANUFACTURER),
                PartNumber = part.PartNr ?? string.Empty,
                Description = ReadProperty(part.Properties.ARTICLE_DESCR1),
                Variant = part.Variant ?? string.Empty,
                TypeNumber = ReadProperty(part.Properties.ARTICLE_TYPENR),
                OrderNumber = ReadProperty(part.Properties.ARTICLE_ORDERNR),
                PictureReferences = ReadProperty(part.Properties.ARTICLE_PICTUREFILE),
                MacroReferences = ReadProperty(part.Properties.ARTICLE_GROUPSYMBOLMACRO)
            };
        }
        finally
        {
            part?.Dispose();
            CloseAndDispose(database);
        }
    }

    private static List<string> ReadPartKeys(MDPartsManagement partsManagement, string databasePath)
    {
        MDPartsDatabase? database = null;
        MDPart[] parts = Array.Empty<MDPart>();
        try
        {
            database = partsManagement.OpenDatabase(databasePath);
            parts = database.Parts;
            return parts
                .Select(part => (part.PartNr ?? string.Empty) + "/" + (part.Variant ?? string.Empty))
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
        }
        finally
        {
            DisposeParts(parts);
            CloseAndDispose(database);
        }
    }

    private static string ReadProperty(MDPropertyValue value)
    {
        if (value == null || value.IsEmpty)
        {
            return string.Empty;
        }

        if (!value.Definition.IsIndexed)
        {
            return value.ToString() ?? string.Empty;
        }

        var values = new List<string>();
        foreach (var index in value.Indexes)
        {
            var indexedValue = value[index];
            if (!indexedValue.IsEmpty)
            {
                values.Add(indexedValue.ToString() ?? string.Empty);
            }
        }

        return string.Join(" || ", values);
    }

    private static void CompareSnapshots(PartRoundTripRecord record)
    {
        var before = record.BeforeExport ?? throw new InvalidOperationException("Missing pre-export snapshot.");
        var after = record.AfterImport ?? throw new InvalidOperationException("Missing post-import snapshot.");

        CompareField(record, "Manufacturer", before.Manufacturer, after.Manufacturer);
        CompareField(record, "PartNumber", before.PartNumber, after.PartNumber);
        CompareField(record, "Description", before.Description, after.Description);
        CompareField(record, "Variant", before.Variant, after.Variant);
        record.MetadataMatches = !record.Differences.Any(difference =>
            difference.StartsWith("Manufacturer", StringComparison.Ordinal) ||
            difference.StartsWith("PartNumber", StringComparison.Ordinal) ||
            difference.StartsWith("Description", StringComparison.Ordinal) ||
            difference.StartsWith("Variant", StringComparison.Ordinal));

        var pictureMatches = string.Equals(before.PictureReferences, after.PictureReferences, StringComparison.Ordinal);
        var macroMatches = string.Equals(before.MacroReferences, after.MacroReferences, StringComparison.Ordinal);
        record.ResourceReferencesMatch = pictureMatches && macroMatches;
        if (!pictureMatches)
        {
            record.Differences.Add("PictureReferences: before='" + before.PictureReferences + "', after='" + after.PictureReferences + "'.");
        }

        if (!macroMatches)
        {
            record.Differences.Add("MacroReferences: before='" + before.MacroReferences + "', after='" + after.MacroReferences + "'.");
        }
    }

    private static void VerifyReferencedResources(PartRoundTripRecord record, string temporaryRoot)
    {
        var snapshot = record.AfterImport ?? throw new InvalidOperationException("Missing post-import snapshot.");
        var references = new List<Tuple<string, string>>();
        references.AddRange(SplitReferences(snapshot.PictureReferences).Select(value => Tuple.Create("MD_IMG", value)));
        references.AddRange(SplitReferences(snapshot.MacroReferences).Select(value => Tuple.Create("MD_MACROS", value)));

        foreach (var reference in references)
        {
            var resolved = PathMap.SubstitutePath(reference.Item2);
            if (!Path.IsPathRooted(resolved))
            {
                var baseDirectory = PathMap.SubstitutePath("$(" + reference.Item1 + ")");
                resolved = Path.Combine(baseDirectory, resolved);
            }

            if (!IsPathWithin(temporaryRoot, resolved) || !File.Exists(resolved))
            {
                record.MissingResourceFiles.Add(resolved);
            }
        }

        record.ReferencedResourceFilesExist = record.MissingResourceFiles.Count == 0;
        if (!record.ReferencedResourceFilesExist)
        {
            record.Differences.Add("One or more picture/macro references did not resolve to a file inside the temporary workspace.");
        }
    }

    private static IEnumerable<string> SplitReferences(string references)
    {
        return string.IsNullOrWhiteSpace(references)
            ? Enumerable.Empty<string>()
            : references.Split(new[] { " || " }, StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => value.Length > 0);
    }

    private static void CompareField(PartRoundTripRecord record, string field, string before, string after)
    {
        if (!string.Equals(before, after, StringComparison.Ordinal))
        {
            record.Differences.Add(field + ": before='" + before + "', after='" + after + "'.");
        }
    }

    private static void CloseAndDispose(MDPartsDatabase? database)
    {
        if (database == null)
        {
            return;
        }

        try
        {
            if (database.IsOpen)
            {
                database.Close();
            }
        }
        finally
        {
            database.Dispose();
        }
    }

    private static void DisposeParts(IEnumerable<MDPart> parts)
    {
        foreach (var part in parts)
        {
            part.Dispose();
        }
    }

    private static void DisposeIfPossible(object? value, EnvironmentReport report, string stepName)
    {
        if (!(value is IDisposable disposable))
        {
            return;
        }

        TryStep(report, stepName, disposable.Dispose, "Disposed.");
    }

    private static void TryCaptureSourceIntegrity(Phase15Options options, EnvironmentReport report)
    {
        try
        {
            report.SourceSha256After = ComputeSha256(options.SourceEdzPath);
            report.SourceUnchanged = string.Equals(
                report.SourceSha256Before,
                report.SourceSha256After,
                StringComparison.OrdinalIgnoreCase);
            if (!report.SourceUnchanged)
            {
                report.OverallVerdict = "FAIL";
                AddMissingCapability(report, "Source EDZ integrity check failed: SHA-256 changed.");
            }

            AddPassedStep(report, "Source EDZ SHA-256 after", report.SourceSha256After);
        }
        catch (Exception exception)
        {
            report.OverallVerdict = "FAIL";
            AddFailedStep(report, "Source EDZ SHA-256 after", exception);
        }
    }

    private static void TryCleanup(Phase15Options options, EnvironmentReport report)
    {
        var expectedBase = Path.Combine(Path.GetTempPath(), "EplanEdzManager", "TestDatabase");
        try
        {
            if (!IsPathWithin(expectedBase, options.TemporaryRoot))
            {
                throw new InvalidOperationException("Cleanup safety gate rejected a path outside the expected temporary base.");
            }

            var reparsePoint = Directory
                .EnumerateFileSystemEntries(options.TemporaryRoot, "*", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .FirstOrDefault(info => (info.Attributes & FileAttributes.ReparsePoint) != 0);
            if (reparsePoint != null)
            {
                throw new InvalidOperationException("Cleanup safety gate found a reparse point: " + reparsePoint.FullName);
            }

            Directory.Delete(options.TemporaryRoot, true);
            report.TemporaryRootDeleted = !Directory.Exists(options.TemporaryRoot);
            if (!report.TemporaryRootDeleted)
            {
                throw new IOException("Temporary root still exists after cleanup.");
            }

            AddPassedStep(report, "Delete temporary workspace", options.TemporaryRoot);
        }
        catch (Exception exception)
        {
            report.TemporaryRootDeleted = false;
            report.OverallVerdict = "FAIL";
            AddMissingCapability(report, "Temporary workspace cleanup failed.");
            AddFailedStep(report, "Delete temporary workspace", exception);
        }
    }

    private static string ComputeSha256(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha256 = SHA256.Create())
        {
            var hash = sha256.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (var value in hash)
            {
                builder.Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }
    }

    private static bool IsPathWithin(string root, string candidate)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string EscapeSqlLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    private static void WriteReport(string outputDirectory, EnvironmentReport report)
    {
        Directory.CreateDirectory(outputDirectory);
        var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 100 };
        var path = Path.Combine(outputDirectory, "EnvironmentReport.json");
        File.WriteAllText(path, serializer.Serialize(report), new UTF8Encoding(false));
    }

    private static T RunRequiredStep<T>(
        EnvironmentReport report,
        string name,
        Func<T> action,
        Func<T, string> detail)
    {
        var step = StartStep(name);
        report.Steps.Add(step);
        try
        {
            var result = action();
            step.Status = "PASS";
            step.Detail = detail(result);
            return result;
        }
        catch (Exception exception)
        {
            FillFailure(step, exception);
            throw;
        }
        finally
        {
            step.FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private static void RunRequiredStep(
        EnvironmentReport report,
        string name,
        Action action,
        string detail)
    {
        RunRequiredStep(report, name, () =>
        {
            action();
            return true;
        }, _ => detail);
    }

    private static void TryStep(EnvironmentReport report, string name, Action action, string detail)
    {
        var step = StartStep(name);
        report.Steps.Add(step);
        try
        {
            action();
            step.Status = "PASS";
            step.Detail = detail;
        }
        catch (Exception exception)
        {
            report.OverallVerdict = "FAIL";
            FillFailure(step, exception);
        }
        finally
        {
            step.FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        }
    }

    private static StepRecord StartStep(string name)
    {
        return new StepRecord
        {
            Name = name,
            Status = "RUNNING",
            StartedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    private static void AddPassedStep(EnvironmentReport report, string name, string detail)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        report.Steps.Add(new StepRecord
        {
            Name = name,
            Status = "PASS",
            StartedUtc = now,
            FinishedUtc = now,
            Detail = detail
        });
    }

    private static void AddSkippedStep(EnvironmentReport report, string name, string detail)
    {
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        report.Steps.Add(new StepRecord
        {
            Name = name,
            Status = "SKIPPED",
            StartedUtc = now,
            FinishedUtc = now,
            Detail = detail
        });
    }

    private static void AddFailedStep(EnvironmentReport report, string name, Exception exception)
    {
        var step = StartStep(name);
        FillFailure(step, exception);
        step.FinishedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        report.Steps.Add(step);
    }

    private static void AddFailedStepIfNeeded(EnvironmentReport report, string name, Exception exception)
    {
        var last = report.Steps.LastOrDefault();
        if (last == null || !string.Equals(last.Status, "FAIL", StringComparison.Ordinal))
        {
            AddFailedStep(report, name, exception);
        }
    }

    private static void FillFailure(StepRecord step, Exception exception)
    {
        step.Status = "FAIL";
        step.ExceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        step.ExceptionMessage = FlattenException(exception);
        step.ExceptionStackTrace = exception.ToString();
    }

    private static string FlattenException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current != null; current = current.InnerException)
        {
            messages.Add(current.GetType().Name + ": " + current.Message);
        }

        return string.Join(" --> ", messages);
    }

    private static string ClassifyFailure(Exception exception)
    {
        var text = FlattenException(exception);
        if (text.IndexOf("license", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("Lizenz", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "EPLAN API runtime/license capability is unavailable: " + text;
        }

        if (text.IndexOf("database", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("Datenbank", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "Temporary parts database capability failed: " + text;
        }

        if (text.IndexOf("IXPartsImportExportEdz", StringComparison.OrdinalIgnoreCase) >= 0 ||
            text.IndexOf("converter", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "EDZ converter capability failed: " + text;
        }

        return text;
    }

    private static void AddMissingCapability(EnvironmentReport report, string value)
    {
        if (!report.MissingCapabilities.Contains(value, StringComparer.Ordinal))
        {
            report.MissingCapabilities.Add(value);
        }
    }

    private static string SafeRead(Func<string> getter)
    {
        try
        {
            return getter() ?? string.Empty;
        }
        catch (Exception exception)
        {
            return "<unavailable: " + exception.Message + ">";
        }
    }

    private static void ValidateOptions(Phase15Options options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        RequireDirectory(options.EplanVariantBinDirectory, nameof(options.EplanVariantBinDirectory));
        RequireDirectory(options.EplanPlatformBinDirectory, nameof(options.EplanPlatformBinDirectory));
        RequireDirectory(options.TemporaryRoot, nameof(options.TemporaryRoot));
        RequireDirectory(options.OutputDirectory, nameof(options.OutputDirectory));
        if (!File.Exists(options.SourceEdzPath))
        {
            throw new FileNotFoundException("Source EDZ was not found.", options.SourceEdzPath);
        }

        if (options.ExportRequests.Count == 0)
        {
            throw new ArgumentException("At least one export request is required.", nameof(options));
        }
    }

    private static void RequireDirectory(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(parameterName + " does not exist: " + path);
        }
    }
}
