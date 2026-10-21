$ErrorActionPreference = "Stop"
$fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("WeezTail-shared-fixtures-" + [Guid]::NewGuid().ToString("N"))
$validator = Join-Path $PSScriptRoot "Validate-SharedArtifact.ps1"

function Expect-Rejection {
    param([scriptblock]$Action)
    $rejected = $false
    try { & $Action } catch { $rejected = $true }
    if (-not $rejected) { throw "Invalid shared artifact was accepted." }
}

try {
    $release = Join-Path $fixtureRoot "releases\1.2.3"
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    foreach ($file in @("WeezTail.Launcher.exe", "WeezTail.Mcp.Launcher.exe")) {
        Set-Content -LiteralPath (Join-Path $fixtureRoot $file) -Value "fixture"
    }
    foreach ($file in @("WeezTail.exe", "WeezTail.Mcp.exe")) {
        Set-Content -LiteralPath (Join-Path $release $file) -Value "fixture"
    }
    $manifest = Join-Path $fixtureRoot "current.json"
    $config = Join-Path $release "WeezTail.install.json"
    Set-Content -LiteralPath $manifest -Value '{"version":"1.2.3"}'
    Set-Content -LiteralPath $config -Value '{"installMode":"Portable","storageMode":"LocalAppData"}'
    & $validator -PublishDirectory $fixtureRoot
    Set-Content -LiteralPath $manifest -Value '{"version":"../1.2.3"}'
    Expect-Rejection { & $validator -PublishDirectory $fixtureRoot }
    Set-Content -LiteralPath $manifest -Value '{"version":"1.2.3"}'
    Set-Content -LiteralPath $config -Value '{"installMode":"Portable","storageMode":"ExeDirectory"}'
    Expect-Rejection { & $validator -PublishDirectory $fixtureRoot }
    Set-Content -LiteralPath $config -Value '{"installMode":"Portable","storageMode":"LocalAppData"}'
    $symbol = Join-Path $fixtureRoot "unexpected.pdb"
    Set-Content -LiteralPath $symbol -Value "fixture"
    Expect-Rejection { & $validator -PublishDirectory $fixtureRoot }
    Remove-Item -LiteralPath $symbol
    Remove-Item -LiteralPath (Join-Path $fixtureRoot "WeezTail.Mcp.Launcher.exe")
    Expect-Rejection { & $validator -PublishDirectory $fixtureRoot }
    Set-Content -LiteralPath (Join-Path $fixtureRoot "WeezTail.Mcp.Launcher.exe") -Value "fixture"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipPath = Join-Path $fixtureRoot "..\$([System.IO.Path]::GetFileName($fixtureRoot)).zip"
    [System.IO.Compression.ZipFile]::CreateFromDirectory($fixtureRoot, $zipPath)
    & $validator -ZipPath $zipPath
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try { $zip.CreateEntry("../escape.exe") | Out-Null } finally { $zip.Dispose() }
    Expect-Rejection { & $validator -ZipPath $zipPath }
    Remove-Item -LiteralPath $zipPath
    $releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $fixtureRoot "releases"))
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    if (-not $releaseRoot.StartsWith($resolvedFixture + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Release fixture cleanup must remain inside the fixture root."
    }
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
    Move-Item -LiteralPath $manifest -Destination (Join-Path $fixtureRoot "current.example.json")
    & $validator -PublishDirectory $fixtureRoot -LauncherOnly
    Write-Host "Shared artifact validator fixtures passed."
}
finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($fixtureRoot)
    $temporaryRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if (-not $resolvedRoot.StartsWith($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture cleanup must remain under the temporary directory."
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
    if ($zipPath -and (Test-Path -LiteralPath $zipPath)) { Remove-Item -LiteralPath $zipPath -Force }
}
