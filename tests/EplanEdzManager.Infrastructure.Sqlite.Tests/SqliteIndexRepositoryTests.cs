using EplanEdzManager.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EplanEdzManager.Infrastructure.Sqlite.Tests;

public sealed class SqliteIndexRepositoryTests
{
    [Fact]
    public async Task Migration_is_idempotent_and_search_supports_all_requested_filters()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        await repository.InitializeAsync();
        var directoryId = await repository.AddDirectoryAsync(workspace.DirectoryPath, recursive: false);
        var source = Path.Combine(workspace.DirectoryPath, "catalog.edz");
        await repository.ReplaceFileAsync(directoryId, CreateDocument(source));

        var fullText = await repository.SearchAsync(new PartSearchQuery(Text: "DR-100-24"));
        Assert.Single(fullText);
        Assert.Equal("DR-100-24", fullText[0].PartNumber);

        var manufacturer = await repository.SearchAsync(new PartSearchQuery(Manufacturer: "MEAN"));
        Assert.Single(manufacturer);

        var partNumber = await repository.SearchAsync(new PartSearchQuery(PartNumber: "100-24"));
        Assert.Single(partNumber);

        var typeNumber = await repository.SearchAsync(new PartSearchQuery(TypeNumber: "DIN"));
        Assert.Single(typeNumber);

        var allResources = await repository.SearchAsync(new PartSearchQuery(ResourceFilter: ResourceExistenceFilter.AllPresent));
        Assert.Single(allResources);
        Assert.Equal("DR-100-24", allResources[0].PartNumber);

        var missingResources = await repository.SearchAsync(new PartSearchQuery(ResourceFilter: ResourceExistenceFilter.Missing));
        Assert.Single(missingResources);
        Assert.Equal("BROKEN-1", missingResources[0].PartNumber);

        var noResources = await repository.SearchAsync(new PartSearchQuery(ResourceFilter: ResourceExistenceFilter.None));
        Assert.Single(noResources);
        Assert.Equal("EMPTY-1", noResources[0].PartNumber);

        var statistics = await repository.GetStatisticsAsync();
        Assert.Equal(new IndexStatistics(1, 1, 0, 3, 3), statistics);
    }

    [Fact]
    public async Task Replacing_and_pruning_a_file_removes_stale_parts_and_fts_rows()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directoryId = await repository.AddDirectoryAsync(workspace.DirectoryPath, recursive: false);
        var source = Path.Combine(workspace.DirectoryPath, "catalog.edz");
        await repository.ReplaceFileAsync(directoryId, CreateDocument(source));

        var replacement = new IndexedEdzData(source, 20, 200, new[]
        {
            CreatePart("NEW-1", "New description", Array.Empty<IndexedResourceData>())
        });
        await repository.ReplaceFileAsync(directoryId, replacement);

        Assert.Empty(await repository.SearchAsync(new PartSearchQuery(Text: "DR-100-24")));
        Assert.Single(await repository.SearchAsync(new PartSearchQuery(Text: "NEW-1")));

        var removed = await repository.RemoveMissingFilesAsync(directoryId, Array.Empty<string>());
        Assert.Equal(1, removed);
        Assert.Empty(await repository.SearchAsync(new PartSearchQuery(Text: "NEW-1")));
        Assert.Equal(0, (await repository.GetStatisticsAsync()).FileCount);
    }

    [Fact]
    public async Task Integrity_check_reports_clean_database_and_foreign_key_violation()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        await repository.InitializeAsync();

        var clean = await repository.CheckIntegrityAsync();
        Assert.True(clean.IntegrityOk);
        Assert.Equal(0, clean.ForeignKeyViolationCount);

        await using (var connection = new SqliteConnection("Data Source=" + workspace.DatabasePath))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF; INSERT INTO my_collection_items(collection_id,saved_part_id,added_utc) VALUES(999999,999999,'2026-10-04T00:00:00Z');";
            await command.ExecuteNonQueryAsync();
        }

        var broken = await repository.CheckIntegrityAsync();
        Assert.True(broken.IntegrityOk);
        Assert.Equal(2, broken.ForeignKeyViolationCount);
        Assert.Contains(broken.Messages, message => message.Contains("my_collection_items", StringComparison.Ordinal));
    }

    internal static IndexedEdzData CreateDocument(string path)
    {
        return new IndexedEdzData(path, 10, 100, new[]
        {
            CreatePart("DR-100-24", "DIN rail power supply", new[]
            {
                new IndexedResourceData("picture", "front", "front.png", "items/picture/front.png", true, 100, 1),
                new IndexedResourceData("macro", "main", "main.ema", "items/macro/main.ema", true, 200, 1)
            }),
            CreatePart("BROKEN-1", "Missing resource", new[]
            {
                new IndexedResourceData("picture", "missing", "missing.png", "items/picture/missing.png", false, null, 1)
            }) with { Manufacturer = "OTHER", TypeNumber = "PANEL" },
            CreatePart("EMPTY-1", "No resources", Array.Empty<IndexedResourceData>()) with { Manufacturer = "OTHER", TypeNumber = "PANEL" }
        });
    }

    internal static IndexedPartData CreatePart(string partNumber, string description, IReadOnlyList<IndexedResourceData> resources)
    {
        return new IndexedPartData(
            "MEAN WELL",
            partNumber,
            "DIN-24V",
            partNumber,
            description,
            "Power supply",
            "1",
            partNumber,
            "items/partxml/" + partNumber + ".xml",
            resources);
    }
}

internal sealed class TemporaryWorkspace : IDisposable
{
    public TemporaryWorkspace()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "EplanEdzManager.Sqlite.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        DatabasePath = Path.Combine(DirectoryPath, "index.db");
    }

    public string DirectoryPath { get; }
    public string DatabasePath { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DirectoryPath))
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
    }
}
