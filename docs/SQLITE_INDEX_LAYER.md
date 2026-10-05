# SQLite Index Layer

## Scope

This layer indexes the read-only output of `EplanEdzManager.Edz`. It does not alter an EDZ, does not use the EPLAN API, and does not modify an EPLAN parts database.

The dependency direction is:

```text
EplanEdzManager.Core          (no database dependency)
          ↑
EplanEdzManager.Edz           (read-only EDZ parser)
          ↑
EplanEdzManager.Infrastructure.Sqlite
          ↑
EplanEdzIndex                 (CLI)
```

No WPF or Add-In project is introduced.

## Database design

| Table | Responsibility |
|---|---|
| `schema_migrations` | Applied migration versions. |
| `indexed_directories` | Registered scan roots, recursion setting, and last successful scan time. |
| `edz_files` | EDZ fingerprint and indexing status. One row per normalized absolute path. |
| `parts` | Searchable part metadata and resource counters. Deleted by cascade when its EDZ index is removed. |
| `resources` | Resource references and whether each referenced archive entry exists. |
| `parts_fts` | FTS5 external-content index for manufacturer, part number, type number, and description. |

Migrations are embedded in the infrastructure assembly and applied transactionally. `001_initial.sql` owns the disposable Catalog index; `002_my_library.sql` adds durable personal-library metadata and stable logical identities. Schema updates must be added as monotonically numbered scripts; applied scripts are never edited in place. Existing v1 databases upgrade in place and Catalog identities are backfilled after migration. See `MY_LIBRARY_DATA_MODEL.md` for the v2 ownership and deletion rules.

The implementation uses `Microsoft.Data.Sqlite` 8.0.31. Its default native SQLite bundle includes FTS5. A new connection is opened per repository operation, foreign keys are enabled on every connection, WAL mode is enabled at database initialization, and write batches use transactions. This follows the provider's documented connection/concurrency model:

- <https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.31>
- <https://learn.microsoft.com/dotnet/standard/data/sqlite/database-errors>
- <https://learn.microsoft.com/dotnet/standard/data/sqlite/transactions>
- <https://learn.microsoft.com/dotnet/standard/data/sqlite/custom-versions>
- <https://www.sqlite.org/fts5.html>

## Incremental index rules

An EDZ is treated as unchanged only when all of these match the successful row already stored:

1. normalized absolute file path;
2. file size;
3. `LastWriteTimeUtc.Ticks`;
4. prior status is `Indexed`.

This avoids hashing and reopening unchanged, very large EDZ files. A changed or previously failed file is read again. Each file replacement is atomic: old parts/resources are removed and the replacement rows are inserted in one transaction. If parsing fails, stale parts are deleted and a `Failed` file row stores the exception type/message.

At the end of each directory scan, indexed files no longer present under that registered directory are deleted. Foreign-key cascades remove their parts and resources; FTS triggers remove their search entries.

## Background scanning

`EdzIndexBuilder.ScanDirectoryAsync` starts the directory scan on a worker task and supports cancellation and `IProgress<IndexProgress>`. A builder serializes its scans because SQLite permits only one pending writer and an EDZ archive exposes one leased entry stream at a time. Callers can remain responsive without creating competing write pipelines.

`ScanRegisteredDirectoriesAsync` processes all enabled registered directories. It is intended to be called by a future application service or scheduler; it has no UI dependency.

## Search API

`SqliteIndexRepository.SearchAsync(PartSearchQuery)` supports:

- FTS5 full-text query across Manufacturer, PartNumber, TypeNumber, and Description;
- Manufacturer substring filter;
- PartNumber substring filter;
- TypeNumber substring filter;
- resource filters:
  - `Any`: no resource restriction;
  - `HasAny`: at least one referenced resource exists;
  - `AllPresent`: at least one reference exists and every reference resolves;
  - `Missing`: at least one reference is unresolved;
  - `None`: the part has no resource references;
- bounded limit (1–1000) and offset.

User text is converted to a parameterized FTS query, and all field filters are parameterized. SQL text is not assembled from raw user values.

## CLI

Examples:

```powershell
dotnet run --project tools/EplanEdzIndex -- init D:\indexes\parts.db

dotnet run --project tools/EplanEdzIndex -- add-dir D:\indexes\parts.db D:\EDZ --recursive

dotnet run --project tools/EplanEdzIndex -- scan D:\indexes\parts.db

dotnet run --project tools/EplanEdzIndex -- search D:\indexes\parts.db DR-100-24 --resources present --json

dotnet run --project tools/EplanEdzIndex -- search D:\indexes\parts.db --manufacturer "MEAN WELL" --type-number DIN --json

dotnet run --project tools/EplanEdzIndex -- prune D:\indexes\parts.db D:\EDZ

dotnet run --project tools/EplanEdzIndex -- stats D:\indexes\parts.db --json
```

`scan` with an explicit directory also registers or updates that directory. `scan` without a directory scans all registered directories. `remove-dir` deletes the registration and its complete index through cascading foreign keys; it never deletes source EDZ files.

## Verification coverage

`EplanEdzManager.Infrastructure.Sqlite.Tests` covers:

- idempotent migration;
- repository inserts/replacement;
- full-text and field-specific searches;
- all resource-existence modes;
- FTS cleanup after replacement and stale-file removal;
- CLI initialization, directory registration, search, statistics, and removal;
- a real read-only scan of `samples/明纬.edz`;
- unchanged-file skip on the second scan;
- removal of the copied EDZ followed by stale-index cleanup;
- SHA-256 verification that the original sample is unchanged.
