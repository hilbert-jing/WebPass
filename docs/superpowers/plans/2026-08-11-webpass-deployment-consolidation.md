# WebPass Deployment Documentation Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace every live production deployment guide with one executable Simplified Chinese `DEPLOYMENT.md` that documents the approved three-stage offline Windows/IIS release path.

**Architecture:** The connected preparation machine produces a commit-bound offline dependency kit; the matching offline checkout produces the website, administrator utility, migration bundle, and IIS initialization script; the IIS server receives only those versioned release artifacts. The repository root runbook owns the complete operator flow, while `README.md` contains only a link and historical specifications/plans remain unchanged.

**Tech Stack:** Markdown, PowerShell 5.1+, Git, .NET 10 SDK, EF Core/dotnet-ef 10.0.0, IIS WebAdministration, SQL Server 2025 Express, Windows `win-x64` framework-dependent publishing.

## Global Constraints

- `DEPLOYMENT.md` is the only live production deployment runbook and is written in Simplified Chinese.
- Its H2 headings are exactly `环境要求`, `数据库准备`, `配置文件`, `构建`, `部署`, `启动`, `验证`, and `回滚`, in that order.
- The sole production route is connected preparation machine to offline build machine to IIS/deployment server.
- Do not regenerate `WebPassMigrationOfflineKit`, publish output, or `WebPass.Migrations.exe` while implementing this documentation-only change.
- Do not change application runtime behavior, migrations, entities, authentication, authorization, or UI code.
- Preserve historical specifications and plans even when they mention superseded document paths.
- Use `apply_patch` for every repository edit and preserve UTF-8 text.
- Run each verification once after its owning change; rerun only a failed check after correcting its cause.

## File Map

- Create `DEPLOYMENT.md`: sole production runbook and operator command sequence.
- Modify `README.md`: remove alternate production instructions and link to the canonical runbook.
- Modify `scripts/Initialize-WebPass.ps1`: point its completion message to `DEPLOYMENT.md`.
- Delete `docs/deployment/acceptance-test-record.md` and `_ZH.md`: checklist absorbed into canonical verification.
- Delete `docs/deployment/certificates-and-key-recovery.md` and `_ZH.md`: certificate requirements absorbed into environment, configuration, verification, and rollback.
- Delete `docs/deployment/windows-server-iis.md` and `_ZH.md`: competing runbook replaced by the root document.
- Create the English and Chinese plan files in `docs/superpowers/plans/`; they describe this execution and remain historical records.

---

### Task 1: Create the canonical production runbook

**Files:**
- Create: `DEPLOYMENT.md`

**Interfaces:**
- Consumes: `scripts/Prepare-WebPassMigrationOfflineKit.ps1`, `scripts/Build-WebPassMigrationBundle.ps1`, `scripts/Initialize-WebPass.ps1`, both project files, `src/WebPass.Web/appsettings.json`, and `/health` behavior.
- Produces: the only live deployment entry point, with exact machine boundaries, artifacts, configuration keys, SQL roles, IIS commands, validation gates, and rollback boundary.

- [ ] **Step 1: Prove the canonical file does not yet satisfy the required structure**

Run:

```powershell
$path = 'DEPLOYMENT.md'
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw 'DEPLOYMENT.md is not present yet.'
}
```

Expected: the command fails because the file does not exist.

- [ ] **Step 2: Write the eight-section runbook**

Create `DEPLOYMENT.md` with one H1 title and the exact eight H2 headings from
Global Constraints. Include these concrete contracts:

- Environment: connected preparation, offline build, and IIS servers have
  separate prerequisites; IIS precedes the .NET 10 Hosting Bundle; IIS has
  `AspNetCoreModuleV2`; SQL Server is local-only; HTTPS and RSA data-encryption
  certificates are separate; the encrypted PFX recovery copy is tested.
- Database: create `WebPass`, a configurable Windows deployment login, and
  `IIS APPPOOL\WebPass`; grant the deployment user temporary `db_owner`; grant
  the runtime user only `db_datareader` and `db_datawriter`; remove `db_owner`
  from the deployment user after migration and administrator initialization.
- Configuration: store complete production JSON at
  `C:\WebPass\releases\$releaseId\site\appsettings.Production.json`; use the
  local integrated-security connection string and data-certificate thumbprint.
- Build: generate the kit on the connected machine; on the offline machine,
  verify `manifest.json` commit equality, restore only from the kit, publish
  `site` and `admin`, build `WebPass.Migrations.exe`, and copy
  `Initialize-WebPass.ps1` into the same release root.
- Deploy: transfer only the four release components, back up and verify the
  database, execute the matching migration bundle, create the initial
  administrator only on first deployment, and remove the administrator output.
- Start: preview then run the initialization script on first deployment;
  updates stop the site, change its physical path to the new versioned `site`,
  and explicitly start the application pool and site.
- Verify: validate artifact presence, IIS HTTPS-only binding, certificate ACL,
  firewall scope, local-only SQL, `/health` JSON, login and secret flow, restart
  recovery, and database-role removal.
- Rollback: stop IIS and switch to the retained previous directory; if the
  migration is incompatible, restore the verified pre-deployment SQL backup
  and matching application release together; never use CSV/XLSX as backup.

- [ ] **Step 3: Verify the heading contract and mandatory repository interfaces**

Run:

```powershell
$content = Get-Content -LiteralPath 'DEPLOYMENT.md' -Raw -Encoding UTF8
$actual = @($content -split "`r?`n" | Where-Object { $_ -match '^## ' })
$expected = @(
    '## 环境要求',
    '## 数据库准备',
    '## 配置文件',
    '## 构建',
    '## 部署',
    '## 启动',
    '## 验证',
    '## 回滚')
if (Compare-Object $expected $actual -SyncWindow 0) {
    throw 'DEPLOYMENT.md H2 structure is incorrect.'
}
foreach ($required in @(
    'Prepare-WebPassMigrationOfflineKit.ps1',
    'Build-WebPassMigrationBundle.ps1',
    'Initialize-WebPass.ps1',
    'WebPass.Migrations.exe',
    'WebPass.AdminInit.exe',
    'appsettings.Production.json',
    'IIS APPPOOL\WebPass',
    '/health')) {
    if (-not $content.Contains($required)) {
        throw "DEPLOYMENT.md is missing $required"
    }
}
```

Expected: zero exit and no output.

- [ ] **Step 4: Commit the canonical runbook**

```powershell
git add -- DEPLOYMENT.md
git commit -m "docs: add canonical WebPass deployment runbook"
```

---

### Task 2: Remove competing deployment guidance

**Files:**
- Modify: `README.md`
- Delete: `docs/deployment/acceptance-test-record.md`
- Delete: `docs/deployment/acceptance-test-record_ZH.md`
- Delete: `docs/deployment/certificates-and-key-recovery.md`
- Delete: `docs/deployment/certificates-and-key-recovery_ZH.md`
- Delete: `docs/deployment/windows-server-iis.md`
- Delete: `docs/deployment/windows-server-iis_ZH.md`

**Interfaces:**
- Consumes: `DEPLOYMENT.md` from Task 1.
- Produces: one README deployment entry and no competing live runbook.

- [ ] **Step 1: Replace README production procedures with one canonical link**

Keep the product overview, development setup, local configuration, local
database initialization, local running, build/test, security notes, project
structure, design links, and scope. Remove the offline deployment subsection,
the Windows Server publication summary, and the three old deployment links.
Add one short `## 生产部署` section linking to `DEPLOYMENT.md`, and show
`DEPLOYMENT.md` rather than `docs/deployment/` in the project tree.

- [ ] **Step 2: Delete the six duplicate deployment files**

Use one `apply_patch` deletion covering the exact six tracked files listed in
this task. Do not delete historical files under `docs/superpowers/`.

- [ ] **Step 3: Verify that live documentation has one deployment entry**

Run:

```powershell
$live = @('README.md', 'DEPLOYMENT.md', 'scripts/Initialize-WebPass.ps1')
$oldNames = @(
    'windows-server-iis',
    'certificates-and-key-recovery',
    'acceptance-test-record',
    'docs/deployment')
foreach ($file in $live) {
    $content = Get-Content -LiteralPath $file -Raw -Encoding UTF8
    foreach ($oldName in $oldNames) {
        if ($content.Contains($oldName)) {
            throw "$file still references $oldName"
        }
    }
}
$readme = Get-Content -LiteralPath 'README.md' -Raw -Encoding UTF8
if (($readme | Select-String -Pattern '\(DEPLOYMENT\.md\)' -AllMatches).Matches.Count -ne 1) {
    throw 'README.md must link to DEPLOYMENT.md exactly once.'
}
```

Expected: this check initially fails only because Task 3 has not updated the
script; the README and deleted-file assertions otherwise pass.

- [ ] **Step 4: Commit the documentation consolidation**

```powershell
git add -- README.md docs/deployment
git commit -m "docs: remove duplicate deployment guidance"
```

---

### Task 3: Update the initialization-script handoff

**Files:**
- Modify: `scripts/Initialize-WebPass.ps1`

**Interfaces:**
- Consumes: canonical `DEPLOYMENT.md` verification section.
- Produces: a completion message that cannot direct operators to a deleted file.

- [ ] **Step 1: Replace the stale completion message**

Change only:

```powershell
Write-Host 'Complete docs/deployment/acceptance-test-record.md from a trusted LAN client.'
```

to:

```powershell
Write-Host 'Complete the verification section in DEPLOYMENT.md from a trusted LAN client.'
```

- [ ] **Step 2: Parse the PowerShell script**

Run:

```powershell
$tokens = $null
$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile(
    (Resolve-Path -LiteralPath 'scripts/Initialize-WebPass.ps1').Path,
    [ref]$tokens,
    [ref]$errors)
if ($errors.Count -ne 0) {
    $errors | ForEach-Object { Write-Error $_.Message }
    throw 'Initialize-WebPass.ps1 has parse errors.'
}
```

Expected: zero exit and no parse errors.

- [ ] **Step 3: Re-run the live-reference check from Task 2**

Expected: zero exit and no output after the script message is updated.

- [ ] **Step 4: Commit the script-reference correction**

```powershell
git add -- scripts/Initialize-WebPass.ps1
git commit -m "chore: point deployment completion to canonical runbook"
```

---

### Task 4: Perform one proportional final verification pass

**Files:**
- Verify only; correct failures in their owning task before rerunning the failed check.

**Interfaces:**
- Consumes: all prior task outputs.
- Produces: structural, reference, syntax, whitespace, and regression evidence.

- [ ] **Step 1: Verify tracked deployment-document state**

Run:

```powershell
$deploymentDocs = @(git ls-files -- '*DEPLOYMENT*.md' 'docs/deployment/**')
if ($deploymentDocs -notcontains 'DEPLOYMENT.md') {
    throw 'DEPLOYMENT.md is not tracked.'
}
if ($deploymentDocs | Where-Object { $_ -like 'docs/deployment/*' }) {
    throw 'A duplicate deployment document remains tracked.'
}
```

Expected: only root `DEPLOYMENT.md` is a live deployment runbook; historical
specification and plan filenames are outside this path query.

- [ ] **Step 2: Run heading, interface, live-reference, and PowerShell parse checks**

Run the exact checks from Tasks 1 through 3 once as one verification pass.

Expected: all checks exit zero.

- [ ] **Step 3: Check repository whitespace and scope**

Run:

```powershell
git diff --check HEAD~3
git status --short
git log --oneline -5
```

Expected: no whitespace errors; only intentional commits are present; no
generated kit, package, publish directory, or migration bundle is added.

- [ ] **Step 4: Run the existing solution tests once without restore**

Run:

```powershell
dotnet test WebPass.sln -c Release --no-restore
```

Expected: zero failed tests. If the command cannot start because the existing
local package cache is incomplete, record that environmental limitation and do
not perform a network restore for this documentation-only change.

- [ ] **Step 5: Review the final diff**

Run:

```powershell
git show --stat --oneline HEAD~2..HEAD
git diff HEAD~3 -- DEPLOYMENT.md README.md scripts/Initialize-WebPass.ps1 docs/deployment
```

Confirm that the final runbook is executable, has no second deployment route,
and contains no H2 section beyond the required eight.

## Expected Commit Sequence

1. `docs: add canonical WebPass deployment runbook`
2. `docs: remove duplicate deployment guidance`
3. `chore: point deployment completion to canonical runbook`

The approved bilingual design commit and bilingual plan commit precede this
three-commit implementation sequence.

