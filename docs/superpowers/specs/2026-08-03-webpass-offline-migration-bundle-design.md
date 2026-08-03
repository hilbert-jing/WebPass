# WebPass Offline Migration Bundle Design

**Date:** 2026-08-03

**Status:** Approved design

## Goal

Provide a repeatable migration-bundle workflow for a WebPass build machine
that has the matching .NET 10 SDK but cannot access `nuget.org` and has no
internal NuGet source. An approved offline dependency kit is prepared once on
an internet-connected machine, transferred by removable media or an internal
file share, and never committed to Git.

The resulting `WebPass.Migrations.exe` remains a framework-dependent
`win-x64` EF Core migration bundle. It is built from the reviewed WebPass
source commit and applied to the local SQL Server instance by a deployment
identity.

## Scope

This change includes:

- a PowerShell script that prepares the offline dependency kit on a connected
  Windows machine;
- strict offline support in the existing migration-bundle build script;
- synchronized English and Simplified Chinese Windows/IIS deployment
  instructions;
- SQL Server integration coverage that generates and executes the bundle;
- ignore rules needed to keep offline packages and generated executables out
  of Git.

This change does not modify the WebPass user interface, authentication,
authorization, auditing, entities, `WebPassDbContext`, model snapshot, or
existing migrations. It does not add database backup, automatic deployment,
an internal package server, or application-side migration execution.

## Environment Assumptions

- The connected preparation machine and offline build machine run Windows.
- Both have a WebPass-compatible .NET 10 SDK.
- The preparation machine can access trusted NuGet sources.
- The offline build machine cannot access `nuget.org` and has no internal
  NuGet source.
- SQL Server Express is local to the deployment environment.
- The offline kit is transferred through an operator-controlled medium or
  internal file share and is not checked into Git.
- The deployment server already has the .NET 10 Hosting Bundle required by
  the framework-dependent WebPass deployment.

## Selected Architecture

### Connected preparation script

Add:

```text
scripts/Prepare-WebPassMigrationOfflineKit.ps1
```

Interface:

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath <offline-kit-directory> `
  [-Force]
```

The script builds the kit in a unique staging directory. It publishes the
target directory only after the kit has passed its own offline validation.
Without `-Force`, an existing target is an error. With `-Force`, replacement
occurs only after the new staged kit is complete, so a failed preparation does
not destroy a previously valid kit.

The preparation flow is:

1. Validate the source tree, .NET 10 SDK, Git commit, and output path.
2. Install exactly `dotnet-ef 10.0.0` into the staged `tools` directory.
3. Restore WebPass and `win-x64` dependencies into an isolated NuGet
   global-packages directory.
4. Generate a temporary migration bundle to force restoration of dependencies
   used by the EF bundle publish step.
5. Collect the required `.nupkg` files into a local feed.
6. Recreate an empty validation cache and generate the bundle again with only
   the local feed available.
7. Delete the validation bundle and write the final manifest.
8. Move the validated staging directory into the requested output location.

The preparation script may use configured trusted network sources. The final
kit must not retain a network package source.

### Offline kit layout

```text
WebPassMigrationOfflineKit/
|-- manifest.json
|-- NuGet.Config
|-- tools/
|   |-- dotnet-ef.exe
|   `-- .store/...
|-- packages/
|   `-- <expanded NuGet global-packages content>
`-- feed/
    `-- <required .nupkg files>
```

`NuGet.Config` clears inherited package sources and enables only the kit's
local feed. The manifest contains:

- offline-kit format version;
- source Git commit;
- `dotnet-ef` version `10.0.0`;
- SDK major version `10`;
- target runtime `win-x64`;
- creation timestamp in UTC.

The offline kit contains neither WebPass source code nor the final production
migration bundle.

### Offline bundle build

Extend:

```text
scripts/Build-WebPassMigrationBundle.ps1
```

Interface:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>] `
  [-OfflineKitPath <offline-kit-directory>]
```

When `-OfflineKitPath` is omitted, the existing developer workflow remains:
the script uses `.tools\dotnet-ef.exe` and does not install tools implicitly.

When `-OfflineKitPath` is supplied, the script:

1. validates the manifest and required kit directories;
2. requires manifest values for `dotnet-ef 10.0.0`, SDK 10, and `win-x64`;
3. requires the manifest source commit to equal the current source `HEAD`;
4. executes the kit's `dotnet-ef --version` and checks the exact version;
5. temporarily points `NUGET_PACKAGES` and restore sources at the kit only;
6. disables network-source fallback and NuGet audit network access for this
   strictly offline operation;
7. generates a framework-dependent `win-x64` bundle in a temporary output;
8. replaces the requested output only after generation succeeds;
9. restores all changed process environment variables in `finally`.

A missing package, invalid manifest, mismatched commit, wrong tool version, or
missing output is a terminating error. The script does not modify user-level
or machine-level NuGet configuration.

The default output remains:

```text
src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe
```

## Deployment Flow

The English and Simplified Chinese Windows/IIS runbooks describe the same
three-stage process.

### 1. Connected preparation machine

- Check out the reviewed WebPass source commit.
- Run the preparation script.
- Transfer the entire resulting offline kit to the offline build machine.

### 2. Offline build machine

- Check out the same reviewed source commit.
- Run the bundle script with `-OfflineKitPath`.
- Stop if manifest validation, restore, or bundle generation fails.
- Transfer only the website publish output and
  `WebPass.Migrations.exe` to the deployment server.

### 3. Deployment server

- Do not install the .NET SDK or `dotnet-ef`.
- Run `WebPass.Migrations.exe --connection <connection-string>` with a
  deployment identity that can alter the WebPass database.
- Stop deployment if migration execution fails.
- Remove schema-owner or server-administrator rights from the runtime IIS
  identity after migration.

The bundle is regenerated for every reviewed release. An offline kit may be
reused only when its source commit, SDK major version, runtime identifier, and
dependency set still match the build source.

## Testing

Extend the existing SQL Server integration coverage in:

```text
tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
```

The end-to-end test obtains a kit in one of two ways:

- if `WEBPASS_MIGRATION_OFFLINE_KIT` is set, use that transferred kit;
- otherwise call the preparation script to create a temporary kit using the
  development or CI machine's configured package sources.

It then:

1. builds `WebPass.Migrations.exe` in strict offline mode;
2. executes the bundle against a uniquely named local SQL Server database;
3. compares the migrations known to `WebPassDbContext` with the migrations
   recorded as applied in SQL Server;
4. deletes the database, generated bundle, and test-owned temporary kit in a
   `finally` block.

Additional focused tests verify rejection of:

- an incomplete manifest or missing kit directory;
- a manifest with the wrong `dotnet-ef` version;
- a kit with a missing dependency;
- a source-commit mismatch.

The failure tests must confirm that no output bundle is left behind and that
the build does not fall back to an HTTP package source. These tests are
functional tests only; no load or stress testing is added.

## Security and Maintenance Constraints

- Offline packages, local caches, tool binaries, temporary bundles, and final
  bundles are ignored by Git.
- No connection strings, passwords, package-source credentials, or other
  secrets are written to the manifest or build logs.
- The scripts accept only explicit filesystem paths and validate exact target
  directories before replacement or cleanup.
- Temporary directories are unique and only their exact resolved paths are
  deleted.
- Tool and package versions are fixed; the scripts never select a latest
  version implicitly.
- The running WebPass application never applies migrations automatically.

## Acceptance Criteria

- A connected machine can create a validated offline kit without committing
  generated artifacts.
- A clean offline source checkout with .NET 10 SDK and the transferred kit can
  generate `WebPass.Migrations.exe` without an HTTP package source.
- The bundle applies every committed migration to a unique SQL Server
  database in the integration test.
- Re-running the bundle build safely replaces the requested output.
- English and Simplified Chinese deployment instructions remain synchronized.
- No WebPass runtime feature, security behavior, entity, or migration changes.
