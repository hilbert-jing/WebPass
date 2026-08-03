[CmdletBinding()]
param(
    [string]$OutputPath,
    [string]$OfflineKitPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$DotnetEfVersion = '10.0.0'
$TargetRuntime = 'win-x64'
$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..')).Path
$webProject = Join-Path $repositoryRoot (
    'src\WebPass.Web\WebPass.Web.csproj')

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

    $normalizedExclusions = @($ExcludedPaths | ForEach-Object {
        [System.IO.Path]::GetFullPath($_).TrimEnd('\')
    })
    $changes = @($tracked + $untracked | Where-Object {
        $relativeSegments = @($_.Replace('\', '/') -split '/')
        $isGeneratedProjectOutput =
            $relativeSegments.Count -ge 3 -and
            $relativeSegments[0].Equals(
                'src',
                [StringComparison]::OrdinalIgnoreCase) -and
            ($relativeSegments -contains 'bin' -or
                $relativeSegments -contains 'obj')
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

if (-not (Test-Path -LiteralPath $webProject -PathType Leaf)) {
    throw "WebPass project was not found: $webProject"
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot (
        'src\WebPass.Web\bin\Release\migrations\win-x64\WebPass.Migrations.exe')
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot $OutputPath
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
Assert-ReviewedSourceTree -ExcludedPaths @($resolvedOutputPath)

if ($OfflineKitPath) {
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

    $toolVersionOutput = (& $efTool --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $toolVersionOutput -notmatch '(^|\s)10\.0\.0($|\s)') {
        throw 'Offline migration kit dotnet-ef executable is not version 10.0.0.'
    }
}
else {
    $efTool = Join-Path $repositoryRoot '.tools\dotnet-ef.exe'
    if (-not (Test-Path -LiteralPath $efTool -PathType Leaf)) {
        throw "Repository-local EF tool was not found: $efTool"
    }
}

$outputDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force |
    Out-Null
$temporaryOutput = Join-Path $outputDirectory (
    '.' + [System.IO.Path]::GetFileName($resolvedOutputPath) +
    '.' + [Guid]::NewGuid().ToString('N') + '.tmp.exe')

function Publish-Bundle {
    if (-not (Test-Path -LiteralPath $temporaryOutput -PathType Leaf)) {
        throw "Migration bundle was not created: $temporaryOutput"
    }
    Move-Item -LiteralPath $temporaryOutput `
        -Destination $resolvedOutputPath `
        -Force
    $script:temporaryOutput = $null
    Write-Output $resolvedOutputPath
}

if ($OfflineKitPath) {
    $oldPackages = $env:NUGET_PACKAGES
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
        $env:NUGET_PACKAGES = $packages
        $env:NUGET_FALLBACK_PACKAGES = ''
        $env:RestoreSources = $feed
        $env:RestoreConfigFile = $configPath
        $env:RestorePackagesPath = $packages
        $env:RestoreFallbackFolders = ''
        $env:RestoreAdditionalProjectFallbackFolders = ''
        $env:RestoreAdditionalProjectSources = ''
        $env:DisableImplicitNuGetFallbackFolder = 'true'
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

        Publish-Bundle
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
        if ($temporaryOutput -and
            (Test-Path -LiteralPath $temporaryOutput -PathType Leaf)) {
            Remove-Item -LiteralPath $temporaryOutput -Force
        }
    }
}
else {
    try {
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

        Publish-Bundle
    }
    finally {
        if ($temporaryOutput -and
            (Test-Path -LiteralPath $temporaryOutput -PathType Leaf)) {
            Remove-Item -LiteralPath $temporaryOutput -Force
        }
    }
}
