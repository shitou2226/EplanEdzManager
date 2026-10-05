# Contributing

This is a beta project. Reproducible issues are welcome; pull-request review and response times are not guaranteed.

Before proposing a change, read [Safety](docs/SAFETY.md) and [Security](SECURITY.md). Keep EPLAN references isolated in the existing integration boundary. Do not add a custom EDZ writer or direct SQL writes to EPLAN MDB databases.

Run `pwsh -File scripts/Test-Offline.ps1` on Windows. The public test path does not require proprietary EPLAN assemblies or vendor EDZ samples. Full connected testing is local only; see [Local integration tests](docs/LOCAL_INTEGRATION_TESTS.md).

Use small, independently created synthetic test data. Do not contribute vendor EDZ, MDB, EPLAN DLLs, API XML documentation, ERX, real projects, personal paths, logs or credentials. Keep machine-specific configuration ignored. Do not post private files in issues.

Before submitting source, stage the intended files and run `pwsh -File scripts/Test-PublicReleaseGate.ps1`. Do not silently bypass the gate with force-adds. Include test results and clearly distinguish offline verification from real EPLAN verification.

Project-owned code uses the [MIT License](LICENSE). Contributions must be your own work or supplied with compatible rights; this does not authorize redistribution of EPLAN or vendor data.
