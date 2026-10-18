param(
    [string]$PublishDirectory,
    [string]$ZipPath,
    [switch]$LauncherOnly
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'ReleasePackaging.ps1')

function Assert-SharedDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    $pointerName = if ($LauncherOnly) { "current.example.json" } else { "current.json" }
    $required = @("WeezTail.Launcher.exe", "WeezTail.Mcp.Launcher.exe", $pointerName)
    foreach ($name in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path $name) -PathType Leaf)) {
            throw "Shared artifact is missing '$name'."
        }
    }
    $allowed = $required
    if (-not $LauncherOnly) { $allowed += "releases" }
    foreach ($entry in Get-ChildItem -LiteralPath $Path -Force) {
        if ($entry.Name -notin $allowed) {
            throw "Shared artifact contains unexpected root entry '$($entry.Name)'."
        }
    }
    if (@(Get-ChildItem -LiteralPath $Path -Recurse -Force | Where-Object {
        $_.Extension -eq ".pdb" -or ($_.Attributes -band [System.IO.FileAttributes]::ReparsePoint)
    }).Count -ne 0) {
        throw "Shared artifact contains debug symbols or redirected entries."
    }
    $pointer = Get-Content -LiteralPath (Join-Path $Path $pointerName) -Raw | ConvertFrom-Json
    if ($pointer.version -isnot [string] -or $pointer.version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') {
        throw "Shared artifact pointer requires three numeric version fields."
    }
    $parsedVersion = $null
    if (-not [Version]::TryParse($pointer.version, [ref]$parsedVersion)) {
        throw "Shared artifact pointer has an invalid version."
    }
    if (-not $LauncherOnly) {
        $releaseRoot = Join-Path $Path "releases"
        if (-not (Test-Path -LiteralPath $releaseRoot -PathType Container)) {
            throw "Shared artifact is missing releases."
        }
        $entries = @(Get-ChildItem -LiteralPath $releaseRoot -Force)
        if ($entries.Count -ne 1 -or -not $entries[0].PSIsContainer -or $entries[0].Name -cne $pointer.version) {
            throw "Shared artifact must contain exactly the selected release directory."
        }
        & (Join-Path $PSScriptRoot "Validate-PortableArtifact.ps1") `
            -PublishDirectory $entries[0].FullName -StorageMode LocalAppData
    }
}

if (-not $PublishDirectory -and -not $ZipPath) {
    throw "Specify PublishDirectory or ZipPath."
}
if ($PublishDirectory) { Assert-SharedDirectory $PublishDirectory }
if ($ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ZipPath).Path)
    $temporaryDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("WeezTail-shared-validation-" + [Guid]::NewGuid().ToString("N"))
    try {
        Assert-ReleaseZipEntries $zip
        $zip.Dispose()
        [System.IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $ZipPath).Path, $temporaryDirectory)
        Assert-SharedDirectory $temporaryDirectory
    }
    finally {
        $zip.Dispose()
        $resolvedTemporary = [System.IO.Path]::GetFullPath($temporaryDirectory)
        $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedTemporary.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Validation cleanup must remain under the temporary directory."
        }
        if (Test-Path -LiteralPath $resolvedTemporary) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
}
Write-Host "Shared artifact validation passed."
