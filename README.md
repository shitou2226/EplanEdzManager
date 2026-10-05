# EPLAN EDZ Manager

[简体中文](README.zh-CN.md)

A local Windows x64 parts-library manager for EPLAN P8 2.9. Index and search EDZ libraries without modifying source files, organize a personal parts library, and use the installed EPLAN API for selective EDZ export and guarded MDB import.

**Unsigned beta: 1.0.0-beta.3.** This project is an independent tool and is not affiliated with or endorsed by EPLAN GmbH & Co. KG. EPLAN is a trademark of its respective owner.

## Features

- Read-only EDZ indexing with incremental SQLite indexing and FTS search.
- My Parts Library: favorites, collections, tags and preferred source tracking.
- Image/resource preview and export of existing resources.
- Official selective EDZ export through a locally installed EPLAN API.
- Safe MDB import with inspection, preview, explicit confirmation, backup and verification.
- EPLAN 2.9 Add-In for local context handoff to the Desktop application.
- Offline mode when a verified EPLAN environment is unavailable.

## Screenshots

Real application screenshots are not included yet. [Screenshot slots and privacy requirements](docs/images/README.md) are reserved under `docs/images/`. No mockups are presented as software screenshots.

## Architecture

```text
.NET 8 Desktop (WPF x64)
       | Application -> SQLite / read-only EDZ reader
       |
       +-- Named Pipe -- net472 x64 Bridge -- local EPLAN 2.9 API
       |
       +-- Named Pipe -- net472 x64 Add-In (inside EPLAN)
```

EPLAN references remain in the integration boundary; the Desktop, reader and SQLite layer do not reference EPLAN proprietary binaries. See [Developer Guide](docs/DEVELOPER_GUIDE.md) and the [Add-In architecture](docs/EPLAN_ADDIN_ARCHITECTURE.md).

## Requirements

- Windows x64.
- Desktop and command-line release tools are self-contained .NET 8 applications; end users do not need Visual Studio, a .NET SDK, Git or NuGet.
- Offline indexing, search, My Parts Library and resource preview/export do not require EPLAN.
- Connected features require a valid local EPLAN installation and the required API capability/licensing. The only verified version is **EPLAN P8 2.9.4.14642**.
- Bridge and Add-In require .NET Framework 4.7.2 or later. Missing or incompatible EPLAN environments disable connected operations safely.

## Installation

Download the Setup or Portable ZIP from [GitHub Releases](https://github.com/shitou2226/EplanEdzManager/releases); source checkout is not an installation package.

### Setup

Run `EplanEdzManager-1.0.0-beta.3-x64-Setup.exe`. The installer is per-user and does not require administrator rights. Application data is stored under `%LOCALAPPDATA%\EplanEdzManager`, not in the installation or EPLAN directory.

### Portable

Extract `EplanEdzManager-1.0.0-beta.3-x64.zip` and launch `EplanEdzManager\Desktop\EplanEdzManager.Desktop.exe`. Keep the complete directory layout. “Portable” refers to application distribution: persistent user data still lives in LocalAppData; temporary sessions use the user's Temp directory.

Verify downloads using `SHA256SUMS.txt`. This beta is unsigned: Windows may display trust warnings. Do not disable Defender, SmartScreen or other security protection. Follow your organization's software approval policy.

See [User Guide](docs/USER_GUIDE.md), [Troubleshooting](docs/TROUBLESHOOTING.md) and the [clean-machine checklist](docs/CLEAN_MACHINE_TEST_CHECKLIST.md).

## EPLAN Add-In

Registration is manual using EPLAN's Add-In mechanism. The installer installs the Add-In DLL and configuration, but does not write into the EPLAN installation directory or automatically register it. Follow [Add-In installation](docs/EPLAN_ADDIN_INSTALLATION.md). For portable installations, use the relative configuration example supplied beside the Add-In.

## Safety

- Source EDZ files are opened read-only. No custom EDZ writer is implemented.
- Selective EDZ export uses EPLAN's official API and validates results before publishing output.
- No direct SQL writes to EPLAN MDB databases; the local SQLite catalog is separate.
- MDB import requires Preview / Backup / explicit confirmation / Verify. Backups are retained for recovery.
- The tool does not perform automatic project assignment or macro placement.
- Uninstall retains personal application data and does not remove source libraries.
- No EPLAN DLLs, API documentation, ERX files, vendor EDZ libraries or real MDB databases are redistributed.

See [Safety](docs/SAFETY.md), [MDB import safety](docs/PARTS_DATABASE_IMPORT_SAFETY.md) and [Privacy and Data](docs/PRIVACY_AND_DATA.md).

## Known Limitations

- Beta software; use copies of important databases and verify results independently.
- Only EPLAN 2.9.4.14642 is verified. Other versions are not supported as verified environments.
- EMA/3D rendering is not implemented; existing resources may be exported without rendering.
- Export coverage is verified for picture and EMA macro references. PDF, construction, mechanical-model and accessory resource coverage is not yet established.
- Add-In context depends on available EPLAN API context; unsupported information is reported as Unavailable / Not Yet Supported, not inferred.
- No production code signing, automatic updater or public EPLAN-integration CI is provided.
- Real vendor regression fixtures are intentionally excluded; their local integration tests require independently obtained inputs.

## Build

Building source requires a Windows x64 development environment with a .NET 8-capable SDK and PowerShell 7. These are developer prerequisites, not end-user runtime requirements.

### Without EPLAN

```powershell
pwsh -File scripts/Test-Offline.ps1
```

This builds Desktop and tools, then runs the EPLAN-independent test projects with real-fixture integration cases excluded. Test data is synthetic and created locally; proprietary fixtures are not downloaded or included.

### Full local release

Provide `EPLAN29_API_DIR` through your local environment or ignored `Directory.Build.props.local`. Point it to API assemblies from your own licensed installation; do not copy them into this repository. Install the .NET Framework 4.7.2 targeting pack for development and Inno Setup for installer compilation.

```powershell
pwsh -File build-release.ps1
```

Connected tests additionally need local Platform/Variant Bin configuration and legally obtained private fixtures. See [Local integration tests](docs/LOCAL_INTEGRATION_TESTS.md). `-IncludeEplanIntegration` explicitly opts into real EPLAN operations; `-IncludeLocalFixtures` opts into fixture-dependent reader/application tests.

Before publishing source, run `pwsh -File scripts/Test-PublicReleaseGate.ps1 -RequireLicense` against the staged file list. No CI badge is claimed; public EPLAN integration tests cannot run without a licensed local EPLAN installation.

## License

Project-owned source is licensed under the [MIT License](LICENSE), selected by the owner. Third-party components retain their own licenses; the MIT grant does not cover proprietary EPLAN software or user data.

## Third Party

[Third-Party Notices](THIRD_PARTY_NOTICES.md) lists runtime dependencies and their licenses. EPLAN proprietary binaries are external prerequisites, not bundled dependencies. See [Contributing](CONTRIBUTING.md) and [Security](SECURITY.md) before sharing logs or diagnostics.
