# Security Policy

EPLAN EDZ Manager is a local-first, unsigned beta. The latest beta is the review target; no security-support SLA is promised. The application does not require telemetry, a cloud service or an API key.

## Product safety boundaries

- Source EDZ packages are opened read-only and are never overwritten.
- The application does not directly edit EPLAN database tables with SQL or file patches.
- Selected EDZ output uses the official EPLAN 2.9 import/export path; there is no custom EDZ writer.
- Safe MDB import requires a user-selected closed MDB, Preview, explicit confirmation, fingerprint rechecks, verified backup, official import and post-import verification.
- Existing and conflicting parts default to Skip. Selective Update is disabled.
- EPLAN proprietary assemblies, licenses and resources are not redistributed.
- Diagnostics exclude full EDZ files, user parts databases, `index.db`, credentials, API keys and project-file contents.

See [Safety Boundaries](docs/SAFETY.md), [EPLAN Bridge Security](docs/EPLAN_BRIDGE_SECURITY.md), [MDB Import Safety](docs/PARTS_DATABASE_IMPORT_SAFETY.md) and [Privacy and Data](docs/PRIVACY_AND_DATA.md).

## Reporting a vulnerability

Do not disclose exploitation details, credentials, private paths, EDZ/MDB databases, project files or unredacted diagnostic bundles in public issues.

GitHub private vulnerability reporting is enabled. Use [Report a vulnerability](https://github.com/shitou2226/EplanEdzManager/security/advisories/new) in the Security tab. If that action is unavailable, open a minimal public issue requesting a private reporting channel without sensitive details. No personal email address is published by this project.

## Safe sharing

- Use synthetic reproduction data and redact usernames, paths, customer/project names and identifiers.
- Inspect diagnostic bundles before sharing; do not attach source EDZ/MDB files or backups.
- Never commit tokens, API keys, passwords, private keys or local configuration.
- Do not disable Defender/SmartScreen or create security exclusions to run this beta.
- Follow your organization's approval policy and keep independent backups before connected import operations.

## Supported security scope

Only EPLAN P8 `2.9.4.14642` is verified. Other 2.9 builds remain unverified and EPLAN write capabilities fail closed; EPLAN 2022+ is unsupported by this build.
