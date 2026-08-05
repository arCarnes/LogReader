param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot
$appProjectPath = Join-Path $productRoot "LogReader.App\LogReader.App.csproj"
$mcpProjectPath = Join-Path $productRoot "LogReader.Mcp\LogReader.Mcp.csproj"
$outputDir = Join-Path $productRoot "artifacts\publish\Portable"
$configTemplatePath = Join-Path $packagingRoot "Portable.WeezTail.install.json"
$validationScriptPath = Join-Path $scriptRoot "Validate-PortableArtifact.ps1"
$mcpSmokeScriptPath = Join-Path $scriptRoot "Test-McpStdioArtifact.ps1"

if (Test-Path $outputDir) {
    Remove-Item $outputDir -Recurse -Force
}

& dotnet restore $appProjectPath `
    -r $Runtime `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the portable package."
}

& dotnet restore $mcpProjectPath `
    -r $Runtime `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the portable MCP server."
}

& dotnet publish $appProjectPath `
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
    throw "dotnet publish failed for the portable application."
}

& dotnet publish $mcpProjectPath `
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
    throw "dotnet publish failed for the portable MCP server."
}

$portableConfigPath = Join-Path $outputDir "WeezTail.install.json"
$dataDir = Join-Path $outputDir "Data"
$cacheDir = Join-Path $outputDir "Cache"
$pdbPath = Join-Path $outputDir "WeezTail.pdb"

Copy-Item $configTemplatePath $portableConfigPath -Force

if (Test-Path $pdbPath) {
    Remove-Item $pdbPath -Force
}

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null

& $validationScriptPath -PublishDirectory $outputDir

if ($LASTEXITCODE -ne 0) {
    throw "Portable artifact validation failed."
}

& $mcpSmokeScriptPath -ExecutablePath (Join-Path $outputDir "WeezTail.Mcp.exe")

if ($LASTEXITCODE -ne 0) {
    throw "Portable MCP stdio smoke test failed."
}

Write-Host "Portable package published to $outputDir"
