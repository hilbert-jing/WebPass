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

if (-not (Test-Path -LiteralPath $webProject -PathType Leaf)) {
    throw "WebPass project was not found: $webProject"
}

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

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot (
        'src\WebPass.Web\bin\Release\migrations\win-x64\WebPass.Migrations.exe')
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot $OutputPath
}

$resolvedOutputPath = [System.IO.Path]::GetFullPath($OutputPath)
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

        Publish-Bundle
    }
    finally {
        $env:NUGET_PACKAGES = $oldPackages
        $env:RestoreSources = $oldRestoreSources
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
