using System.Globalization;
using EplanEdzManager.Core.Identity;
using Microsoft.Data.Sqlite;

namespace EplanEdzManager.Infrastructure.Sqlite;

public sealed partial class SqliteIndexRepository
{
    public async Task<IReadOnlyList<SavedPartSearchResult>> SearchMyLibraryAsync(MyLibrarySearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(query.Offset, 0);
        var (where, ftsQuery) = BuildMyLibraryWhere(query);
        var sql = $"""
            SELECT sp.id, sp.stable_identity, sp.identity_kind, sp.manufacturer, sp.part_number, sp.variant,
                   sp.type_number_snapshot, sp.order_number_snapshot, sp.description_snapshot,
                   sp.product_group_snapshot, sp.package_key_snapshot, sp.note, sp.favorite,
                   sp.created_utc, sp.updated_utc,
                   (SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id = sp.id AND s.current_part_id IS NOT NULL),
                   (SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id = sp.id),
                   ps.id, ps.edz_path_snapshot, ps.current_part_id, ps.raw_metadata_reference_snapshot,
                   COALESCE(ps.resource_count_snapshot, 0), COALESCE(ps.existing_resource_count_snapshot, 0),
                   COALESCE((SELECT group_concat(name, ', ') FROM (
                       SELECT c.name AS name FROM my_collection_items ci
                       JOIN my_collections c ON c.id = ci.collection_id
                       WHERE ci.saved_part_id = sp.id ORDER BY c.name COLLATE NOCASE)), ''),
                   COALESCE((SELECT group_concat(name, ', ') FROM (
                       SELECT t.name AS name FROM my_saved_part_tags st
                       JOIN my_tags t ON t.id = st.tag_id
                       WHERE st.saved_part_id = sp.id ORDER BY t.name COLLATE NOCASE)), '')
            FROM my_saved_parts sp
            LEFT JOIN my_saved_part_sources ps ON ps.saved_part_id = sp.id AND ps.is_preferred = 1
            WHERE {where}
            ORDER BY {BuildMyLibraryOrderBy(query)}
            LIMIT $limit OFFSET $offset;
            """;

        var results = new List<SavedPartSearchResult>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        AddMyLibraryParameters(command, query, ftsQuery);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var available = reader.GetInt32(15);
            var total = reader.GetInt32(16);
            results.Add(new SavedPartSearchResult(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3),
                GetNullableString(reader, 4), GetNullableString(reader, 5), GetNullableString(reader, 6),
                GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9),
                GetNullableString(reader, 10), reader.GetString(11), reader.GetInt64(12) != 0,
                ParseDatabaseTimestamp(reader.GetString(13)), ParseDatabaseTimestamp(reader.GetString(14)),
                available, total, reader.IsDBNull(17) ? null : reader.GetInt64(17), GetNullableString(reader, 18),
                reader.IsDBNull(19) ? null : reader.GetInt64(19), GetNullableString(reader, 20), reader.GetInt32(21),
                reader.GetInt32(22), reader.GetString(23), reader.GetString(24), DetermineSourceState(available, total)));
        }

        return results;
    }

    public async Task<int> CountMyLibraryAsync(MyLibrarySearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var (where, ftsQuery) = BuildMyLibraryWhere(query);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM my_saved_parts sp WHERE " + where + ";";
        AddMyLibraryParameters(command, query, ftsQuery);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<SavedPartSourceResult>> GetSavedPartSourcesAsync(long savedPartId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SavedPartSourceResult>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, saved_part_id, source_instance_identity, current_part_id, edz_path_snapshot,
                   package_key_snapshot, raw_metadata_reference_snapshot, edz_file_size_snapshot,
                   edz_last_write_utc_ticks_snapshot, indexed_utc_snapshot, resource_count_snapshot,
                   existing_resource_count_snapshot, is_preferred, first_seen_utc, last_seen_utc
            FROM my_saved_part_sources WHERE saved_part_id = $id
            ORDER BY is_preferred DESC, current_part_id IS NOT NULL DESC, last_seen_utc DESC, id;
            """;
        command.Parameters.AddWithValue("$id", savedPartId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new SavedPartSourceResult(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt64(3),
                reader.GetString(4), GetNullableString(reader, 5), reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt64(8),
                reader.IsDBNull(9) ? null : ParseDatabaseTimestamp(reader.GetString(9)), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt64(12) != 0, ParseDatabaseTimestamp(reader.GetString(13)), ParseDatabaseTimestamp(reader.GetString(14)),
                !reader.IsDBNull(3)));
        }
        return results;
    }

    public Task SetSavedPartFavoriteAsync(long savedPartId, bool favorite, CancellationToken cancellationToken = default) =>
        UpdateSavedPartAsync(savedPartId, "favorite = $value", favorite ? 1 : 0, cancellationToken);

    public async Task SetSavedPartsFavoriteAsync(IReadOnlyCollection<long> savedPartIds, bool favorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedPartIds);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in savedPartIds.Distinct())
            await ExecuteMyLibraryNonQueryAsync(connection, transaction, "UPDATE my_saved_parts SET favorite=$favorite, updated_utc=$utc WHERE id=$id;", cancellationToken,
                ("$favorite", favorite ? 1 : 0), ("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow)), ("$id", id)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task UpdateSavedPartNoteAsync(long savedPartId, string? note, CancellationToken cancellationToken = default) =>
        UpdateSavedPartAsync(savedPartId, "note = $value", note?.Trim() ?? string.Empty, cancellationToken);

    private async Task UpdateSavedPartAsync(long savedPartId, string assignment, object value, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE my_saved_parts SET {assignment}, updated_utc = $utc WHERE id = $id;";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$id", savedPartId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0) throw new KeyNotFoundException("The saved part no longer exists.");
    }

    public async Task SetPreferredSourceAsync(long savedPartId, long sourceId, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteMyLibraryNonQueryAsync(connection, transaction, "UPDATE my_saved_part_sources SET is_preferred = 0 WHERE saved_part_id = $part;", cancellationToken, ("$part", savedPartId)).ConfigureAwait(false);
        var changed = await ExecuteMyLibraryNonQueryAsync(connection, transaction, "UPDATE my_saved_part_sources SET is_preferred = 1 WHERE id = $source AND saved_part_id = $part;", cancellationToken, ("$source", sourceId), ("$part", savedPartId)).ConfigureAwait(false);
        if (changed == 0) throw new KeyNotFoundException("The source candidate no longer exists.");
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> DeleteSavedPartsAsync(IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(savedPartIds);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var count = 0;
        foreach (var id in savedPartIds.Distinct())
            count += await ExecuteMyLibraryNonQueryAsync(connection, transaction, "DELETE FROM my_saved_parts WHERE id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return count;
    }

    public Task<IReadOnlyList<NamedItemCount>> GetCollectionsAsync(CancellationToken cancellationToken = default) =>
        GetNamedItemsAsync("my_collections", "my_collection_items", "collection_id", cancellationToken);

    public Task<IReadOnlyList<NamedItemCount>> GetTagsAsync(CancellationToken cancellationToken = default) =>
        GetNamedItemsAsync("my_tags", "my_saved_part_tags", "tag_id", cancellationToken);

    public Task<long> CreateCollectionAsync(string name, IReadOnlyCollection<long>? savedPartIds = null, CancellationToken cancellationToken = default) =>
        CreateNamedItemAsync("my_collections", "my_collection_items", "collection_id", name, savedPartIds, cancellationToken);

    public Task<long> CreateTagAsync(string name, IReadOnlyCollection<long>? savedPartIds = null, CancellationToken cancellationToken = default) =>
        CreateNamedItemAsync("my_tags", "my_saved_part_tags", "tag_id", name, savedPartIds, cancellationToken);

    public Task RenameCollectionAsync(long id, string name, CancellationToken cancellationToken = default) => RenameNamedItemAsync("my_collections", id, name, cancellationToken);
    public Task RenameTagAsync(long id, string name, CancellationToken cancellationToken = default) => RenameNamedItemAsync("my_tags", id, name, cancellationToken);
    public Task DeleteCollectionAsync(long id, CancellationToken cancellationToken = default) => DeleteNamedItemAsync("my_collections", id, cancellationToken);
    public Task DeleteTagAsync(long id, CancellationToken cancellationToken = default) => DeleteNamedItemAsync("my_tags", id, cancellationToken);
    public Task AddSavedPartsToCollectionAsync(long id, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => UpdateNamedMembershipAsync("my_collection_items", "collection_id", id, savedPartIds, true, cancellationToken);
    public Task RemoveSavedPartsFromCollectionAsync(long id, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => UpdateNamedMembershipAsync("my_collection_items", "collection_id", id, savedPartIds, false, cancellationToken);
    public Task AddTagsToSavedPartsAsync(long tagId, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => UpdateNamedMembershipAsync("my_saved_part_tags", "tag_id", tagId, savedPartIds, true, cancellationToken);
    public Task RemoveTagsFromSavedPartsAsync(long tagId, IReadOnlyCollection<long> savedPartIds, CancellationToken cancellationToken = default) => UpdateNamedMembershipAsync("my_saved_part_tags", "tag_id", tagId, savedPartIds, false, cancellationToken);

    public async Task<RebindResult> RebindSavedPartsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteMyLibraryNonQueryAsync(connection, transaction, "UPDATE my_saved_part_sources SET current_part_id = NULL;", cancellationToken).ConfigureAwait(false);
        var now = ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        await ExecuteMyLibraryNonQueryAsync(connection, transaction, "DROP TABLE IF EXISTS temp.my_rebind_matches;", cancellationToken).ConfigureAwait(false);
        await ExecuteMyLibraryNonQueryAsync(connection, transaction, """
            CREATE TEMP TABLE my_rebind_matches(
              source_id INTEGER PRIMARY KEY, part_id INTEGER NOT NULL, resource_count INTEGER NOT NULL,
              existing_resource_count INTEGER NOT NULL, file_size INTEGER NOT NULL,
              last_write_utc_ticks INTEGER NOT NULL, indexed_utc TEXT NOT NULL) WITHOUT ROWID;
            INSERT OR REPLACE INTO my_rebind_matches
            SELECT s.id, p.id, p.resource_count, p.existing_resource_count,
                   f.file_size, f.last_write_utc_ticks, f.indexed_utc
            FROM my_saved_part_sources s
            CROSS JOIN my_saved_parts sp ON sp.id=s.saved_part_id
            CROSS JOIN parts p INDEXED BY ix_parts_logical_identity ON p.logical_identity=sp.stable_identity
            CROSS JOIN edz_files f ON f.id=p.edz_file_id
            WHERE f.path=s.edz_path_snapshot
              AND COALESCE(p.package_key,'')=COALESCE(s.package_key_snapshot,'')
              AND p.raw_metadata_reference=s.raw_metadata_reference_snapshot;
            """, cancellationToken).ConfigureAwait(false);
        var reboundExisting = await ExecuteMyLibraryNonQueryAsync(connection, transaction, """
            UPDATE my_saved_part_sources AS s SET
              current_part_id=m.part_id,
              resource_count_snapshot=m.resource_count,
              existing_resource_count_snapshot=m.existing_resource_count,
              edz_file_size_snapshot=m.file_size,
              edz_last_write_utc_ticks_snapshot=m.last_write_utc_ticks,
              indexed_utc_snapshot=m.indexed_utc,
              last_seen_utc=$now
            FROM my_rebind_matches m WHERE m.source_id=s.id;
            """, cancellationToken, ("$now", now)).ConfigureAwait(false);
        var candidates = new List<(long SavedPartId, long PartId)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT sp.id, p.id FROM my_saved_parts sp
                CROSS JOIN parts p INDEXED BY ix_parts_logical_identity ON p.logical_identity = sp.stable_identity
                CROSS JOIN edz_files f ON f.id=p.edz_file_id
                WHERE NOT EXISTS (
                  SELECT 1 FROM my_saved_part_sources s WHERE s.saved_part_id=sp.id
                    AND s.edz_path_snapshot=f.path
                    AND COALESCE(s.package_key_snapshot,'')=COALESCE(p.package_key,'')
                    AND s.raw_metadata_reference_snapshot=p.raw_metadata_reference)
                ORDER BY sp.id, p.id;
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) candidates.Add((reader.GetInt64(0), reader.GetInt64(1)));
        }
        foreach (var candidate in candidates)
        {
            var catalog = await ReadCatalogIdentityRowAsync(connection, transaction, candidate.PartId, cancellationToken).ConfigureAwait(false);
            await UpsertSourceAsync(connection, transaction, candidate.SavedPartId, candidate.PartId, catalog, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        await ExecuteMyLibraryNonQueryAsync(connection, transaction, "DROP TABLE IF EXISTS temp.my_rebind_matches;", cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var states = await GetMyLibraryStateCountsAsync(cancellationToken).ConfigureAwait(false);
        return new RebindResult(states.Total, states.Bound, states.Multiple, states.Missing, states.Unresolved, reboundExisting + candidates.Count);
    }

    public async Task<MyLibrarySnapshot> GetMyLibrarySnapshotAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var parts = new List<SavedPartSnapshot>();
        var collections = await GetCollectionsAsync(cancellationToken).ConfigureAwait(false);
        var tags = await GetTagsAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var baseRows = new List<(long Id, string Identity, string Kind, string? Manufacturer, string? PartNumber, string? Variant, string? TypeNumber, string? OrderNumber, string? Description, string? ProductGroup, string? PackageKey, string Note, bool Favorite, DateTimeOffset Created, DateTimeOffset Updated)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, stable_identity, identity_kind, manufacturer, part_number, variant,
                       type_number_snapshot, order_number_snapshot, description_snapshot,
                       product_group_snapshot, package_key_snapshot, note, favorite, created_utc, updated_utc
                FROM my_saved_parts ORDER BY id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                baseRows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), GetNullableString(reader, 3), GetNullableString(reader, 4),
                    GetNullableString(reader, 5), GetNullableString(reader, 6), GetNullableString(reader, 7), GetNullableString(reader, 8),
                    GetNullableString(reader, 9), GetNullableString(reader, 10), reader.GetString(11), reader.GetInt64(12) != 0,
                    ParseDatabaseTimestamp(reader.GetString(13)), ParseDatabaseTimestamp(reader.GetString(14))));
        }

        foreach (var row in baseRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sources = new List<SavedSourceSnapshot>();
            await using (var sourceCommand = connection.CreateCommand())
            {
                sourceCommand.CommandText = """
                    SELECT source_instance_identity, edz_path_snapshot, package_key_snapshot,
                           raw_metadata_reference_snapshot, edz_file_size_snapshot, edz_last_write_utc_ticks_snapshot,
                           indexed_utc_snapshot, resource_count_snapshot, existing_resource_count_snapshot,
                           is_preferred, first_seen_utc, last_seen_utc
                    FROM my_saved_part_sources WHERE saved_part_id = $id ORDER BY id;
                    """;
                sourceCommand.Parameters.AddWithValue("$id", row.Id);
                await using var reader = await sourceCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    sources.Add(new SavedSourceSnapshot(reader.GetString(0), reader.GetString(1), GetNullableString(reader, 2), reader.GetString(3),
                        reader.IsDBNull(4) ? null : reader.GetInt64(4), reader.IsDBNull(5) ? null : reader.GetInt64(5),
                        reader.IsDBNull(6) ? null : ParseDatabaseTimestamp(reader.GetString(6)), reader.GetInt32(7), reader.GetInt32(8),
                        reader.GetInt64(9) != 0, ParseDatabaseTimestamp(reader.GetString(10)), ParseDatabaseTimestamp(reader.GetString(11))));
            }
            var collectionNames = await GetNamesForSavedPartAsync(connection, row.Id, "my_collection_items", "my_collections", "collection_id", cancellationToken).ConfigureAwait(false);
            var tagNames = await GetNamesForSavedPartAsync(connection, row.Id, "my_saved_part_tags", "my_tags", "tag_id", cancellationToken).ConfigureAwait(false);
            parts.Add(new SavedPartSnapshot(row.Identity, row.Kind, row.Manufacturer, row.PartNumber, row.Variant, row.TypeNumber,
                row.OrderNumber, row.Description, row.ProductGroup, row.PackageKey, row.Note, row.Favorite, row.Created, row.Updated,
                sources, collectionNames, tagNames));
        }

        return new MyLibrarySnapshot(parts, collections.Select(x => new CollectionSnapshot(x.Name)).ToArray(), tags.Select(x => new TagSnapshot(x.Name)).ToArray());
    }

    public async Task RestoreMyLibrarySnapshotAsync(MyLibrarySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var collectionIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var tagIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in snapshot.Collections.Select(x => x.Name).Concat(snapshot.SavedParts.SelectMany(x => x.Collections)).Distinct(StringComparer.OrdinalIgnoreCase))
            collectionIds[item] = await GetOrCreateNamedItemAsync(connection, transaction, "my_collections", item, cancellationToken).ConfigureAwait(false);
        foreach (var item in snapshot.Tags.Select(x => x.Name).Concat(snapshot.SavedParts.SelectMany(x => x.Tags)).Distinct(StringComparer.OrdinalIgnoreCase))
            tagIds[item] = await GetOrCreateNamedItemAsync(connection, transaction, "my_tags", item, cancellationToken).ConfigureAwait(false);

        foreach (var part in snapshot.SavedParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var savedId = await UpsertSnapshotPartAsync(connection, transaction, part, cancellationToken).ConfigureAwait(false);
            var importedPreferred = part.Sources.FirstOrDefault(x => x.IsPreferred);
            if (importedPreferred is not null)
                await ExecuteMyLibraryNonQueryAsync(connection, transaction, "UPDATE my_saved_part_sources SET is_preferred=0 WHERE saved_part_id=$id;", cancellationToken, ("$id", savedId)).ConfigureAwait(false);
            foreach (var source in part.Sources)
                await UpsertSnapshotSourceAsync(connection, transaction, savedId, source, source == importedPreferred, cancellationToken).ConfigureAwait(false);
            foreach (var collection in part.Collections.Distinct(StringComparer.OrdinalIgnoreCase))
                await ExecuteMyLibraryNonQueryAsync(connection, transaction, "INSERT OR IGNORE INTO my_collection_items(collection_id, saved_part_id, added_utc) VALUES ($parent,$part,$utc);", cancellationToken,
                    ("$parent", collectionIds[collection]), ("$part", savedId), ("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow))).ConfigureAwait(false);
            foreach (var tag in part.Tags.Distinct(StringComparer.OrdinalIgnoreCase))
                await ExecuteMyLibraryNonQueryAsync(connection, transaction, "INSERT OR IGNORE INTO my_saved_part_tags(tag_id, saved_part_id, added_utc) VALUES ($parent,$part,$utc);", cancellationToken,
                    ("$parent", tagIds[tag]), ("$part", savedId), ("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow))).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RebindSavedPartsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlySet<string>> GetExistingStableIdentitiesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT stable_identity FROM my_saved_parts;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyList<SavedPartHealth>> GetSavedPartHealthIssuesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<SavedPartHealth>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH health AS (
              SELECT sp.id, sp.stable_identity, sp.part_number, ps.edz_path_snapshot,
                (SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id=sp.id AND s.current_part_id IS NOT NULL) available,
                (SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id=sp.id) total,
                CASE WHEN ps.current_part_id IS NOT NULL THEN 1 ELSE 0 END preferred_available
              FROM my_saved_parts sp LEFT JOIN my_saved_part_sources ps ON ps.saved_part_id=sp.id AND ps.is_preferred=1)
            SELECT id, stable_identity, part_number, edz_path_snapshot, available, total, preferred_available
            FROM health WHERE available<>1 OR total=0 OR preferred_available=0 ORDER BY id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new SavedPartHealth(reader.GetInt64(0), reader.GetString(1), GetNullableString(reader, 2), GetNullableString(reader, 3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6) != 0));
        return result;
    }
    private static async Task BackfillPartIdentitiesAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var pending = new List<(long Id, string? Manufacturer, string? PartNumber, string? Variant, string? PackageKey)>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, manufacturer, part_number, variant, package_key FROM parts WHERE logical_identity IS NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                pending.Add((reader.GetInt64(0), GetNullableString(reader, 1), GetNullableString(reader, 2), GetNullableString(reader, 3), GetNullableString(reader, 4)));
            }
        }

        if (pending.Count == 0) return;
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!StablePartIdentity.TryCreate(row.Manufacturer, row.PartNumber, row.Variant, row.PackageKey, out var identity)) continue;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE parts SET logical_identity = $identity, identity_kind = $kind WHERE id = $id;";
            update.Parameters.AddWithValue("$identity", identity!.Value);
            update.Parameters.AddWithValue("$kind", identity.Kind.ToString());
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SavedPartWriteResult> SaveCatalogPartAsync(long catalogPartId, bool favorite, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var result = await SaveCatalogPartCoreAsync(connection, transaction, catalogPartId, favorite, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<SavedPartWriteResult>> SaveCatalogPartsAsync(IReadOnlyCollection<long> catalogPartIds, bool favorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalogPartIds);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<SavedPartWriteResult>();
        foreach (var partId in catalogPartIds.Distinct())
        {
            results.Add(await SaveCatalogPartCoreAsync(connection, transaction, partId, favorite, cancellationToken).ConfigureAwait(false));
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return results;
    }

    private static async Task<SavedPartWriteResult> SaveCatalogPartCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long catalogPartId,
        bool favorite,
        CancellationToken cancellationToken)
    {
        var catalog = await ReadCatalogIdentityRowAsync(connection, transaction, catalogPartId, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var existingId = await FindSavedPartIdAsync(connection, transaction, catalog.StableIdentity, cancellationToken).ConfigureAwait(false);
        long savedPartId;
        if (existingId is null)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO my_saved_parts(
                    stable_identity, identity_kind, manufacturer, part_number, variant,
                    type_number_snapshot, order_number_snapshot, description_snapshot,
                    product_group_snapshot, package_key_snapshot, note, favorite, created_utc, updated_utc)
                VALUES ($identity, $kind, $manufacturer, $partNumber, $variant,
                    $typeNumber, $orderNumber, $description, $productGroup, $packageKey,
                    '', $favorite, $now, $now)
                RETURNING id;
                """;
            AddCatalogSnapshotParameters(insert, catalog);
            insert.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            insert.Parameters.AddWithValue("$now", ToDatabaseTimestamp(now));
            savedPartId = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        else
        {
            savedPartId = existingId.Value;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE my_saved_parts SET
                    identity_kind = $kind, manufacturer = $manufacturer, part_number = $partNumber,
                    variant = $variant, type_number_snapshot = $typeNumber, order_number_snapshot = $orderNumber,
                    description_snapshot = $description, product_group_snapshot = $productGroup,
                    package_key_snapshot = $packageKey,
                    favorite = CASE WHEN $favorite = 1 THEN 1 ELSE favorite END,
                    updated_utc = $now
                WHERE id = $savedPartId;
                """;
            AddCatalogSnapshotParameters(update, catalog);
            update.Parameters.AddWithValue("$favorite", favorite ? 1 : 0);
            update.Parameters.AddWithValue("$now", ToDatabaseTimestamp(now));
            update.Parameters.AddWithValue("$savedPartId", savedPartId);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await UpsertSourceAsync(connection, transaction, savedPartId, catalogPartId, catalog, now, cancellationToken).ConfigureAwait(false);
        var sourceCount = await CountSourcesAsync(connection, transaction, savedPartId, cancellationToken).ConfigureAwait(false);
        return new SavedPartWriteResult(savedPartId, catalog.StableIdentity, existingId is null, sourceCount);
    }

    private static void AddCatalogSnapshotParameters(SqliteCommand command, CatalogIdentityRow catalog)
    {
        command.Parameters.AddWithValue("$identity", catalog.StableIdentity);
        command.Parameters.AddWithValue("$kind", catalog.IdentityKind);
        AddNullableText(command, "$manufacturer", catalog.Manufacturer);
        AddNullableText(command, "$partNumber", catalog.PartNumber);
        AddNullableText(command, "$variant", catalog.Variant);
        AddNullableText(command, "$typeNumber", catalog.TypeNumber);
        AddNullableText(command, "$orderNumber", catalog.OrderNumber);
        AddNullableText(command, "$description", catalog.Description);
        AddNullableText(command, "$productGroup", catalog.ProductGroup);
        AddNullableText(command, "$packageKey", catalog.PackageKey);
    }

    private static async Task<long?> FindSavedPartIdAsync(SqliteConnection connection, SqliteTransaction transaction, string stableIdentity, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM my_saved_parts WHERE stable_identity = $identity;";
        command.Parameters.AddWithValue("$identity", stableIdentity);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task UpsertSourceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long savedPartId,
        long catalogPartId,
        CatalogIdentityRow catalog,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sourceIdentity = PartSourceIdentity.Create(catalog.EdzPath, catalog.PackageKey, catalog.RawMetadataReference);
        var hasSources = await CountSourcesAsync(connection, transaction, savedPartId, cancellationToken).ConfigureAwait(false) > 0;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO my_saved_part_sources(
                saved_part_id, source_instance_identity, current_part_id, edz_path_snapshot,
                package_key_snapshot, raw_metadata_reference_snapshot, edz_file_size_snapshot,
                edz_last_write_utc_ticks_snapshot, indexed_utc_snapshot, resource_count_snapshot,
                existing_resource_count_snapshot, is_preferred, first_seen_utc, last_seen_utc)
            VALUES ($savedPartId, $sourceIdentity, $partId, $edzPath, $packageKey, $metadata,
                $fileSize, $lastWrite, $indexedUtc, $resourceCount, $existingCount,
                $preferred, $now, $now)
            ON CONFLICT(saved_part_id, source_instance_identity) DO UPDATE SET
                current_part_id = excluded.current_part_id,
                edz_path_snapshot = excluded.edz_path_snapshot,
                package_key_snapshot = excluded.package_key_snapshot,
                raw_metadata_reference_snapshot = excluded.raw_metadata_reference_snapshot,
                edz_file_size_snapshot = excluded.edz_file_size_snapshot,
                edz_last_write_utc_ticks_snapshot = excluded.edz_last_write_utc_ticks_snapshot,
                indexed_utc_snapshot = excluded.indexed_utc_snapshot,
                resource_count_snapshot = excluded.resource_count_snapshot,
                existing_resource_count_snapshot = excluded.existing_resource_count_snapshot,
                last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$savedPartId", savedPartId);
        command.Parameters.AddWithValue("$sourceIdentity", sourceIdentity);
        command.Parameters.AddWithValue("$partId", catalogPartId);
        command.Parameters.AddWithValue("$edzPath", catalog.EdzPath);
        AddNullableText(command, "$packageKey", catalog.PackageKey);
        command.Parameters.AddWithValue("$metadata", catalog.RawMetadataReference);
        command.Parameters.AddWithValue("$fileSize", catalog.FileSize);
        command.Parameters.AddWithValue("$lastWrite", catalog.LastWriteUtcTicks);
        command.Parameters.AddWithValue("$indexedUtc", ToDatabaseTimestamp(catalog.IndexedUtc));
        command.Parameters.AddWithValue("$resourceCount", catalog.ResourceCount);
        command.Parameters.AddWithValue("$existingCount", catalog.ExistingResourceCount);
        command.Parameters.AddWithValue("$preferred", hasSources ? 0 : 1);
        command.Parameters.AddWithValue("$now", ToDatabaseTimestamp(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> CountSourcesAsync(SqliteConnection connection, SqliteTransaction transaction, long savedPartId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM my_saved_part_sources WHERE saved_part_id = $id;";
        command.Parameters.AddWithValue("$id", savedPartId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<CatalogIdentityRow> ReadCatalogIdentityRowAsync(SqliteConnection connection, SqliteTransaction transaction, long catalogPartId, CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT p.logical_identity, p.identity_kind, p.manufacturer, p.part_number, p.variant,
                   p.type_number, p.order_number, p.description, p.product_group, p.package_key,
                   p.raw_metadata_reference, p.resource_count, p.existing_resource_count,
                   f.path, f.file_size, f.last_write_utc_ticks, f.indexed_utc
            FROM parts p JOIN edz_files f ON f.id = p.edz_file_id
            WHERE p.id = $id;
            """;
        select.Parameters.AddWithValue("$id", catalogPartId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) throw new KeyNotFoundException("The catalog part no longer exists.");
        if (reader.IsDBNull(0)) throw new InvalidOperationException("The catalog part does not have enough metadata for a stable identity.");
        return new CatalogIdentityRow(
            reader.GetString(0), reader.GetString(1), GetNullableString(reader, 2), GetNullableString(reader, 3), GetNullableString(reader, 4),
            GetNullableString(reader, 5), GetNullableString(reader, 6), GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9),
            reader.GetString(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetString(13), reader.GetInt64(14), reader.GetInt64(15),
            ParseDatabaseTimestamp(reader.GetString(16)));
    }

    private static SavedPartSourceState DetermineSourceState(int available, int total) => available switch
    {
        > 1 => SavedPartSourceState.MultipleCandidates,
        1 => SavedPartSourceState.Bound,
        _ when total > 0 => SavedPartSourceState.SourceMissing,
        _ => SavedPartSourceState.Unresolved
    };

    private static (string Where, string? FtsQuery) BuildMyLibraryWhere(MyLibrarySearchQuery query)
    {
        var clauses = new List<string> { "1 = 1" };
        var fts = BuildFtsQuery(query.Text);
        if (fts is not null)
        {
            clauses.Add("(sp.id IN (SELECT rowid FROM my_saved_parts_fts WHERE my_saved_parts_fts MATCH $text) " +
                        "OR EXISTS (SELECT 1 FROM my_saved_part_tags st JOIN my_tags t ON t.id = st.tag_id WHERE st.saved_part_id = sp.id AND t.name LIKE $like ESCAPE '\\') " +
                        "OR EXISTS (SELECT 1 FROM my_collection_items ci JOIN my_collections c ON c.id = ci.collection_id WHERE ci.saved_part_id = sp.id AND c.name LIKE $like ESCAPE '\\'))");
        }
        if (query.FavoritesOnly) clauses.Add("sp.favorite = 1");
        if (query.CollectionId is not null) clauses.Add("EXISTS (SELECT 1 FROM my_collection_items ci WHERE ci.saved_part_id = sp.id AND ci.collection_id = $collection)");
        if (query.TagId is not null) clauses.Add("EXISTS (SELECT 1 FROM my_saved_part_tags st WHERE st.saved_part_id = sp.id AND st.tag_id = $tag)");
        return (string.Join(" AND ", clauses), fts);
    }

    private static void AddMyLibraryParameters(SqliteCommand command, MyLibrarySearchQuery query, string? ftsQuery)
    {
        if (ftsQuery is not null)
        {
            command.Parameters.AddWithValue("$text", ftsQuery);
            command.Parameters.AddWithValue("$like", "%" + EscapeLike(query.Text!.Trim()) + "%");
        }
        if (query.CollectionId is not null) command.Parameters.AddWithValue("$collection", query.CollectionId.Value);
        if (query.TagId is not null) command.Parameters.AddWithValue("$tag", query.TagId.Value);
    }

    private static string BuildMyLibraryOrderBy(MyLibrarySearchQuery query)
    {
        var direction = query.SortDirection == SortDirection.Descending ? " DESC" : " ASC";
        var column = query.SortColumn switch
        {
            MyLibrarySortColumn.Manufacturer => "sp.manufacturer COLLATE NOCASE",
            MyLibrarySortColumn.PartNumber => "sp.part_number COLLATE NOCASE",
            MyLibrarySortColumn.TypeNumber => "sp.type_number_snapshot COLLATE NOCASE",
            MyLibrarySortColumn.Description => "sp.description_snapshot COLLATE NOCASE",
            MyLibrarySortColumn.SourceStatus => "(SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id = sp.id AND s.current_part_id IS NOT NULL)",
            _ => "sp.updated_utc"
        };
        return column + direction + ", sp.id";
    }

    private async Task<IReadOnlyList<NamedItemCount>> GetNamedItemsAsync(string parentTable, string joinTable, string foreignKey, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<NamedItemCount>();
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT p.id, p.name, COUNT(j.saved_part_id) FROM {parentTable} p LEFT JOIN {joinTable} j ON j.{foreignKey} = p.id GROUP BY p.id, p.name ORDER BY p.name COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) results.Add(new NamedItemCount(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
        return results;
    }

    private async Task<long> CreateNamedItemAsync(string parentTable, string joinTable, string foreignKey, string name, IReadOnlyCollection<long>? savedPartIds, CancellationToken cancellationToken)
    {
        var normalizedName = RequireName(name);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var now = ToDatabaseTimestamp(DateTimeOffset.UtcNow);
        long id;
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = $"INSERT INTO {parentTable}(name, created_utc, updated_utc) VALUES ($name, $utc, $utc) RETURNING id;";
            insert.Parameters.AddWithValue("$name", normalizedName);
            insert.Parameters.AddWithValue("$utc", now);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        }
        if (savedPartIds is not null)
            foreach (var partId in savedPartIds.Distinct())
                await ExecuteMyLibraryNonQueryAsync(connection, transaction, $"INSERT OR IGNORE INTO {joinTable}({foreignKey}, saved_part_id, added_utc) VALUES ($parent, $part, $utc);", cancellationToken, ("$parent", id), ("$part", partId), ("$utc", now)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    private async Task RenameNamedItemAsync(string table, long id, string name, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var changed = await ExecuteMyLibraryNonQueryAsync(connection, null, $"UPDATE {table} SET name = $name, updated_utc = $utc WHERE id = $id;", cancellationToken,
            ("$name", RequireName(name)), ("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow)), ("$id", id)).ConfigureAwait(false);
        if (changed == 0) throw new KeyNotFoundException("The item no longer exists.");
    }

    private async Task DeleteNamedItemAsync(string table, long id, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteMyLibraryNonQueryAsync(connection, null, $"DELETE FROM {table} WHERE id = $id;", cancellationToken, ("$id", id)).ConfigureAwait(false);
    }

    private async Task UpdateNamedMembershipAsync(string joinTable, string foreignKey, long parentId, IReadOnlyCollection<long> savedPartIds, bool add, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(savedPartIds);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var partId in savedPartIds.Distinct())
        {
            var sql = add
                ? $"INSERT OR IGNORE INTO {joinTable}({foreignKey}, saved_part_id, added_utc) VALUES ($parent, $part, $utc);"
                : $"DELETE FROM {joinTable} WHERE {foreignKey} = $parent AND saved_part_id = $part;";
            await ExecuteMyLibraryNonQueryAsync(connection, transaction, sql, cancellationToken,
                ("$parent", parentId), ("$part", partId), ("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow))).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int Total, int Bound, int Multiple, int Missing, int Unresolved)> GetMyLibraryStateCountsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            WITH states AS (
              SELECT sp.id,
                (SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id=sp.id AND s.current_part_id IS NOT NULL) available,
                (SELECT COUNT(*) FROM my_saved_part_sources s WHERE s.saved_part_id=sp.id) total
              FROM my_saved_parts sp)
            SELECT COUNT(*),
              COALESCE(SUM(CASE WHEN available=1 THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN available>1 THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN available=0 AND total>0 THEN 1 ELSE 0 END),0),
              COALESCE(SUM(CASE WHEN total=0 THEN 1 ELSE 0 END),0)
            FROM states;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private static async Task<int> ExecuteMyLibraryNonQueryAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<string>> GetNamesForSavedPartAsync(SqliteConnection connection, long savedPartId, string joinTable, string parentTable, string foreignKey, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT p.name FROM {joinTable} j JOIN {parentTable} p ON p.id=j.{foreignKey} WHERE j.saved_part_id=$id ORDER BY p.name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$id", savedPartId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<long> GetOrCreateNamedItemAsync(SqliteConnection connection, SqliteTransaction transaction, string table, string name, CancellationToken cancellationToken)
    {
        var normalized = RequireName(name);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {table}(name, created_utc, updated_utc) VALUES ($name, $utc, $utc)
            ON CONFLICT(name) DO UPDATE SET name=excluded.name
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$name", normalized);
        command.Parameters.AddWithValue("$utc", ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<long> UpsertSnapshotPartAsync(SqliteConnection connection, SqliteTransaction transaction, SavedPartSnapshot part, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO my_saved_parts(stable_identity, identity_kind, manufacturer, part_number, variant,
              type_number_snapshot, order_number_snapshot, description_snapshot, product_group_snapshot,
              package_key_snapshot, note, favorite, created_utc, updated_utc)
            VALUES ($identity,$kind,$manufacturer,$partNumber,$variant,$typeNumber,$orderNumber,$description,
              $productGroup,$packageKey,$note,$favorite,$created,$updated)
            ON CONFLICT(stable_identity) DO UPDATE SET
              identity_kind=excluded.identity_kind, manufacturer=COALESCE(excluded.manufacturer,my_saved_parts.manufacturer),
              part_number=COALESCE(excluded.part_number,my_saved_parts.part_number), variant=COALESCE(excluded.variant,my_saved_parts.variant),
              type_number_snapshot=COALESCE(excluded.type_number_snapshot,my_saved_parts.type_number_snapshot),
              order_number_snapshot=COALESCE(excluded.order_number_snapshot,my_saved_parts.order_number_snapshot),
              description_snapshot=COALESCE(excluded.description_snapshot,my_saved_parts.description_snapshot),
              product_group_snapshot=COALESCE(excluded.product_group_snapshot,my_saved_parts.product_group_snapshot),
              package_key_snapshot=COALESCE(excluded.package_key_snapshot,my_saved_parts.package_key_snapshot),
              note=CASE WHEN excluded.note<>'' THEN excluded.note ELSE my_saved_parts.note END,
              favorite=MAX(my_saved_parts.favorite,excluded.favorite), updated_utc=excluded.updated_utc
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$identity", part.StableIdentity);
        command.Parameters.AddWithValue("$kind", part.IdentityKind);
        AddNullableText(command, "$manufacturer", part.Manufacturer);
        AddNullableText(command, "$partNumber", part.PartNumber);
        AddNullableText(command, "$variant", part.Variant);
        AddNullableText(command, "$typeNumber", part.TypeNumber);
        AddNullableText(command, "$orderNumber", part.OrderNumber);
        AddNullableText(command, "$description", part.Description);
        AddNullableText(command, "$productGroup", part.ProductGroup);
        AddNullableText(command, "$packageKey", part.PackageKey);
        command.Parameters.AddWithValue("$note", part.Note ?? string.Empty);
        command.Parameters.AddWithValue("$favorite", part.Favorite ? 1 : 0);
        command.Parameters.AddWithValue("$created", ToDatabaseTimestamp(part.CreatedUtc));
        command.Parameters.AddWithValue("$updated", ToDatabaseTimestamp(DateTimeOffset.UtcNow));
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task UpsertSnapshotSourceAsync(SqliteConnection connection, SqliteTransaction transaction, long savedPartId, SavedSourceSnapshot source, bool preferred, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO my_saved_part_sources(saved_part_id,source_instance_identity,current_part_id,edz_path_snapshot,
              package_key_snapshot,raw_metadata_reference_snapshot,edz_file_size_snapshot,edz_last_write_utc_ticks_snapshot,
              indexed_utc_snapshot,resource_count_snapshot,existing_resource_count_snapshot,is_preferred,first_seen_utc,last_seen_utc)
            VALUES ($part,$identity,NULL,$path,$package,$metadata,$size,$lastWrite,$indexed,$resources,$existing,$preferred,$first,$last)
            ON CONFLICT(saved_part_id,source_instance_identity) DO UPDATE SET
              edz_path_snapshot=excluded.edz_path_snapshot, package_key_snapshot=excluded.package_key_snapshot,
              raw_metadata_reference_snapshot=excluded.raw_metadata_reference_snapshot,
              edz_file_size_snapshot=excluded.edz_file_size_snapshot, edz_last_write_utc_ticks_snapshot=excluded.edz_last_write_utc_ticks_snapshot,
              indexed_utc_snapshot=excluded.indexed_utc_snapshot, resource_count_snapshot=excluded.resource_count_snapshot,
              existing_resource_count_snapshot=excluded.existing_resource_count_snapshot,
              is_preferred=excluded.is_preferred, last_seen_utc=excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$part", savedPartId);
        command.Parameters.AddWithValue("$identity", source.SourceInstanceIdentity);
        command.Parameters.AddWithValue("$path", source.EdzPath);
        AddNullableText(command, "$package", source.PackageKey);
        command.Parameters.AddWithValue("$metadata", source.RawMetadataReference);
        command.Parameters.AddWithValue("$size", source.EdzFileSize is null ? DBNull.Value : source.EdzFileSize.Value);
        command.Parameters.AddWithValue("$lastWrite", source.EdzLastWriteUtcTicks is null ? DBNull.Value : source.EdzLastWriteUtcTicks.Value);
        command.Parameters.AddWithValue("$indexed", source.IndexedUtc is null ? DBNull.Value : ToDatabaseTimestamp(source.IndexedUtc.Value));
        command.Parameters.AddWithValue("$resources", source.ResourceCount);
        command.Parameters.AddWithValue("$existing", source.ExistingResourceCount);
        command.Parameters.AddWithValue("$preferred", preferred ? 1 : 0);
        command.Parameters.AddWithValue("$first", ToDatabaseTimestamp(source.FirstSeenUtc));
        command.Parameters.AddWithValue("$last", ToDatabaseTimestamp(source.LastSeenUtc));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string RequireName(string name)
    {
        var result = name?.Trim();
        if (string.IsNullOrWhiteSpace(result)) throw new ArgumentException("A non-empty name is required.", nameof(name));
        if (result.Length > 120) throw new ArgumentException("The name cannot exceed 120 characters.", nameof(name));
        return result;
    }

    private sealed record CatalogIdentityRow(
        string StableIdentity,
        string IdentityKind,
        string? Manufacturer,
        string? PartNumber,
        string? Variant,
        string? TypeNumber,
        string? OrderNumber,
        string? Description,
        string? ProductGroup,
        string? PackageKey,
        string RawMetadataReference,
        int ResourceCount,
        int ExistingResourceCount,
        string EdzPath,
        long FileSize,
        long LastWriteUtcTicks,
        DateTimeOffset IndexedUtc);
}
