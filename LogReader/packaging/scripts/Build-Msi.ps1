param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot
$projectPath = Join-Path $productRoot "LogReader.App\LogReader.App.csproj"
$setupProjectPath = Join-Path $productRoot "LogReader.Setup\LogReader.Setup.wixproj"
$configTemplatePath = Join-Path $packagingRoot "Msi.LogReader.install.json"
$publishDir = Join-Path $productRoot "artifacts\publish\LogReader.MsiPayload"
$installerOutputDir = Join-Path $productRoot "artifacts\installer"

& dotnet restore $projectPath `
    -r $Runtime `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the MSI payload publish."
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
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for the MSI payload."
}

Copy-Item $configTemplatePath (Join-Path $publishDir "LogReader.install.json") -Force

& dotnet restore $setupProjectPath `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the WiX installer project."
}

& dotnet build $setupProjectPath `
    -c $Configuration `
    --no-restore `
    /p:NuGetAudit=false `
    /p:AppPublishDir=$publishDir `
    /p:OutputPath=$installerOutputDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed for the WiX installer project."
}

Write-Host "MSI package built under $installerOutputDir"
