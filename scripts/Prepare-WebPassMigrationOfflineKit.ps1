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

function Remove-StagingDrive {
    if ($script:stagingDrive) {
        & subst.exe $script:stagingDrive '/D'
        $script:stagingDrive = $null
    }
}

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
$script:stagingDrive = $null

try {
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

    $tools = Join-Path $staging 'tools'
    $seedPackages = Join-Path $staging 'seed-packages'
    $feed = Join-Path $staging 'feed'
    New-Item -ItemType Directory -Path $tools,$seedPackages,$feed |
        Out-Null

    foreach ($letter in [char[]](90..68)) {
        $drive = $letter + ':\\'
        if (Test-Path -LiteralPath $drive) {
            continue
        }

        & subst.exe ($letter + ':') $staging
        if ($LASTEXITCODE -eq 0) {
            $script:stagingDrive = $letter + ':'
            break
        }
    }
    if (-not $script:stagingDrive) {
        throw 'A temporary drive letter is required for the offline-kit package cache.'
    }
    $workingStaging = $script:stagingDrive + '\\'
    $workingSeedPackages = Join-Path $workingStaging 'seed-packages'
    $workingPackages = Join-Path $workingStaging 'packages'

    Invoke-CheckedCommand dotnet @(
        'tool', 'install', 'dotnet-ef',
        '--tool-path', $tools,
        '--version', $DotnetEfVersion)

    $oldPackages = $env:NUGET_PACKAGES
    try {
        $env:NUGET_PACKAGES = $workingSeedPackages
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

    Get-ChildItem -LiteralPath $workingSeedPackages -Recurse -Filter '*.nupkg' |
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

    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="WebPassOffline" value="feed" />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath (Join-Path $staging 'NuGet.Config') `
        -Encoding utf8

    $packages = Join-Path $staging 'packages'
    $config = Join-Path $staging 'NuGet.Config'
    New-Item -ItemType Directory -Path $packages | Out-Null
    $oldRestoreSources = $env:RestoreSources
    $oldAudit = $env:NuGetAudit
    try {
        $env:NUGET_PACKAGES = $workingPackages
        $env:RestoreSources = $feed
        $env:NuGetAudit = 'false'
        Invoke-CheckedCommand dotnet @(
            'restore', $webProject,
            '--runtime', $TargetRuntime,
            '--configfile', $config,
            '--packages', $workingPackages,
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

    $validationBundle = Join-Path $staging 'validation-bundle.exe'
    if (-not (Test-Path -LiteralPath $validationBundle)) {
        throw "Offline validation bundle was not created: $validationBundle"
    }

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
        $validationBundle, `
        $workingSeedPackages `
        -Recurse -Force

    Remove-StagingDrive
    Invoke-CheckedCommand dotnet @(
        'restore', $solution)

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
}
finally {
    Remove-StagingDrive
    if ($staging -and (Test-Path -LiteralPath $staging)) {
        try {
            Remove-Item -LiteralPath $staging -Recurse -Force
        }
        catch [System.IO.DirectoryNotFoundException] {
            if (Test-Path -LiteralPath $staging) {
                Remove-Item -LiteralPath $staging -Recurse -Force `
                    -ErrorAction SilentlyContinue
            }
        }
    }
}
