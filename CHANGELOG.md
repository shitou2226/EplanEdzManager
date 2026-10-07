# Changelog

## 1.0.0-beta.6 — localization, resilient indexing and part preview

### Added

- Double-click image preview for the first supported picture embedded in a part's source EDZ.
- Four real application screenshots covering the catalog, part preview, localized safe import and My Library source status.
- A dedicated beta.6 release note with the verified safety and compatibility boundaries.

### Fixed

- Normalized recoverable EPLAN multilingual description text instead of exposing stray language markers or `??` placeholders.
- Accepted large valid EDZ catalogs that were previously rejected by a small-entry protection rule.
- Localized the complete eight-step safe MDB import workflow, including dynamic states, decisions, progress and errors.
- Kept the Desktop shortcut and EPLAN Add-In launcher on the same installed application and explicit icon.

### Verification

- Release build completed with 0 warnings and 0 errors.
- 171/171 tests passed, including 14 local EPLAN 2.9 Bridge integration tests.
- Installer, Portable ZIP, offline launch, Add-In configuration and entry-point consistency smoke checks passed.
- Only EPLAN P8 2.9.4.14642 is labeled verified; beta remains unsigned.

## 1.0.0-beta.3 — initial public-source snapshot

### Added

- Read-only EDZ catalog, incremental SQLite/FTS indexing and My Parts Library.
- Resource preview/export, official selective EDZ export, guarded MDB import and EPLAN 2.9 Add-In.
- Independent public-source layout, bilingual README and an EPLAN-free build/test entry point.

These product capabilities already existed in the audited beta. The public repository preparation adds no business features and does not recreate private development history.

### Safety

- Per-user installation and user-scoped LocalAppData/Temp storage.
- Self-contained Desktop/tools; end-user operation does not require a development environment.
- Verified EPLAN detection, Offline Mode, source EDZ read-only access and guarded import operations.
- No redistributed EPLAN proprietary binaries, API documentation, ERX, real vendor EDZ/MDB fixtures or private configuration.
- Public source gate rejects credentials, private paths and forbidden files.

### Known limitations

- Unsigned beta; no automatic updater.
- Only EPLAN 2.9.4.14642 is verified; connected features require local API licensing and .NET Framework 4.7.2+.
- No EMA/3D rendering, automatic project assignment or macro placement.
- Picture/EMA references verified; PDF, construction, mechanical-model and accessory resources remain unverified.
- Public offline tests exclude legally private fixture-based cases; EPLAN integration is local only.
- Real screenshots are pending; no fake screenshots or CI badges are used.
