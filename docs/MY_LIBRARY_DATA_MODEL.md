# My Library Data Model

## Purpose

Phase 3 adds a durable personal metadata library above the disposable EDZ catalog index. Favorites, notes, collections and tags must survive rescans, path changes and source removal. No resource binary, EDZ archive, EPLAN DLL or WPF state is copied into My Library.

## Identity model

### Logical Part Identity

`StableIdentity` is `LP1:` plus a SHA-256 digest over length-prefixed canonical fields.

Primary identity fields:

1. Manufacturer
2. Part Number
3. Variant (empty is valid)

Canonicalization is deterministic: Unicode NFKC, trim, internal whitespace collapse, and invariant uppercase. Manufacturer aliases are not guessed. `Siemens` and `SIEMENS AG` therefore remain separate until an explicit future alias feature exists.

When Manufacturer or Part Number is absent, a non-empty Package Key is required and the identity kind is `PackageFallback`. This makes incomplete metadata savable without silently coalescing every blank part. A part with neither the primary identity nor Package Key is rejected from My Library as unresolved input.

Package Key does not participate in a normal primary identity. Consequently the same manufacturer/part/variant from `Siemens_2025.edz` and `Siemens_2026.edz` is one logical part with two source candidates.

### Source Instance Identity

`SourceInstanceIdentity` is `SI1:` plus a SHA-256 digest over normalized full EDZ path, Package Key and raw part-metadata archive path. It distinguishes EDZ occurrences without making the path part of the logical identity.

Moving an EDZ creates a new observed source instance. Rebinding associates it with the existing Saved Part by `StableIdentity`; the previous source snapshot remains as unavailable history. No “newer file is better” rule exists.

## Schema v2

Migration `002_my_library.sql` extends catalog `parts` with:

- `logical_identity`
- `identity_kind`
- an index on `logical_identity`

Existing v1 rows are backfilled by the repository after migration using the same Core identity implementation used for future inserts.

### `my_saved_parts`

Durable logical records:

- stable identity and identity kind;
- Manufacturer, Part Number and Variant snapshots;
- Type Number, Order Number, Description, Product Group and Package Key snapshots;
- Note and Favorite;
- Created/Updated timestamps.

The table does not reference `parts.id`. It remains after every catalog/source deletion.

### `my_saved_part_sources`

Historical/current source candidates:

- owning Saved Part;
- source-instance identity;
- nullable current catalog `part_id` with `ON DELETE SET NULL`;
- EDZ path, Package Key and raw metadata path snapshots;
- EDZ size/mtime/index time and resource-count snapshots;
- preferred-source flag and last-seen timestamp.

A partial unique index allows at most one preferred source per Saved Part. A missing preferred source remains preferred until the user chooses another; the system does not silently switch it.

### `my_collections` and `my_collection_items`

Collections have case-insensitively unique names. The junction table implements many-to-many membership. Deleting a collection cascades only its membership rows.

### `my_tags` and `my_saved_part_tags`

Tags have case-insensitively unique names. The junction table implements many-to-many tagging. Deleting a tag removes only tag relationships.

### `my_saved_parts_fts`

FTS5 indexes the Saved Part metadata snapshot and note. It has the conventional insert/delete/update trigger set. Tags and collections are intentionally not copied into FTS: their many-to-many updates would require a wider trigger network. They are searched and filtered with indexed `EXISTS` queries instead.

## Source states

| State | Meaning |
|---|---|
| `Bound` | Exactly one current catalog candidate is available. |
| `MultipleCandidates` | More than one current candidate is available. |
| `SourceMissing` | Historical source rows exist, but none are currently bound/available. |
| `Unresolved` | No source snapshot and no current candidate exist. |

Availability is based on the current catalog binding. A filesystem change is reflected after the normal rescan/prune or an explicit rebind; the durable Saved Part remains visible during both states.

## Rebinding

`RebindSavedParts` is transactional:

1. clear current catalog bindings while retaining source snapshots;
2. join current catalog parts to Saved Parts by `StableIdentity`;
3. update or insert source candidates by `SourceInstanceIdentity`;
4. refresh metadata/resource snapshots and last-seen time;
5. keep user-selected preferred source unchanged;
6. emit diagnostics for missing preferred sources, multiple candidates or unresolved records.

Application scan workflows call rebinding after successful/pruned catalog scans. It is also available explicitly after restore.

## Transaction boundaries

These operations are single SQLite transactions:

- add/update Saved Part plus source candidate;
- create a collection and add initial members;
- bulk favorite/tag/collection/remove operations;
- backup restore;
- source rebinding.

SQLite remains WAL-based with one connection per operation and a busy timeout. Catalog scan writes and My Library writes serialize without sharing UI objects.

## Search and paging

Catalog Search and My Library Search are separate repository/application requests. My Library query supports:

- snapshot Manufacturer, Part Number, Type Number and Description;
- Note through FTS;
- Tag and Collection text/filter queries;
- Favorites Only, Collection and Tag filters;
- database-side sorting, `LIMIT`, `OFFSET` and total count.

No operation binds all Saved Parts to one `ObservableCollection`.

## Backup and restore

The JSON envelope contains:

- `schemaVersion`;
- `exportedUtc`;
- `applicationVersion`;
- Saved Parts and source snapshots;
- Collections and memberships;
- Tags and relationships.

Import is `Validation → Preview → Apply`. Unknown JSON fields are ignored. Preview reports invalid, duplicate-in-file, existing and new logical records. Apply merges by Stable Identity and relationship names in one transaction; it never replaces the SQLite file and never imports resource binaries.

## Deletion semantics

- Remove from Collection: delete one membership.
- Remove Tag: delete one relationship.
- Remove from My Library: delete the Saved Part and its personal relationships/source snapshots.
- Remove EDZ Registration: remove only catalog index rows; Saved Parts remain.
- Delete Source File: unsupported.

## Future bridge boundary

Phase 3 can create a pure `PartExportRequest` containing Stable Identity, preferred EDZ path, Package Key, Part Number and Variant. The future net472 bridge consumes that DTO or an IPC representation; it never reads WPF selection state. Phase 3 does not implement IPC or export execution.
