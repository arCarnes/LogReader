param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productRoot = Split-Path -Parent $scriptRoot
$portableScriptPath = Join-Path $scriptRoot "scripts\Publish-Portable.ps1"
$msiScriptPath = Join-Path $scriptRoot "scripts\Build-Msi.ps1"
$publishRoot = Join-Path $productRoot "artifacts\publish"
$installerOutputDir = Join-Path $productRoot "artifacts\installer"
$portableOutputDir = Join-Path $productRoot "artifacts\publish\Portable"
$msiPayloadOutputDir = Join-Path $productRoot "artifacts\publish\LogReader.MsiPayload"
$versionPropsPath = Join-Path $productRoot "Directory.Build.props"

function Remove-ReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path $Path) {
        Remove-Item $Path -Recurse -Force
    }
}

[xml]$versionProps = Get-Content $versionPropsPath
$version = $versionProps.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Could not resolve the product version from Directory.Build.props."
}

$portableZipPath = Join-Path $productRoot "artifacts\publish\LogReader-$version-portable-$Runtime.zip"

Remove-ReleaseArtifact $portableOutputDir
Remove-ReleaseArtifact $msiPayloadOutputDir
Remove-ReleaseArtifact $installerOutputDir

if (Test-Path $publishRoot) {
    Get-ChildItem $publishRoot -File -Filter "LogReader-*-portable-*.zip" | Remove-Item -Force
}

& $portableScriptPath -Configuration $Configuration -Runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    throw "Portable publish failed."
}

if (Test-Path $portableZipPath) {
    Remove-Item $portableZipPath -Force
}

Compress-Archive -Path (Join-Path $portableOutputDir "*") -DestinationPath $portableZipPath -Force

& $msiScriptPath -Configuration $Configuration -Runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    throw "MSI publish failed."
}

Write-Host "Portable package published to $portableOutputDir"
Write-Host "Portable zip created at $portableZipPath"
Write-Host "MSI package built under $(Join-Path $productRoot 'artifacts\installer')"
