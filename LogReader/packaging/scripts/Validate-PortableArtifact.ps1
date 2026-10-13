param(
    [string]$PublishDirectory,
    [string]$ZipPath,
    [ValidateSet("ExeDirectory", "LocalAppData")]
    [string]$StorageMode = "ExeDirectory"
)

$ErrorActionPreference = "Stop"

function Assert-PortableConfig {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Json,
        [Parameter(Mandatory = $true)]
        [string]$Source
    )

    try {
        $config = $Json | ConvertFrom-Json
    }
    catch {
        throw "Portable install config in '$Source' is not valid JSON. $($_.Exception.Message)"
    }

    if ($config.installMode -ne "Portable") {
        throw "Portable install config in '$Source' has installMode '$($config.installMode)' instead of 'Portable'."
    }

    if ($config.storageMode -ne $StorageMode) {
        throw "Portable install config in '$Source' has storageMode '$($config.storageMode)' instead of '$StorageMode'."
    }
}

function Test-ZipEntry {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.Compression.ZipArchive]$Zip,
        [Parameter(Mandatory = $true)]
        [string]$EntryName
    )

    foreach ($entry in $Zip.Entries) {
        if ($entry.FullName -eq $EntryName) {
            return $true
        }
    }

    return $false
}

function Validate-PublishDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path -PathType Container)) {
        throw "Portable publish directory not found at '$Path'."
    }

    $requiredFiles = @("WeezTail.exe", "WeezTail.Mcp.exe", "WeezTail.install.json")
    foreach ($file in $requiredFiles) {
        $filePath = Join-Path $Path $file
        if (-not (Test-Path $filePath -PathType Leaf)) {
            throw "Portable publish directory is missing '$file'."
        }
    }

    $requiredDirectories = if ($StorageMode -eq "ExeDirectory") { @("Data", "Cache") } else { @() }
    foreach ($directory in $requiredDirectories) {
        $directoryPath = Join-Path $Path $directory
        if (-not (Test-Path $directoryPath -PathType Container)) {
            throw "Portable publish directory is missing '$directory'."
        }
    }

    $pdbFiles = @(Get-ChildItem $Path -Recurse -File -Filter "*.pdb")
    if ($pdbFiles.Count -gt 0) {
        throw "Portable publish directory contains debug symbol files: $($pdbFiles.FullName -join ', ')"
    }

    $unexpectedRootEntries = @(
        Get-ChildItem $Path -Force | Where-Object {
            $_.Name -notin (@("WeezTail.exe", "WeezTail.Mcp.exe", "WeezTail.install.json") + $requiredDirectories)
        }
    )
    if ($unexpectedRootEntries.Count -gt 0) {
        throw "Portable publish directory contains unexpected root entries: $($unexpectedRootEntries.Name -join ', ')"
    }

    Assert-PortableConfig (Get-Content (Join-Path $Path "WeezTail.install.json") -Raw) (Join-Path $Path "WeezTail.install.json")
}

function Validate-Zip {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Portable zip not found at '$Path'."
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $Path).Path)
    try {
        $requiredEntries = @("WeezTail.exe", "WeezTail.Mcp.exe", "WeezTail.install.json")
        if ($StorageMode -eq "ExeDirectory") {
            $requiredEntries += @("Data/", "Cache/")
        }
        foreach ($entryName in $requiredEntries) {
            if (-not (Test-ZipEntry $zip $entryName)) {
                throw "Portable zip is missing '$entryName'."
            }
        }

        $pdbEntries = @($zip.Entries | Where-Object { $_.FullName.EndsWith(".pdb", [System.StringComparison]::OrdinalIgnoreCase) })
        if ($pdbEntries.Count -gt 0) {
            throw "Portable zip contains debug symbol entries: $($pdbEntries.FullName -join ', ')"
        }

        $unexpectedRootEntries = @(
            $zip.Entries |
                Where-Object {
                    if ($StorageMode -eq "LocalAppData") {
                        $_.FullName -notin $requiredEntries
                    } else {
                        $_.FullName -notin $requiredEntries -and $_.FullName -notmatch "^(Data|Cache)/"
                    }
                }
        )
        if ($unexpectedRootEntries.Count -gt 0) {
            throw "Portable zip contains unexpected root entries: $($unexpectedRootEntries.FullName -join ', ')"
        }

        $configEntry = $zip.GetEntry("WeezTail.install.json")
        $reader = New-Object System.IO.StreamReader($configEntry.Open())
        try {
            Assert-PortableConfig $reader.ReadToEnd() "$Path!WeezTail.install.json"
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $zip.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($PublishDirectory) -and [string]::IsNullOrWhiteSpace($ZipPath)) {
    throw "Specify at least one of -PublishDirectory or -ZipPath."
}

if (-not [string]::IsNullOrWhiteSpace($PublishDirectory)) {
    Validate-PublishDirectory $PublishDirectory
    Write-Host "Portable publish directory validated: $PublishDirectory"
}

if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    Validate-Zip $ZipPath
    Write-Host "Portable zip validated: $ZipPath"
}
