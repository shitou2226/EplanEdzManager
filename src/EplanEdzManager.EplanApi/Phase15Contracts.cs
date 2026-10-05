using System;
using System.Collections.Generic;

namespace EplanEdzManager.EplanApi;

public sealed class Phase15Options
{
    public string EplanVariantBinDirectory { get; set; } = string.Empty;

    public string EplanPlatformBinDirectory { get; set; } = string.Empty;

    public string TemporaryRoot { get; set; } = string.Empty;

    public string OutputDirectory { get; set; } = string.Empty;

    public string SourceEdzPath { get; set; } = string.Empty;

    public string SystemConfigurationScheme { get; set; } = "EplanEdzManagerTemp";

    public bool ExecuteMutatingSmokeTest { get; set; }

    public List<PartExportRequest> ExportRequests { get; } = new List<PartExportRequest>();
}

public sealed class PartExportRequest
{
    public string Name { get; set; } = string.Empty;

    public string PartNumber { get; set; } = string.Empty;

    public string OutputFileName { get; set; } = string.Empty;
}

public sealed class EnvironmentReport
{
    public string SchemaVersion { get; set; } = "1.0";

    public string StartedUtc { get; set; } = string.Empty;

    public string FinishedUtc { get; set; } = string.Empty;

    public string OverallVerdict { get; set; } = "FAIL";

    public string Version { get; set; } = string.Empty;

    public string Variant { get; set; } = string.Empty;

    public string License { get; set; } = string.Empty;

    public string ProcessArchitecture { get; set; } = string.Empty;

    public string Framework { get; set; } = string.Empty;

    public bool ExecuteRequested { get; set; }

    public bool RuntimeInitialized { get; set; }

    public string TemporaryRoot { get; set; } = string.Empty;

    public bool TemporaryRootDeleted { get; set; }

    public string SourceEdzPath { get; set; } = string.Empty;

    public string SourceSha256Before { get; set; } = string.Empty;

    public string SourceSha256After { get; set; } = string.Empty;

    public bool SourceUnchanged { get; set; }

    public List<AssemblyRecord> Assemblies { get; } = new List<AssemblyRecord>();

    public List<string> AvailableApi { get; } = new List<string>();

    public List<string> MissingCapabilities { get; } = new List<string>();

    public List<PathIsolationRecord> PathIsolation { get; } = new List<PathIsolationRecord>();

    public List<StepRecord> Steps { get; } = new List<StepRecord>();

    public List<PartRoundTripRecord> RoundTrips { get; } = new List<PartRoundTripRecord>();
}

public sealed class AssemblyRecord
{
    public string Assembly { get; set; } = string.Empty;

    public string Location { get; set; } = string.Empty;

    public string AssemblyVersion { get; set; } = string.Empty;

    public string FileVersion { get; set; } = string.Empty;

    public string ProductVersion { get; set; } = string.Empty;
}

public sealed class PathIsolationRecord
{
    public string Variable { get; set; } = string.Empty;

    public string ResolvedPath { get; set; } = string.Empty;

    public bool IsUnderTemporaryRoot { get; set; }

    public string Error { get; set; } = string.Empty;
}

public sealed class StepRecord
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string StartedUtc { get; set; } = string.Empty;

    public string FinishedUtc { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public string ExceptionType { get; set; } = string.Empty;

    public string ExceptionMessage { get; set; } = string.Empty;

    public string ExceptionStackTrace { get; set; } = string.Empty;
}

public sealed class PartRoundTripRecord
{
    public string TestName { get; set; } = string.Empty;

    public string PartNumber { get; set; } = string.Empty;

    public string Filter { get; set; } = string.Empty;

    public string OutputEdzPath { get; set; } = string.Empty;

    public bool ExportCreated { get; set; }

    public long ExportSizeBytes { get; set; }

    public int SourceDatabasePartCount { get; set; }

    public int RoundTripDatabasePartCount { get; set; }

    public bool OnlyRequestedPartNumbersPresent { get; set; }

    public bool MetadataMatches { get; set; }

    public bool ResourceReferencesMatch { get; set; }

    public bool ReferencedResourceFilesExist { get; set; }

    public PartSnapshot? BeforeExport { get; set; }

    public PartSnapshot? AfterImport { get; set; }

    public List<string> ImportedPartKeys { get; } = new List<string>();

    public List<string> Differences { get; } = new List<string>();

    public List<string> MissingResourceFiles { get; } = new List<string>();
}

public sealed class PartSnapshot
{
    public string Manufacturer { get; set; } = string.Empty;

    public string PartNumber { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Variant { get; set; } = string.Empty;

    public string TypeNumber { get; set; } = string.Empty;

    public string OrderNumber { get; set; } = string.Empty;

    public string PictureReferences { get; set; } = string.Empty;

    public string MacroReferences { get; set; } = string.Empty;
}
