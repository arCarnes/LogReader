$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$validator = Join-Path $scriptRoot "Validate-PortableArtifact.ps1"
$validationRoot = [System.IO.Path]::GetFullPath((Join-Path $productRoot "artifacts\validation"))
$fixtureRoot = Join-Path $validationRoot ("zip-layout-" + [guid]::NewGuid().ToString("N"))

function Assert-Rejected {
    param([scriptblock]$Action)

    $rejected = $false
    try {
        & $Action | Out-Null
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "ZIP artifact validator accepted an invalid fixture."
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    foreach ($mode in @("ExeDirectory", "LocalAppData")) {
        $fixture = Join-Path $fixtureRoot $mode
        New-Item -ItemType Directory -Force -Path $fixture | Out-Null
        foreach ($name in @("WeezTail.exe", "WeezTail.Mcp.exe")) {
            [System.IO.File]::WriteAllText((Join-Path $fixture $name), "fixture")
        }
        @{ installMode = "Portable"; storageMode = $mode } |
            ConvertTo-Json | Set-Content (Join-Path $fixture "WeezTail.install.json")
        if ($mode -eq "ExeDirectory") {
            foreach ($name in @("Data", "Cache")) {
                New-Item -ItemType Directory -Path (Join-Path $fixture $name) | Out-Null
            }
        }
        $zipPath = Join-Path $fixtureRoot ($mode + ".zip")
        $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($file in Get-ChildItem $fixture -File) {
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Name) | Out-Null
            }
            if ($mode -eq "ExeDirectory") {
                $zip.CreateEntry("Data/") | Out-Null
                $zip.CreateEntry("Cache/") | Out-Null
            }
        }
        finally {
            $zip.Dispose()
        }
        & $validator -PublishDirectory $fixture -ZipPath $zipPath -StorageMode $mode

        $wrongMode = if ($mode -eq "ExeDirectory") { "LocalAppData" } else { "ExeDirectory" }
        Assert-Rejected { & $validator -PublishDirectory $fixture -StorageMode $wrongMode }
        Assert-Rejected { & $validator -ZipPath $zipPath -StorageMode $wrongMode }

        if ($mode -eq "LocalAppData") {
            $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Update)
            try {
                $zip.CreateEntry("Cache/idx/unexpected.bin") | Out-Null
            }
            finally {
                $zip.Dispose()
            }
            Assert-Rejected { & $validator -ZipPath $zipPath -StorageMode $mode }
            New-Item -ItemType Directory -Path (Join-Path $fixture "Cache") | Out-Null
            Assert-Rejected { & $validator -PublishDirectory $fixture -StorageMode $mode }
        }
    }
    Write-Host "ZIP artifact validator fixtures passed for Portable and AppData."
}
finally {
    $resolvedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedFixture.StartsWith($validationRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Fixture cleanup must remain within '$validationRoot'."
    }
    if (Test-Path -LiteralPath $resolvedFixture) {
        Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
    }
}
