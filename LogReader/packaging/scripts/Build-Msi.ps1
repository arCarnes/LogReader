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
$setupProjectPath = Join-Path $productRoot "LogReader.Setup\LogReader.Setup.wixproj"
$configTemplatePath = Join-Path $packagingRoot "Msi.WeezTail.install.json"
$installerActionValidationScriptPath = Join-Path $scriptRoot "Validate-InstallerActions.ps1"
$identityValidationScriptPath = Join-Path $scriptRoot "Validate-MsiIdentity.ps1"
$shortcutValidationScriptPath = Join-Path $scriptRoot "Validate-MsiShortcuts.ps1"
$mcpSmokeScriptPath = Join-Path $scriptRoot "Test-McpStdioArtifact.ps1"
$publishDir = Join-Path $productRoot "artifacts\publish\WeezTail.MsiPayload"
$installerOutputDir = Join-Path $productRoot "artifacts\installer"

& $installerActionValidationScriptPath

if ($LASTEXITCODE -ne 0) {
    throw "Installer action validation failed."
}

& dotnet restore $appProjectPath `
    -r $Runtime `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the MSI payload publish."
}

& dotnet restore $mcpProjectPath `
    -r $Runtime `
    /p:NuGetAudit=false

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed for the MSI MCP server publish."
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
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for the MSI application payload."
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
    -o $publishDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed for the MSI MCP server payload."
}

Copy-Item $configTemplatePath (Join-Path $publishDir "WeezTail.install.json") -Force

& $mcpSmokeScriptPath -ExecutablePath (Join-Path $publishDir "WeezTail.Mcp.exe")

if ($LASTEXITCODE -ne 0) {
    throw "MSI payload MCP stdio smoke test failed."
}

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

$msiPath = Join-Path $installerOutputDir "WeezTail.Setup.msi"
& $identityValidationScriptPath -MsiPath $msiPath

if ($LASTEXITCODE -ne 0) {
    throw "MSI identity validation failed."
}

& $shortcutValidationScriptPath -MsiPath $msiPath

if ($LASTEXITCODE -ne 0) {
    throw "MSI shortcut validation failed."
}

Write-Host "MSI package built under $installerOutputDir"
