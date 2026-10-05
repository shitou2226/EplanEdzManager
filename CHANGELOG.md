# Changelog

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
