using EplanEdzManager.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EplanEdzManager.Infrastructure.Sqlite.Tests;

public sealed class MyLibraryRepositoryTests
{
    [Fact]
    public async Task Reset_catalog_index_preserves_user_library_metadata_and_registered_folder()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        await repository.ReplaceFileAsync(directory, Document(Path.Combine(workspace.DirectoryPath, "reset.edz"), "P-RESET", "A"));
        var catalog = Assert.Single(await repository.SearchAsync(new PartSearchQuery()));
        var saved = await repository.SaveCatalogPartAsync(catalog.Id, true);
        await repository.UpdateSavedPartNoteAsync(saved.SavedPartId, "keep me");
        await repository.CreateCollectionAsync("keep collection", new[] { saved.SavedPartId });
        await repository.CreateTagAsync("keep tag", new[] { saved.SavedPartId });

        await repository.ResetCatalogIndexAsync();

        Assert.Equal(0, await repository.CountSearchAsync(new PartSearchQuery()));
        var preserved = Assert.Single(await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery()));
        Assert.True(preserved.Favorite);
        Assert.Equal("keep me", preserved.Note);
        Assert.Equal("keep collection", preserved.Collections);
        Assert.Equal("keep tag", preserved.Tags);
        Assert.Single(await repository.GetDirectoriesAsync());
    }

    [Fact]
    public async Task Same_logical_part_from_two_edz_files_has_one_saved_part_and_two_sources()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        await repository.ReplaceFileAsync(directory, Document(Path.Combine(workspace.DirectoryPath, "siemens-2025.edz"), "6ES7-214", "A"));
        await repository.ReplaceFileAsync(directory, Document(Path.Combine(workspace.DirectoryPath, "siemens-2026.edz"), "6ES7-214", "A"));
        var catalog = await repository.SearchAsync(new PartSearchQuery(Text: "6ES7-214"));

        var saved = await repository.SaveCatalogPartsAsync(catalog.Select(x => x.Id).ToArray(), false);
        var library = await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery());
        var sources = await repository.GetSavedPartSourcesAsync(saved[0].SavedPartId);

        Assert.Equal(2, catalog.Count);
        Assert.Single(library);
        Assert.Equal(2, sources.Count);
        Assert.Equal(SavedPartSourceState.MultipleCandidates, library[0].SourceState);
        Assert.Single(sources, x => x.IsPreferred);
    }

    [Fact]
    public async Task Favorite_note_collections_and_tags_support_bulk_many_to_many_operations()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        await repository.ReplaceFileAsync(directory, Document(Path.Combine(workspace.DirectoryPath, "parts.edz"), "P-1", "A", "P-2"));
        var catalog = await repository.SearchAsync(new PartSearchQuery(Limit: 100));
        var saved = await repository.SaveCatalogPartsAsync(catalog.Select(x => x.Id).ToArray(), false);
        var ids = saved.Select(x => x.SavedPartId).ToArray();
        var common = await repository.CreateCollectionAsync("控制柜通用件", ids);
        var project = await repository.CreateCollectionAsync("项目A", new[] { ids[0] });
        var plc = await repository.CreateTagAsync("PLC", ids);
        var voltage = await repository.CreateTagAsync("24V", new[] { ids[0] });
        await repository.SetSavedPartFavoriteAsync(ids[0], true);
        await repository.SetSavedPartFavoriteAsync(ids[0], false);
        Assert.Equal(0, await repository.CountMyLibraryAsync(new MyLibrarySearchQuery(FavoritesOnly: true)));
        await repository.SetSavedPartFavoriteAsync(ids[0], true);
        await repository.UpdateSavedPartNoteAsync(ids[0], "已验证宏正常");
        await repository.AddSavedPartsToCollectionAsync(project, new[] { ids[1] });
        await repository.RemoveTagsFromSavedPartsAsync(voltage, new[] { ids[0] });

        var first = Assert.Single(await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery(Text: "已验证")));
        Assert.True(first.Favorite);
        Assert.Contains("控制柜通用件", first.Collections);
        Assert.Contains("项目A", first.Collections);
        Assert.Contains("PLC", first.Tags);
        Assert.DoesNotContain("24V", first.Tags);
        Assert.Equal(2, (await repository.GetCollectionsAsync()).Single(x => x.Id == common).Count);
        Assert.Equal(2, (await repository.GetTagsAsync()).Single(x => x.Id == plc).Count);
        Assert.Equal(2, await repository.DeleteSavedPartsAsync(ids));
        Assert.Equal(0, await repository.CountMyLibraryAsync(new MyLibrarySearchQuery()));
        Assert.Equal(2, await repository.CountSearchAsync(new PartSearchQuery()));
    }

    [Fact]
    public async Task Source_deletion_does_not_delete_saved_part_and_rebind_recovers_it()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        var path = Path.Combine(workspace.DirectoryPath, "movable.edz");
        await repository.ReplaceFileAsync(directory, Document(path, "P-1", "A"));
        var catalog = Assert.Single(await repository.SearchAsync(new PartSearchQuery()));
        var saved = await repository.SaveCatalogPartAsync(catalog.Id, true);
        await repository.RemoveMissingFilesAsync(directory, Array.Empty<string>());

        var missing = Assert.Single(await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery()));
        Assert.Equal(SavedPartSourceState.SourceMissing, missing.SourceState);
        Assert.Equal(saved.SavedPartId, missing.Id);

        await repository.ReplaceFileAsync(directory, Document(path, "P-1", "A"));
        var rebound = await repository.RebindSavedPartsAsync();
        var available = Assert.Single(await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery()));
        Assert.Equal(1, rebound.Bound);
        Assert.Equal(SavedPartSourceState.Bound, available.SourceState);
        Assert.True(available.Favorite);
    }

    [Fact]
    public async Task Preferred_source_is_explicit_and_survives_rebind()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        await repository.ReplaceFileAsync(directory, Document(Path.Combine(workspace.DirectoryPath, "old.edz"), "P-1", "A"));
        await repository.ReplaceFileAsync(directory, Document(Path.Combine(workspace.DirectoryPath, "new.edz"), "P-1", "A"));
        var catalog = await repository.SearchAsync(new PartSearchQuery());
        var saved = await repository.SaveCatalogPartsAsync(catalog.Select(x => x.Id).ToArray(), false);
        var sources = await repository.GetSavedPartSourcesAsync(saved[0].SavedPartId);
        var chosen = sources.Single(x => x.EdzPath.EndsWith("new.edz", StringComparison.OrdinalIgnoreCase));
        await repository.SetPreferredSourceAsync(saved[0].SavedPartId, chosen.Id);
        await repository.RebindSavedPartsAsync();

        Assert.True((await repository.GetSavedPartSourcesAsync(saved[0].SavedPartId)).Single(x => x.Id == chosen.Id).IsPreferred);
    }

    [Fact]
    public async Task Snapshot_restore_preserves_notes_tags_collections_and_sources()
    {
        using var sourceWorkspace = new TemporaryWorkspace();
        var source = new SqliteIndexRepository(sourceWorkspace.DatabasePath);
        var directory = await source.AddDirectoryAsync(sourceWorkspace.DirectoryPath, false);
        await source.ReplaceFileAsync(directory, Document(Path.Combine(sourceWorkspace.DirectoryPath, "backup.edz"), "P-1", "A"));
        var catalog = Assert.Single(await source.SearchAsync(new PartSearchQuery()));
        var saved = await source.SaveCatalogPartAsync(catalog.Id, true);
        await source.UpdateSavedPartNoteAsync(saved.SavedPartId, "优先采购");
        await source.CreateCollectionAsync("常用PLC", new[] { saved.SavedPartId });
        await source.CreateTagAsync("Modbus", new[] { saved.SavedPartId });
        var snapshot = await source.GetMyLibrarySnapshotAsync();

        using var targetWorkspace = new TemporaryWorkspace();
        var target = new SqliteIndexRepository(targetWorkspace.DatabasePath);
        await target.RestoreMyLibrarySnapshotAsync(snapshot);
        var restored = Assert.Single(await target.SearchMyLibraryAsync(new MyLibrarySearchQuery()));

        Assert.True(restored.Favorite);
        Assert.Equal("优先采购", restored.Note);
        Assert.Equal("常用PLC", restored.Collections);
        Assert.Equal("Modbus", restored.Tags);
        Assert.Equal(SavedPartSourceState.SourceMissing, restored.SourceState);
    }

    [Fact]
    public async Task Favorite_update_and_catalog_rescan_can_run_concurrently_without_losing_saved_part()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        var directory = await repository.AddDirectoryAsync(workspace.DirectoryPath, false);
        var path = Path.Combine(workspace.DirectoryPath, "concurrent.edz");
        await repository.ReplaceFileAsync(directory, Document(path, "P-1", "A"));
        var catalog = Assert.Single(await repository.SearchAsync(new PartSearchQuery()));
        var saved = await repository.SaveCatalogPartAsync(catalog.Id, false);

        await Task.WhenAll(
            repository.ReplaceFileAsync(directory, Document(path, "P-1", "A")),
            repository.SetSavedPartFavoriteAsync(saved.SavedPartId, true));
        await repository.RebindSavedPartsAsync();

        var row = Assert.Single(await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery()));
        Assert.True(row.Favorite);
        Assert.Equal(SavedPartSourceState.Bound, row.SourceState);
    }

    [Fact]
    public async Task V1_database_migrates_to_v2_without_rebuild_and_backfills_identity()
    {
        using var workspace = new TemporaryWorkspace();
        var migrationPath = FindRepositoryFile("src", "EplanEdzManager.Infrastructure.Sqlite", "Migrations", "001_initial.sql");
        await using (var connection = new SqliteConnection($"Data Source={workspace.DatabasePath}"))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = await File.ReadAllTextAsync(migrationPath) + "\nCREATE TABLE schema_migrations(version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL); INSERT INTO schema_migrations VALUES(1,'001_initial.sql','2026-01-01T00:00:00.0000000Z');";
            await command.ExecuteNonQueryAsync();
            command.CommandText = "INSERT INTO indexed_directories(id,path,recursive,enabled,added_utc) VALUES(1,$path,0,1,'2026-01-01T00:00:00.0000000Z'); INSERT INTO edz_files(id,directory_id,path,file_size,last_write_utc_ticks,status,indexed_utc) VALUES(1,1,$file,1,1,'Indexed','2026-01-01T00:00:00.0000000Z'); INSERT INTO parts(id,edz_file_id,manufacturer,part_number,variant,package_key,raw_metadata_reference,resource_count,existing_resource_count) VALUES(1,1,'OMRON','CJ2M','A','pkg','part.xml',0,0);";
            command.Parameters.AddWithValue("$path", workspace.DirectoryPath);
            command.Parameters.AddWithValue("$file", Path.Combine(workspace.DirectoryPath, "v1.edz"));
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        var part = Assert.Single(await repository.SearchAsync(new PartSearchQuery()));
        var saved = await repository.SaveCatalogPartAsync(part.Id, false);

        Assert.StartsWith("LP1:", saved.StableIdentity);
        await using var verify = new SqliteConnection($"Data Source={workspace.DatabasePath}");
        await verify.OpenAsync();
        var version = verify.CreateCommand();
        version.CommandText = "SELECT MAX(version) FROM schema_migrations;";
        Assert.Equal(2L, (long)(await version.ExecuteScalarAsync())!);
    }

    private static IndexedEdzData Document(string path, string partNumber, string variant, string? secondPart = null)
    {
        var parts = new List<IndexedPartData> { Part(partNumber, variant) };
        if (secondPart is not null) parts.Add(Part(secondPart, variant));
        return new IndexedEdzData(path, 100, DateTime.UtcNow.Ticks, parts);
    }

    private static IndexedPartData Part(string number, string variant) => new("SIEMENS", number, "TYPE-" + number,
        "ORDER-" + number, "Automation part " + number, "PLC", variant, "PKG-" + number, "items/partxml/" + number + ".xml",
        new[] { new IndexedResourceData("picture", "front", "front.png", "items/picture/front.png", true, 10, 1) });

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = segments.Aggregate(directory.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
