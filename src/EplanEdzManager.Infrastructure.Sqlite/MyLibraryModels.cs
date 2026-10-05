namespace EplanEdzManager.Infrastructure.Sqlite;

public enum SavedPartSourceState
{
    Bound,
    MultipleCandidates,
    SourceMissing,
    Unresolved
}

public enum MyLibrarySortColumn
{
    Manufacturer,
    PartNumber,
    TypeNumber,
    Description,
    UpdatedUtc,
    SourceStatus
}

public sealed record MyLibrarySearchQuery(
    string? Text = null,
    bool FavoritesOnly = false,
    long? CollectionId = null,
    long? TagId = null,
    MyLibrarySortColumn SortColumn = MyLibrarySortColumn.UpdatedUtc,
    SortDirection SortDirection = SortDirection.Descending,
    int Limit = 100,
    int Offset = 0);

public sealed record SavedPartSearchResult(
    long Id,
    string StableIdentity,
    string IdentityKind,
    string? Manufacturer,
    string? PartNumber,
    string? Variant,
    string? TypeNumber,
    string? OrderNumber,
    string? Description,
    string? ProductGroup,
    string? PackageKey,
    string Note,
    bool Favorite,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    int AvailableSourceCount,
    int TotalSourceCount,
    long? PreferredSourceId,
    string? PreferredEdzPath,
    long? PreferredCurrentPartId,
    string? PreferredRawMetadataReference,
    int PreferredResourceCount,
    int PreferredExistingResourceCount,
    string Collections,
    string Tags,
    SavedPartSourceState SourceState);

public sealed record SavedPartSourceResult(
    long Id,
    long SavedPartId,
    string SourceInstanceIdentity,
    long? CurrentPartId,
    string EdzPath,
    string? PackageKey,
    string RawMetadataReference,
    long? EdzFileSize,
    long? EdzLastWriteUtcTicks,
    DateTimeOffset? IndexedUtc,
    int ResourceCount,
    int ExistingResourceCount,
    bool IsPreferred,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    bool IsBound);

public sealed record NamedItemCount(long Id, string Name, int Count);

public sealed record SavedPartWriteResult(long SavedPartId, string StableIdentity, bool Created, int SourceCount);

public sealed record RebindResult(int SavedParts, int Bound, int MultipleCandidates, int SourceMissing, int Unresolved, int SourcesUpdated);
public sealed record SavedPartHealth(long SavedPartId, string StableIdentity, string? PartNumber, string? PreferredEdzPath, int AvailableSources, int TotalSources, bool PreferredAvailable);

public sealed record MyLibrarySnapshot(
    IReadOnlyList<SavedPartSnapshot> SavedParts,
    IReadOnlyList<CollectionSnapshot> Collections,
    IReadOnlyList<TagSnapshot> Tags);

public sealed record SavedPartSnapshot(
    string StableIdentity,
    string IdentityKind,
    string? Manufacturer,
    string? PartNumber,
    string? Variant,
    string? TypeNumber,
    string? OrderNumber,
    string? Description,
    string? ProductGroup,
    string? PackageKey,
    string Note,
    bool Favorite,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<SavedSourceSnapshot> Sources,
    IReadOnlyList<string> Collections,
    IReadOnlyList<string> Tags);

public sealed record SavedSourceSnapshot(
    string SourceInstanceIdentity,
    string EdzPath,
    string? PackageKey,
    string RawMetadataReference,
    long? EdzFileSize,
    long? EdzLastWriteUtcTicks,
    DateTimeOffset? IndexedUtc,
    int ResourceCount,
    int ExistingResourceCount,
    bool IsPreferred,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc);

public sealed record CollectionSnapshot(string Name);
public sealed record TagSnapshot(string Name);

public sealed record RestorePreviewCounts(int NewParts, int ExistingParts, int DuplicateParts, int InvalidParts);
