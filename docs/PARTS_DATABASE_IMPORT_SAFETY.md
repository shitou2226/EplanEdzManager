# Parts Database Import Safety

## Safety objective

Phase 5 writes only to an EPLAN 2.9 Parts Database explicitly selected by the user. It prioritizes a reviewable, recoverable path over online convenience: inspect, Preview, decide, confirm, fingerprint recheck, verified backup, official import, reopen and verify.

It does not detect or switch the current EPLAN GUI database, change the user's System Configuration, update schema, write EPLAN tables with SQL, delete parts, run in the background or provide an Add-In.

## Supported boundary

| Area | Phase 5 status |
|---|---|
| EPLAN 2.9 Access MDB | Supported and runtime verified. |
| SQL Server or other backends | Not Yet Verified; rejected by v1. |
| Closed, exclusively accessible MDB | Required. |
| MDB active/locked in EPLAN Parts Management | Rejected as `DB003`; close Parts Management or switch away first. |
| Automatic schema migration | Not supported. |
| Selective Update | Disabled; existing/different/conflicting rows are Skip-only in this Beta. |
| Transactional/atomic import claim | Not made; API rollback coverage is not proven. |

The user must also explicitly choose the target EPLAN data root used to resolve master-data resources. It is never inferred from the active GUI database.

## Framework and runtime isolation

```text
Desktop .NET 8
  -> Application
  -> Bridge Client
  -> current-user Windows Named Pipe + JSON 1.1
  -> EplanBridge.exe net472/x64
  -> EplanApi
  -> EPLAN 2.9 Runtime
```

Desktop, Application and Infrastructure.Sqlite have no reference to EplanApi. The Bridge creates a private temporary System Configuration. `CFG_USER`, `CFG_STATION` and `CFG_COMPANY` remain under the Bridge session root. A separate session-local target scheme maps only the explicit target EPLAN data root for `MD_IMG`, `MD_MACROS`, `MD_DOCUMENTS` and `MD_MECHANICALMODELS`; no user System Configuration file is edited.

The selected target MDB stays at its user-selected path. It is not copied into the isolation root and then misreported as the user database.

## Inspection and stale-preview gate

Before Preview/import, the Bridge:

1. canonicalizes the explicit path and requires `.mdb`;
2. checks existence and a non-zero file;
3. probes exclusive read/write access;
4. opens it through `MDPartsManagement.OpenDatabase`;
5. reads `Type`, `IsSchemeUpToDate` and `Parts`;
6. records path, size, last-write UTC ticks and SHA-256;
7. records resource mappings and refuses out-of-bound target mappings.

Preview returns a random `previewId` and confirmation token. It fingerprints both the target MDB and the validated SelectedParts EDZ. Import checks the in-session token in constant time and rechecks the target path/fingerprint plus the EDZ SHA-256 before backup and immediately before the official write. Any change returns `DB005`; the user must Preview again.

## Backup policy

Immediately before any official write, Phase 5 repeats the fingerprint and exclusive-access checks, then creates:

```text
<target-directory>\backup\<database>.before-<UTC timestamp>-<GUID>.mdb
```

The name is unique and never overwrites an earlier backup. The copy is opened exclusively and flushed to disk, hashed with SHA-256, reopened through `MDPartsManagement`, and required to have the same part count as the source. Failure is `DB006` and import does not start.

This is a verified closed-MDB file backup, not an EPLAN server backup and not a claim of online-copy consistency. It protects the MDB. It does not roll back resource files that the official importer may already have written under the explicit target master-data paths.

## Official write path

No code issues SQL INSERT/UPDATE/DELETE or edits internal table structures. The enabled mode calls the EPLAN 2.9 official API:

```csharp
partsService.PartsDatabase = explicitTargetMdb;
partsService.ImportPartsListToSystem(
    validatedSelectedPartsEdz,
    "IXPartsImportExportEdz",
    string.Empty,
    PartsService.ImportMode.AppendNewRecords);
```

The EDZ must be the Phase 4 official, round-trip and offline validated handoff. Phase 5 repeats offline archive/reference validation and does not rebuild or custom-merge EDZ files.

`AppendNewRecords` applies to the complete validated EDZ. If it contains multiple New rows, the Beta requires all of those rows to be selected for import or all to be skipped; it does not claim per-row selective import inside one EDZ.

## Post-import verification

The database is reopened through `MDPartsManagement`. Each requested Manufacturer + PartNumber + normalized Variant is queried and reported as Added, Skipped or Failed. Picture and EMA references are resolved using the target `MD_IMG`/`MD_MACROS` mappings, required to remain under the explicit target root, and checked for file existence.

PDF, Construction, Mechanical Model and Accessory resources remain **Not Yet Covered**. Their mappings are recorded but no success claim is made.

## Cancellation and failure truthfulness

Cancellation never uses `Thread.Abort`. Before the official call it stops at a safe point. During a non-interruptible EPLAN call it waits, reopens the MDB and reports the observed final state. A partial write is never labelled “no changes.”

Official-call failure keeps the unique backup and leaves the audit trail/log available. Because rollback/transaction semantics have not been proven, the user must use the per-part outcome and post-failure openability check rather than assume atomicity.

## Audit and privacy

Every actual import writes a local `ImportReport.json` under `%LOCALAPPDATA%\EplanEdzManager\Logs\Bridge\ImportReports`. It records operation/time/version, explicit target, before/after fingerprints, decisions, per-part outcomes, backup/hash, warnings, diagnostics and phase timings. No passwords, API keys, tokens or source-document content are uploaded; Phase 5 has no network path.

## Local evidence used

- EPLAN 2.9 API XML: `eplan-api/api-xml-docs/Eplan.EplApi.HEServicesu.xml`, `PartsService.PartsDatabase`, import overloads and `ImportMode` enum sections.
- EPLAN 2.9 API XML: `eplan-api/api-xml-docs/Eplan.EplApi.MasterDatau.xml`, `MDPartsManagement.CreateDatabase/OpenDatabase`, `MDPartsDatabase.Parts/Type` and transaction members.
- Local manual: `EPLAN Data Portal 使用手册（信捷产品）`, PDF pages 18–20, for EDZ exchange, target database selection/path mapping and documented UI import-mode semantics.

The product behavior is based on the local 2.9.4 runtime smoke/integration results, not on newer-version API assumptions.
