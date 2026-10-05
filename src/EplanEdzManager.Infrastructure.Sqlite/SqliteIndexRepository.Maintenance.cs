using Microsoft.Data.Sqlite;

namespace EplanEdzManager.Infrastructure.Sqlite;

public sealed partial class SqliteIndexRepository
{
    public async Task<DatabaseIntegritySnapshot> CheckIntegrityAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var messages = new List<string>();
        var integrityOk = true;
        var foreignKeyViolations = 0;
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check(100);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var message = reader.GetString(0);
                if (string.Equals(message, "ok", StringComparison.OrdinalIgnoreCase)) continue;
                integrityOk = false;
                messages.Add(message);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_key_check;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                foreignKeyViolations++;
                if (messages.Count < 100)
                    messages.Add("Foreign key: table=" + reader.GetString(0) + "; rowid=" + reader.GetInt64(1) +
                        "; parent=" + reader.GetString(2) + "; fk=" + reader.GetInt32(3));
            }
        }

        return new DatabaseIntegritySnapshot(integrityOk, foreignKeyViolations, messages);
    }

    public async Task<DatabaseMaintenanceSnapshot> GetMaintenanceSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              COALESCE((SELECT MAX(version) FROM schema_migrations), 0),
              (SELECT COUNT(*) FROM parts),
              (SELECT COUNT(*) FROM resources),
              (SELECT COUNT(*) FROM my_saved_parts),
              (SELECT COUNT(*) FROM my_collections),
              (SELECT COUNT(*) FROM my_tags),
              (SELECT COUNT(*) FROM resources WHERE exists_in_archive=0);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var size = File.Exists(_databasePath) ? new FileInfo(_databasePath).Length : 0;
        return new DatabaseMaintenanceSnapshot(_databasePath, size, reader.GetInt32(0), reader.GetInt32(1),
            reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6));
    }

    public async Task<IReadOnlyList<IndexedFileState>> GetIndexedFilesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<IndexedFileState>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id,directory_id,path,file_size,last_write_utc_ticks,status,error FROM edz_files ORDER BY path COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new IndexedFileState(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetString(5), GetNullableString(reader, 6)));
        return result;
    }

    public async Task VacuumAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "VACUUM;", cancellationToken).ConfigureAwait(false);
    }

    public async Task OptimizeAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, null, "PRAGMA optimize; ANALYZE;", cancellationToken).ConfigureAwait(false);
    }

    public async Task ResetCatalogIndexAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(connection, transaction, "DELETE FROM edz_files; UPDATE indexed_directories SET last_scan_utc=NULL;", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
