param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productRoot = Split-Path -Parent $scriptRoot
$portableScriptPath = Join-Path $scriptRoot "scripts\Publish-Portable.ps1"
$msiScriptPath = Join-Path $scriptRoot "scripts\Build-Msi.ps1"
$portableValidationScriptPath = Join-Path $scriptRoot "scripts\Validate-PortableArtifact.ps1"
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

function New-PortableZip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    if (Test-Path $DestinationPath) {
        Remove-Item $DestinationPath -Force
    }

    $resolvedSource = (Resolve-Path $SourceDirectory).Path
    $zip = [System.IO.Compression.ZipFile]::Open($DestinationPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($directory in Get-ChildItem $resolvedSource -Directory -Recurse) {
            $relativePath = Resolve-ZipRelativePath $resolvedSource $directory.FullName
            if (-not $relativePath.EndsWith("/")) {
                $relativePath += "/"
            }

            $zip.CreateEntry($relativePath) | Out-Null
        }

        foreach ($file in Get-ChildItem $resolvedSource -File -Recurse) {
            $relativePath = Resolve-ZipRelativePath $resolvedSource $file.FullName
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip,
                $file.FullName,
                $relativePath,
                [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Resolve-ZipRelativePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseDirectory,
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $baseUri = New-Object System.Uri((Join-Path $BaseDirectory "."))
    $pathUri = New-Object System.Uri($Path)
    return [System.Uri]::UnescapeDataString($baseUri.MakeRelativeUri($pathUri).ToString())
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

New-PortableZip -SourceDirectory $portableOutputDir -DestinationPath $portableZipPath

& $portableValidationScriptPath -PublishDirectory $portableOutputDir -ZipPath $portableZipPath

if ($LASTEXITCODE -ne 0) {
    throw "Portable release artifact validation failed."
}

& $msiScriptPath -Configuration $Configuration -Runtime $Runtime

if ($LASTEXITCODE -ne 0) {
    throw "MSI publish failed."
}

Write-Host "Portable package published to $portableOutputDir"
Write-Host "Portable zip created at $portableZipPath"
Write-Host "MSI package built under $(Join-Path $productRoot 'artifacts\installer')"
