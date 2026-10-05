using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using EplanEdzManager.Core.Identity;
using Microsoft.Data.Sqlite;

namespace EplanEdzManager.Infrastructure.Sqlite;

public sealed partial class SqliteIndexRepository
{
    private static readonly Regex SearchToken = new Regex(@"[\p{L}\p{N}_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initializationGate = new SemaphoreSlim(1, 1);
    private volatile bool _initialized;

    public SqliteIndexRepository(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("A database path is required.", nameof(databasePath));
        }

        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var parent = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, null, "PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                null,
                "CREATE TABLE IF NOT EXISTS schema_migrations (version INTEGER PRIMARY KEY, name TEXT NOT NULL, applied_utc TEXT NOT NULL);",
                cancellationToken).ConfigureAwait(false);

            var applied = new HashSet<int>();
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT version FROM schema_migrations;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    applied.Add(reader.GetInt32(0));
                }
            }

            foreach (var migration in LoadMigrations())
            {
                if (applied.Contains(migration.Version))
                {
                    continue;
                }

                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteNonQueryAsync(connection, transaction, migration.Sql, cancellationToken).ConfigureAwait(false);
                await using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText = "INSERT INTO schema_migrations(version, name, applied_utc) VALUES ($version, $name, $utc);";
                record.Parameters.AddWithValue("$version", migration.Version);
                record.Parameters.AddWithValue("$name", migration.Name);
                record.Parameters.AddWithValue("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow));
                await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            await BackfillPartIdentitiesAsync(connection, cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<long> AddDirectoryAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var normalized = NormalizeDirectoryPath(path);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO indexed_directories(path, recursive, enabled, added_utc)
            VALUES ($path, $recursive, 1, $utc)
            ON CONFLICT(path) DO UPDATE SET recursive = excluded.recursive, enabled = 1
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$recursive", recursive ? 1 : 0);
        command.Parameters.AddWithValue("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<IndexedDirectory>> GetDirectoriesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<IndexedDirectory>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, recursive, enabled, added_utc, last_scan_utc FROM indexed_directories ORDER BY path;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexedDirectory(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2) != 0,
                reader.GetInt64(3) != 0,
                ParseDatabaseTimestamp(reader.GetString(4)),
                reader.IsDBNull(5) ? null : ParseDatabaseTimestamp(reader.GetString(5))));
        }

        return results;
    }

    public async Task<bool> RemoveDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM indexed_directories WHERE path = $path;";
        command.Parameters.AddWithValue("$path", NormalizeDirectoryPath(path));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IndexedFileState?> GetFileStateAsync(string path, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, directory_id, path, file_size, last_write_utc_ticks, status, error FROM edz_files WHERE path = $path;";
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new IndexedFileState(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    public async Task ReplaceFileAsync(long directoryId, IndexedEdzData data, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fileId = await UpsertFileAsync(connection, transaction, directoryId, data.Path, data.FileSize, data.LastWriteUtcTicks, "Indexed", null, cancellationToken).ConfigureAwait(false);
        await DeletePartsAsync(connection, transaction, fileId, cancellationToken).ConfigureAwait(false);

        foreach (var part in data.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existingCount = part.Resources.Count(resource => resource.ExistsInArchive);
            StablePartIdentity.TryCreate(part.Manufacturer, part.PartNumber, part.Variant, part.PackageKey, out var identity);
            await using var insertPart = connection.CreateCommand();
            insertPart.Transaction = transaction;
            insertPart.CommandText = """
                INSERT INTO parts(
                    edz_file_id, manufacturer, part_number, type_number, order_number, description,
                    product_group, variant, package_key, raw_metadata_reference, resource_count, existing_resource_count,
                    logical_identity, identity_kind)
                VALUES (
                    $fileId, $manufacturer, $partNumber, $typeNumber, $orderNumber, $description,
                    $productGroup, $variant, $packageKey, $metadata, $resourceCount, $existingCount,
                    $logicalIdentity, $identityKind)
                RETURNING id;
                """;
            insertPart.Parameters.AddWithValue("$fileId", fileId);
            AddNullableText(insertPart, "$manufacturer", part.Manufacturer);
            AddNullableText(insertPart, "$partNumber", part.PartNumber);
            AddNullableText(insertPart, "$typeNumber", part.TypeNumber);
            AddNullableText(insertPart, "$orderNumber", part.OrderNumber);
            AddNullableText(insertPart, "$description", part.Description);
            AddNullableText(insertPart, "$productGroup", part.ProductGroup);
            AddNullableText(insertPart, "$variant", part.Variant);
            AddNullableText(insertPart, "$packageKey", part.PackageKey);
            insertPart.Parameters.AddWithValue("$metadata", part.RawMetadataReference);
            insertPart.Parameters.AddWithValue("$resourceCount", part.Resources.Count);
            insertPart.Parameters.AddWithValue("$existingCount", existingCount);
            AddNullableText(insertPart, "$logicalIdentity", identity?.Value);
            AddNullableText(insertPart, "$identityKind", identity?.Kind.ToString());
            var partId = Convert.ToInt64(await insertPart.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

            foreach (var resource in part.Resources)
            {
                await using var insertResource = connection.CreateCommand();
                insertResource.Transaction = transaction;
                insertResource.CommandText = """
                    INSERT INTO resources(
                        part_id, resource_type, name, raw_locator, archive_path,
                        exists_in_archive, size, reference_count)
                    VALUES ($partId, $type, $name, $locator, $archivePath, $exists, $size, $referenceCount);
                    """;
                insertResource.Parameters.AddWithValue("$partId", partId);
                AddNullableText(insertResource, "$type", resource.ResourceType);
                AddNullableText(insertResource, "$name", resource.Name);
                AddNullableText(insertResource, "$locator", resource.RawLocator);
                AddNullableText(insertResource, "$archivePath", resource.ArchivePath);
                insertResource.Parameters.AddWithValue("$exists", resource.ExistsInArchive ? 1 : 0);
                insertResource.Parameters.AddWithValue("$size", resource.Size is null ? DBNull.Value : resource.Size.Value);
                insertResource.Parameters.AddWithValue("$referenceCount", resource.ReferenceCount);
                await insertResource.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(
        long directoryId,
        string path,
        long fileSize,
        long lastWriteUtcTicks,
        string error,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var fileId = await UpsertFileAsync(connection, transaction, directoryId, path, fileSize, lastWriteUtcTicks, "Failed", error, cancellationToken).ConfigureAwait(false);
        await DeletePartsAsync(connection, transaction, fileId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RemoveMissingFilesAsync(long directoryId, IReadOnlyCollection<string> existingPaths, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var normalized = new HashSet<string>(existingPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var staleIds = new List<long>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = "SELECT id, path FROM edz_files WHERE directory_id = $directoryId;";
            select.Parameters.AddWithValue("$directoryId", directoryId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!normalized.Contains(reader.GetString(1)))
                {
                    staleIds.Add(reader.GetInt64(0));
                }
            }
        }

        foreach (var id in staleIds)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM edz_files WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return staleIds.Count;
    }

    public async Task MarkDirectoryScannedAsync(long directoryId, DateTimeOffset scannedUtc, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE indexed_directories SET last_scan_utc = $utc WHERE id = $id;";
        command.Parameters.AddWithValue("$utc", ToDatabaseTimestamp(scannedUtc));
        command.Parameters.AddWithValue("$id", directoryId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PartSearchResult>> SearchAsync(PartSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(query.Offset, 0);
        var plan = BuildSearchPlan(query);
        var sql = new StringBuilder("""
            SELECT p.id, f.path, p.manufacturer, p.part_number, p.type_number, p.order_number,
                   p.description, p.product_group, p.variant, p.package_key, p.raw_metadata_reference,
                   p.resource_count, p.existing_resource_count,
            """);
        sql.Append(plan.UsesFts ? "bm25(parts_fts)" : "0.0");
        sql.Append(" AS rank, sp.id, COALESCE(sp.favorite, 0) FROM parts p JOIN edz_files f ON f.id = p.edz_file_id ");
        sql.Append("LEFT JOIN my_saved_parts sp ON sp.stable_identity = p.logical_identity ");
        sql.Append(plan.Join);
        sql.Append("WHERE ").Append(plan.Where);
        sql.Append(" ORDER BY ").Append(BuildOrderBy(query, plan.UsesFts));
        sql.Append(" LIMIT $limit OFFSET $offset;");

        var results = new List<PartSearchResult>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql.ToString();
        AddSearchParameters(command, query, plan);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new PartSearchResult(
                reader.GetInt64(0),
                reader.GetString(1),
                GetNullableString(reader, 2),
                GetNullableString(reader, 3),
                GetNullableString(reader, 4),
                GetNullableString(reader, 5),
                GetNullableString(reader, 6),
                GetNullableString(reader, 7),
                GetNullableString(reader, 8),
                GetNullableString(reader, 9),
                reader.GetString(10),
                reader.GetInt32(11),
                reader.GetInt32(12),
                reader.GetDouble(13),
                reader.IsDBNull(14) ? null : reader.GetInt64(14),
                reader.GetInt64(15) != 0));
        }

        return results;
    }

    public async Task<int> CountSearchAsync(PartSearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var plan = BuildSearchPlan(query);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM parts p JOIN edz_files f ON f.id = p.edz_file_id LEFT JOIN my_saved_parts sp ON sp.stable_identity = p.logical_identity "
            + plan.Join + "WHERE " + plan.Where + ";";
        AddSearchParameters(command, query, plan);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<AggregateCount>> GetManufacturersAsync(CancellationToken cancellationToken = default)
    {
        return await GetAggregateCountsAsync("manufacturer", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AggregateCount>> GetProductGroupsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAggregateCountsAsync("product_group", cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexedDirectorySummary>> GetDirectorySummariesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<IndexedDirectorySummary>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT d.id, d.path, d.recursive, d.last_scan_utc,
                   COUNT(DISTINCT f.id), COUNT(DISTINCT p.id),
                   COUNT(DISTINCT CASE WHEN f.status = 'Failed' THEN f.id END)
            FROM indexed_directories d
            LEFT JOIN edz_files f ON f.directory_id = d.id
            LEFT JOIN parts p ON p.edz_file_id = f.id
            GROUP BY d.id, d.path, d.recursive, d.last_scan_utc
            ORDER BY d.path COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexedDirectorySummary(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt64(2) != 0,
                reader.IsDBNull(3) ? null : ParseDatabaseTimestamp(reader.GetString(3)),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6)));
        }

        return results;
    }

    public async Task<IReadOnlyList<IndexedResource>> GetResourcesAsync(long partId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<IndexedResource>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.id, r.part_id, f.path, r.resource_type, r.name, r.raw_locator,
                   r.archive_path, r.exists_in_archive, r.size, r.reference_count
            FROM resources r
            JOIN parts p ON p.id = r.part_id
            JOIN edz_files f ON f.id = p.edz_file_id
            WHERE r.part_id = $partId
            ORDER BY r.resource_type COLLATE NOCASE, r.name COLLATE NOCASE, r.id;
            """;
        command.Parameters.AddWithValue("$partId", partId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexedResource(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                GetNullableString(reader, 3), GetNullableString(reader, 4), GetNullableString(reader, 5),
                GetNullableString(reader, 6), reader.GetInt64(7) != 0,
                reader.IsDBNull(8) ? null : reader.GetInt64(8), reader.GetInt32(9)));
        }

        return results;
    }

    public async Task<IReadOnlyList<IndexedDiagnostic>> GetDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<IndexedDiagnostic>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT path, status, error, indexed_utc FROM edz_files WHERE status <> 'Indexed' OR error IS NOT NULL ORDER BY indexed_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new IndexedDiagnostic(reader.GetString(0), reader.GetString(1), GetNullableString(reader, 2), ParseDatabaseTimestamp(reader.GetString(3))));
        }

        return results;
    }

    public async Task<IndexStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM indexed_directories),
                (SELECT COUNT(*) FROM edz_files),
                (SELECT COUNT(*) FROM edz_files WHERE status = 'Failed'),
                (SELECT COUNT(*) FROM parts),
                (SELECT COUNT(*) FROM resources);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new IndexStatistics(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private async Task<IReadOnlyList<AggregateCount>> GetAggregateCountsAsync(string column, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<AggregateCount>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column}, COUNT(*) FROM parts WHERE {column} IS NOT NULL AND trim({column}) <> '' GROUP BY {column} COLLATE NOCASE ORDER BY COUNT(*) DESC, {column} COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AggregateCount(reader.GetString(0), reader.GetInt32(1)));
        }

        return results;
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> UpsertFileAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long directoryId,
        string path,
        long fileSize,
        long lastWriteUtcTicks,
        string status,
        string? error,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO edz_files(directory_id, path, file_size, last_write_utc_ticks, indexed_utc, status, error)
            VALUES ($directoryId, $path, $size, $ticks, $utc, $status, $error)
            ON CONFLICT(path) DO UPDATE SET
                directory_id = excluded.directory_id,
                file_size = excluded.file_size,
                last_write_utc_ticks = excluded.last_write_utc_ticks,
                indexed_utc = excluded.indexed_utc,
                status = excluded.status,
                error = excluded.error
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$directoryId", directoryId);
        command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
        command.Parameters.AddWithValue("$size", fileSize);
        command.Parameters.AddWithValue("$ticks", lastWriteUtcTicks);
        command.Parameters.AddWithValue("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", error is null ? DBNull.Value : error);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task DeletePartsAsync(SqliteConnection connection, SqliteTransaction transaction, long fileId, CancellationToken cancellationToken)
    {
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM parts WHERE edz_file_id = $fileId;";
        delete.Parameters.AddWithValue("$fileId", fileId);
        await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IEnumerable<(int Version, string Name, string Sql)> LoadMigrations()
    {
        var assembly = typeof(SqliteIndexRepository).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            var fileName = resourceName[(resourceName.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
            var separator = fileName.IndexOf('_');
            if (separator <= 0 || !int.TryParse(fileName[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                throw new InvalidOperationException("Invalid embedded migration name: " + resourceName);
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException("Could not read embedded migration: " + resourceName);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            yield return (version, fileName, reader.ReadToEnd());
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A directory path is required.", nameof(path));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    private static SearchPlan BuildSearchPlan(PartSearchQuery query)
    {
        var where = new List<string>();
        var ftsQuery = query.MatchMode == PartSearchMatchMode.FullText ? BuildFtsQuery(query.Text) : null;
        var usesFts = ftsQuery is not null;
        if (usesFts)
        {
            var column = query.TextField switch
            {
                PartSearchField.PartNumber => "part_number",
                PartSearchField.TypeNumber => "type_number",
                PartSearchField.Manufacturer => "manufacturer",
                PartSearchField.Description => "description",
                _ => null
            };
            if (column is not null)
            {
                ftsQuery = column + " : (" + ftsQuery + ")";
            }

            where.Add("parts_fts MATCH $text");
        }
        else if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var columns = query.TextField switch
            {
                PartSearchField.PartNumber => new[] { "p.part_number" },
                PartSearchField.TypeNumber => new[] { "p.type_number" },
                PartSearchField.Manufacturer => new[] { "p.manufacturer" },
                PartSearchField.Description => new[] { "p.description" },
                _ => new[] { "p.part_number", "p.type_number", "p.order_number", "p.manufacturer", "p.description" }
            };
            var comparison = query.MatchMode == PartSearchMatchMode.Exact
                ? " = $searchText COLLATE NOCASE"
                : " LIKE $searchText ESCAPE '\\'";
            where.Add("(" + string.Join(" OR ", columns.Select(column => column + comparison)) + ")");
        }

        AddContainsFilter(where, "p.manufacturer", "$manufacturer", query.Manufacturer);
        AddContainsFilter(where, "p.part_number", "$partNumber", query.PartNumber);
        AddContainsFilter(where, "p.type_number", "$typeNumber", query.TypeNumber);
        AddContainsFilter(where, "p.product_group", "$productGroup", query.ProductGroup);
        if (query.FavoritesOnly) where.Add("sp.favorite = 1");
        where.Add(query.ResourceFilter switch
        {
            ResourceExistenceFilter.HasAny => "p.existing_resource_count > 0",
            ResourceExistenceFilter.AllPresent => "p.resource_count > 0 AND p.existing_resource_count = p.resource_count",
            ResourceExistenceFilter.Missing => "p.existing_resource_count < p.resource_count",
            ResourceExistenceFilter.None => "p.resource_count = 0",
            _ => "1 = 1"
        });

        return new SearchPlan(
            usesFts,
            usesFts ? "JOIN parts_fts ON parts_fts.rowid = p.id " : string.Empty,
            string.Join(" AND ", where),
            ftsQuery,
            !usesFts && !string.IsNullOrWhiteSpace(query.Text));
    }

    private static void AddSearchParameters(SqliteCommand command, PartSearchQuery query, SearchPlan plan)
    {
        if (plan.UsesFts)
        {
            command.Parameters.AddWithValue("$text", plan.FtsQuery!);
        }
        else if (plan.HasPlainText)
        {
            var text = query.Text!.Trim();
            command.Parameters.AddWithValue("$searchText", query.MatchMode == PartSearchMatchMode.Exact
                ? text
                : "%" + EscapeLike(text) + "%");
        }

        AddOptionalFilterParameter(command, "$manufacturer", query.Manufacturer);
        AddOptionalFilterParameter(command, "$partNumber", query.PartNumber);
        AddOptionalFilterParameter(command, "$typeNumber", query.TypeNumber);
        AddOptionalFilterParameter(command, "$productGroup", query.ProductGroup);
    }

    private static string BuildOrderBy(PartSearchQuery query, bool usesFts)
    {
        if (query.SortColumn == PartSortColumn.Relevance)
        {
            return usesFts
                ? "rank, p.id"
                : "p.manufacturer COLLATE NOCASE, p.part_number COLLATE NOCASE, p.id";
        }

        var column = query.SortColumn switch
        {
            PartSortColumn.Manufacturer => "p.manufacturer COLLATE NOCASE",
            PartSortColumn.PartNumber => "p.part_number COLLATE NOCASE",
            PartSortColumn.TypeNumber => "p.type_number COLLATE NOCASE",
            PartSortColumn.Description => "p.description COLLATE NOCASE",
            PartSortColumn.SourceEdz => "f.path COLLATE NOCASE",
            _ => "p.id"
        };
        var direction = query.SortDirection == SortDirection.Descending ? " DESC" : " ASC";
        return column + direction + ", p.id";
    }

    private static string? BuildFtsQuery(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var tokens = SearchToken.Matches(value)
            .Select(match => match.Value.Replace("\"", "\"\"", StringComparison.Ordinal))
            .Where(token => token.Length > 0)
            .Select(token => "\"" + token + "\"*")
            .ToArray();
        return tokens.Length == 0 ? null : string.Join(" AND ", tokens);
    }

    private static void AddContainsFilter(ICollection<string> where, string column, string parameter, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            where.Add(column + " LIKE " + parameter + " ESCAPE '\\'");
        }
    }

    private static void AddOptionalFilterParameter(SqliteCommand command, string parameter, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            command.Parameters.AddWithValue(parameter, "%" + EscapeLike(value.Trim()) + "%");
        }
    }

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static void AddNullableText(SqliteCommand command, string name, string? value)
    {
        command.Parameters.AddWithValue(name, value is null ? DBNull.Value : value);
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string ToDatabaseTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDatabaseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private sealed record SearchPlan(bool UsesFts, string Join, string Where, string? FtsQuery, bool HasPlainText);
}
