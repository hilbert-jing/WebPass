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

function Assert-ReviewedSourceTree {
    param([string[]]$ExcludedPaths = @())

    $pathspecs = @(
        'src',
        'WebPass.sln',
        'global.json',
        'Directory.Build.props',
        'Directory.Build.targets',
        'Directory.Packages.props',
        'NuGet.Config')
    $tracked = @(& git -C $repositoryRoot diff `
        --name-only --no-renames HEAD -- @pathspecs)
    if ($LASTEXITCODE -ne 0) {
        throw 'The reviewed WebPass source changes could not be determined.'
    }
    $untracked = @(& git -C $repositoryRoot ls-files `
        --others -- @pathspecs)
    if ($LASTEXITCODE -ne 0) {
        throw 'The untracked WebPass source files could not be determined.'
    }
    $trackedProjects = @(& git -C $repositoryRoot ls-files -- 'src' |
        Where-Object {
            [System.IO.Path]::GetExtension($_).Equals(
                '.csproj',
                [StringComparison]::OrdinalIgnoreCase)
        })
    if ($LASTEXITCODE -ne 0) {
        throw 'The tracked WebPass projects could not be determined.'
    }
    $generatedProjectOutputPrefixes = @($trackedProjects | ForEach-Object {
        $projectRoot = [System.IO.Path]::GetDirectoryName($_).
            Replace('\', '/').TrimEnd('/')
        $projectRoot + '/bin/'
        $projectRoot + '/obj/'
    } | Sort-Object -Unique)

    $normalizedExclusions = @($ExcludedPaths | ForEach-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd('\')
    })
    $changes = @($tracked + $untracked | Where-Object {
        $relativePath = $_.Replace('\', '/')
        $isGeneratedProjectOutput = @(
            $generatedProjectOutputPrefixes | Where-Object {
                $relativePath.StartsWith(
                    $_,
                    [StringComparison]::OrdinalIgnoreCase)
            }).Count -gt 0
        $fullPath = [System.IO.Path]::GetFullPath(
            (Join-Path $repositoryRoot $_)).TrimEnd('\')
        -not $isGeneratedProjectOutput -and
        -not ($normalizedExclusions | Where-Object {
            $fullPath.Equals($_, [StringComparison]::OrdinalIgnoreCase) -or
            $fullPath.StartsWith(
                $_ + '\',
                [StringComparison]::OrdinalIgnoreCase)
        })
    } | Sort-Object -Unique)
    if ($changes.Count -gt 0) {
        throw "The WebPass source tree contains unreviewed changes: $($changes -join ', ')"
    }
}

function Remove-StagingDrive {
    if ($script:stagingDrive) {
        & subst.exe $script:stagingDrive '/D'
        $script:stagingDrive = $null
    }
}

function Remove-ExactDirectoryTree {
    param([Parameter(Mandatory)][string]$LiteralPath)

    if (-not (Test-Path -LiteralPath $LiteralPath)) {
        return
    }

    $firstError = $null
    $retryError = $null
    try {
        Remove-Item -LiteralPath $LiteralPath -Recurse -Force
    }
    catch {
        $firstError = $_.Exception.Message
    }

    if (Test-Path -LiteralPath $LiteralPath) {
        try {
            foreach ($letter in [char[]](90..68)) {
                $driveRoot = $letter + ':\'
                if (Test-Path -LiteralPath $driveRoot) {
                    continue
                }

                & subst.exe ($letter + ':') $LiteralPath
                if ($LASTEXITCODE -eq 0) {
                    $script:stagingDrive = $letter + ':'
                    break
                }
            }
            if (-not $script:stagingDrive) {
                throw 'A temporary drive letter could not be allocated.'
            }

            $shortRoot = $script:stagingDrive + '\'
            Get-ChildItem -LiteralPath $shortRoot -Force |
                ForEach-Object {
                    Remove-Item -LiteralPath $_.FullName -Recurse -Force
                }
        }
        catch {
            $retryError = $_.Exception.Message
        }
        finally {
            Remove-StagingDrive
        }
    }

    if ((Test-Path -LiteralPath $LiteralPath) -and
        [string]::IsNullOrWhiteSpace($retryError)) {
        try {
            Remove-Item -LiteralPath $LiteralPath -Force
        }
        catch {
            $retryError = $_.Exception.Message
        }
    }

    if (Test-Path -LiteralPath $LiteralPath) {
        $details = @($firstError, $retryError) |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        throw "Exact directory cleanup failed: $LiteralPath. $($details -join ' ')"
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath(
    $(if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath
    }
    else {
        Join-Path $repositoryRoot $OutputPath
    }))
$normalizedRepositoryRoot = $repositoryRoot.TrimEnd('\')
$normalizedOutput = $resolvedOutput.TrimEnd('\')
$repositoryPrefix = $normalizedRepositoryRoot + '\'
if ($normalizedRepositoryRoot.Equals(
        $normalizedOutput,
        [StringComparison]::OrdinalIgnoreCase) -or
    $repositoryPrefix.StartsWith(
        $normalizedOutput + '\',
        [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The offline-kit output cannot be the repository or its parent.'
}
if ((Test-Path -LiteralPath $resolvedOutput) -and -not $Force) {
    throw "Offline-kit output already exists: $resolvedOutput"
}

Assert-ReviewedSourceTree -ExcludedPaths @($resolvedOutput)

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
    $oldFallbackPackages = $env:NUGET_FALLBACK_PACKAGES
    $oldRestoreSources = $env:RestoreSources
    $oldRestoreConfigFile = $env:RestoreConfigFile
    $oldRestorePackagesPath = $env:RestorePackagesPath
    $oldRestoreFallbackFolders = $env:RestoreFallbackFolders
    $oldAdditionalFallbackFolders =
        $env:RestoreAdditionalProjectFallbackFolders
    $oldAdditionalSources = $env:RestoreAdditionalProjectSources
    $oldDisableImplicitFallback =
        $env:DisableImplicitNuGetFallbackFolder
    $oldAudit = $env:NuGetAudit
    try {
        $env:NUGET_PACKAGES = $workingPackages
        $env:NUGET_FALLBACK_PACKAGES = ''
        $env:RestoreSources = $feed
        $env:RestoreConfigFile = $config
        $env:RestorePackagesPath = $workingPackages
        $env:RestoreFallbackFolders = ''
        $env:RestoreAdditionalProjectFallbackFolders = ''
        $env:RestoreAdditionalProjectSources = ''
        $env:DisableImplicitNuGetFallbackFolder = 'true'
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
        $env:NUGET_FALLBACK_PACKAGES = $oldFallbackPackages
        $env:RestoreSources = $oldRestoreSources
        $env:RestoreConfigFile = $oldRestoreConfigFile
        $env:RestorePackagesPath = $oldRestorePackagesPath
        $env:RestoreFallbackFolders = $oldRestoreFallbackFolders
        $env:RestoreAdditionalProjectFallbackFolders =
            $oldAdditionalFallbackFolders
        $env:RestoreAdditionalProjectSources = $oldAdditionalSources
        $env:DisableImplicitNuGetFallbackFolder =
            $oldDisableImplicitFallback
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

    if ($backup -and (Test-Path -LiteralPath $backup)) {
        try {
            Remove-ExactDirectoryTree -LiteralPath $backup
        }
        catch {
            throw "Offline-kit publication succeeded, but backup cleanup failed: $backup. $($_.Exception.Message)"
        }
        $backup = $null
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
