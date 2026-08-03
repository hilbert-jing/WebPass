# WebPass Offline Migration Bundle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build and verify `WebPass.Migrations.exe` on a Windows build machine that has the matching .NET 10 SDK but has no access to `nuget.org` or an internal NuGet source.

**Architecture:** A connected preparation script creates a validated, source-commit-bound offline dependency kit containing `dotnet-ef 10.0.0`, a local NuGet feed, and an isolated global-packages cache. The existing bundle script gains a strict `-OfflineKitPath` mode that uses only that kit, while retaining its current repository-local developer mode. Integration tests prepare or consume a kit, build the bundle without HTTP package sources, apply it to a unique local SQL Server database, and compare all applied migrations.

**Tech Stack:** PowerShell 5.1+, .NET 10 SDK, EF Core/dotnet-ef 10.0.0, NuGet local feeds, Git, xUnit, SQL Server Express, Windows `win-x64` framework-dependent publishing.

## Global Constraints

- Work from the reviewed WebPass source commit on Windows.
- The offline build machine has a matching .NET 10 SDK but cannot access `nuget.org` and has no internal NuGet source.
- The connected preparation machine may access configured trusted NuGet sources.
- The offline kit is transferred by operator-controlled media or an internal file share and is never committed to Git.
- Pin `dotnet-ef` exactly to `10.0.0`; never select a latest version implicitly.
- Generate a framework-dependent `win-x64` bundle named `WebPass.Migrations.exe`.
- Preserve the existing default output `src/WebPass.Web/bin/Release/migrations/win-x64/WebPass.Migrations.exe`.
- Preserve the existing developer workflow when `-OfflineKitPath` is omitted; do not auto-install tools in that mode.
- Strict offline mode must not fall back to HTTP package sources or modify user-level or machine-level NuGet configuration.
- Do not commit offline packages, NuGet caches, tool binaries, temporary bundles, or `WebPass.Migrations.exe`.
- Do not log connection strings, passwords, package-source credentials, or other secrets.
- Do not modify the WebPass UI, authentication, authorization, auditing, entities, `WebPassDbContext`, model snapshot, or existing migrations.
- Use exact unique temporary directories and delete only their resolved paths.
- Run no load or stress tests.
- Stop after each task checkpoint, report the next task's expected files and token cost, and obtain approval before continuing.

## File Map

- Create `scripts/Prepare-WebPassMigrationOfflineKit.ps1`: connected-machine kit preparation, validation, manifest creation, and safe publication.
- Modify `scripts/Build-WebPassMigrationBundle.ps1`: optional strict offline-kit validation and bundle generation.
- Create `tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs`: prepare one test-owned kit or use `WEBPASS_MIGRATION_OFFLINE_KIT`, run child processes with timeouts, and clean only owned paths.
- Modify `tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`: preparation validation, strict-offline rejection cases, and actual SQL Server bundle application.
- Modify `docs/deployment/windows-server-iis.md`: connected preparation, offline build, and deployment-server workflow.
- Modify `docs/deployment/windows-server-iis_ZH.md`: Simplified Chinese version of the same workflow, kept UTF-8.
- Verify `.gitignore`: existing `/artifacts/`, `.tools/`, `**/bin/`, and `**/obj/` rules cover the default kit/tool/cache/bundle locations; modify only if a test proves a generated path is visible to Git.

---

### Task 1: Prepare and validate the offline dependency kit

**Checkpoint estimate:** 2 new files; 14k-20k tokens.

**Files:**
- Create: `scripts/Prepare-WebPassMigrationOfflineKit.ps1`
- Create: `tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs`
- Modify: `tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`

**Interfaces:**
- Consumes: .NET 10 SDK, Git, configured trusted NuGet sources, `src/WebPass.Web/WebPass.Web.csproj`, and `WebPass.sln`.
- Produces:

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath <offline-kit-directory> `
  [-Force]
```

- Produces manifest JSON with exact properties:

```json
{
  "formatVersion": 1,
  "sourceCommit": "<40-character lowercase Git commit>",
  "dotnetEfVersion": "10.0.0",
  "sdkMajorVersion": 10,
  "targetRuntime": "win-x64",
  "createdAtUtc": "<ISO-8601 UTC timestamp>"
}
```

- Produces test fixture contract:

```csharp
[CollectionDefinition(Name)]
public sealed class MigrationOfflineKitCollection
    : ICollectionFixture<MigrationOfflineKitFixture>
{
    public const string Name = "Migration offline kit";
}

public sealed class MigrationOfflineKitFixture : IAsyncLifetime
{
    public string RepositoryRoot { get; }
    public string KitPath { get; private set; } = string.Empty;
    public bool OwnsKit { get; private set; }
    public Task InitializeAsync();
    public Task DisposeAsync();
    public static Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null);
}
```

- [ ] **Step 1: Add the failing preparation test and shared fixture**

Create `MigrationOfflineKitFixture.cs`. Resolve the repository root from
`AppContext.BaseDirectory` using five `..` segments, exactly as the existing
bundle test does. If `WEBPASS_MIGRATION_OFFLINE_KIT` is set, resolve it and set
`OwnsKit = false`. Otherwise create:

```csharp
var root = Path.Combine(
    Path.GetTempPath(),
    "WebPassMigrationOfflineKitTests",
    Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
KitPath = Path.Combine(root, "kit");
OwnsKit = true;
```

The fixture must invoke PowerShell with a 15-minute timeout:

```csharp
var result = await RunAsync(
    "powershell.exe",
    [
        "-NoProfile",
        "-File",
        Path.Combine(
            RepositoryRoot,
            "scripts",
            "Prepare-WebPassMigrationOfflineKit.ps1"),
        "-OutputPath",
        KitPath,
    ],
    timeout: TimeSpan.FromMinutes(15));

Assert.True(
    result.ExitCode == 0,
    $"Offline-kit preparation failed.{Environment.NewLine}" +
    $"{result.Error}{Environment.NewLine}{result.Output}");
```

`RunAsync` must redirect standard output/error, use
`WaitForExitAsync(cancellationToken)`, call `Kill(entireProcessTree: true)` on
timeout, await both redirected streams, and return:

```csharp
public sealed record ProcessResult(
    int ExitCode,
    string Output,
    string Error);
```

`DisposeAsync` deletes only the fixture's unique parent directory when
`OwnsKit` is true. It never deletes a caller-provided kit.

Annotate `MigrationBundleTests` with:

```csharp
[Collection(MigrationOfflineKitCollection.Name)]
public sealed class MigrationBundleTests(
    MigrationOfflineKitFixture offlineKit)
```

Add:

```csharp
[Fact]
public async Task Preparation_script_creates_a_valid_offline_kit()
{
    Assert.True(File.Exists(Path.Combine(
        offlineKit.KitPath,
        "manifest.json")));
    Assert.True(File.Exists(Path.Combine(
        offlineKit.KitPath,
        "NuGet.Config")));
    Assert.True(File.Exists(Path.Combine(
        offlineKit.KitPath,
        "tools",
        "dotnet-ef.exe")));
    Assert.NotEmpty(Directory.EnumerateFiles(
        Path.Combine(offlineKit.KitPath, "feed"),
        "*.nupkg"));
    Assert.NotEmpty(Directory.EnumerateDirectories(
        Path.Combine(offlineKit.KitPath, "packages")));

    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
        Path.Combine(offlineKit.KitPath, "manifest.json")));
    var root = manifest.RootElement;
    Assert.Equal(1, root.GetProperty("formatVersion").GetInt32());
    Assert.Equal(
        "10.0.0",
        root.GetProperty("dotnetEfVersion").GetString());
    Assert.Equal(10, root.GetProperty("sdkMajorVersion").GetInt32());
    Assert.Equal(
        "win-x64",
        root.GetProperty("targetRuntime").GetString());

    var config = await File.ReadAllTextAsync(Path.Combine(
        offlineKit.KitPath,
        "NuGet.Config"));
    Assert.Contains("<clear />", config, StringComparison.Ordinal);
    Assert.DoesNotContain(
        "http://",
        config,
        StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain(
        "https://",
        config,
        StringComparison.OrdinalIgnoreCase);
}
```

Add `using System.Text.Json;` and remove the old private `ProcessResult` and
`RunAsync` from `MigrationBundleTests`; later tests consume the fixture helper.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release `
  --filter FullyQualifiedName~Preparation_script_creates_a_valid_offline_kit
```

Expected: FAIL because
`scripts/Prepare-WebPassMigrationOfflineKit.ps1` does not exist.

- [ ] **Step 3: Implement safe path and command helpers in the preparation script**

Start the script with:

```powershell
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputPath,

    [switch]$Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DotnetEfVersion = '10.0.0'
$TargetRuntime = 'win-x64'
$KitFormatVersion = 1
$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..')).Path
$webProject = Join-Path $repositoryRoot (
    'src\WebPass.Web\WebPass.Web.csproj')
$solution = Join-Path $repositoryRoot 'WebPass.sln'
```

Add an external-command helper that preserves native exit codes:

```powershell
function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}
```

Resolve the absolute target, reject the repository root and its parents, and
place the staging directory beside the target:

```powershell
$resolvedOutput = [System.IO.Path]::GetFullPath(
    $(if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath
    }
    else {
        Join-Path $repositoryRoot $OutputPath
    }))
$repositoryPrefix = $repositoryRoot.TrimEnd('\') + '\'
if ($repositoryRoot.StartsWith(
        $resolvedOutput.TrimEnd('\') + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The offline-kit output cannot be the repository or its parent.'
}
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw "Offline-kit output already exists: $resolvedOutput"
}

$parent = Split-Path -Parent $resolvedOutput
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent (
    '.webpass-offline-kit-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null
```

The script's `finally` block removes only `$staging` when it still exists.

- [ ] **Step 4: Implement connected restore and the first bundle generation**

Validate the SDK and commit:

```powershell
$sdkVersion = (& dotnet --version).Trim()
$parsedSdk = $null
if ($LASTEXITCODE -ne 0 -or
    -not [Version]::TryParse($sdkVersion, [ref]$parsedSdk) -or
    $parsedSdk.Major -ne 10) {
    throw "A .NET 10 SDK is required. Found: $sdkVersion"
}
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$') {
    throw 'The reviewed source Git commit could not be determined.'
}
```

Create `tools`, `seed-packages`, and `feed`, install the pinned tool, and warm
the project/runtime dependency graph:

```powershell
$tools = Join-Path $staging 'tools'
$seedPackages = Join-Path $staging 'seed-packages'
$feed = Join-Path $staging 'feed'
New-Item -ItemType Directory -Path $tools,$seedPackages,$feed |
    Out-Null

Invoke-CheckedCommand dotnet @(
    'tool', 'install', 'dotnet-ef',
    '--tool-path', $tools,
    '--version', $DotnetEfVersion)

$oldPackages = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = $seedPackages
    Invoke-CheckedCommand dotnet @(
        'restore', $solution,
        '--runtime', $TargetRuntime)
    Invoke-CheckedCommand (Join-Path $tools 'dotnet-ef.exe') @(
        'migrations', 'bundle',
        '--project', $webProject,
        '--startup-project', $webProject,
        '--configuration', 'Release',
        '--target-runtime', $TargetRuntime,
        '--output', (Join-Path $staging 'seed-bundle.exe'),
        '--force')
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
}
```

Copy every unique `*.nupkg` from `seed-packages` into `feed`; if two files
have the same name but different SHA-256 hashes, terminate instead of
overwriting:

```powershell
Get-ChildItem -LiteralPath $seedPackages -Recurse -Filter '*.nupkg' |
    ForEach-Object {
        $destination = Join-Path $feed $_.Name
        if (Test-Path -LiteralPath $destination) {
            $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            $targetHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
            if ($sourceHash -ne $targetHash) {
                throw "Conflicting package file: $($_.Name)"
            }
        }
        else {
            Copy-Item -LiteralPath $_.FullName -Destination $destination
        }
    }
```

- [ ] **Step 5: Validate from only the local feed and publish the kit**

Write `NuGet.Config` in UTF-8 without a network source:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="WebPassOffline" value="feed" />
  </packageSources>
</configuration>
```

Create a fresh `packages` directory, set process-scoped restore variables, run
an explicit restore with the kit config, and generate a validation bundle:

```powershell
$packages = Join-Path $staging 'packages'
$config = Join-Path $staging 'NuGet.Config'
New-Item -ItemType Directory -Path $packages | Out-Null
$oldRestoreSources = $env:RestoreSources
$oldAudit = $env:NuGetAudit
try {
    $env:NUGET_PACKAGES = $packages
    $env:RestoreSources = $feed
    $env:NuGetAudit = 'false'
    Invoke-CheckedCommand dotnet @(
        'restore', $webProject,
        '--runtime', $TargetRuntime,
        '--configfile', $config,
        '--packages', $packages,
        '--no-http-cache')
    Invoke-CheckedCommand (Join-Path $tools 'dotnet-ef.exe') @(
        'migrations', 'bundle',
        '--project', $webProject,
        '--startup-project', $webProject,
        '--configuration', 'Release',
        '--target-runtime', $TargetRuntime,
        '--output', (Join-Path $staging 'validation-bundle.exe'),
        '--force')
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:RestoreSources = $oldRestoreSources
    $env:NuGetAudit = $oldAudit
}
```

Require the validation bundle, delete `seed-packages`, both temporary bundles,
and write the manifest:

```powershell
$manifest = [ordered]@{
    formatVersion = $KitFormatVersion
    sourceCommit = $sourceCommit
    dotnetEfVersion = $DotnetEfVersion
    sdkMajorVersion = 10
    targetRuntime = $TargetRuntime
    createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
$manifest | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $staging 'manifest.json') `
        -Encoding utf8
Remove-Item -LiteralPath `
    (Join-Path $staging 'seed-bundle.exe'), `
    (Join-Path $staging 'validation-bundle.exe'), `
    $seedPackages `
    -Recurse -Force
```

Publish without exposing a partial target:

```powershell
$backup = $null
try {
    if (Test-Path -LiteralPath $resolvedOutput) {
        $backup = Join-Path $parent (
            '.webpass-offline-kit-backup-' +
            [Guid]::NewGuid().ToString('N'))
        Move-Item -LiteralPath $resolvedOutput -Destination $backup
    }
    Move-Item -LiteralPath $staging -Destination $resolvedOutput
    $staging = $null
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        Remove-Item -LiteralPath $backup -Recurse -Force
        $backup = $null
    }
}
catch {
    if ($backup -and (Test-Path -LiteralPath $backup)) {
        if (Test-Path -LiteralPath $resolvedOutput) {
            Remove-Item -LiteralPath $resolvedOutput -Recurse -Force
        }
        Move-Item -LiteralPath $backup -Destination $resolvedOutput
        $backup = $null
    }
    throw
}
```

The outer `finally` removes `$staging` only when the variable is nonempty and
that exact resolved staging path still exists. It never deletes an unrestored
backup.

- [ ] **Step 6: Run the preparation test and verify GREEN**

Run the Step 2 command.

Expected: one test passes, no tests are skipped, and the temporary test-owned
kit is removed after the test process exits.

- [ ] **Step 7: Verify generated artifacts remain ignored and commit Task 1**

Run:

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath artifacts\WebPassMigrationOfflineKit `
  -Force
git check-ignore -v -- `
  artifacts\WebPassMigrationOfflineKit\manifest.json `
  artifacts\WebPassMigrationOfflineKit\tools\dotnet-ef.exe `
  artifacts\WebPassMigrationOfflineKit\feed
git diff --check
git status --short
```

Expected: `.gitignore` reports `/artifacts/`; only the three Task 1 source/test
files appear in status.

Stage and commit:

```powershell
git add -- `
  scripts/Prepare-WebPassMigrationOfflineKit.ps1 `
  tests/WebPass.IntegrationTests/Deployment/MigrationOfflineKitFixture.cs `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git commit -m "feat: prepare offline migration dependency kit"
```

Stop and obtain approval for Task 2.

---

### Task 2: Build and apply the migration bundle in strict offline mode

**Checkpoint estimate:** 2 modified files; 14k-20k tokens.

**Files:**
- Modify: `scripts/Build-WebPassMigrationBundle.ps1`
- Modify: `tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs`

**Interfaces:**
- Consumes the Task 1 kit and manifest.
- Preserves:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>]
```

- Adds:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  [-OutputPath <path-to-WebPass.Migrations.exe>] `
  [-OfflineKitPath <offline-kit-directory>]
```

- [ ] **Step 1: Replace the existing end-to-end test with strict offline mode and add rejection tests**

Change the existing bundle build invocation to:

```csharp
var build = await MigrationOfflineKitFixture.RunAsync(
    "powershell.exe",
    [
        "-NoProfile",
        "-File",
        Path.Combine(
            offlineKit.RepositoryRoot,
            "scripts",
            "Build-WebPassMigrationBundle.ps1"),
        "-OfflineKitPath",
        offlineKit.KitPath,
        "-OutputPath",
        bundle,
    ],
    timeout: TimeSpan.FromMinutes(10));
```

Keep the unique SQL Server database, actual bundle execution, migration-list
comparison, and exact cleanup from the existing test. Assert the build output
does not contain `http://` or `https://`.

Add a helper that creates only test-owned invalid kit directories:

```csharp
private static async Task<string> NewInvalidKitAsync(
    string repositoryRoot,
    Action<Dictionary<string, object?>> mutate)
{
    var path = Path.Combine(
        Path.GetTempPath(),
        "WebPassInvalidMigrationKits",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(path, "tools"));
    Directory.CreateDirectory(Path.Combine(path, "packages"));
    Directory.CreateDirectory(Path.Combine(path, "feed"));
    await File.WriteAllTextAsync(
        Path.Combine(path, "NuGet.Config"),
        "<configuration><packageSources><clear />" +
        "<add key=\"WebPassOffline\" value=\"feed\" />" +
        "</packageSources></configuration>");
    var commit = (await MigrationOfflineKitFixture.RunAsync(
        "git",
        ["-C", repositoryRoot, "rev-parse", "HEAD"])).Output.Trim();
    var manifest = new Dictionary<string, object?>
    {
        ["formatVersion"] = 1,
        ["sourceCommit"] = commit,
        ["dotnetEfVersion"] = "10.0.0",
        ["sdkMajorVersion"] = 10,
        ["targetRuntime"] = "win-x64",
        ["createdAtUtc"] = DateTimeOffset.UtcNow,
    };
    mutate(manifest);
    await File.WriteAllTextAsync(
        Path.Combine(path, "manifest.json"),
        JsonSerializer.Serialize(manifest));
    return path;
}
```

Add tests named:

```text
Offline_build_rejects_an_incomplete_kit_without_output
Offline_build_rejects_the_wrong_tool_version_without_output
Offline_build_rejects_a_source_commit_mismatch_without_output
Offline_build_fails_on_missing_packages_without_http_fallback
```

The first three use a minimal invalid kit and assert a nonzero process exit,
the expected stable error fragment, and `File.Exists(output) == false`.

For the missing-package test, copy only the real kit's `tools` directory into
a unique invalid kit, leave `packages` and `feed` empty, write a valid manifest
and local-only config, then assert failure, no output, and no `http://` or
`https://` in either output stream. Delete each exact invalid-kit root in
`finally`.

Use this test-only directory-copy helper so junctions or unrelated parent
paths are never followed:

```csharp
private static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var file in Directory.EnumerateFiles(source))
    {
        File.Copy(
            file,
            Path.Combine(destination, Path.GetFileName(file)));
    }
    foreach (var directory in Directory.EnumerateDirectories(source))
    {
        var attributes = File.GetAttributes(directory);
        Assert.False(attributes.HasFlag(FileAttributes.ReparsePoint));
        CopyDirectory(
            directory,
            Path.Combine(destination, Path.GetFileName(directory)));
    }
}
```

- [ ] **Step 2: Run strict-offline tests and verify RED**

Run:

```powershell
dotnet test tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
  -c Release `
  --filter "FullyQualifiedName~MigrationBundleTests"
```

Expected: FAIL because the current build script does not accept
`-OfflineKitPath` and does not validate manifests.

- [ ] **Step 3: Add offline-kit parsing and validation to the build script**

Extend the parameter block:

```powershell
param(
    [string]$OutputPath,
    [string]$OfflineKitPath
)
```

Add constants. Resolve the source commit only inside the offline-kit branch so
the existing developer mode retains its current behavior:

```powershell
$DotnetEfVersion = '10.0.0'
$TargetRuntime = 'win-x64'
```

When `-OfflineKitPath` is supplied, resolve and validate:

```powershell
$kit = (Resolve-Path -LiteralPath $OfflineKitPath).Path
$manifestPath = Join-Path $kit 'manifest.json'
$configPath = Join-Path $kit 'NuGet.Config'
$efTool = Join-Path $kit 'tools\dotnet-ef.exe'
$packages = Join-Path $kit 'packages'
$feed = Join-Path $kit 'feed'

foreach ($file in @($manifestPath, $configPath, $efTool)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Offline migration kit file is missing: $file"
    }
}
foreach ($directory in @($packages, $feed)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Offline migration kit directory is missing: $directory"
    }
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw |
    ConvertFrom-Json
$sourceCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[a-f0-9]{40}$') {
    throw 'The current source Git commit could not be determined.'
}
if ($manifest.formatVersion -ne 1) {
    throw 'Offline migration kit format version must be 1.'
}
if ($manifest.dotnetEfVersion -ne $DotnetEfVersion) {
    throw 'Offline migration kit dotnet-ef version must be 10.0.0.'
}
if ($manifest.sdkMajorVersion -ne 10 -or
    $manifest.targetRuntime -ne $TargetRuntime) {
    throw 'Offline migration kit SDK or target runtime does not match WebPass.'
}
if ($manifest.sourceCommit -ne $sourceCommit) {
    throw 'Offline migration kit source commit does not match the current source.'
}
```

Capture and validate the tool version:

```powershell
$toolVersionOutput = (& $efTool --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or
    $toolVersionOutput -notmatch '(^|\s)10\.0\.0($|\s)') {
    throw 'Offline migration kit dotnet-ef executable is not version 10.0.0.'
}
```

When no kit is supplied, retain the existing `.tools\dotnet-ef.exe`
validation unchanged.

- [ ] **Step 4: Implement process-scoped offline restore and atomic output**

Create the output directory and a unique temporary output in that exact
directory:

```powershell
$temporaryOutput = Join-Path $outputDirectory (
    '.' + [System.IO.Path]::GetFileName($resolvedOutputPath) +
    '.' + [Guid]::NewGuid().ToString('N') + '.tmp.exe')
```

In offline mode, save and restore these variables in `finally`:

```powershell
$oldPackages = $env:NUGET_PACKAGES
$oldRestoreSources = $env:RestoreSources
$oldAudit = $env:NuGetAudit
try {
    $env:NUGET_PACKAGES = $packages
    $env:RestoreSources = $feed
    $env:NuGetAudit = 'false'
    & dotnet restore $webProject `
        --runtime $TargetRuntime `
        --configfile $configPath `
        --packages $packages `
        --no-http-cache
    if ($LASTEXITCODE -ne 0) {
        throw "Offline restore failed with exit code $LASTEXITCODE."
    }

    & $efTool migrations bundle `
        --project $webProject `
        --startup-project $webProject `
        --configuration Release `
        --target-runtime $TargetRuntime `
        --output $temporaryOutput `
        --force
    if ($LASTEXITCODE -ne 0) {
        throw "Migration bundle build failed with exit code $LASTEXITCODE."
    }
}
finally {
    $env:NUGET_PACKAGES = $oldPackages
    $env:RestoreSources = $oldRestoreSources
    $env:NuGetAudit = $oldAudit
    if (Test-Path -LiteralPath $temporaryOutput -PathType Leaf) {
        Remove-Item -LiteralPath $temporaryOutput -Force
    }
}
```

Move the generated temporary file only after checking it exists:

```powershell
if (-not (Test-Path -LiteralPath $temporaryOutput -PathType Leaf)) {
    throw "Migration bundle was not created: $temporaryOutput"
}
Move-Item -LiteralPath $temporaryOutput `
    -Destination $resolvedOutputPath `
    -Force
$temporaryOutput = $null
Write-Output $resolvedOutputPath
```

In developer mode, use the same temporary-output strategy but do not change
NuGet environment variables. The `finally` cleanup first checks that
`$temporaryOutput` is nonempty, then removes only that exact file when it
still exists.

- [ ] **Step 5: Run Task 2 tests and verify GREEN**

Run the Step 2 command.

Expected: preparation, rejection, strict-offline build, actual SQL migration,
and cleanup tests all pass with zero failures and skips.

- [ ] **Step 6: Re-run the default developer workflow twice**

Run:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1
.\scripts\Build-WebPassMigrationBundle.ps1
$bundle = 'src\WebPass.Web\bin\Release\migrations\win-x64\WebPass.Migrations.exe'
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
    throw "Missing bundle: $bundle"
}
git check-ignore -v -- $bundle
```

Expected: both builds succeed, the file exists, and `**/bin/` ignores it.

- [ ] **Step 7: Review and commit Task 2**

Run:

```powershell
git diff --check
git diff -- `
  scripts/Build-WebPassMigrationBundle.ps1 `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git status --short
```

Stage only the two Task 2 files and commit:

```powershell
git add -- `
  scripts/Build-WebPassMigrationBundle.ps1 `
  tests/WebPass.IntegrationTests/Deployment/MigrationBundleTests.cs
git commit -m "feat: build migration bundle from offline kit"
```

Stop and obtain approval for Task 3.

---

### Task 3: Document connected preparation, offline build, and IIS deployment

**Checkpoint estimate:** 2 modified files; 5k-8k tokens.

**Files:**
- Modify: `docs/deployment/windows-server-iis.md`
- Modify: `docs/deployment/windows-server-iis_ZH.md`

**Interfaces:**
- Documents the two PowerShell interfaces produced by Tasks 1 and 2.
- Preserves the existing IIS initialization, administrator initialization,
  certificates, acceptance checks, update, and rollback guidance.

- [ ] **Step 1: Add the connected-machine preparation instructions in English**

In section 3, before the website publish command, add:

```markdown
On an internet-connected Windows preparation machine, check out the reviewed
source commit and create the offline dependency kit:

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath D:\WebPassTransfer\WebPassMigrationOfflineKit `
  -Force
```

Transfer the entire `WebPassMigrationOfflineKit` directory to the offline
build machine through approved removable media or an internal file share. The
kit is build material, not a deployment artifact, and must not be copied to
the IIS server.
```

- [ ] **Step 2: Replace the English bundle build command with strict offline mode**

Use:

```powershell
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath E:\WebPassMigrationOfflineKit `
  -OutputPath C:\WebPass\staging\WebPass.Migrations.exe
```

State explicitly that the source checkout must be at the commit in
`manifest.json`, any missing dependency stops deployment, and the offline
build machine does not require access to an HTTP NuGet source.

Retain the existing bundle execution command and database privilege guidance.
Add that the IIS/deployment server requires neither the .NET SDK nor
`dotnet-ef`; it retains the existing .NET 10 Hosting Bundle requirement.

- [ ] **Step 3: Apply the equivalent Simplified Chinese instructions**

Write the corresponding section in `windows-server-iis_ZH.md` as UTF-8:

```markdown
在可联网的 Windows 准备机上检出已审核的源代码提交，并制作离线依赖包：

```powershell
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath D:\WebPassTransfer\WebPassMigrationOfflineKit `
  -Force
```

通过批准的移动介质或内网文件共享，将整个
`WebPassMigrationOfflineKit` 目录复制到离线构建机。该目录属于构建材料，
不是部署产物，不要将其复制到 IIS 服务器。
```

Use the same strict-offline build command as the English document. State that
the source commit must match the manifest, missing packages terminate the
build, the build machine does not access an HTTP NuGet source, and the IIS
server needs neither the SDK nor `dotnet-ef`.

- [ ] **Step 4: Verify both documents remain synchronized**

Run:

```powershell
$documents = @(
  'docs\deployment\windows-server-iis.md',
  'docs\deployment\windows-server-iis_ZH.md')
foreach ($document in $documents) {
    $content = Get-Content -LiteralPath $document -Raw -Encoding utf8
    foreach ($required in @(
        'Prepare-WebPassMigrationOfflineKit.ps1',
        'Build-WebPassMigrationBundle.ps1',
        '-OfflineKitPath',
        'WebPass.Migrations.exe')) {
        if (-not $content.Contains($required)) {
            throw "$document is missing $required"
        }
    }
}
```

Expected: exit zero. Visually confirm both documents retain the order:
connected preparation, transfer, offline build, bundle execution, least
database privilege.

- [ ] **Step 5: Review and commit Task 3**

Run:

```powershell
git diff --check
git diff -- `
  docs/deployment/windows-server-iis.md `
  docs/deployment/windows-server-iis_ZH.md
git status --short
```

Stage and commit:

```powershell
git add -- `
  docs/deployment/windows-server-iis.md `
  docs/deployment/windows-server-iis_ZH.md
git commit -m "docs: describe offline migration bundle deployment"
```

Stop and obtain approval for Task 4.

---

### Task 4: Final regression and offline deployment verification

**Checkpoint estimate:** no planned source changes; 6k-10k tokens.

**Files:**
- Verify only; modify no file unless a verified defect requires returning to
  its owning Task and repeating RED/GREEN.

**Interfaces:**
- Consumes all deliverables from Tasks 1-3.
- Produces final verification evidence and a clean implementation diff.

- [ ] **Step 1: Generate a fresh kit and build the bundle twice offline**

Run:

```powershell
$kit = 'artifacts\WebPassMigrationOfflineKit'
$bundle = 'artifacts\WebPass.Migrations.exe'
.\scripts\Prepare-WebPassMigrationOfflineKit.ps1 `
  -OutputPath $kit `
  -Force
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath $kit `
  -OutputPath $bundle
.\scripts\Build-WebPassMigrationBundle.ps1 `
  -OfflineKitPath $kit `
  -OutputPath $bundle
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) {
    throw "Missing bundle: $bundle"
}
```

Expected: preparation and both builds succeed using the local feed, and the
second build safely replaces the first output.

- [ ] **Step 2: Run focused deployment integration tests**

Run:

```powershell
$env:WEBPASS_MIGRATION_OFFLINE_KIT = (
  Resolve-Path -LiteralPath $kit).Path
try {
    dotnet test `
      tests\WebPass.IntegrationTests\WebPass.IntegrationTests.csproj `
      -c Release `
      --filter FullyQualifiedName~Deployment
}
finally {
    Remove-Item Env:WEBPASS_MIGRATION_OFFLINE_KIT `
      -ErrorAction SilentlyContinue
}
```

Expected: all deployment tests pass with zero failures and skips, including
actual bundle execution against SQL Server.

- [ ] **Step 3: Verify EF model stability**

Run:

```powershell
.\.tools\dotnet-ef.exe migrations has-pending-model-changes `
  --project src\WebPass.Web\WebPass.Web.csproj `
  --startup-project src\WebPass.Web\WebPass.Web.csproj `
  --configuration Release `
  --no-build
```

Expected:

```text
No changes have been made to the model since the last migration.
```

- [ ] **Step 4: Run the full Release solution**

Run:

```powershell
$env:WEBPASS_MIGRATION_OFFLINE_KIT = (
  Resolve-Path -LiteralPath $kit).Path
try {
    dotnet test WebPass.sln -c Release
}
finally {
    Remove-Item Env:WEBPASS_MIGRATION_OFFLINE_KIT `
      -ErrorAction SilentlyContinue
}
```

Expected: Unit and Integration projects pass with zero failures and zero
skips. Record the exact per-project and total counts.

- [ ] **Step 5: Audit scope, generated artifacts, and repository state**

Run:

```powershell
git diff --check
git status --short
git log --oneline -4
git diff --name-only b698999
git ls-files -- `
  '*.nupkg' `
  '*WebPass.Migrations.exe' `
  'artifacts/**' `
  '.tools/**'
git diff --name-only b698999 -- `
  'src/WebPass.Web/Data/**' `
  'src/WebPass.Web/Migrations/**' `
  'src/WebPass.Web/Pages/**' `
  'src/WebPass.Web/wwwroot/**'
```

Confirm:

- only the scripts, deployment tests, and two deployment documents listed in
  this plan changed after design commit `b698999`;
- no offline kit, package, tool binary, cache, or generated bundle is tracked;
- no WebPass runtime, UI, entity, DbContext, snapshot, or migration file
  changed;
- the working tree is clean.

- [ ] **Step 6: Finish the branch**

Use `superpowers:verification-before-completion` before making any completion
claim. Then use `superpowers:finishing-a-development-branch` to detect the
base branch and offer local merge, push/PR, or keep-as-is. Do not merge, push,
delete a branch, or remove a worktree without the user's explicit choice.

## Expected Commit Sequence

1. `feat: prepare offline migration dependency kit`
2. `feat: build migration bundle from offline kit`
3. `docs: describe offline migration bundle deployment`

The approved design and this implementation plan remain separate commits
before the implementation commits.
