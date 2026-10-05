using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace EplanEdzManager.Infrastructure.Sqlite.Tests;

public sealed class Phase3PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task Catalog_100k_and_saved_library_20k_keep_database_operations_bounded()
    {
        using var workspace = new TemporaryWorkspace();
        var repository = new SqliteIndexRepository(workspace.DatabasePath);
        await repository.InitializeAsync();
        await SeedAsync(workspace.DatabasePath, workspace.DirectoryPath);

        var metrics = new Dictionary<string, long>();
        metrics["my_library_page_ms"] = await MeasureAsync(async () =>
        {
            var rows = await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery(Limit: 200));
            Assert.Equal(200, rows.Count);
        });
        metrics["favorite_toggle_ms"] = await MeasureAsync(() => repository.SetSavedPartFavoriteAsync(10_000, true));
        metrics["collection_filter_ms"] = await MeasureAsync(async () =>
        {
            var rows = await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery(CollectionId: 1, Limit: 200));
            Assert.Equal(200, rows.Count);
        });
        metrics["tag_filter_ms"] = await MeasureAsync(async () =>
        {
            var rows = await repository.SearchMyLibraryAsync(new MyLibrarySearchQuery(TagId: 1, Limit: 200));
            Assert.Equal(200, rows.Count);
        });
        metrics["stable_identity_lookup_ms"] = await MeasureAsync(async () =>
        {
            var result = await repository.SaveCatalogPartAsync(99_999, false);
            Assert.Equal("LP1:PERF099999", result.StableIdentity);
        });
        metrics["source_rebind_ms"] = await MeasureAsync(async () =>
        {
            var result = await repository.RebindSavedPartsAsync();
            Assert.Equal(20_001, result.Bound);
        });
        metrics["catalog_fts_ms"] = await MeasureAsync(async () =>
        {
            var rows = await repository.SearchAsync(new PartSearchQuery(Text: "P-099998", Limit: 25));
            Assert.Single(rows);
        });

        foreach (var metric in metrics) output.WriteLine($"{metric.Key}={metric.Value}");
        Assert.True(metrics["my_library_page_ms"] < 5_000);
        Assert.True(metrics["favorite_toggle_ms"] < 5_000);
        Assert.True(metrics["collection_filter_ms"] < 5_000);
        Assert.True(metrics["tag_filter_ms"] < 5_000);
        Assert.True(metrics["stable_identity_lookup_ms"] < 5_000);
        Assert.True(metrics["source_rebind_ms"] < 30_000);
        Assert.True(metrics["catalog_fts_ms"] < 5_000);
    }

    private static async Task SeedAsync(string databasePath, string directoryPath)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath}");
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys=ON;
            INSERT INTO indexed_directories(id,path,recursive,enabled,added_utc) VALUES(1,$directory,0,1,$utc);
            INSERT INTO edz_files(id,directory_id,path,file_size,last_write_utc_ticks,indexed_utc,status)
              VALUES(1,1,$source,1,1,$utc,'Indexed');
            WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM numbers WHERE value<100000)
            INSERT INTO parts(id,edz_file_id,manufacturer,part_number,type_number,order_number,description,product_group,variant,package_key,
              raw_metadata_reference,resource_count,existing_resource_count,logical_identity,identity_kind)
            SELECT value,1,CASE WHEN value%2=0 THEN 'OMRON' ELSE 'SIEMENS' END,printf('P-%06d',value),printf('T-%06d',value),
              printf('O-%06d',value),'Performance fixture','PLC','A',printf('PKG-%06d',value),printf('parts/%06d.xml',value),1,1,
              printf('LP1:PERF%06d',value),'Primary' FROM numbers;
            WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM numbers WHERE value<20000)
            INSERT INTO my_saved_parts(id,stable_identity,identity_kind,manufacturer,part_number,variant,type_number_snapshot,description_snapshot,
              note,favorite,created_utc,updated_utc)
            SELECT value,printf('LP1:PERF%06d',value),'Primary',CASE WHEN value%2=0 THEN 'OMRON' ELSE 'SIEMENS' END,
              printf('P-%06d',value),'A',printf('T-%06d',value),'Performance fixture','',value%7=0,$utc,$utc FROM numbers;
            WITH RECURSIVE numbers(value) AS (SELECT 1 UNION ALL SELECT value+1 FROM numbers WHERE value<20000)
            INSERT INTO my_saved_part_sources(saved_part_id,source_instance_identity,current_part_id,edz_path_snapshot,package_key_snapshot,
              raw_metadata_reference_snapshot,resource_count_snapshot,existing_resource_count_snapshot,is_preferred,first_seen_utc,last_seen_utc)
            SELECT value,printf('SI1:PERF%06d',value),value,$source,printf('PKG-%06d',value),printf('parts/%06d.xml',value),1,1,1,$utc,$utc FROM numbers;
            INSERT INTO my_collections(id,name,created_utc,updated_utc) VALUES(1,'控制柜',$utc,$utc);
            INSERT INTO my_tags(id,name,created_utc,updated_utc) VALUES(1,'24V',$utc,$utc);
            INSERT INTO my_collection_items(collection_id,saved_part_id,added_utc) SELECT 1,id,$utc FROM my_saved_parts WHERE id%2=0;
            INSERT INTO my_saved_part_tags(tag_id,saved_part_id,added_utc) SELECT 1,id,$utc FROM my_saved_parts WHERE id%3=0;
            """;
        command.Parameters.AddWithValue("$directory", directoryPath);
        command.Parameters.AddWithValue("$source", Path.Combine(directoryPath, "performance.edz"));
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> MeasureAsync(Func<Task> action)
    {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }
}
