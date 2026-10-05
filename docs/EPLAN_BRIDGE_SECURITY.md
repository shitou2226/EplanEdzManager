# EPLAN Bridge Security Boundary

## Security objective

Phase 4 may read explicitly selected source EDZ files and create a user-selected output EDZ. It must not open, switch, write or persist settings into the user's active EPLAN Parts Database. The user database is outside the Bridge trust boundary.

## Framework and dependency isolation

The dependency direction is:

```text
EplanEdzManager.Desktop (.NET 8 WPF)
        -> EplanEdzManager.Application (.NET 8 Windows)
        -> EplanEdzManager.EplanBridge.Client (.NET 8 Windows)
        -> named pipe + Protocol DTOs (netstandard2.0)
        -> EplanEdzManager.EplanBridge.exe (net472, x64)
        -> EplanEdzManager.EplanApi (net472, x64)
        -> EPLAN 2.9 Runtime
```

Desktop, Application and Infrastructure.Sqlite have no ProjectReference to `EplanEdzManager.EplanApi`. Only the net472/x64 Bridge loads it.

## Named-pipe boundary

- The pipe is created with a protected ACL granting FullControl only to the current Windows user SID.
- After connection, the server impersonates the client long enough to compare the client SID with the Bridge SID.
- The client connects to local machine `.` only.
- The name includes Windows session id plus a cryptographically unpredictable GUID generated for that Bridge instance.
- There is one client slot; the name is passed directly as a process argument and is not a global fixed name.
- No TCP port, HTTP listener, localhost web API or gRPC service exists.

## Process launch

The .NET 8 client uses `ProcessStartInfo.ArgumentList`, `UseShellExecute=false` and an explicit executable path. Pipe name, session GUID, variant bin and platform bin are separate arguments; no shell command is concatenated. Standard output/error are captured locally and size limited for crash diagnostics.

The Bridge validates that session id is a GUID in `N` format, that the pipe begins with the expected session prefix, and that supplied EPLAN directories exist.

## Temporary System Configuration

Each Bridge process creates exactly one root:

```text
%TEMP%\EplanEdzManager\Bridge\<session-guid>\
```

It copies the EPLAN 2.9 variant into that root, adds an `EplanEdzManagerTemp` System Configuration and rewrites user/station/company settings, EPLAN data and rights paths to isolated subdirectories. Before any source import, the runtime resolves and verifies every required path:

- `EPLAN_DATA`
- `CFG_USER`
- `CFG_STATION`
- `CFG_COMPANY`
- `MD_PARTS`
- `MD_IMG`
- `MD_MACROS`
- `MD_DOCUMENTS`
- `MD_MECHANICALMODELS`

Any resolved path outside the session root fails as `BRIDGE103` before import. Temporary databases are created only with `MDPartsManagement.CreateDatabase`; the implementation does not edit EPLAN internal tables or use SQL `DELETE`.

## Official import/export path

For every Preferred Source group, the Bridge:

1. creates a fresh isolated source Parts Database;
2. calls `PartsService.ImportPartsListToSystem(sourceEdz, "IXPartsImportExportEdz")`;
3. reads `MDPartsDatabase` to verify Manufacturer + PartNumber + Variant;
4. exports only the selected set through the documented `PartsService` SQL-WHERE filter;
5. imports the official subset EDZ into one isolated combined database;
6. creates one official final EDZ;
7. officially re-imports it into another fresh isolated database.

The filter documentation used is the local EPLAN 2.9 API XML `eplan-api/api-xml-docs/Eplan.EplApi.HEServicesu.xml`, lines 18315-18349: `strSqlFilterPart` uses regular SQL `WHERE` syntax and tblPart column names. Phase 4 therefore filters on escaped Manufacturer + PartNumber and verifies Variant after import; it does not invent a tblVariant join.

## Input and identity controls

- Application performs an early, no-runtime preflight.
- Bridge independently rejects empty requests, missing paths, existing output, missing identity/PartNumber and ambiguous Preferred Sources.
- Apostrophes in filter literals are doubled; NUL is rejected.
- Clauses are deduplicated and ordinal-sorted for deterministic output.
- Default batch limits are 100 terms and 24,000 characters.
- Backtick, double quote, spaces, Unicode, hyphen, underscore and slash are preserved as literal content; only SQL apostrophe escaping is applied.

## Output publishing

EPLAN always writes first inside the operation root. Official round-trip and offline validation complete before publish. The Bridge then copies to a random sibling temporary file in the selected destination directory, flushes it to disk and renames it to the final name. The Bridge refuses an existing destination by default. The Desktop's standard Save dialog is the only replacement confirmation; Phase 4 never silently overwrites.

## Cancellation, crash and cleanup

- Cancellation is cooperative and never uses `Thread.Abort` or process injection.
- The current EPLAN API call is allowed to return; the next safe point stops subsequent work.
- Operation handles/databases are closed before recursive cleanup.
- Cleanup first verifies the target is inside the approved Bridge temp base and rejects reparse points.
- Operation cleanup failure returns `BRIDGE401`, not normal success.
- A process crash is isolated from WPF. The client closes the private pipe, reports the log location and can start a new Bridge.
- A hard OS/process kill can leave the current session root because no process remains to execute `finally`; it is identifiable by the session GUID and is never mistaken for user data.

## Logs and privacy

Bridge JSONL logs are written under:

```text
%LOCALAPPDATA%\EplanEdzManager\Logs\Bridge\
```

They contain UTC timestamp, SessionId, RequestId, stage, source file name, counts, sizes, elapsed time and local diagnostics. They do not record passwords, API keys, tokens or source document contents. Logs remain local; Phase 4 performs no network upload.

## Explicit exclusions

Phase 4 does not implement an EPLAN Add-In, import into the user's active database, a custom EDZ writer, background resident Bridge, MES integration, AI classification, EMA rendering or 3D rendering.

