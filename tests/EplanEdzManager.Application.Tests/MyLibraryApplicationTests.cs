using EplanEdzManager.Application;
using EplanEdzManager.Infrastructure.Sqlite;
using System.IO;
using Xunit;

namespace EplanEdzManager.Application.Tests;

public sealed class MyLibraryApplicationTests
{
    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task Acceptance_DR_100_24_survives_source_move_and_recovers_after_rescan()
    {
        using var workspace = new TestWorkspace();
        var source = FindSample("明纬.edz");
        var indexedPath = Path.Combine(workspace.DirectoryPath, "明纬.edz");
        File.Copy(source, indexedPath);
        var app = new EdzManagerApplication(workspace.DatabasePath);
        await app.InitializeAsync();
        await app.AddLibraryAsync(workspace.DirectoryPath, recursive: false);
        await app.ScanLibraryAsync(workspace.DirectoryPath);
        var catalog = Assert.Single((await app.SearchAsync(new SearchRequest("DR-100-24", SearchField.PartNumber, SearchMatchMode.Exact, PageSize: 25))).Items);
        var saved = await app.AddToMyLibraryAsync(catalog.Id, favorite: true);
        await app.CreateCollectionAsync("常用电源", new[] { saved.SavedPartId });

        var holding = Path.Combine(workspace.DirectoryPath, "holding");
        Directory.CreateDirectory(holding);
        File.Move(indexedPath, Path.Combine(holding, "明纬.edz"));
        await app.ScanLibraryAsync(workspace.DirectoryPath);
        var missing = Assert.Single((await app.SearchMyLibraryAsync(new MyLibraryRequest("DR-100-24", PageSize: 25))).Items);
        Assert.Equal(MyLibrarySourceStatus.SourceMissing, missing.SourceStatus);
        Assert.Equal("常用电源", missing.Collections);

        File.Move(Path.Combine(holding, "明纬.edz"), indexedPath);
        await app.ScanLibraryAsync(workspace.DirectoryPath);
        var rebound = Assert.Single((await app.SearchMyLibraryAsync(new MyLibraryRequest("DR-100-24", PageSize: 25))).Items);
        Assert.Equal(MyLibrarySourceStatus.Bound, rebound.SourceStatus);
        Assert.True(rebound.IsFavorite);
    }

    [Fact]
    public async Task Catalog_favorite_creates_saved_part_and_favorites_filter_is_database_backed()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateApplicationAsync(workspace, 2);
        var catalog = await app.SearchAsync(new SearchRequest(PageSize: 25));

        await app.SetCatalogFavoriteAsync(catalog.Items[0], true);

        var favorites = await app.SearchAsync(new SearchRequest(PageSize: 25, FavoritesOnly: true));
        var library = await app.SearchMyLibraryAsync(new MyLibraryRequest(FavoritesOnly: true, PageSize: 25));
        Assert.Single(favorites.Items);
        Assert.Single(library.Items);
        Assert.True(favorites.Items[0].IsFavorite);
        Assert.True(library.Items[0].IsFavorite);
    }

    [Fact]
    public async Task Backup_preview_reports_new_and_existing_then_restore_keeps_relationships()
    {
        using var sourceWorkspace = new TestWorkspace();
        var source = await CreateApplicationAsync(sourceWorkspace, 1);
        var catalog = Assert.Single((await source.SearchAsync(new SearchRequest(PageSize: 25))).Items);
        var saved = await source.AddToMyLibraryAsync(catalog.Id, true);
        await source.SaveNoteAsync(saved.SavedPartId, "公司A项目使用");
        await source.CreateCollectionAsync("项目A", new[] { saved.SavedPartId });
        await source.CreateTagAsync("待验证", new[] { saved.SavedPartId });
        var backup = Path.Combine(sourceWorkspace.DirectoryPath, "my-library.json");
        await source.ExportMyLibraryAsync(backup);
        var json = await File.ReadAllTextAsync(backup);
        await File.WriteAllTextAsync(backup, json.Replace("{", "{\n  \"futureField\": true,", StringComparison.Ordinal));

        var sourcePreview = await source.PreviewMyLibraryImportAsync(backup);
        Assert.Equal(1, sourcePreview.ExistingParts);
        Assert.True(sourcePreview.CanApply);

        using var targetWorkspace = new TestWorkspace();
        var target = new EdzManagerApplication(targetWorkspace.DatabasePath);
        await target.InitializeAsync();
        var targetPreview = await target.PreviewMyLibraryImportAsync(backup);
        Assert.Equal(1, targetPreview.NewParts);
        Assert.True(targetPreview.CanApply);
        await target.RestoreMyLibraryAsync(targetPreview);

        var restored = Assert.Single((await target.SearchMyLibraryAsync(new MyLibraryRequest(PageSize: 25))).Items);
        Assert.Equal("公司A项目使用", restored.Note);
        Assert.Equal("项目A", restored.Collections);
        Assert.Equal("待验证", restored.Tags);
        Assert.Equal(MyLibrarySourceStatus.SourceMissing, restored.SourceStatus);
    }

    [Fact]
    public async Task Backup_preview_rejects_duplicate_identity_without_writing_database()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateApplicationAsync(workspace, 1);
        var catalog = Assert.Single((await app.SearchAsync(new SearchRequest(PageSize: 25))).Items);
        await app.AddToMyLibraryAsync(catalog.Id);
        var backup = Path.Combine(workspace.DirectoryPath, "duplicate.json");
        await app.ExportMyLibraryAsync(backup);
        var json = await File.ReadAllTextAsync(backup);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        var part = root.GetProperty("library").GetProperty("savedParts")[0].GetRawText();
        var updated = json.Replace("\"savedParts\": [", "\"savedParts\": [" + part + ",", StringComparison.Ordinal);
        await File.WriteAllTextAsync(backup, updated);

        var preview = await app.PreviewMyLibraryImportAsync(backup);
        Assert.False(preview.CanApply);
        Assert.Equal(1, preview.DuplicateParts);
        await Assert.ThrowsAsync<InvalidDataException>(() => app.RestoreMyLibraryAsync(preview));
    }

    [Fact]
    public async Task Restore_rejects_backup_changed_after_preview()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateApplicationAsync(workspace, 1);
        var backup = Path.Combine(workspace.DirectoryPath, "changed.json");
        await app.ExportMyLibraryAsync(backup);
        var preview = await app.PreviewMyLibraryImportAsync(backup);
        Assert.True(preview.CanApply);

        await File.AppendAllTextAsync(backup, Environment.NewLine);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() => app.RestoreMyLibraryAsync(preview));
        Assert.Contains("changed after Preview", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preview_rejects_backup_over_64_mib_without_deserializing()
    {
        using var workspace = new TestWorkspace();
        var app = new EdzManagerApplication(workspace.DatabasePath);
        await app.InitializeAsync();
        var backup = Path.Combine(workspace.DirectoryPath, "oversized.json");
        await using (var stream = new FileStream(backup, FileMode.Create, FileAccess.Write, FileShare.None))
            stream.SetLength(64L * 1024 * 1024 + 1);

        var preview = await app.PreviewMyLibraryImportAsync(backup);

        Assert.False(preview.CanApply);
        Assert.Contains(preview.Errors, error => error.Contains("64 MiB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Selection_model_is_ui_independent_and_produces_pure_export_request()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateApplicationAsync(workspace, 1);
        var catalog = Assert.Single((await app.SearchAsync(new SearchRequest(PageSize: 25))).Items);
        var saved = await app.AddToMyLibraryAsync(catalog.Id);
        app.Selection.Select(new PartSelectionItem(saved.SavedPartId, saved.StableIdentity));

        var requests = await app.CreateExportRequestsAsync(app.Selection.Snapshot().Select(x => x.SavedPartId).ToArray());

        var request = Assert.Single(requests);
        Assert.Equal(saved.StableIdentity, request.StablePartIdentity);
        Assert.Equal("P-001", request.PartNumber);
        app.Selection.Clear();
        Assert.Equal(0, app.Selection.Count);
    }

    [Fact]
    public async Task Export_preflight_rejects_a_source_that_disappears_before_bridge_start()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateApplicationAsync(workspace, 1);
        var catalog = Assert.Single((await app.SearchAsync(new SearchRequest(PageSize: 25))).Items);
        var saved = await app.AddToMyLibraryAsync(catalog.Id);
        app.Selection.Select(new PartSelectionItem(saved.SavedPartId, saved.StableIdentity));
        File.Delete(catalog.EdzPath);

        var preparation = await app.PrepareSelectedPartsExportAsync(new[] { saved.SavedPartId });

        Assert.False(preparation.CanExport);
        Assert.Contains(preparation.Issues, issue => issue.Code == "EXPORT-SOURCE-MISSING");
        await Assert.ThrowsAsync<SelectedPartsExportValidationException>(() => app.CreateExportRequestsAsync(new[] { saved.SavedPartId }));
    }

    [Fact]
    public void Catalog_export_preflight_rejects_empty_and_missing_source_selection()
    {
        var empty = EdzManagerApplication.PrepareCatalogPartsExport(Array.Empty<PartSummary>());
        var missing = EdzManagerApplication.PrepareCatalogPartsExport(new[]
        {
            new PartSummary(1, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".edz"), "M", "P", null, null, null, null, "1", "P", string.Empty, 0, 0)
        });

        Assert.False(empty.CanExport);
        Assert.Contains(empty.Issues, issue => issue.Code == "EXPORT-EMPTY");
        Assert.False(missing.CanExport);
        Assert.Contains(missing.Issues, issue => issue.Code == "EXPORT-SOURCE-MISSING");
    }

    private static async Task<EdzManagerApplication> CreateApplicationAsync(TestWorkspace workspace, int partCount)
    {
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        var path = Path.Combine(workspace.DirectoryPath, "catalog.edz");
        await File.WriteAllTextAsync(path, "placeholder");
        var parts = Enumerable.Range(1, partCount).Select(index => new IndexedPartData(
            "OMRON", $"P-{index:000}", $"TYPE-{index:000}", $"ORDER-{index:000}", "PLC part", "PLC", "A", $"PKG-{index:000}",
            $"items/partxml/P-{index:000}.xml", new[] { new IndexedResourceData("picture", "front", "front.png", "items/picture/front.png", true, 10, 1) })).ToArray();
        await repository.ReplaceFileAsync(directory, new IndexedEdzData(path, 10, 10, parts));
        var app = new EdzManagerApplication(workspace.DatabasePath);
        await app.InitializeAsync();
        return app;
    }

    private static string FindSample(string name)
    {
        var fixtureRoot = Environment.GetEnvironmentVariable("EPLAN_EDZ_TEST_FIXTURE_DIR");
        if (!string.IsNullOrWhiteSpace(fixtureRoot))
        {
            var configured = Path.Combine(Path.GetFullPath(fixtureRoot), name);
            if (!File.Exists(configured)) throw new FileNotFoundException("Configured local fixture was not found.", configured);
            return configured;
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "samples", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Could not locate sample " + name);
    }
}
