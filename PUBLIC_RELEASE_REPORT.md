# Public Release Report

## Status

Local public-source preparation: **PASS**. GitHub repository creation, push, release upload and fresh public clone verification: **PENDING**. This document does not claim that publication has occurred.

## Repository and history

- Repository URL: pending creation under the owner-confirmed account `shitou2226`, name `EplanEdzManager`.
- Requested visibility: Public.
- Public commit SHA: pending the first independent commit; retrieve using `git rev-parse HEAD`.
- Planned tag: `v1.0.0-beta.3`; no tag has been pushed yet.
- Release URL: pending.
- Git identity: owner-confirmed GitHub username and public noreply email, configured only in the new repository.
- License: MIT, explicitly selected by the owner.
- Fresh `git init` on `main`; no old `.git`, objects, refs or reflogs copied. No invented development history.
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
- Repeat `git status`, `git ls-files`, `scripts/Test-PublicReleaseGate.ps1 -RequireLicense` and exact file-count review immediately before push.

## Verification

- New-directory offline Desktop/tools Release builds: 0 warnings / 0 errors.
- With process-local EPLAN and fixture settings removed: **127/127 tests PASS across 7 projects**.
- Offline path does not build EPLAN-dependent projects or require proprietary DLLs.
- Real fixture tests remain in source, opt-in and configured through `EPLAN_EDZ_TEST_FIXTURE_DIR`; no vendor fixture or resource is redistributed.
- Public-repository full beta.3 packaging and connected regression verification: pending.
- Setup / Portable / SHA256SUMS release assets: pending rebuild and upload.
- GitHub-clone verification and asset-download hash verification: pending publication.

## Known limitations

- Unsigned beta; do not disable Defender/SmartScreen/security protection.
- Only EPLAN 2.9.4.14642 verified; connected features require installed API capability/licensing and .NET Framework 4.7.2+.
- No EMA/3D rendering, automatic project assignment/macro placement or updater.
- Picture/EMA references verified; other resource categories remain unverified.
- Private vendor regression baselines are unavailable in the public checkout by design. Passing offline tests is not a claim of connected integration verification.
- No public CI workflow/badge added. Licensed EPLAN integration is local-only.
- Private vulnerability-reporting availability must be verified after repository creation; no sensitive contact address published.
- GitHub CLI authentication must be completed locally by the owner; credentials are not collected in chat or committed.

Stop after publication and verification. No video production or new business features are included in this phase.
