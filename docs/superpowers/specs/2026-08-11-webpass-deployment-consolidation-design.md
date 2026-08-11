# WebPass Deployment Documentation Consolidation Design

**Date:** 2026-08-11

**Status:** Approved design

## Goal

Replace the competing production deployment instructions with one canonical
Chinese runbook at the repository root: `DEPLOYMENT.md`. The runbook must
describe one repeatable Windows Server/IIS path and contain only these eight
top-level subjects: environment requirements, database preparation,
configuration file, build, deploy, start, verify, and rollback.

## Current-State Audit

The tracked deployment guidance is split between `README.md` and three
English/Chinese document pairs under `docs/deployment/`. The content is not a
single executable procedure:

- `README.md` recommends producing deployable artifacts on a connected build
  machine, while the IIS runbook requires a connected preparation machine, an
  offline build machine, and a separate IIS server.
- The IIS runbook says the IIS server requires neither the .NET SDK nor
  `dotnet-ef`, but its administrator step later shows `dotnet publish` without
  establishing that it runs on the build machine or transferring its output.
- Initial deployment uses a fixed `C:\WebPass\staging` directory, while rollback
  assumes releases were deployed to versioned directories.
- Database preparation states privilege principles but provides no exact
  database, Windows login, database user, or runtime-role setup.
- Production configuration names the required settings but does not define one
  authoritative file, its exact location, or its complete contents.
- The Simplified Chinese IIS runbook links to the English certificate and
  acceptance documents, and six parallel files must be kept synchronized.
- `README.md` repeats a partial deployment procedure, so operators can follow a
  different path without opening the formal runbook.

The certificate recovery and acceptance checklists contain valid operational
requirements, but separate documents are unnecessary for the requested final
shape. Their deployment-critical requirements will be absorbed into the eight
allowed sections of `DEPLOYMENT.md`.

## Selected Deployment Architecture

The sole production path is the three-stage offline release flow already
implemented and covered by deployment integration tests:

1. On a connected Windows preparation machine, check out the reviewed commit
   and create `WebPassMigrationOfflineKit` with
   `Prepare-WebPassMigrationOfflineKit.ps1`.
2. On an offline Windows build machine, check out the same commit, restore only
   from that kit, and produce the website, `WebPass.AdminInit.exe`,
   `WebPass.Migrations.exe`, and a release copy of
   `Initialize-WebPass.ps1` for `win-x64` deployment.
3. Transfer only those release artifacts to the IIS server. Apply the migration
   bundle with a temporary deployment privilege, configure a versioned release
   directory, initialize or switch IIS, start the site, and verify it from the
   server and an approved LAN client.

The IIS server retains only the .NET 10 Hosting Bundle. It does not receive the
.NET SDK, `dotnet-ef`, source checkout, offline dependency kit, NuGet cache, or
local feed.

## Canonical Document Structure

`DEPLOYMENT.md` will be written in Simplified Chinese and will have exactly one
H1 title followed by these eight H2 headings in this order:

1. `环境要求`
2. `数据库准备`
3. `配置文件`
4. `构建`
5. `部署`
6. `启动`
7. `验证`
8. `回滚`

Subheadings may organize commands inside those sections, but no additional H2
subjects will be introduced. The file will explicitly say that it is the only
production deployment path.

## Environment and Security Boundary

The runbook will assign requirements to the machine that needs them:

- connected preparation machine: Windows, Git, .NET 10 SDK, reviewed clean
  checkout, and access to trusted NuGet sources;
- offline build machine: Windows, Git, matching .NET 10 SDK, reviewed checkout
  at the manifest commit, and the transferred offline kit;
- deployment server: supported 64-bit Windows Server, IIS installed before the
  .NET 10 Hosting Bundle, `AspNetCoreModuleV2`, local SQL Server 2025 Express,
  PowerShell 5.1 or later, fixed IPv4 address, and approved LAN CIDRs.

The deployment server will use two separate certificates in
`LocalMachine\My`: one HTTPS certificate whose SAN matches the client access
address, and one exportable RSA data-encryption certificate. Only
`IIS AppPool\WebPass` and approved administrators may read the data certificate
private key. An encrypted offline PFX recovery copy must be tested before the
deployment is accepted.

## Database and Configuration Contract

Database preparation will provide executable SQL for:

- creating the local `WebPass` database;
- creating a Windows login and user for the deployment operator;
- temporarily adding the deployment user to `db_owner` for the migration
  bundle and initial administrator creation;
- creating the `IIS APPPOOL\WebPass` login and user;
- granting the runtime identity only `db_datareader` and `db_datawriter`;
- removing the deployment user's `db_owner` membership after deployment.

The site will use
`<versioned-release>\site\appsettings.Production.json` as the canonical
production configuration file. It will contain the local integrated-security
connection string, positive ping limits, and the data-encryption certificate
thumbprint. The HTTPS certificate thumbprint remains an IIS binding input and
must not be placed in `SecretEncryption:CertificateThumbprint`.

## Release, Startup, and Rollback Contract

Every reviewed commit will be built into a distinct directory and transferred
to `C:\WebPass\releases\<commit>`. Its `site`, `admin`, migration bundle, and
`Initialize-WebPass.ps1` must come from the same commit. A full SQL Server
backup is required before migration.

For the first deployment, `Initialize-WebPass.ps1` creates the dedicated
application pool, HTTPS-only site, certificate ACL, and LAN-scoped firewall
rule. For later deployments, operators stop the site, switch its physical path
to the new versioned `site` directory, and start it explicitly. The initial
administrator tool runs on the IIS server from the transferred `admin`
directory and is removed after successful first use.

Application rollback switches IIS back to the previous retained release.
Database schema downgrade is never improvised. If a release migration is not
backward compatible, the site remains stopped until the reviewed pre-deployment
database backup and matching prior release are restored together. Exported
CSV/XLSX files are never treated as backups.

## Repository Changes

- Create `DEPLOYMENT.md` as the only live production runbook.
- Delete all six tracked files under `docs/deployment/`.
- Remove the production procedure and old deployment links from `README.md`;
  keep one link to `DEPLOYMENT.md` and update the project tree.
- Change the completion message in `scripts/Initialize-WebPass.ps1` from the
  deleted acceptance record to `DEPLOYMENT.md`.
- Preserve historical design specifications and implementation plans even when
  they mention the paths that existed when those plans were executed.
- Do not change runtime application behavior, database migrations, entities,
  authentication, authorization, or UI code.

## Verification

Verification will check that:

- `DEPLOYMENT.md` has exactly the eight required H2 headings in the required
  order and names only the selected three-stage path;
- every command, file path, configuration key, target framework, runtime
  identifier, script parameter, and health response matches the repository;
- the website and administrator outputs are built offline, and the matching
  initialization script is included before transfer;
- no tracked live document or script references a deleted deployment file;
- the PowerShell initialization script still parses;
- Markdown links resolve, `git diff --check` passes, and the existing solution
  tests pass without a restore when the local dependency cache permits it.

## Acceptance Criteria

- A new operator can follow only `DEPLOYMENT.md` from environment preparation
  through verified startup and rollback.
- The instructions never require an SDK, source tree, NuGet package cache, or
  `dotnet-ef` on the IIS server.
- The website, administrator utility, migration bundle, and IIS initialization
  script are demonstrably tied to the same reviewed commit.
- Runtime database access is limited to data reads and writes after migration.
- The old deployment document set and every live link to it are removed.
- `DEPLOYMENT.md` contains no top-level subject outside the eight requested
  sections.

