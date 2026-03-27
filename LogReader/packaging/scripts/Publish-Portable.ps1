param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot
$projectPath = Join-Path $productRoot "LogReader.App\LogReader.App.csproj"
$outputDir = Join-Path $productRoot "artifacts\publish\Portable"
$configTemplatePath = Join-Path $packagingRoot "Portable.LogReader.install.json"

& dotnet restore $projectPath `
    -r $Runtime `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the portable package."
}

& dotnet publish $projectPath `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    --no-restore `
    /p:PublishSingleFile=true `
    /p:IncludeNativeLibrariesForSelfExtract=true `
    /p:DebugType=None `
    /p:DebugSymbols=false `
    /p:NuGetAudit=false `
    -o $outputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for the portable package."
}

$portableConfigPath = Join-Path $outputDir "LogReader.install.json"
$dataDir = Join-Path $outputDir "Data"
$cacheDir = Join-Path $outputDir "Cache"
$pdbPath = Join-Path $outputDir "LogReader.pdb"

Copy-Item $configTemplatePath $portableConfigPath -Force

if (Test-Path $pdbPath) {
    Remove-Item $pdbPath -Force
}

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

Write-Host "Portable package published to $outputDir"
