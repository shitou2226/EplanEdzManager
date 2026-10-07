using EplanEdzManager.Application;
using EplanEdzManager.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using System.IO;
using Xunit;

namespace EplanEdzManager.Application.Tests;

public sealed class ApplicationServiceTests
{
    [Fact]
    public void Preview_selector_prefers_an_embedded_picture_and_rejects_non_image_macros()
    {
        var macro = new ResourceItem(1, 1, "sample.edz", "macro", "layout", "layout.ema", "items/macro/layout.ema", true, 20, 1, ResourceCategory.Macro);
        var renderedThreeDimensional = new ResourceItem(2, 1, "sample.edz", "gmacro", "3d-preview", "3d-preview.png", "items/gmacro/3d-preview.png", true, 30, 1, ResourceCategory.Mechanical);
        var picture = new ResourceItem(3, 1, "sample.edz", "picture", "front-picture", "front.jpg", "items/picture/front.jpg", true, 40, 1, ResourceCategory.Picture);

        Assert.Same(picture, PartPreviewSelector.SelectPreferred(new[] { macro, renderedThreeDimensional, picture }));
        Assert.Null(PartPreviewSelector.SelectPreferred(new[] { macro }));
    }

    [Fact]
    public async Task Search_cleans_EPLAN_multilingual_markers_from_an_existing_index()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.DirectoryPath, "legacy.edz");
        await File.WriteAllTextAsync(source, "test placeholder");
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directoryId = await repository.AddDirectoryAsync(workspace.DirectoryPath, recursive: false);
        var part = new IndexedPartData("AB", "1756-A10", "1756-A10", null, "??_??@Controllogix系列;", "1/26/0", "1", "1756-A10",
            "items/partxml/1756-A10.part.xml", Array.Empty<IndexedResourceData>());
        await repository.ReplaceFileAsync(directoryId, new IndexedEdzData(source, 10, 10, new[] { part }));
        var app = new EdzManagerApplication(workspace.DatabasePath);
        await app.InitializeAsync();

        var result = await app.SearchAsync(new SearchRequest("1756-A10", SearchField.PartNumber, SearchMatchMode.Exact));

        Assert.Equal("Controllogix系列", Assert.Single(result.Items).Description);
    }

    [Fact]
    public async Task Search_supports_pagination_manufacturer_filter_and_database_sorting()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateSeededApplicationAsync(workspace, partCount: 30);

        var first = await app.SearchAsync(new SearchRequest(PageSize: 25, SortColumn: SearchSortColumn.PartNumber, SortDirection: SearchSortDirection.Descending));
        var second = await app.SearchAsync(new SearchRequest(Page: 2, PageSize: 25, SortColumn: SearchSortColumn.PartNumber, SortDirection: SearchSortDirection.Descending));
        var filtered = await app.SearchAsync(new SearchRequest(Manufacturer: "OMRON", PageSize: 100));

        Assert.Equal(30, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Equal(5, second.Items.Count);
        Assert.Equal("P-030", first.Items[0].PartNumber);
        Assert.Equal(15, filtered.TotalCount);
        Assert.All(filtered.Items, item => Assert.Equal("OMRON", item.Manufacturer));
    }

    [Fact]
    public async Task Exact_field_search_returns_only_the_matching_part()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateSeededApplicationAsync(workspace, 3);

        var result = await app.SearchAsync(new SearchRequest("P-002", SearchField.PartNumber, SearchMatchMode.Exact));

        Assert.Single(result.Items);
        Assert.Equal("P-002", result.Items[0].PartNumber);
    }

    [Fact]
    public async Task Part_detail_returns_resources_and_reports_missing_source()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateSeededApplicationAsync(workspace, 1, createSourceFile: false);
        var result = await app.SearchAsync(new SearchRequest());

        var part = Assert.Single(result.Items);
        var detail = await app.GetPartDetailAsync(part);

        Assert.True(part.SourceMissing);
        Assert.Equal(2, detail.Resources.Count);
        Assert.Single(detail.Resources, item => item.Category == ResourceCategory.Picture);
        Assert.Single(detail.Resources, item => item.Category == ResourceCategory.Macro);
    }

    [Fact]
    public async Task Manufacturer_and_product_group_aggregates_are_dynamic()
    {
        using var workspace = new TestWorkspace();
        var app = await CreateSeededApplicationAsync(workspace, 4);

        var manufacturers = await app.GetManufacturersAsync();
        var groups = await app.GetProductGroupsAsync();

        Assert.Equal(2, manufacturers.Single(item => item.Value == "OMRON").Count);
        Assert.Equal(4, groups.Single(item => item.Value == "Real metadata group").Count);
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task Scan_reports_progress_and_can_be_cancelled()
    {
        using var workspace = new TestWorkspace();
        var sample = FindSample("明纬.edz");
        File.Copy(sample, Path.Combine(workspace.DirectoryPath, "明纬.edz"));
        var app = new EdzManagerApplication(workspace.DatabasePath);
        await app.InitializeAsync();
        await app.AddLibraryAsync(workspace.DirectoryPath, recursive: false);
        var reports = new List<ScanProgressInfo>();

        var summary = await app.ScanLibraryAsync(workspace.DirectoryPath, new InlineProgress<ScanProgressInfo>(reports.Add));

        Assert.Equal(1, summary.Added);
        Assert.Equal(26, summary.PartsIndexed);
        Assert.Contains(reports, item => item.Added == 1);

        var search = await app.SearchAsync(new SearchRequest("DR-100-24", SearchField.PartNumber, SearchMatchMode.Exact));
        var part = Assert.Single(search.Items);
        var detail = await app.GetPartDetailAsync(part);
        var picture = Assert.Single(detail.Resources, item => item.Category == ResourceCategory.Picture);
        var macro = Assert.Single(detail.Resources, item => item.Category == ResourceCategory.Macro && item.Name == "groupsymbolmacro");
        var pictureBytes = await app.ReadResourceAsync(picture);
        var rawMetadata = await app.ReadRawMetadataAsync(part);
        var exportedMacro = Path.Combine(workspace.DirectoryPath, "exported.ema");
        await app.ExportResourceAsync(macro, exportedMacro);

        Assert.NotEmpty(pictureBytes);
        Assert.Contains("DR-100-24", rawMetadata, StringComparison.OrdinalIgnoreCase);
        Assert.True(new FileInfo(exportedMacro).Length > 0);

        var movedSource = Path.Combine(workspace.DirectoryPath, "明纬-moved.edz");
        File.Move(Path.Combine(workspace.DirectoryPath, "明纬.edz"), movedSource);
        var missingResult = await app.SearchAsync(new SearchRequest("DR-100-24", SearchField.PartNumber, SearchMatchMode.Exact));
        Assert.True(Assert.Single(missingResult.Items).SourceMissing);
        File.Move(movedSource, Path.Combine(workspace.DirectoryPath, "明纬.edz"));
        var restored = await app.ScanLibraryAsync(workspace.DirectoryPath);
        Assert.Equal(1, restored.Unchanged);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.ScanLibraryAsync(workspace.DirectoryPath, cancellationToken: cancellation.Token));
    }

    [Fact]
    [Trait("Category", "LocalFixture")]
    public async Task Offline_workflows_support_read_only_unicode_and_space_source_paths()
    {
        using var workspace = new TestWorkspace();
        var library = Path.Combine(workspace.DirectoryPath, "EPLAN Parts Library 中文 (只读)-_");
        Directory.CreateDirectory(library);
        var source = Path.Combine(library, "明纬 部件 (read-only)-_.edz");
        File.Copy(FindSample("明纬.edz"), source);
        var originalHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source)));
        var originalWriteTime = File.GetLastWriteTimeUtc(source);
        File.SetAttributes(source, File.GetAttributes(source) | FileAttributes.ReadOnly);
        try
        {
            var app = new EdzManagerApplication(Path.Combine(workspace.DirectoryPath, "missing", "数据库 index.db"));
            await app.InitializeAsync();
            await app.AddLibraryAsync(library, recursive: false);

            var summary = await app.ScanLibraryAsync(library);
            var search = await app.SearchAsync(new SearchRequest("DR-100-24", SearchField.PartNumber, SearchMatchMode.Exact));
            var part = Assert.Single(search.Items);
            var detail = await app.GetPartDetailAsync(part);
            var picture = Assert.Single(detail.Resources, item => item.Category == ResourceCategory.Picture);
            var resourceBytes = await app.ReadResourceAsync(picture);
            var exportPath = Path.Combine(workspace.DirectoryPath, "导出 资源 (preview)-_." + Path.GetExtension(picture.ArchivePath!).TrimStart('.'));
            await app.ExportResourceAsync(picture, exportPath);

            Assert.Equal(1, summary.Added);
            Assert.NotEmpty(resourceBytes);
            Assert.True(new FileInfo(exportPath).Length > 0);
            Assert.Equal(originalHash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(source))));
            Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(source));
            Assert.True(File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            if (File.Exists(source)) File.SetAttributes(source, FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task Removing_library_only_removes_index_registration()
    {
        using var workspace = new TestWorkspace();
        var source = Path.Combine(workspace.DirectoryPath, "keep.edz");
        await File.WriteAllTextAsync(source, "not a real archive");
        var app = new EdzManagerApplication(workspace.DatabasePath);
        await app.InitializeAsync();
        await app.AddLibraryAsync(workspace.DirectoryPath);

        await app.RemoveLibraryAsync(workspace.DirectoryPath);

        Assert.True(File.Exists(source));
        Assert.Empty(await app.GetLibrariesAsync());
    }

    private static async Task<EdzManagerApplication> CreateSeededApplicationAsync(TestWorkspace workspace, int partCount, bool createSourceFile = true)
    {
        var source = Path.Combine(workspace.DirectoryPath, "catalog.edz");
        if (createSourceFile) await File.WriteAllTextAsync(source, "test placeholder");
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directoryId = await repository.AddDirectoryAsync(workspace.DirectoryPath, recursive: false);
        var parts = Enumerable.Range(1, partCount).Select(index => new IndexedPartData(
            index % 2 == 0 ? "OMRON" : "MEAN WELL",
            $"P-{index:000}",
            $"T-{index:000}",
            $"O-{index:000}",
            "Indexed part " + index,
            "Real metadata group",
            "1",
            $"P-{index:000}",
            $"items/partxml/P-{index:000}.part.xml",
            new[]
            {
                new IndexedResourceData("picture", "picturefile", $"P-{index:000}.jpg", $"items/picture/P-{index:000}.jpg", true, 100, 1),
                new IndexedResourceData("macro", "groupsymbolmacro", $"P-{index:000}.ema", $"items/macro/P-{index:000}.ema", true, 200, 1)
            })).ToArray();
        await repository.ReplaceFileAsync(directoryId, new IndexedEdzData(source, 10, 10, parts));
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

internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "index.db");
    }

    public string DirectoryPath { get; }
    public string DatabasePath { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
