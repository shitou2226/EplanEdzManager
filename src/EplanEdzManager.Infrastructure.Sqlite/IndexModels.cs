namespace EplanEdzManager.Infrastructure.Sqlite;

public enum ResourceExistenceFilter
{
    Any,
    HasAny,
    AllPresent,
    Missing,
    None
}

public enum PartSearchField
{
    All,
    PartNumber,
    TypeNumber,
    Manufacturer,
    Description
}

public enum PartSearchMatchMode
{
    FullText,
    Contains,
    Exact
}

public enum PartSortColumn
{
    Relevance,
    Manufacturer,
    PartNumber,
    TypeNumber,
    Description,
    SourceEdz
}

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed record IndexedDirectory(
    long Id,
    string Path,
    bool Recursive,
    bool Enabled,
    DateTimeOffset AddedUtc,
    DateTimeOffset? LastScanUtc);

public sealed record IndexedFileState(
    long Id,
    long DirectoryId,
    string Path,
    long FileSize,
    long LastWriteUtcTicks,
    string Status,
    string? Error);

public sealed record IndexedResourceData(
    string? ResourceType,
    string? Name,
    string? RawLocator,
    string? ArchivePath,
    bool ExistsInArchive,
    long? Size,
    int ReferenceCount);

public sealed record IndexedPartData(
    string? Manufacturer,
    string? PartNumber,
    string? TypeNumber,
    string? OrderNumber,
    string? Description,
    string? ProductGroup,
    string? Variant,
    string? PackageKey,
    string RawMetadataReference,
    IReadOnlyList<IndexedResourceData> Resources);

public sealed record IndexedEdzData(
    string Path,
    long FileSize,
    long LastWriteUtcTicks,
    IReadOnlyList<IndexedPartData> Parts);

public sealed record PartSearchQuery(
    string? Text = null,
    string? Manufacturer = null,
    string? PartNumber = null,
    string? TypeNumber = null,
    ResourceExistenceFilter ResourceFilter = ResourceExistenceFilter.Any,
    int Limit = 100,
    int Offset = 0,
    PartSearchField TextField = PartSearchField.All,
    PartSearchMatchMode MatchMode = PartSearchMatchMode.FullText,
    string? ProductGroup = null,
    PartSortColumn SortColumn = PartSortColumn.Relevance,
    SortDirection SortDirection = SortDirection.Ascending,
    bool FavoritesOnly = false);

public sealed record PartSearchResult(
    long Id,
    string EdzPath,
    string? Manufacturer,
    string? PartNumber,
    string? TypeNumber,
    string? OrderNumber,
    string? Description,
    string? ProductGroup,
    string? Variant,
    string? PackageKey,
    string RawMetadataReference,
    int ResourceCount,
    int ExistingResourceCount,
    double Rank,
    long? SavedPartId = null,
    bool IsFavorite = false);

public sealed record IndexStatistics(
    int DirectoryCount,
    int FileCount,
    int FailedFileCount,
    int PartCount,
    int ResourceCount);

public sealed record AggregateCount(string Value, int Count);

public sealed record IndexedDirectorySummary(
    long Id,
    string Path,
    bool Recursive,
    DateTimeOffset? LastScanUtc,
    int FileCount,
    int PartCount,
    int FailedFileCount);

public sealed record IndexedResource(
    long Id,
    long PartId,
    string EdzPath,
    string? ResourceType,
    string? Name,
    string? RawLocator,
    string? ArchivePath,
    bool ExistsInArchive,
    long? Size,
    int ReferenceCount);

public sealed record IndexedDiagnostic(
    string EdzPath,
    string Status,
    string? Error,
    DateTimeOffset IndexedUtc);

public sealed record DatabaseMaintenanceSnapshot(
    string Path,
    long SizeBytes,
    int SchemaVersion,
    int Parts,
    int Resources,
    int SavedParts,
    int Collections,
    int Tags,
    int MissingResources);

public sealed record DatabaseIntegritySnapshot(
    bool IntegrityOk,
    int ForeignKeyViolationCount,
    IReadOnlyList<string> Messages);

public enum IndexProgressKind
{
    Discovered,
    Added,
    Updated,
    Skipped,
    Indexed,
    Failed,
    Removed
}

public sealed record IndexProgress(IndexProgressKind Kind, string Path, int Completed, int Total, string? Message = null);

public sealed record IndexScanResult(
    string Directory,
    int Discovered,
    int Added,
    int Updated,
    int Skipped,
    int Failed,
    int Removed,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc)
{
    public int Indexed => Added + Updated;
}
