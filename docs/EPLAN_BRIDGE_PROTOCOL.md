# EPLAN Bridge Protocol 1.1

## Scope

The protocol connects the .NET 8 Desktop/Application process to the x64 .NET Framework 4.7.2 EPLAN 2.9 Bridge. It is a local named-pipe protocol only. Protocol 1.1 preserves the Phase 4 export contract and adds the Phase 5 inspect, preview and confirmed-import workflow. It exposes no HTTP, TCP, gRPC, Add-In endpoint or public automation service.

## Transport and framing

- Windows named pipe, full duplex, one Desktop client per Bridge process.
- Pipe name: `EplanEdzManager.S<WindowsSessionId>.<random-guid>`.
- Endpoint: local machine (`.`); network pipes are not used.
- UTF-8 without BOM; one compact JSON object per line.
- Every message carries `protocolVersion`, `requestId` and `messageType`.
- Unknown JSON fields are ignored; unknown message types are rejected.

```json
{
  "protocolVersion": "1.1",
  "requestId": "9f86818e911c48d3b11f89326816fe63",
  "messageType": "Ping"
}
```

The Bridge validates the envelope before deserializing a command. A version mismatch returns `BRIDGE002` and executes nothing. This is an intentional protocol-version change; a 1.0 client cannot silently use 1.1 semantics.

## Commands

| Command | Response/events | Purpose |
|---|---|---|
| `Ping` | `PingResult` | Verifies pipe/process identity. |
| `GetCapabilities` | `CapabilitiesResult` | Reports local EPLAN runtime/API readiness. |
| `ExportParts` | `ExportProgress`, then `ExportResult` | Phase 4 isolated official selected-parts export. |
| `InspectPartsDatabase` | `InspectPartsDatabaseResult` | Inspects one explicit target MDB without changing its contents. |
| `PreviewPartsImport` | `PreviewPartsImportResult` | Compares a validated EDZ with the explicit target and creates an in-session confirmation capability. |
| `ImportParts` | `ImportProgress`, then `ImportPartsResult` | Rechecks Preview/fingerprint, creates a verified backup, performs official import and verifies actual outcomes. |
| `CancelOperation` | `CancelAcknowledged`, then the target operation reaches its next safe outcome | Requests cooperative cancellation; never aborts the EPLAN thread. |
| `Shutdown` | `ShutdownAcknowledged` | Ends the short-lived Bridge session. |

## Phase 4 export contract

`ExportParts` carries `outputPath` and transport-only part records: Stable Identity, Preferred Source EDZ, Manufacturer, Package Key, Part Number and Variant. The Bridge groups sources, uses the official `IXPartsImportExportEdz` path, performs official round-trip plus offline validation and publishes only after validation. Existing destinations are refused. See `SAFETY.md` for the export safety boundary.

## InspectPartsDatabase

The request carries one canonical explicit database path. Version 1.1 accepts only an existing EPLAN 2.9 Access MDB. Inspection returns:

- database type, open/read/write/compatibility/schema flags and part count;
- SHA-256 fingerprint containing canonical path, file size and last-write UTC ticks;
- resolved `MD_IMG`, `MD_MACROS`, `MD_DOCUMENTS` and `MD_MECHANICALMODELS` mappings;
- detected-version diagnostic, `canImport`, structured error details and elapsed time.

An exclusive read/write probe is required. A locked or read-only MDB is not considered importable. The command never updates schema and never discovers or changes the EPLAN GUI's active database.

## PreviewPartsImport

The request carries the explicit target MDB, the already generated and validated Phase 4 EDZ, and application transport identities. The Bridge repeats offline EDZ validation, reads the target through `MDPartsDatabase`, and returns each item as `New`, `ExistingSame`, `ExistingDifferent`, `Ambiguous` or `Invalid`.

Comparison identity is Manufacturer + PartNumber + normalized Variant. Stable Identity remains an application identifier and is not claimed to be EPLAN's internal key. Differences include Type Number, Order Number, Description, Picture and Macro.

The result contains totals, metadata/differences, suggested actions, target resource mappings, fingerprint, `previewId`, a cryptographically random `confirmationToken`, supported modes and elapsed time. The token is held only in the current Bridge session and does not authorize any later database/path/fingerprint mismatch.

## ImportParts

The request must echo the exact `previewId`, `confirmationToken`, target path, target fingerprint and one explicit decision for every Preview item. The Bridge verifies all of them before writing. The actual target fingerprint is recomputed immediately before backup/import; a change returns `DB005` and requires a new Preview.

Version 1.1 product decisions are deliberately restricted to:

- `ImportNew` for `New` items;
- `Skip` for any item.

`Update` exists as a protocol value so it can be rejected explicitly and audited, but it is not an enabled product mode. It returns `DB102`. Although EPLAN 2.9 exposes whole-file update enum values, the product has not proven a safe per-row way to prevent unselected rows in a multi-part EDZ from being updated.

Because `AppendNewRecords` also applies to the whole EDZ, a request may import all New Preview rows or skip all of them; it cannot import only some New rows from that EDZ. Mixed ImportNew/Skip decisions among New rows return `DB102` and require a new Phase 4 validated subset EDZ.

Successful import uses `PartsService.ImportPartsListToSystem(validatedEdz, "IXPartsImportExportEdz", "", AppendNewRecords)`. The target MDB is closed/reopened and every requested identity is queried. Results contain Added/Updated/Skipped/Failed totals and per-part outcomes, before/after fingerprints, backup path/hash, audit path, runtime version, total/backup/official-import/verification timings, warnings and diagnostics.

## Import progress and cancellation

Import progress stages are `InspectingDatabase`, `CheckingConflicts`, `PreparingBackup`, `GeneratingValidatedEdz`, `OpeningTargetDatabase`, `Importing`, `VerifyingParts`, `VerifyingResources`, `ClosingDatabase` and `Completed`. `GeneratingValidatedEdz` is available for workflows that need the Phase 4 handoff; current Phase 5 imports an already validated EDZ and does not regenerate it.

`CancelOperation` uses its own request id plus `targetRequestId`. If cancellation arrives before the official call, the operation returns `DB301` at a safe point. If it arrives while EPLAN is inside a non-interruptible call, the call is allowed to return, the target is reopened, and actual Added/Skipped/Failed outcomes are reported with a warning. The client must never translate such a result into “no changes were made.”

## Errors

| Code | Meaning |
|---|---|
| `BRIDGE001`–`BRIDGE401` | Existing Phase 4 runtime, protocol, export, validation, cancellation and cleanup errors. |
| `DB001` | Target database not found. |
| `DB002` | Unsupported backend/schema/environment mapping; v1 supports MDB only. |
| `DB003` | Target MDB is locked/not exclusively accessible. |
| `DB004` | Target MDB is read-only. |
| `DB005` | Target changed after Preview. |
| `DB006` | Unique verified backup could not be created. |
| `DB101` | Preview failed or the validated EDZ/request cannot be resolved. |
| `DB102` | Missing/invalid decision or unsafe Update request. |
| `DB201` | Official target import failed. |
| `DB202` | Partial import. |
| `DB203` | Post-import identity/resource verification failed. |
| `DB301` | Cancelled at a safe point before completion. |

Errors carry `userMessage`, `technicalDetails` and `logPath`; the client never treats `Exception.Message` as the complete protocol contract.

## Timeouts and process failure

Connection, capability, operation-idle and shutdown timeouts remain separate. Progress resets the long operation-idle wait. Unexpected process exit becomes `EplanBridgeProcessExitedException`; it cannot crash WPF, and a later session can start a fresh Bridge.
