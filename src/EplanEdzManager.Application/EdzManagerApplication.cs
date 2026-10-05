using System.Diagnostics;
using EplanEdzManager.Infrastructure.Sqlite;

namespace EplanEdzManager.Application;

public sealed partial class EdzManagerApplication
{
    private readonly SqliteIndexRepository _repository;
    private readonly EdzIndexBuilder _indexBuilder;
    private readonly ResourceContentService _resources = new();
    private readonly IApplicationLogger _logger;

    public EdzManagerApplication(string databasePath, IApplicationLogger? logger = null, ApplicationSettings? runtimeSettings = null)
    {
        _repository = new SqliteIndexRepository(databasePath);
        _indexBuilder = new EdzIndexBuilder(_repository);
        _logger = logger ?? new NullApplicationLogger();
        _runtimeSettings = runtimeSettings;
    }

    public string DatabasePath => _repository.DatabasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _logger.WriteAsync("Information", "ApplicationInitialized", "SQLite index initialized.",
            new Dictionary<string, object?> { ["databasePath"] = DatabasePath }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PagedResult<PartSummary>> SearchAsync(SearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pageSize = Math.Clamp(request.PageSize, 25, 1000);
        var page = Math.Max(1, request.Page);
        var query = MapSearch(request with { Page = page, PageSize = pageSize });
        var stopwatch = Stopwatch.StartNew();
        var countTask = _repository.CountSearchAsync(query, cancellationToken);
        var resultsTask = _repository.SearchAsync(query, cancellationToken);
        await Task.WhenAll(countTask, resultsTask).ConfigureAwait(false);
        var items = resultsTask.Result.Select(MapPart).ToArray();
        stopwatch.Stop();
        await _logger.WriteAsync("Information", "SearchCompleted", "Part search completed.", new Dictionary<string, object?>
        {
            ["resultCount"] = items.Length,
            ["totalCount"] = countTask.Result,
            ["page"] = page,
            ["pageSize"] = pageSize,
            ["elapsedMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds
        }, cancellationToken).ConfigureAwait(false);
        return new PagedResult<PartSummary>(items, countTask.Result, page, pageSize);
    }

    public async Task<PartDetail> GetPartDetailAsync(PartSummary part, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        var stopwatch = Stopwatch.StartNew();
        var resources = (await _repository.GetResourcesAsync(part.Id, cancellationToken).ConfigureAwait(false))
            .Select(MapResource)
            .ToArray();
        var summary = new ResourceSummary(
            resources.Count(item => item.Category == ResourceCategory.Picture),
            resources.Count(item => item.Category == ResourceCategory.Macro),
            resources.Count(item => item.Category == ResourceCategory.Document),
            resources.Count(item => item.Category == ResourceCategory.Mechanical),
            resources.Count(item => item.Category == ResourceCategory.Construction),
            resources.Count(item => item.Category == ResourceCategory.Unknown));
        stopwatch.Stop();
        await _logger.WriteAsync("Information", "PartDetailLoaded", "Part detail loaded.", new Dictionary<string, object?>
        {
            ["partId"] = part.Id,
            ["resourceCount"] = resources.Length,
            ["sourceMissing"] = part.SourceMissing,
            ["elapsedMilliseconds"] = stopwatch.Elapsed.TotalMilliseconds
        }, cancellationToken).ConfigureAwait(false);
        return new PartDetail(part, resources, summary);
    }

    public async Task<IReadOnlyList<LibraryFolder>> GetLibrariesAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _repository.GetDirectorySummariesAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new LibraryFolder(row.Id, row.Path, row.Recursive, row.LastScanUtc, row.FileCount, row.PartCount, row.FailedFileCount)).ToArray();
    }

    public async Task<IReadOnlyList<FilterCount>> GetManufacturersAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _repository.GetManufacturersAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new FilterCount(row.Value, row.Count)).ToArray();
    }

    public async Task<IReadOnlyList<FilterCount>> GetProductGroupsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _repository.GetProductGroupsAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new FilterCount(row.Value, row.Count)).ToArray();
    }

    public async Task AddLibraryAsync(string path, bool recursive = true, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("The selected EDZ library folder does not exist: " + path);
        await _repository.AddDirectoryAsync(path, recursive, cancellationToken).ConfigureAwait(false);
        await _logger.WriteAsync("Information", "LibraryAdded", "Library folder registered.", new Dictionary<string, object?> { ["path"] = Path.GetFullPath(path), ["recursive"] = recursive }, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveLibraryAsync(string path, CancellationToken cancellationToken = default)
    {
        var removed = await _repository.RemoveDirectoryAsync(path, cancellationToken).ConfigureAwait(false);
        await _logger.WriteAsync("Information", "LibraryRemoved", "Library index registration removed; source files were not deleted.", new Dictionary<string, object?> { ["path"] = Path.GetFullPath(path), ["removed"] = removed }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ScanSummary> ScanLibraryAsync(string path, IProgress<ScanProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        var libraries = await GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        var library = libraries.FirstOrDefault(item => string.Equals(item.Path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), StringComparison.OrdinalIgnoreCase));
        if (library is null) throw new InvalidOperationException("The EDZ library folder is not registered.");
        var started = Stopwatch.StartNew();
        var state = new MutableScanProgress();
        var adapter = new InlineProgress<IndexProgress>(item =>
        {
            state.Apply(item);
            progress?.Report(state.Snapshot("扫描中"));
        });
        try
        {
            await _logger.WriteAsync("Information", "ScanStarted", "Library scan started.", new Dictionary<string, object?> { ["path"] = library.Path }, cancellationToken).ConfigureAwait(false);
            var result = await _indexBuilder.ScanDirectoryAsync(library.Path, library.Recursive, adapter, cancellationToken).ConfigureAwait(false);
            started.Stop();
            var rebind = await _repository.RebindSavedPartsAsync(cancellationToken).ConfigureAwait(false);
            var statistics = await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
            var summary = new ScanSummary(result.Discovered, result.Skipped, result.Updated, result.Added, result.Removed, result.Failed, statistics.PartCount, started.Elapsed);
            progress?.Report(state.Snapshot(result.Failed > 0 ? "完成（有错误）" : "完成"));
            await _logger.WriteAsync(result.Failed > 0 ? "Warning" : "Information", "ScanCompleted", "Library scan completed.", new Dictionary<string, object?>
            {
                ["path"] = library.Path,
                ["added"] = result.Added,
                ["updated"] = result.Updated,
                ["unchanged"] = result.Skipped,
                ["failed"] = result.Failed,
                ["removed"] = result.Removed,
                ["savedPartsRebound"] = rebind.SourcesUpdated,
                ["elapsedMilliseconds"] = started.Elapsed.TotalMilliseconds
            }, cancellationToken).ConfigureAwait(false);
            return summary;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(state.Snapshot("已取消"));
            await _logger.WriteAsync("Information", "ScanCancelled", "Library scan cancelled.", new Dictionary<string, object?> { ["path"] = library.Path }).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            await LogExceptionAsync("ScanFailed", exception, library.Path).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ScanSummary> ScanAllAsync(IProgress<ScanProgressInfo>? progress = null, CancellationToken cancellationToken = default)
    {
        var libraries = await GetLibrariesAsync(cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.StartNew();
        var scanned = 0;
        var unchanged = 0;
        var updated = 0;
        var added = 0;
        var removed = 0;
        var failed = 0;
        foreach (var library in libraries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ScanLibraryAsync(library.Path, progress, cancellationToken).ConfigureAwait(false);
            scanned += result.Scanned;
            unchanged += result.Unchanged;
            updated += result.Updated;
            added += result.Added;
            removed += result.Removed;
            failed += result.Failed;
        }

        started.Stop();
        var statistics = await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        return new ScanSummary(scanned, unchanged, updated, added, removed, failed, statistics.PartCount, started.Elapsed);
    }

    public async Task<LibraryStatus> GetStatusAsync(int currentResults, string scanState, CancellationToken cancellationToken = default)
    {
        var statistics = await _repository.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
        return new LibraryStatus(statistics.FileCount, statistics.PartCount, statistics.FailedFileCount, GetDatabaseBytes(), scanState);
    }

    public async Task<IReadOnlyList<DiagnosticItem>> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var rows = await _repository.GetDiagnosticsAsync(cancellationToken).ConfigureAwait(false);
        var result = rows.Select(row => new DiagnosticItem(
            "Error", "EDZ-INDEX", "无法读取此 EDZ 文件。", row.Error ?? "Unknown indexing error.", row.EdzPath)).ToList();
        foreach (var health in await _repository.GetSavedPartHealthIssuesAsync(cancellationToken).ConfigureAwait(false))
        {
            var item = health.TotalSources == 0
                ? new DiagnosticItem("Warning", "MYLIB-UNRESOLVED", "SavedPart 没有来源候选。", health.StableIdentity, null)
                : health.AvailableSources == 0
                    ? new DiagnosticItem("Warning", "MYLIB-SOURCE-MISSING", "SavedPart 的来源当前缺失。", health.StableIdentity, health.PreferredEdzPath)
                    : health.AvailableSources > 1
                        ? new DiagnosticItem("Information", "MYLIB-MULTIPLE-SOURCES", $"SavedPart 有 {health.AvailableSources} 个可用来源候选。", health.StableIdentity, health.PreferredEdzPath)
                        : new DiagnosticItem("Warning", "MYLIB-PREFERRED-MISSING", "Preferred Source 不可用，但存在其他候选来源。", health.StableIdentity, health.PreferredEdzPath);
            result.Add(item);
        }
        return result;
    }

    public Task<byte[]> ReadResourceAsync(ResourceItem resource, int maximumBytes = 20 * 1024 * 1024, CancellationToken cancellationToken = default) =>
        _resources.ReadResourceAsync(resource, maximumBytes, cancellationToken);

    public Task<string> ReadRawMetadataAsync(PartSummary part, int maximumBytes = 4 * 1024 * 1024, CancellationToken cancellationToken = default) =>
        _resources.ReadRawMetadataAsync(part, maximumBytes, cancellationToken);

    public async Task ExportResourceAsync(ResourceItem resource, string destinationPath, CancellationToken cancellationToken = default)
    {
        await _resources.ExportResourceAsync(resource, destinationPath, cancellationToken).ConfigureAwait(false);
        await _logger.WriteAsync("Information", "ResourceExported", "A single EDZ resource was exported.", new Dictionary<string, object?>
        {
            ["resourceId"] = resource.Id,
            ["destinationPath"] = Path.GetFullPath(destinationPath)
        }, cancellationToken).ConfigureAwait(false);
    }

    public void OpenContainingFolder(string sourcePath) => _resources.OpenContainingFolder(sourcePath);

    private long GetDatabaseBytes()
    {
        return new[] { DatabasePath, DatabasePath + "-wal", DatabasePath + "-shm" }
            .Where(File.Exists)
            .Sum(path => new FileInfo(path).Length);
    }

    private Task LogExceptionAsync(string eventName, Exception exception, string? path = null) =>
        _logger.WriteAsync("Error", eventName, exception.Message, new Dictionary<string, object?>
        {
            ["exceptionType"] = exception.GetType().FullName,
            ["path"] = path
        });

    private static PartSearchQuery MapSearch(SearchRequest request) => new(
        Text: request.Text,
        Manufacturer: request.Manufacturer,
        ResourceFilter: ResourceExistenceFilter.Any,
        Limit: request.PageSize,
        Offset: (request.Page - 1) * request.PageSize,
        TextField: (PartSearchField)request.Field,
        MatchMode: (PartSearchMatchMode)request.MatchMode,
        ProductGroup: request.ProductGroup,
        SortColumn: (PartSortColumn)request.SortColumn,
        SortDirection: (SortDirection)request.SortDirection,
        FavoritesOnly: request.FavoritesOnly);

    private static PartSummary MapPart(PartSearchResult row) => new(
        row.Id, row.EdzPath, row.Manufacturer, row.PartNumber, row.TypeNumber, row.OrderNumber,
        row.Description, row.ProductGroup, row.Variant, row.PackageKey, row.RawMetadataReference,
        row.ResourceCount, row.ExistingResourceCount, row.SavedPartId, row.IsFavorite);

    private static ResourceItem MapResource(IndexedResource row) => new(
        row.Id, row.PartId, row.EdzPath, row.ResourceType, row.Name, row.RawLocator, row.ArchivePath,
        row.ExistsInArchive, row.Size, row.ReferenceCount, Categorize(row.ResourceType, row.Name, row.ArchivePath));

    private static ResourceCategory Categorize(string? type, string? name, string? archivePath)
    {
        var normalizedType = (type ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedName = (name ?? string.Empty).Trim().ToLowerInvariant();
        var extension = Path.GetExtension(archivePath ?? string.Empty).ToLowerInvariant();
        if (normalizedType == "picture") return ResourceCategory.Picture;
        if (normalizedType == "macro") return ResourceCategory.Macro;
        if (normalizedType == "gmacro") return ResourceCategory.Mechanical;
        if (normalizedType == "partxml" && normalizedName.Contains("construction", StringComparison.Ordinal)) return ResourceCategory.Construction;
        if (extension is ".pdf" or ".doc" or ".docx" or ".txt" || normalizedType.Contains("document", StringComparison.Ordinal)) return ResourceCategory.Document;
        if (normalizedType == "partxml" || normalizedType.Contains("manufacturer", StringComparison.Ordinal) || normalizedType.Contains("supplier", StringComparison.Ordinal)) return ResourceCategory.Metadata;
        return ResourceCategory.Unknown;
    }

    private sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private sealed class MutableScanProgress
    {
        private string _currentFile = string.Empty;
        private int _total;
        private int _completed;
        private int _added;
        private int _updated;
        private int _unchanged;
        private int _failed;
        private int _removed;

        public void Apply(IndexProgress progress)
        {
            _currentFile = progress.Path;
            _total = Math.Max(_total, progress.Total);
            _completed = Math.Max(_completed, progress.Completed);
            switch (progress.Kind)
            {
                case IndexProgressKind.Added: _added++; break;
                case IndexProgressKind.Updated:
                case IndexProgressKind.Indexed: _updated++; break;
                case IndexProgressKind.Skipped: _unchanged++; break;
                case IndexProgressKind.Failed: _failed++; break;
                case IndexProgressKind.Removed: _removed += ParseLeadingInt(progress.Message); break;
            }
        }

        public ScanProgressInfo Snapshot(string state) => new(_currentFile, _total, _completed, _added, _updated, _unchanged, _failed, _removed, state);

        private static int ParseLeadingInt(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            var token = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return int.TryParse(token, out var result) ? result : 0;
        }
    }
}
