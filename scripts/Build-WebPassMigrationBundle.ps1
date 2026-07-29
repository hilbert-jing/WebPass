[CmdletBinding()]
param(
    [string]$OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (
    Join-Path $PSScriptRoot '..')).Path
$efTool = Join-Path $repositoryRoot '.tools\dotnet-ef.exe'
$webProject = Join-Path $repositoryRoot (
    'src\WebPass.Web\WebPass.Web.csproj')

if (-not (Test-Path -LiteralPath $efTool -PathType Leaf)) {
    throw "Repository-local EF tool was not found: $efTool"
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
$outputDirectory = Split-Path -Parent $resolvedOutputPath
New-Item -ItemType Directory -Path $outputDirectory -Force |
    Out-Null

& $efTool migrations bundle `
    --project $webProject `
    --startup-project $webProject `
    --configuration Release `
    --target-runtime win-x64 `
    --output $resolvedOutputPath `
    --force

if ($LASTEXITCODE -ne 0) {
    throw "Migration bundle build failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath $resolvedOutputPath -PathType Leaf)) {
    throw "Migration bundle was not created: $resolvedOutputPath"
}

Write-Output $resolvedOutputPath
