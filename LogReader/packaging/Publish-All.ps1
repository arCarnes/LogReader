param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64")][string]$Runtime = "win-x64",
    [string]$LauncherPackagePath,
    [string]$LauncherReleaseMetadataPath,
    [string]$Repository = "arCarnes/LogReader"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "scripts/ReleasePackaging.ps1")

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productRoot = Split-Path -Parent $scriptRoot
$portableScriptPath = Join-Path $scriptRoot "scripts\Publish-Portable.ps1"
$msiScriptPath = Join-Path $scriptRoot "scripts\Build-Msi.ps1"
$portableValidationScriptPath = Join-Path $scriptRoot "scripts\Validate-PortableArtifact.ps1"
$publishRoot = Join-Path $productRoot "artifacts\publish"
$installerOutputDir = Join-Path $productRoot "artifacts\installer"
$portableOutputDir = Join-Path $productRoot "artifacts\publish\Portable"
$appDataOutputDir = Join-Path $productRoot "artifacts\publish\AppData"
$launcherOutputDir = Join-Path $productRoot "artifacts\publish\Launchers"
$sharedOutputDir = Join-Path $productRoot "artifacts\publish\Shared"
$sharedValidationScriptPath = Join-Path $scriptRoot "scripts\Validate-SharedArtifact.ps1"
$msiPayloadOutputDir = Join-Path $productRoot "artifacts\publish\WeezTail.MsiPayload"

function Remove-ReleaseArtifact {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $resolvedTarget = [System.IO.Path]::GetFullPath($Path)
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $productRoot "artifacts"))
    if (-not $resolvedTarget.StartsWith($artifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release cleanup must remain within '$artifactsRoot'."
    }
    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}

$versions = Get-ReleaseVersions $productRoot
$version = $versions.application
$source = Get-ReleaseSource $productRoot
# Resolve the immutable launcher input before deleting or building application output.
$launcherRelease = & (Join-Path $scriptRoot 'scripts/Get-LauncherRelease.ps1') `
    -Version $versions.bundledLauncher -Runtime $Runtime -Repository $Repository `
    -PackagePath $LauncherPackagePath -ReleaseMetadataPath $LauncherReleaseMetadataPath

$portableZipPath = Join-Path $productRoot "artifacts\publish\WeezTail-$version-portable-$Runtime.zip"
$appDataZipPath = Join-Path $productRoot "artifacts\publish\WeezTail-$version-appdata-$Runtime.zip"
$sharedZipPath = Join-Path $productRoot "artifacts\publish\WeezTail-$version-shared-$Runtime.zip"

Remove-ReleaseArtifact $portableOutputDir
Remove-ReleaseArtifact $appDataOutputDir
Remove-ReleaseArtifact $sharedOutputDir
Remove-ReleaseArtifact $msiPayloadOutputDir
Remove-ReleaseArtifact $installerOutputDir

if (Test-Path $publishRoot) {
    Get-ChildItem $publishRoot -File -Filter "WeezTail-*-portable-*.zip" | Remove-Item -Force
    Get-ChildItem $publishRoot -File -Filter "WeezTail-*-appdata-*.zip" | Remove-Item -Force
}

& (Join-Path $scriptRoot "scripts\Test-ZipArtifactValidation.ps1")
& (Join-Path $scriptRoot "scripts\Test-SharedArtifactValidation.ps1")

& $portableScriptPath -Configuration $Configuration -Runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    throw "Portable publish failed."
}

New-ReleaseZip -SourceDirectory $portableOutputDir -DestinationPath $portableZipPath

& $portableValidationScriptPath -PublishDirectory $portableOutputDir -ZipPath $portableZipPath

if ($LASTEXITCODE -ne 0) {
    throw "Portable release artifact validation failed."
}

& $portableScriptPath -Configuration $Configuration -Runtime $Runtime -StorageMode LocalAppData

if ($LASTEXITCODE -ne 0) {
    throw "AppData publish failed."
}

New-ReleaseZip -SourceDirectory $appDataOutputDir -DestinationPath $appDataZipPath

& $portableValidationScriptPath -PublishDirectory $appDataOutputDir -ZipPath $appDataZipPath -StorageMode LocalAppData

if ($LASTEXITCODE -ne 0) {
    throw "AppData release artifact validation failed."
}

& $sharedValidationScriptPath -PublishDirectory $launcherOutputDir -LauncherOnly
Assert-LauncherExecutables $launcherOutputDir $versions.bundledLauncher $launcherRelease.sourceCommit

New-SharedRelease -OutputDirectory $sharedOutputDir -ApplicationDirectory $appDataOutputDir `
    -LauncherDirectory $launcherOutputDir -ApplicationVersion $version
New-ReleaseZip -SourceDirectory $sharedOutputDir -DestinationPath $sharedZipPath
& $sharedValidationScriptPath -PublishDirectory $sharedOutputDir -ZipPath $sharedZipPath
& (Join-Path $scriptRoot "scripts\Test-McpStdioArtifact.ps1") -ExecutablePath (Join-Path $sharedOutputDir "WeezTail.Mcp.Launcher.exe")

& $msiScriptPath -Configuration $Configuration -Runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    throw "MSI publish failed."
}

$assets = @($portableZipPath, $appDataZipPath, $sharedZipPath, (Join-Path $installerOutputDir 'WeezTail.Setup.msi'))
[ordered]@{
    schemaVersion = 1; stream = 'application'; applicationVersion = $version
    bundledLauncherVersion = $versions.bundledLauncher; source = $source
    launcherRelease = $launcherRelease
    assets = @($assets | ForEach-Object { Get-ReleaseAsset $_ })
} | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $publishRoot 'application-release-evidence.json') -Encoding UTF8
Write-Host "Bundled published launchers $($versions.bundledLauncher) from $($launcherRelease.sourceCommit)"
Write-Host "Portable package published to $portableOutputDir"
Write-Host "Portable zip created at $portableZipPath"
Write-Host "AppData package published to $appDataOutputDir"
Write-Host "AppData zip created at $appDataZipPath"
Write-Host "Shared zip created at $sharedZipPath"
Write-Host "MSI package built under $(Join-Path $productRoot 'artifacts\installer')"
