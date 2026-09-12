# Robustness validation baseline

Date: 2026-09-12 (Asia/Shanghai)

SDK: .NET SDK 10.0.400, Windows x64. `global.json` requests 10.0.100 with `latestFeature`. All runs used existing assets with `--no-restore` and `/m:1`; no production app/service or real macro execution was started.

Results:

- Infrastructure targeted run: exit 1, 83 total / 81 passed / 2 failed / 0 skipped. The two failures are `ProfileManagerTests.GetProfileDirectory_WhenProfileDirectoryIsSymlink_RejectsIt` (test line 190) and `ProfileManagerTests.InitializeAsync_WhenConfigRootHasSymlinkedAncestor_UsesConfiguredTarget` (test line 171). Both fail in `Directory.CreateSymbolicLink` with missing Windows symlink privilege; no product assertion was reached.
- Application targeted run: exit 0, 11/11 passed, 0 skipped.
- UI targeted run: exit 0, 20/20 passed, 0 skipped.

Raw TRX files are in this directory: `infra-robustness.trx`, `application-robustness.trx`, and `ui-robustness.trx`. The runs rebuilt the current source project references. No testhost/vstest/dotnet process remained after completion. Peak RSS/PrivateBytes was not sampled.
