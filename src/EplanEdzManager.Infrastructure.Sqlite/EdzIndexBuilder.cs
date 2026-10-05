using EplanEdzManager.Edz.Documents;

namespace EplanEdzManager.Infrastructure.Sqlite;

public sealed class EdzIndexBuilder
{
    private readonly SqliteIndexRepository _repository;
    private readonly SemaphoreSlim _scanGate = new SemaphoreSlim(1, 1);

    public EdzIndexBuilder(SqliteIndexRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public Task<IndexScanResult> ScanDirectoryAsync(
        string directory,
        bool recursive,
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(
            () => ScanDirectoryCoreAsync(directory, recursive, progress, cancellationToken),
            cancellationToken);
    }

    public async Task<IReadOnlyList<IndexScanResult>> ScanRegisteredDirectoriesAsync(
        IProgress<IndexProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<IndexScanResult>();
        var directories = await _repository.GetDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var directory in directories.Where(candidate => candidate.Enabled))
        {
            results.Add(await ScanDirectoryAsync(directory.Path, directory.Recursive, progress, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    public async Task<int> PruneDirectoryAsync(string directory, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeExistingDirectory(directory);
        var registered = await _repository.GetDirectoriesAsync(cancellationToken).ConfigureAwait(false);
        var record = registered.FirstOrDefault(candidate => string.Equals(candidate.Path, normalized, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            throw new InvalidOperationException("The directory is not registered: " + normalized);
        }

        var files = EnumerateEdzFiles(normalized, record.Recursive, cancellationToken);
        return await _repository.RemoveMissingFilesAsync(record.Id, files, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IndexScanResult> ScanDirectoryCoreAsync(
        string directory,
        bool recursive,
        IProgress<IndexProgress>? progress,
        CancellationToken cancellationToken)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var normalizedDirectory = NormalizeExistingDirectory(directory);
            var directoryId = await _repository.AddDirectoryAsync(normalizedDirectory, recursive, cancellationToken).ConfigureAwait(false);
            var files = EnumerateEdzFiles(normalizedDirectory, recursive, cancellationToken);
            progress?.Report(new IndexProgress(IndexProgressKind.Discovered, normalizedDirectory, 0, files.Count, files.Count + " EDZ file(s)"));

            var added = 0;
            var updated = 0;
            var skipped = 0;
            var failed = 0;
            for (var index = 0; index < files.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = files[index];
                var file = new FileInfo(path);
                var existing = await _repository.GetFileStateAsync(path, cancellationToken).ConfigureAwait(false);
                if (existing is not null
                    && existing.Status == "Indexed"
                    && existing.FileSize == file.Length
                    && existing.LastWriteUtcTicks == file.LastWriteTimeUtc.Ticks)
                {
                    skipped++;
                    progress?.Report(new IndexProgress(IndexProgressKind.Skipped, path, index + 1, files.Count));
                    continue;
                }

                try
                {
                    var data = ReadEdz(path, file.Length, file.LastWriteTimeUtc.Ticks, cancellationToken);
                    await _repository.ReplaceFileAsync(directoryId, data, cancellationToken).ConfigureAwait(false);
                    var kind = existing is null ? IndexProgressKind.Added : IndexProgressKind.Updated;
                    if (existing is null) added++; else updated++;
                    progress?.Report(new IndexProgress(kind, path, index + 1, files.Count, data.Parts.Count + " part(s)"));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
                {
                    var message = exception.GetType().Name + ": " + exception.Message;
                    await _repository.RecordFailureAsync(directoryId, path, file.Length, file.LastWriteTimeUtc.Ticks, message, cancellationToken).ConfigureAwait(false);
                    failed++;
                    progress?.Report(new IndexProgress(IndexProgressKind.Failed, path, index + 1, files.Count, message));
                }
            }

            var removed = await _repository.RemoveMissingFilesAsync(directoryId, files, cancellationToken).ConfigureAwait(false);
            if (removed > 0)
            {
                progress?.Report(new IndexProgress(IndexProgressKind.Removed, normalizedDirectory, files.Count, files.Count, removed + " stale file index(es)"));
            }

            var completedUtc = DateTimeOffset.UtcNow;
            await _repository.MarkDirectoryScannedAsync(directoryId, completedUtc, cancellationToken).ConfigureAwait(false);
            return new IndexScanResult(normalizedDirectory, files.Count, added, updated, skipped, failed, removed, startedUtc, completedUtc);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private static IndexedEdzData ReadEdz(string path, long fileSize, long lastWriteUtcTicks, CancellationToken cancellationToken)
    {
        var openResult = new EdzDocumentReader().Open(path, cancellationToken);
        if (openResult.Document is null)
        {
            var detail = string.Join("; ", openResult.Diagnostics.Select(diagnostic => diagnostic.Code + " " + diagnostic.Message));
            throw new InvalidDataException(detail.Length == 0 ? "The EDZ document could not be opened." : detail);
        }

        using var document = openResult.Document;
        var parts = document.Parts.Take(document.Parts.Count, cancellationToken)
            .Select(part =>
            {
                var resources = string.IsNullOrWhiteSpace(part.PackageKey)
                    ? Array.Empty<EdzPackageResource>()
                    : document.GetResources(part.PackageKey).ToArray();
                return new IndexedPartData(
                    part.Manufacturer,
                    part.PartNumber,
                    part.TypeNumber,
                    part.OrderNumber,
                    part.Description,
                    part.ProductGroup,
                    part.Variant,
                    part.PackageKey,
                    part.RawMetadataReference,
                    resources.Select(resource => new IndexedResourceData(
                        resource.Reference.Type,
                        resource.Reference.Name,
                        resource.Reference.RawLocator,
                        resource.Reference.ResolvedLocator?.ArchivePath,
                        resource.Entry is not null,
                        resource.Entry?.Size,
                        resource.ReferenceCount)).ToArray());
            })
            .ToArray();

        return new IndexedEdzData(Path.GetFullPath(path), fileSize, lastWriteUtcTicks, parts);
    }

    private static IReadOnlyList<string> EnumerateEdzFiles(string directory, bool recursive, CancellationToken cancellationToken)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };
        var paths = new List<string>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.edz", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths.Add(Path.GetFullPath(path));
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    private static string NormalizeExistingDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("A directory path is required.", nameof(directory));
        }

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (!Directory.Exists(normalized))
        {
            throw new DirectoryNotFoundException("The EDZ directory does not exist: " + normalized);
        }

        return normalized;
    }
}
