namespace EplanEdzManager.Application;

public enum SearchField
{
    All,
    PartNumber,
    TypeNumber,
    Manufacturer,
    Description
}

public enum SearchMatchMode
{
    FullText,
    Contains,
    Exact
}

public enum SearchSortColumn
{
    Relevance,
    Manufacturer,
    PartNumber,
    TypeNumber,
    Description,
    SourceEdz
}

public enum SearchSortDirection
{
    Ascending,
    Descending
}

public sealed record SearchRequest(
    string? Text = null,
    SearchField Field = SearchField.All,
    SearchMatchMode MatchMode = SearchMatchMode.FullText,
    string? Manufacturer = null,
    string? ProductGroup = null,
    SearchSortColumn SortColumn = SearchSortColumn.Relevance,
    SearchSortDirection SortDirection = SearchSortDirection.Ascending,
    int Page = 1,
    int PageSize = 200,
    bool FavoritesOnly = false);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)Math.Max(1, PageSize)));
}

public sealed record PartSummary(
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
    long? SavedPartId = null,
    bool IsFavorite = false)
{
    public string SourceEdz => Path.GetFileName(EdzPath);
    public bool SourceMissing => !File.Exists(EdzPath);
    public string ResourceStatus => ResourceCount == 0
        ? "无资源"
        : ExistingResourceCount == ResourceCount ? $"完整 {ResourceCount}" : $"缺失 {ResourceCount - ExistingResourceCount}/{ResourceCount}";
}

public enum ResourceCategory
{
    Picture,
    Macro,
    Document,
    Mechanical,
    Construction,
    Metadata,
    Unknown
}

public sealed record ResourceItem(
    long Id,
    long PartId,
    string EdzPath,
    string? ResourceType,
    string? Name,
    string? RawLocator,
    string? ArchivePath,
    bool ExistsInArchive,
    long? Size,
    int ReferenceCount,
    ResourceCategory Category)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Path.GetFileName(ArchivePath ?? RawLocator ?? "未命名资源")
        : Name;
    public bool CanPreview
    {
        get
        {
            var extension = Path.GetExtension(ArchivePath ?? RawLocator ?? string.Empty);
            return ExistsInArchive && (Category == ResourceCategory.Picture
                || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase));
        }
    }
    public string SizeText => Size is null ? "—" : FormatBytes(Size.Value);

    private static string FormatBytes(long value)
    {
        if (value < 1024) return value + " B";
        if (value < 1024 * 1024) return (value / 1024d).ToString("0.0") + " KiB";
        return (value / 1024d / 1024d).ToString("0.0") + " MiB";
    }
}

public static class PartPreviewSelector
{
    public static ResourceItem? SelectPreferred(IEnumerable<ResourceItem> resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        return resources
            .Where(resource => resource.CanPreview)
            .OrderBy(resource => resource.Category == ResourceCategory.Picture ? 0 : 1)
            .ThenBy(resource => PreviewNamePriority(resource.DisplayName))
            .ThenBy(resource => resource.DisplayName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static int PreviewNamePriority(string name)
    {
        if (name.Contains("preview", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("front", StringComparison.OrdinalIgnoreCase)) return 1;
        if (name.Contains("picture", StringComparison.OrdinalIgnoreCase) || name.Contains("image", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }
}

public sealed record ResourceSummary(
    int Pictures,
    int Macros,
    int Documents,
    int Mechanical,
    int Construction,
    int Unknown);

public sealed record PartDetail(PartSummary Part, IReadOnlyList<ResourceItem> Resources, ResourceSummary Summary);

public sealed record FilterCount(string Value, int Count);

public sealed record LibraryFolder(
    long Id,
    string Path,
    bool Recursive,
    DateTimeOffset? LastScanUtc,
    int FileCount,
    int PartCount,
    int FailedFileCount)
{
    public string Status => !Directory.Exists(Path) ? "源目录缺失" : FailedFileCount > 0 ? $"{FailedFileCount} 个错误" : LastScanUtc is null ? "尚未扫描" : "就绪";
}

public sealed record LibraryStatus(
    int IndexedEdz,
    int IndexedParts,
    int FailedEdz,
    long DatabaseBytes,
    string ScanState);

public sealed record ScanProgressInfo(
    string CurrentFile,
    int TotalFiles,
    int CompletedFiles,
    int Added,
    int Updated,
    int Unchanged,
    int Failed,
    int Removed,
    string State);

public sealed record ScanSummary(
    int Scanned,
    int Unchanged,
    int Updated,
    int Added,
    int Removed,
    int Failed,
    int PartsIndexed,
    TimeSpan Elapsed);

public sealed record DiagnosticItem(string Severity, string Code, string UserMessage, string TechnicalDetails, string? SourcePath);

public sealed record UserFriendlyError(string Code, string Message, string TechnicalDetails)
{
    public static UserFriendlyError FromException(string code, string message, Exception exception) =>
        new(code, message, exception.GetType().Name + ": " + exception.Message + Environment.NewLine + exception.StackTrace);
}

public sealed class ApplicationSettings
{
    public int SettingsSchemaVersion { get; set; } = 3;
    public bool FirstRunCompleted { get; set; }
    public string DatabasePath { get; set; } = ApplicationPaths.DefaultDatabasePath;
    public string DefaultExportFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public int ThumbnailCacheSize { get; set; } = 150;
    public int SearchPageSize { get; set; } = 200;
    public bool RememberWindowSize { get; set; } = true;
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 900;
    public string? DefaultCollection { get; set; }
    public bool AutoAddFavoriteWhenSaving { get; set; }
    public string BackupFolder { get; set; } = ApplicationPaths.DefaultBackupDirectory;
    public string EplanPlatformBinDirectory { get; set; } = string.Empty;
    public string EplanVariantBinDirectory { get; set; } = string.Empty;
    public string? InitialEdzLibraryFolder { get; set; }
    public int LogRetentionDays { get; set; } = 14;
    public string? LastBackupPath { get; set; }
    public DateTimeOffset? LastBackupUtc { get; set; }
}

public static class ApplicationPaths
{
    public static string LocalDataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EplanEdzManager");

    public static string DefaultDatabasePath => Path.Combine(LocalDataRoot, "index.db");
    public static string DefaultSettingsPath => Path.Combine(LocalDataRoot, "settings.json");
    public static string DefaultLogDirectory => Path.Combine(LocalDataRoot, "Logs", "Desktop");
    public static string CrashReportDirectory => Path.Combine(LocalDataRoot, "CrashReports");
    public static string DiagnosticDirectory => Path.Combine(LocalDataRoot, "Diagnostics");
    public static string DefaultBackupDirectory => Path.Combine(LocalDataRoot, "Backups");
}

public interface IEplanPartExporter
{
    Task ExportAsync(IReadOnlyCollection<long> partIds, CancellationToken cancellationToken = default);
}

public enum MyLibrarySourceStatus
{
    Bound,
    MultipleCandidates,
    SourceMissing,
    Unresolved
}

public enum MyLibrarySort
{
    Manufacturer,
    PartNumber,
    TypeNumber,
    Description,
    UpdatedUtc,
    SourceStatus
}

public sealed record MyLibraryRequest(
    string? Text = null,
    bool FavoritesOnly = false,
    long? CollectionId = null,
    long? TagId = null,
    MyLibrarySort Sort = MyLibrarySort.UpdatedUtc,
    SearchSortDirection Direction = SearchSortDirection.Descending,
    int Page = 1,
    int PageSize = 200);

public sealed record SavedPartSummary(
    long Id,
    string StableIdentity,
    string? Manufacturer,
    string? PartNumber,
    string? Variant,
    string? TypeNumber,
    string? OrderNumber,
    string? Description,
    string? ProductGroup,
    string? PackageKey,
    string Note,
    bool IsFavorite,
    int AvailableSourceCount,
    int TotalSourceCount,
    long? PreferredSourceId,
    string? PreferredEdzPath,
    long? PreferredCurrentPartId,
    string? PreferredRawMetadataReference,
    int ResourceCount,
    int ExistingResourceCount,
    string Collections,
    string Tags,
    MyLibrarySourceStatus SourceStatus,
    DateTimeOffset UpdatedUtc)
{
    public string SourceStatusText => SourceStatus switch
    {
        MyLibrarySourceStatus.Bound => "✓ 可用",
        MyLibrarySourceStatus.MultipleCandidates => $"ⓘ {AvailableSourceCount} 个来源",
        MyLibrarySourceStatus.SourceMissing => "⚠ 来源缺失",
        _ => "? 未解析"
    };
    public string PreferredSourceName => string.IsNullOrWhiteSpace(PreferredEdzPath) ? "—" : Path.GetFileName(PreferredEdzPath);
    public bool CanReadResources => PreferredCurrentPartId is not null && !string.IsNullOrWhiteSpace(PreferredEdzPath);
}

public sealed record SourceCandidate(
    long Id,
    long? CurrentPartId,
    string EdzPath,
    string? PackageKey,
    DateTimeOffset? IndexedUtc,
    long? LastModifiedUtcTicks,
    int ResourceCount,
    int ExistingResourceCount,
    bool IsPreferred,
    bool IsAvailable)
{
    public string SourceName => Path.GetFileName(EdzPath);
    public string Availability => IsAvailable ? "可用" : "缺失";
    public string LastModifiedText => LastModifiedUtcTicks is null ? "—" : new DateTime(LastModifiedUtcTicks.Value, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed record NamedLibraryItem(long Id, string Name, int Count);
public sealed record SavedPartResult(long SavedPartId, string StableIdentity, bool Created, int SourceCount);
public sealed record RebindSummary(int SavedParts, int Bound, int MultipleCandidates, int SourceMissing, int Unresolved, int SourcesUpdated);

public sealed record BackupPreview(
    string FilePath,
    int SchemaVersion,
    DateTimeOffset ExportedUtc,
    string ApplicationVersion,
    int NewParts,
    int ExistingParts,
    int DuplicateParts,
    int InvalidParts,
    bool CanApply,
    IReadOnlyList<string> Errors,
    long FileSize = 0,
    string Sha256 = "");

public sealed record PartExportRequest(
    string StablePartIdentity,
    string PreferredSourceEdz,
    string? PackageKey,
    string? PartNumber,
    string? Variant,
    string? Manufacturer = null,
    long SourceFileSize = 0,
    long SourceLastWriteTimeUtcTicks = 0);

public sealed record ExportValidationIssue(string Code, string StablePartIdentity, string Message);

public sealed record SelectedPartsExportPreparation(
    IReadOnlyList<PartExportRequest> Parts,
    IReadOnlyList<ExportValidationIssue> Issues)
{
    public bool CanExport => Parts.Count > 0 && Issues.Count == 0;
    public int SelectedPartCount => Parts.Count + Issues.Select(issue => issue.StablePartIdentity).Distinct(StringComparer.Ordinal).Count(identity => Parts.All(part => !string.Equals(part.StablePartIdentity, identity, StringComparison.Ordinal)));
    public int SourceEdzCount => Parts.Select(part => Path.GetFullPath(part.PreferredSourceEdz)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

public sealed class SelectedPartsExportValidationException : Exception
{
    public SelectedPartsExportValidationException(IReadOnlyList<ExportValidationIssue> issues)
        : base("Selected parts export validation failed: " + string.Join(" ", issues.Select(issue => issue.Message)))
    {
        Issues = issues;
    }

    public IReadOnlyList<ExportValidationIssue> Issues { get; }
}

public sealed record PartSelectionItem(long SavedPartId, string StableIdentity);

public sealed class PartSelectionModel
{
    private readonly object _gate = new();
    private readonly Dictionary<long, PartSelectionItem> _items = new();
    public int Count { get { lock (_gate) return _items.Count; } }
    public void Select(PartSelectionItem item) { lock (_gate) _items[item.SavedPartId] = item; }
    public void Deselect(long savedPartId) { lock (_gate) _items.Remove(savedPartId); }
    public bool Contains(long savedPartId) { lock (_gate) return _items.ContainsKey(savedPartId); }
    public void Clear() { lock (_gate) _items.Clear(); }
    public IReadOnlyList<PartSelectionItem> Snapshot() { lock (_gate) return _items.Values.ToArray(); }
}
