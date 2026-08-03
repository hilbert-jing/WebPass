# Exceptional second final fix report

## Scope

This wave addressed only the remaining reviewed-source bypass described in
`final-second-fix-brief.md`. Both migration scripts previously used
`git ls-files --others --exclude-standard`, which allowed an SDK-compiled
untracked source file to evade the guard when a repository-local or global Git
exclude rule hid it.

No UI, authentication, authorization, audit, entity, DbContext, snapshot, or
migration code changed. No generated bundle, offline kit, package, cache, or
tool is included.

## Implementation

- `Build-WebPassMigrationBundle.ps1` and
  `Prepare-WebPassMigrationOfflineKit.ps1` now enumerate all relevant
  untracked paths with `git ls-files --others`, without applying Git ignore
  configuration.
- The scripts explicitly filter repository-relative path segments to retain
  the repository workflow's generated project output allowance for
  `src/**/bin/**` and `src/**/obj/**`.
- Existing relevant-input pathspecs remain unchanged, so repository-root
  `.tools`, `.worktrees`, and `artifacts` locations remain outside the reviewed
  source input set exactly as before.
- Each script's exact intended output remains allowed by the existing
  normalized, case-insensitive full-path exclusion.

## Regression coverage

Two Deployment tests execute the real PowerShell scripts:

- `Bundle_script_rejects_an_ignored_sdk_compiled_source_file`
- `Preparation_script_rejects_an_ignored_sdk_compiled_source_file`

Each test creates a unique `src/WebPass.Web/Injected*.cs`, adds its exact
repository-relative path to the current worktree's `.git/info/exclude`, and
uses `git check-ignore` to prove that Git hides the file. The test then verifies
that the relevant script still reports the unreviewed source path and creates
no output. The original exclude file is backed up as bytes and restored in an
explicit `finally`; if the file did not previously exist, the test deletes it.
The tests do not read or write user-global or machine-global Git configuration.

The source-mutation tests use a non-parallel xUnit collection so they cannot
race other Deployment tests. The preparation test places a local `dotnet.cmd`
shim first on its child process `PATH`, ensuring a broken guard fails quickly
without starting tool installation or package restore.

## TDD evidence

### RED

Command:

```text
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~MigrationSourceGuardTests" --logger "console;verbosity=normal"
```

Result: exit 1; 2 tests run, 2 failed. The bundle test bypassed the source
guard and failed later while resolving the deliberately missing offline kit.
The preparation test bypassed the source guard and failed later on the shim SDK
version `0.0.0`. Neither result contained the required unreviewed-source error.
Wall time was 137.3 seconds; reported test time was 1.7363 minutes.

### GREEN

Command:

```text
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj --no-build --no-restore --filter "FullyQualifiedName~MigrationSourceGuardTests" --logger "console;verbosity=normal"
```

Result: exit 0; 2 tests run, 2 passed, 0 failed; reported test time 6.0333
seconds.

## Final verification

- PowerShell AST parsing: both changed scripts parsed with 0 errors.
- `git diff --check` for the three implementation/test files: exit 0.
- No-restore integration-test build: succeeded with 0 warnings and 0 errors.
- Focused Deployment verification, using `--no-build --no-restore` and filters
  for `MigrationSourceGuardTests` plus
  `MigrationOfflineKitPreparationScriptTests`: 3 tests run, 3 passed, 0
  failed; reported test time 13.1356 seconds.
- No whole-suite baseline and no restore/network operation were run in this
  wave, per the execution constraint.

## Self-review

- The behavior change is confined to the two reviewed-source guards.
- The relevant source pathspecs and tracked-change behavior are unchanged.
- Filtering is segment-based and repository-relative; it does not consult
  workstation ignore rules or use unsafe string-prefix directory matching.
- Existing exact-output full-path handling is unchanged.
- Tests exercise both real scripts, prove the source is ignored before
  invocation, restore exclude state in `finally`, and clean temporary source,
  shim, kit, and bundle paths.
