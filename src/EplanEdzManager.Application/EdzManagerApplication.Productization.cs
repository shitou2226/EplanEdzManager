using EplanEdzManager.EplanBridge.Client;

namespace EplanEdzManager.Application;

public sealed partial class EdzManagerApplication
{
    private ApplicationSettings? _runtimeSettings;

    public async Task<DatabaseMaintenanceInfo> GetDatabaseMaintenanceInfoAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.GetMaintenanceSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new DatabaseMaintenanceInfo(snapshot.Path, snapshot.SizeBytes, snapshot.SchemaVersion, snapshot.Parts,
            snapshot.Resources, snapshot.SavedParts, snapshot.Collections, snapshot.Tags);
    }

    public Task VacuumDatabaseAsync(CancellationToken cancellationToken = default) => _repository.VacuumAsync(cancellationToken);
    public Task OptimizeDatabaseAsync(CancellationToken cancellationToken = default) => _repository.OptimizeAsync(cancellationToken);

    public async Task ResetCatalogIndexAsync(CancellationToken cancellationToken = default)
    {
        await _repository.ResetCatalogIndexAsync(cancellationToken).ConfigureAwait(false);
        await _repository.RebindSavedPartsAsync(cancellationToken).ConfigureAwait(false);
        await _logger.WriteAsync("Warning", "CATALOG-RESET", "Catalog index was reset by explicit user action. My Library was preserved.", cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryHealthReport> CheckLibraryHealthAsync(CancellationToken cancellationToken = default)
    {
        var directoriesTask = _repository.GetDirectoriesAsync(cancellationToken);
        var filesTask = _repository.GetIndexedFilesAsync(cancellationToken);
        var maintenanceTask = _repository.GetMaintenanceSnapshotAsync(cancellationToken);
        var integrityTask = _repository.CheckIntegrityAsync(cancellationToken);
        var savedTask = _repository.GetSavedPartHealthIssuesAsync(cancellationToken);
        await Task.WhenAll(directoriesTask, filesTask, maintenanceTask, integrityTask, savedTask).ConfigureAwait(false);

        var issues = new List<LibraryHealthIssue>();
        var missingFolders = 0;
        foreach (var directory in directoriesTask.Result)
        {
            if (Directory.Exists(directory.Path)) continue;
            missingFolders++;
            issues.Add(new LibraryHealthIssue("Red", "LIB-FOLDER-MISSING", "Registered EDZ folder is missing.", directory.Path));
        }

        var missingSources = 0;
        var stale = 0;
        var broken = 0;
        foreach (var file in filesTask.Result)
        {
            if (!File.Exists(file.Path))
            {
                missingSources++;
                issues.Add(new LibraryHealthIssue("Red", "LIB-SOURCE-MISSING", "Indexed source EDZ is missing.", file.Path));
                continue;
            }
            var info = new FileInfo(file.Path);
            if (info.Length != file.FileSize || info.LastWriteTimeUtc.Ticks != file.LastWriteUtcTicks)
            {
                stale++;
                issues.Add(new LibraryHealthIssue("Yellow", "LIB-INDEX-STALE", "Source EDZ changed after the last index scan.", file.Path));
            }
            if (!string.Equals(file.Status, "Indexed", StringComparison.OrdinalIgnoreCase))
            {
                broken++;
                issues.Add(new LibraryHealthIssue("Red", "LIB-EDZ-BROKEN", file.Error ?? "EDZ indexing failed.", file.Path));
            }
        }

        foreach (var saved in savedTask.Result)
            issues.Add(new LibraryHealthIssue("Yellow", "LIB-SAVED-SOURCE-ORPHAN", "Saved part has no single available preferred source: " + (saved.PartNumber ?? saved.StableIdentity), saved.PreferredEdzPath));
        if (!integrityTask.Result.IntegrityOk)
            issues.Add(new LibraryHealthIssue("Red", "SQLITE-INTEGRITY", "SQLite integrity_check failed: " + string.Join("; ", integrityTask.Result.Messages.Take(5)), maintenanceTask.Result.Path));
        if (integrityTask.Result.ForeignKeyViolationCount > 0)
            issues.Add(new LibraryHealthIssue("Red", "SQLITE-FOREIGN-KEY", $"SQLite contains {integrityTask.Result.ForeignKeyViolationCount} foreign-key violation(s).", maintenanceTask.Result.Path));
        return new LibraryHealthReport(directoriesTask.Result.Count, filesTask.Result.Count, missingFolders, missingSources,
            stale, broken, maintenanceTask.Result.MissingResources, savedTask.Result.Count, issues);
    }

    private EplanBridgeClientOptions CreateConfiguredBridgeOptions()
    {
        var options = EplanBridgeClientOptions.CreateDefault();
        if (!string.IsNullOrWhiteSpace(_runtimeSettings?.EplanPlatformBinDirectory))
            options.EplanPlatformBinDirectory = _runtimeSettings.EplanPlatformBinDirectory;
        if (!string.IsNullOrWhiteSpace(_runtimeSettings?.EplanVariantBinDirectory))
            options.EplanVariantBinDirectory = _runtimeSettings.EplanVariantBinDirectory;
        return options;
    }
}
