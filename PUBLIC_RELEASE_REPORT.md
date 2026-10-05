# Public Release Report

## Status

**PASS** — independent Public repository, clean source history, source push, beta Pre-release, exact asset upload and fresh GitHub-clone verification completed on 2026-10-05.

## Repository and history

- Repository URL: https://github.com/shitou2226/EplanEdzManager (new Public repository created after authenticated same-name absence check).
- Requested visibility: Public.
- Initial public source commit SHA: `e63086f869e5284da08813a9832d9ed5ad45d859`.
- Verified build/release source commit SHA: `0e4f8464cee8a275538312b1dc6d626e4f24d77d`.
- Tag: `v1.0.0-beta.3`, pushed and pinned to the verified build source above. The later evidence/known-limitations documentation commit does not move this tag or change product code.
- Release URL: https://github.com/shitou2226/EplanEdzManager/releases/tag/v1.0.0-beta.3 (Pre-release, not draft).
- `main` pushed without force, upstream `origin/main`; subsequent evidence documentation is a real follow-up commit, not invented past history.
- Git identity: owner-confirmed GitHub username and public noreply email, configured only in the new repository.
- License: MIT, explicitly selected by the owner.
- Fresh `git init` on `main`; no old `.git`, objects, refs or reflogs copied. No invented development history.
- One independent root commit; the private baseline commit object is absent from the new Git object database.
- Audited private source baseline: `ca5e71466ddd8447e441cc0736c2ea7e726a5db2` (`v1.0.0-beta.3`). The private repository, remotes, beta.2/beta.3 tags and working tree remain unchanged.

## Files selected for publication

The reviewed snapshot has **200 source/documentation files** including this report. Runtime binaries and installer assets are not tracked source files.

Included: product source, safe test source, command-line tools, solution/project/version files, installer/build scripts, selected current user/developer/safety documents, bilingual READMEs, MIT license, upstream third-party notices, security/contribution policies and issue templates.

Excluded: old history, private historical reports, ignored local props/configuration, EPLAN DLL/XML documentation/ERX/installers, real EDZ/MDB/project/backup data, samples, bin/obj, user profiles, logs, crash reports, generated output and artifacts.

## Upload gates

- Pre-copy scan: tracked inputs and ignored local configuration checked; no actual credential found. Test placeholders and assembly PublicKeyToken metadata were classified as non-secrets.
- Staged-index secret scan: recognizable GitHub/OpenAI token/private-key signatures absent. Generic security terms were reviewed as code/contracts, policy text, synthetic confirmation data or upstream legal notices, not usable credentials.
- Path/privacy scan: no actual development root, profile root, development username or unreviewed personal email in the public file list. Upstream license author credits are preserved as required legal attribution.
- Forbidden-file scan: zero EPLAN binaries/docs/ERX, EDZ, MDB, executable/DLL/PDB, private config or generated output in staged source.
- Ignore review: every intended source file retained, including SQLite and Diagnostics code; all generated bin/obj content excluded. No proprietary fixtures force-added.
- License scan: runtime NuGet graphs and package licenses checked. MIT, Apache-2.0, SQLite public-domain status, .NET notices and Inno Setup license documented. Owner-selected MIT does not cover proprietary EPLAN software or user data.
- Markdown relative-link scan: no missing local links. Real screenshots remain placeholders, not fabricated images.
- `git status`, `git ls-files`, `scripts/Test-PublicReleaseGate.ps1 -RequireLicense` and exact 200-file review repeated immediately before push. All source gates PASS.
- Expanded release scan: **900 files**, zero proprietary/data/dev files or actual credentials/private path signatures. A broad `sk-` scan matched a 746-character Microsoft runtime locale table, not a key; all three copies matched the restored official Microsoft runtime package SHA-256. The finding was classified, not silently ignored.
- Portable ZIP: every one of 900 entries hash-matched the scanned staging files. Installer smoke installation was also scanned by the packaging gate.

## Verification

- New-directory offline Desktop/tools Release builds: 0 warnings / 0 errors.
- With process-local EPLAN and fixture settings removed: **127/127 tests PASS across 7 projects**.
- Offline path does not build EPLAN-dependent projects or require proprietary DLLs.
- Real fixture tests remain in source, opt-in and configured through `EPLAN_EDZ_TEST_FIXTURE_DIR`; no vendor fixture or resource is redistributed.
- Public-repository clean/restore/Release build: **0 warnings / 0 errors**. Regular tests **149/149 PASS**, real EPLAN integration tests **14/14 PASS** (163 total).
- Fresh-checkout integration prerequisite defect found and fixed in packaging only: Phase 4 export tests now run explicitly before Phase 5 MDB tests. Previous generated output was preserved outside the fresh test-output root; verification did not rely on stale private-workspace fixtures.
- Public-source beta.3 Desktop/tools self-contained publish, installer compilation, restricted-PATH no-SDK/no-EPLAN startup, tool startup and install/config/startup/uninstall smoke tests: **PASS**.
- Fresh clone from GitHub: README/license/source layout valid, no local proprietary inputs, EPLAN/fixture environment removed, **127/127 offline tests PASS** across 7 projects, 0 warnings / 0 errors and clean working tree.
- The initial clone test was run concurrently with connected tests and hit an exclusively opened Bridge log during diagnostic export. Serial verification passed; the existing concurrency limitation is documented below instead of claiming it was repaired.
- Published Setup/ZIP re-downloaded into the fresh clone's ignored verification area. Both hashes match published `SHA256SUMS.txt` and local artifacts.
- Downloaded Portable ZIP extracted into a Unicode/space path; restricted-PATH/no-SDK/no-EPLAN startup returned Offline and PASS.
- GitHub page metadata verified: Public, default branch main, MIT recognized, rendered README HTML, requested Description/8 Topics, Pre-release state, exactly three uploaded assets and working release links. No fake CI badge or screenshot added.

## Release assets

| Asset | Bytes | SHA-256 |
|---|---:|---|
| EplanEdzManager-1.0.0-beta.3-x64-Setup.exe | 105265386 | `6BBA0FF69C5624E5EBBA4C69E84C11773D2E85F3F68B6C74C23C27DA98A24367` |
| EplanEdzManager-1.0.0-beta.3-x64.zip | 149969490 | `BE521DD3FCD21BE20997106E4F33CE5B86311093645097FD345B30944BB5C80E` |
| SHA256SUMS.txt | 214 | `1FC69D9F245E4FC0AA893C253E955618BD6356AC7AA5A29D1C57D2BBBDFD5AC9` |

GitHub asset state/digest matched these files. Only these three exact assets were uploaded; no output directories, sample libraries, integration MDBs, logs or backups were attached. The new packages include MIT and complete upstream license texts; their hashes intentionally differ from the private audit packages.

## Known limitations

- Unsigned beta; do not disable Defender/SmartScreen/security protection.
- Only EPLAN 2.9.4.14642 verified; connected features require installed API capability/licensing and .NET Framework 4.7.2+.
- No EMA/3D rendering, automatic project assignment/macro placement or updater.
- Picture/EMA references verified; other resource categories remain unverified.
- Diagnostic-bundle export may fail when an active Bridge holds a log file exclusively. Retry after the operation ends; do not run offline verification concurrently with active connected operations sharing the user log root. Product diagnostics code was not changed in this repository-preparation phase.
- Private vendor regression baselines are unavailable in the public checkout by design. Passing offline tests is not a claim of connected integration verification.
- No public CI workflow/badge added. Licensed EPLAN integration is local-only.
- GitHub private vulnerability reporting enabled and verified after repository creation; no sensitive contact address published.
- GitHub CLI authentication was completed locally by the owner and the authenticated account verified; credentials were not collected in chat or committed.

Publication and verification are complete. No video production or new business features were included. Stop and await the next phase.
