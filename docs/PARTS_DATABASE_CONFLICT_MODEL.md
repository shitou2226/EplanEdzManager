# Parts Database Conflict Model

## Identity boundary

Phase 5 compares target rows by Manufacturer + PartNumber + normalized Variant. Application Stable Identity is carried for deterministic selection and audit, but is not claimed to be EPLAN's internal primary key. Matching is case-insensitive after trimming; an empty Variant is normalized to `1`, matching the tested EPLAN 2.9 behavior.

Preview requires exactly one incoming EDZ row for each application request. It then compares that logical identity with the target MDB.

## Classification

| Status | Condition | Default/action allowed |
|---|---|---|
| `New` | One incoming match and no target match. | `ImportNew` suggested; user may choose `Skip`. |
| `ExistingSame` | One target match and no tracked metadata differences. | `Skip` only. |
| `ExistingDifferent` | One target match with one or more tracked differences. | `Skip` only in v1; displayed as Conflict. |
| `Ambiguous` | More than one incoming or target identity match. | `Skip`; import cannot proceed while Preview is ambiguous. |
| `Invalid` | Missing identity fields or no unique incoming match. | `Skip`; import cannot proceed while Preview is invalid. |

Tracked comparison fields are Type Number, Order Number, Description, Picture and Macro. Whitespace is trimmed, but content is not broadly normalized or guessed. The UI shows existing value, incoming value and the explicit action.

`ExistingSame` means equivalent for these tracked Phase 5 fields. It is not a byte-for-byte proof that every EPLAN property is identical.

## Conservative mode policy

The only enabled official product mode is Add New Only / Skip Existing, implemented through EPLAN 2.9 `AppendNewRecords` after Preview. Existing rows are never silently overwritten.

Runtime smoke coverage confirmed that the local API exposes and can invoke the documented enum values `AppendNewRecords`, `UpdateExistingRecords` and `UpdateAndAppend`. That does not make all three safe product features. EPLAN applies those modes to the whole EDZ; Phase 5 has not proven a supported way to update only individually selected conflict rows while guaranteeing that other rows in the same EDZ are untouched. Therefore:

- the UI offers `ImportNew` and `Skip` only;
- conflict and existing rows default to `Skip`;
- a transport `Update` decision is explicitly rejected as `DB102`;
- when a validated EDZ contains multiple New rows, they must be imported together or all skipped; a partial New-row selection is rejected as `DB102` because `AppendNewRecords` applies to the whole EDZ;
- no simulated update, SQL update or custom EDZ rewrite is used.

This distinction prevents a runtime-callability result from being overstated as selective-update safety.

To import only a subset of New rows, the user must return to Phase 4, export exactly that subset as a new officially generated and validated EDZ, then run Phase 5 Preview again.

## Determinism and decisions

Preview items are ordered by Stable Identity. Import requires exactly one decision per Preview item. Missing decisions, duplicate decisions, unknown identities, `ImportNew` on non-New items, or `Update` all fail before the official write.

The final confirmation shows the canonical target path and New/Skipped totals. The confirmation token binds import to the Bridge session's Preview; the target fingerprint binds it to the reviewed database state. A fingerprint change, path change, expired/restarted Bridge, or mismatched token requires a new Preview.

## Verified cases

- Empty MDB: `DR-100-24` classified New and added once.
- Repeat import: classified ExistingSame and skipped; count did not grow.
- Ten real Mean Well parts: all classified New in a fresh MDB and verified after official import.
- Mixed official Phase 4 EDZ: one new and one existing item were deterministically added/skipped.
- Controlled target metadata change: classified ExistingDifferent with a TypeNumber difference; default Skip preserved target data.
- Explicit Update attempt: rejected as `DB102` before write.
- Stale Preview and locked MDB: rejected as `DB005` and `DB003` respectively.

## Future extension rule

Selective Update must remain disabled until a future phase proves, on EPLAN 2.9, an official per-item or safely constrained EDZ workflow, plus backup/verification/partial-failure tests for every affected row and resource. A new UI warning alone is not sufficient evidence.
