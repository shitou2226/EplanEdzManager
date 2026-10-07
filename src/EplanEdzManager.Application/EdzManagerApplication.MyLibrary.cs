using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using EplanEdzManager.Core.Identity;
using EplanEdzManager.Core.Text;
using EplanEdzManager.EplanBridge.Client;
using EplanEdzManager.EplanBridge.Protocol;
using EplanEdzManager.Infrastructure.Sqlite;

namespace EplanEdzManager.Application;

public sealed partial class EdzManagerApplication
{
    private const int BackupSchemaVersion = 1;
    private const long MaximumBackupBytes = 64L * 1024 * 1024;
    private static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public PartSelectionModel Selection { get; } = new();

    public async Task<PagedResult<SavedPartSummary>> SearchMyLibraryAsync(MyLibraryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 25, 1000);
        var query = new MyLibrarySearchQuery(request.Text, request.FavoritesOnly, request.CollectionId, request.TagId,
            (MyLibrarySortColumn)request.Sort, (SortDirection)request.Direction, pageSize, (page - 1) * pageSize);
        var countTask = _repository.CountMyLibraryAsync(query, cancellationToken);
        var rowsTask = _repository.SearchMyLibraryAsync(query, cancellationToken);
        await Task.WhenAll(countTask, rowsTask).ConfigureAwait(false);
        return new PagedResult<SavedPartSummary>(rowsTask.Result.Select(MapSavedPart).ToArray(), countTask.Result, page, pageSize);
    }

    public async Task<SavedPartResult> AddToMyLibraryAsync(long catalogPartId, bool favorite = false, CancellationToken cancellationToken = default)
    {
        var row = await _repository.SaveCatalogPartAsync(catalogPartId, favorite, cancellationToken).ConfigureAwait(false);
        return new SavedPartResult(row.SavedPartId, row.StableIdentity, row.Created, row.SourceCount);
    }

    public async Task<IReadOnlyList<SavedPartResult>> AddManyToMyLibraryAsync(IReadOnlyCollection<long> catalogPartIds, bool favorite = false, CancellationToken cancellationToken = default)
    {
        var rows = await _repository.SaveCatalogPartsAsync(catalogPartIds, favorite, cancellationToken).ConfigureAwait(false);
        return rows.Select(x => new SavedPartResult(x.SavedPartId, x.StableIdentity, x.Created, x.SourceCount)).ToArray();
    }

    public async Task SetCatalogFavoriteAsync(PartSummary part, bool favorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.SavedPartId is null)
        {
            if (favorite) await _repository.SaveCatalogPartAsync(part.Id, true, cancellationToken).ConfigureAwait(false);
            return;
        }
        await _repository.SetSavedPartFavoriteAsync(part.SavedPartId.Value, favorite, cancellationToken).ConfigureAwait(false);
    }

    public Task SetFavoriteAsync(long savedPartId, bool favorite, CancellationToken cancellationToken = default) =>
        _repository.SetSavedPartFavoriteAsync(savedPartId, favorite, cancellationToken);

    public Task SetFavoritesAsync(IReadOnlyCollection<long> savedPartIds, bool favorite, CancellationToken cancellationToken = default) =>
        _repository.SetSavedPartsFavoriteAsync(savedPartIds, favorite, cancellationToken);

    public Task SaveNoteAsync(long savedPartId, string? note, CancellationToken cancellationToken = default) =>
        _repository.UpdateSavedPartNoteAsync(savedPartId, note, cancellationToken);

    public Task<int> RemoveFromMyLibraryAsync(IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) =>
        _repository.DeleteSavedPartsAsync(savedPartIds, cancellationToken);

    public async Task<IReadOnlyList<NamedLibraryItem>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
        (await _repository.GetCollectionsAsync(cancellationToken).ConfigureAwait(false)).Select(x => new NamedLibraryItem(x.Id, x.Name, x.Count)).ToArray();

    public async Task<IReadOnlyList<NamedLibraryItem>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        (await _repository.GetTagsAsync(cancellationToken).ConfigureAwait(false)).Select(x => new NamedLibraryItem(x.Id, x.Name, x.Count)).ToArray();

    public Task<long> CreateCollectionAsync(string name, IReadOnlyCollection<long>? savedPartIds = null, CancellationToken cancellationToken = default) =>
        _repository.CreateCollectionAsync(name, savedPartIds, cancellationToken);
    public Task<long> CreateTagAsync(string name, IReadOnlyCollection<long>? savedPartIds = null, CancellationToken cancellationToken = default) =>
        _repository.CreateTagAsync(name, savedPartIds, cancellationToken);
    public Task RenameCollectionAsync(long id, string name, CancellationToken cancellationToken = default) => _repository.RenameCollectionAsync(id, name, cancellationToken);
    public Task RenameTagAsync(long id, string name, CancellationToken cancellationToken = default) => _repository.RenameTagAsync(id, name, cancellationToken);
    public Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default) => _repository.DeleteCollectionAsync(id, cancellationToken);
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default) => _repository.DeleteTagAsync(id, cancellationToken);
    public Task AddToCollectionAsync(long collectionId, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => _repository.AddSavedPartsToCollectionAsync(collectionId, savedPartIds, cancellationToken);
    public Task RemoveFromCollectionAsync(long collectionId, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => _repository.RemoveSavedPartsFromCollectionAsync(collectionId, savedPartIds, cancellationToken);
    public Task AddTagAsync(long tagId, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => _repository.AddTagsToSavedPartsAsync(tagId, savedPartIds, cancellationToken);
    public Task RemoveTagAsync(long tagId, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => _repository.RemoveTagsFromSavedPartsAsync(tagId, savedPartIds, cancellationToken);
    public Task SetPreferredSourceAsync(long savedPartId, long sourceId, CancellationToken cancellationToken = default) => _repository.SetPreferredSourceAsync(savedPartId, sourceId, cancellationToken);

    public async Task<IReadOnlyList<SourceCandidate>> GetSourceCandidatesAsync(long savedPartId, CancellationToken cancellationToken = default) =>
        (await _repository.GetSavedPartSourcesAsync(savedPartId, cancellationToken).ConfigureAwait(false))
            .Select(x => new SourceCandidate(x.Id, x.CurrentPartId, x.EdzPath, x.PackageKey, x.IndexedUtc, x.EdzLastWriteUtcTicks,
                x.ResourceCount, x.ExistingResourceCount, x.IsPreferred, x.IsBound)).ToArray();

    public async Task<PartDetail?> GetSavedPartDetailAsync(SavedPartSummary savedPart, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedPart);
        if (!savedPart.CanReadResources) return null;
        var part = new PartSummary(savedPart.PreferredCurrentPartId!.Value, savedPart.PreferredEdzPath!, savedPart.Manufacturer,
            savedPart.PartNumber, savedPart.TypeNumber, savedPart.OrderNumber, savedPart.Description, savedPart.ProductGroup,
            savedPart.Variant, savedPart.PackageKey, savedPart.PreferredRawMetadataReference ?? string.Empty,
            savedPart.ResourceCount, savedPart.ExistingResourceCount, savedPart.Id, savedPart.IsFavorite);
        return await GetPartDetailAsync(part, cancellationToken).ConfigureAwait(false);
    }

    public async Task<RebindSummary> RebindSavedPartsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _repository.RebindSavedPartsAsync(cancellationToken).ConfigureAwait(false);
        return new RebindSummary(result.SavedParts, result.Bound, result.MultipleCandidates, result.SourceMissing, result.Unresolved, result.SourcesUpdated);
    }

    public async Task<string> ExportMyLibraryAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A backup path is required.", nameof(filePath));
        filePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var snapshot = await _repository.GetMyLibrarySnapshotAsync(cancellationToken).ConfigureAwait(false);
        var document = new BackupDocument(BackupSchemaVersion, DateTimeOffset.UtcNow,
            Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown", snapshot);
        var temporaryPath = filePath + ".tmp";
        await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            await JsonSerializer.SerializeAsync(stream, document, BackupJsonOptions, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, filePath, true);
        return filePath;
    }

    public async Task<BackupPreview> PreviewMyLibraryImportAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(filePath);
        var errors = new List<string>();
        BackupReadResult read;
        try { read = await ReadBackupAsync(fullPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return new BackupPreview(fullPath, 0, default, "unknown", 0, 0, 0, 1, false, new[] { exception.Message });
        }
        var document = read.Document;
        if (document is null)
            return new BackupPreview(fullPath, 0, default, "unknown", 0, 0, 0, 1, false, new[] { "Backup document is empty." });
        if (document.SchemaVersion != BackupSchemaVersion) errors.Add($"Unsupported backup schema version: {document.SchemaVersion}.");
        if (document.Library?.SavedParts is null) errors.Add("Backup does not contain a savedParts array.");
        if (document.Library?.Collections is null) errors.Add("Backup does not contain a collections array.");
        if (document.Library?.Tags is null) errors.Add("Backup does not contain a tags array.");
        var parts = document.Library?.SavedParts ?? Array.Empty<SavedPartSnapshot>();
        if ((document.Library?.Collections ?? Array.Empty<CollectionSnapshot>()).Any(x => !IsValidPersonalName(x.Name))
            || (document.Library?.Tags ?? Array.Empty<TagSnapshot>()).Any(x => !IsValidPersonalName(x.Name))
            || parts.Any(x => x.Collections is null || x.Tags is null
                || x.Collections.Any(name => !IsValidPersonalName(name)) || x.Tags.Any(name => !IsValidPersonalName(name))))
            errors.Add("Backup contains an empty or overlong Collection/Tag name.");
        var duplicateCount = parts.GroupBy(x => x.StableIdentity, StringComparer.Ordinal).Sum(x => Math.Max(0, x.Count() - 1));
        var invalid = 0;
        foreach (var part in parts)
        {
            if (!ValidateSnapshotIdentity(part) || part.Sources is null || part.Collections is null || part.Tags is null
                || part.Sources.Any(x => !ValidateSourceIdentity(x))
                || part.Sources.GroupBy(x => x.SourceInstanceIdentity, StringComparer.Ordinal).Any(x => x.Count() > 1)
                || (part.Sources.Count > 0 && part.Sources.Count(x => x.IsPreferred) != 1))
                invalid++;
        }
        var existing = await _repository.GetExistingStableIdentitiesAsync(cancellationToken).ConfigureAwait(false);
        var uniqueValid = parts.Where(ValidateSnapshotIdentity).Select(x => x.StableIdentity).Distinct(StringComparer.Ordinal).ToArray();
        var existingCount = uniqueValid.Count(existing.Contains);
        return new BackupPreview(fullPath, document.SchemaVersion, document.ExportedUtc, document.ApplicationVersion ?? "unknown",
            uniqueValid.Length - existingCount, existingCount, duplicateCount, invalid, errors.Count == 0 && invalid == 0 && duplicateCount == 0,
            errors, read.FileSize, read.Sha256);
    }

    public async Task<BackupPreview> RestoreMyLibraryAsync(BackupPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!preview.CanApply) throw new InvalidDataException("Backup cannot be applied: " + string.Join(" ", preview.Errors));
        var read = await ReadBackupAsync(preview.FilePath, cancellationToken).ConfigureAwait(false);
        if (read.FileSize != preview.FileSize || !string.Equals(read.Sha256, preview.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The backup changed after Preview. Run Preview again before restoring.");
        var document = read.Document
            ?? throw new InvalidDataException("Backup document is empty.");
        await _repository.RestoreMyLibrarySnapshotAsync(document.Library!, cancellationToken).ConfigureAwait(false);
        return preview;
    }

    public async Task<IReadOnlyList<PartExportRequest>> CreateExportRequestsAsync(IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default)
    {
        var preparation = await PrepareSelectedPartsExportAsync(savedPartIds, cancellationToken).ConfigureAwait(false);
        if (!preparation.CanExport) throw new SelectedPartsExportValidationException(preparation.Issues);
        return preparation.Parts;
    }

    public async Task<SelectedPartsExportPreparation> PrepareSelectedPartsExportAsync(IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedPartIds);
        var wanted = savedPartIds.ToHashSet();
        var result = new List<PartExportRequest>();
        var issues = new List<ExportValidationIssue>();
        var page = 1;
        while (wanted.Count > 0)
        {
            var batch = await SearchMyLibraryAsync(new MyLibraryRequest(Page: page, PageSize: 1000), cancellationToken).ConfigureAwait(false);
            foreach (var part in batch.Items.Where(x => wanted.Remove(x.Id)))
            {
                var partIssues = ValidateExportPart(part.StableIdentity, part.PreferredEdzPath, part.PartNumber, part.SourceStatus, part.PreferredCurrentPartId);
                issues.AddRange(partIssues);
                if (partIssues.Count == 0)
                {
                    var source = new FileInfo(part.PreferredEdzPath!);
                    result.Add(new PartExportRequest(part.StableIdentity, source.FullName, part.PackageKey, part.PartNumber, part.Variant,
                        part.Manufacturer, source.Length, source.LastWriteTimeUtc.Ticks));
                }
            }
            if (page >= batch.PageCount) break;
            page++;
        }
        foreach (var missingId in wanted.OrderBy(id => id))
            issues.Add(new ExportValidationIssue("EXPORT-SELECTION-MISSING", "saved-part:" + missingId, "The selected SavedPart no longer exists."));
        return new SelectedPartsExportPreparation(result, issues);
    }

    public static SelectedPartsExportPreparation PrepareCatalogPartsExport(IReadOnlyCollection<PartSummary> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var result = new List<PartExportRequest>();
        var issues = new List<ExportValidationIssue>();
        foreach (var part in parts.DistinctBy(item => item.Id).OrderBy(item => item.Id))
        {
            var identity = StablePartIdentity.TryCreate(part.Manufacturer, part.PartNumber, part.Variant, part.PackageKey, out var stable)
                ? stable!.Value
                : "catalog-part:" + part.Id;
            if (stable is null) issues.Add(new ExportValidationIssue("EXPORT-IDENTITY", identity, "Stable identity cannot be created for the Catalog part."));
            if (string.IsNullOrWhiteSpace(part.PartNumber)) issues.Add(new ExportValidationIssue("EXPORT-PART-NUMBER", identity, "PartNumber is empty."));
            if (string.IsNullOrWhiteSpace(part.EdzPath) || !File.Exists(part.EdzPath)) issues.Add(new ExportValidationIssue("EXPORT-SOURCE-MISSING", identity, "Source EDZ is missing: " + part.EdzPath));
            if (issues.All(issue => !string.Equals(issue.StablePartIdentity, identity, StringComparison.Ordinal)))
            {
                var source = new FileInfo(part.EdzPath);
                result.Add(new PartExportRequest(identity, source.FullName, part.PackageKey, part.PartNumber, part.Variant,
                    part.Manufacturer, source.Length, source.LastWriteTimeUtc.Ticks));
            }
        }
        if (parts.Count == 0) issues.Add(new ExportValidationIssue("EXPORT-EMPTY", string.Empty, "Select at least one part."));
        return new SelectedPartsExportPreparation(result, issues);
    }

    public async Task<ExportResult> ExportSelectedPartsAsync(
        SelectedPartsExportPreparation preparation,
        string outputPath,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.CanExport) throw new SelectedPartsExportValidationException(preparation.Issues);
        await using var client = new EplanBridgeClient(CreateConfiguredBridgeOptions());
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        var capabilities = await client.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (!capabilities.EplanRuntimeFound || !capabilities.PartsServiceAvailable || !capabilities.EdzConverterAvailable || !capabilities.TemporaryDatabaseCapability)
            throw new EplanBridgeException(BridgeProtocol.ErrorCodes.RuntimeNotFound, "The EPLAN Bridge capability check did not pass.",
                $"Runtime={capabilities.EplanRuntimeFound}; PartsService={capabilities.PartsServiceAvailable}; EDZ={capabilities.EdzConverterAvailable}; TempDB={capabilities.TemporaryDatabaseCapability}", capabilities.LogPath);
        return await client.ExportPartsAsync(outputPath, preparation.Parts.Select(part => new ExportPartDto
        {
            StablePartIdentity = part.StablePartIdentity,
            PreferredSourceEdz = part.PreferredSourceEdz,
            Manufacturer = part.Manufacturer ?? string.Empty,
            PackageKey = part.PackageKey ?? string.Empty,
            PartNumber = part.PartNumber ?? string.Empty,
            Variant = part.Variant ?? string.Empty,
            SourceFileSize = part.SourceFileSize,
            SourceLastWriteTimeUtcTicks = part.SourceLastWriteTimeUtcTicks
        }).ToArray(), progress, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<ExportValidationIssue> ValidateExportPart(
        string identity,
        string? source,
        string? partNumber,
        MyLibrarySourceStatus sourceStatus,
        long? currentPartId)
    {
        var issues = new List<ExportValidationIssue>();
        if (string.IsNullOrWhiteSpace(partNumber)) issues.Add(new ExportValidationIssue("EXPORT-PART-NUMBER", identity, "PartNumber is empty."));
        if (string.IsNullOrWhiteSpace(source)) issues.Add(new ExportValidationIssue("EXPORT-PREFERRED-SOURCE", identity, "Preferred Source is not resolved."));
        else if (!File.Exists(source)) issues.Add(new ExportValidationIssue("EXPORT-SOURCE-MISSING", identity, "Preferred Source EDZ is missing: " + source));
        if (currentPartId is null || sourceStatus is MyLibrarySourceStatus.SourceMissing or MyLibrarySourceStatus.Unresolved)
            issues.Add(new ExportValidationIssue("EXPORT-SOURCE-HEALTH", identity, "Preferred Source health is not available."));
        return issues;
    }

    private static SavedPartSummary MapSavedPart(SavedPartSearchResult row) => new(row.Id, row.StableIdentity, row.Manufacturer,
        row.PartNumber, row.Variant, row.TypeNumber, row.OrderNumber, EplanMultilingualText.ToDisplayText(row.Description), row.ProductGroup, row.PackageKey,
        row.Note, row.Favorite, row.AvailableSourceCount, row.TotalSourceCount, row.PreferredSourceId, row.PreferredEdzPath,
        row.PreferredCurrentPartId, row.PreferredRawMetadataReference, row.PreferredResourceCount, row.PreferredExistingResourceCount,
        row.Collections, row.Tags, (MyLibrarySourceStatus)row.SourceState, row.UpdatedUtc);

    private static bool ValidateSnapshotIdentity(SavedPartSnapshot part)
    {
        if (!Enum.TryParse<StableIdentityKind>(part.IdentityKind, out var expectedKind)) return false;
        if (!StablePartIdentity.TryCreate(part.Manufacturer, part.PartNumber, part.Variant, part.PackageKey, out var generated)) return false;
        return generated!.Kind == expectedKind && string.Equals(generated.Value, part.StableIdentity, StringComparison.Ordinal);
    }

    private static bool ValidateSourceIdentity(SavedSourceSnapshot source)
    {
        try
        {
            if (source.EdzFileSize is < 0 || source.ResourceCount < 0 || source.ExistingResourceCount < 0
                || source.ExistingResourceCount > source.ResourceCount
                || (source.EdzLastWriteUtcTicks is long ticks && (ticks < 0 || ticks > DateTime.MaxValue.Ticks)))
                return false;
            return string.Equals(PartSourceIdentity.Create(source.EdzPath, source.PackageKey, source.RawMetadataReference), source.SourceInstanceIdentity, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsValidPersonalName(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 120;

    private static async Task<BackupReadResult> ReadBackupAsync(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        if (stream.Length > MaximumBackupBytes)
            throw new InvalidDataException("The backup exceeds the 64 MiB safety limit.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var document = JsonSerializer.Deserialize<BackupDocument>(bytes, BackupJsonOptions);
        return new BackupReadResult(document, bytes.LongLength, sha256);
    }

    private sealed record BackupReadResult(BackupDocument? Document, long FileSize, string Sha256);
    private sealed record BackupDocument(int SchemaVersion, DateTimeOffset ExportedUtc, string? ApplicationVersion, MyLibrarySnapshot? Library);
}
