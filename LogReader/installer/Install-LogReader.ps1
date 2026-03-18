param(
    [switch]$SkipLaunch,
    [string]$InstallRoot,
    [string]$ProgramsFolder,
    [switch]$SkipUninstallEntry
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-InstallerManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceRoot
    )

    $manifestPath = Join-Path $SourceRoot "installer-manifest.json"
    if (-not (Test-Path -LiteralPath $manifestPath)) {
        throw "Missing installer manifest at '$manifestPath'."
    }

    return Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
}

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Stop-InstalledProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath
    )

    if (-not (Test-Path -LiteralPath $ExecutablePath)) {
        return
    }

    $resolvedTargetPath = [System.IO.Path]::GetFullPath($ExecutablePath)
    $processName = [System.IO.Path]::GetFileNameWithoutExtension($ExecutablePath)
    $processes = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)

    foreach ($process in $processes) {
        try {
            $processPath = $process.Path
        }
        catch {
            continue
        }

        if (-not $processPath) {
            continue
        }

        if ([System.IO.Path]::GetFullPath($processPath) -ne $resolvedTargetPath) {
            continue
        }

        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
        }
        catch [System.InvalidOperationException] {
        }
    }
}

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Get-ChildItem -LiteralPath $SourcePath -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DestinationPath -Recurse -Force
    }
}

function New-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath,

        [Parameter(Mandatory = $true)]
        [string]$TargetPath,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $shortcutDirectory = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $shortcutDirectory)) {
        New-Item -ItemType Directory -Path $shortcutDirectory | Out-Null
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $TargetPath
    $shortcut.Description = $Description
    $shortcut.Save()
}

function Set-UninstallEntry {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject]$Manifest,

        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [string]$ExecutablePath,

        [Parameter(Mandatory = $true)]
        [string]$UninstallScriptPath
    )

    $registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$($Manifest.InstallDirectory)"
    $installSizeBytes = (Get-ChildItem -LiteralPath $InstallRoot -Recurse -Force |
        Where-Object { -not $_.PSIsContainer } |
        Measure-Object -Property Length -Sum).Sum
    $installSizeKb = [int][Math]::Ceiling(($installSizeBytes / 1KB))
    $uninstallCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$UninstallScriptPath`""

    New-Item -Path $registryPath -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "DisplayName" -Value $Manifest.ProductName -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "DisplayVersion" -Value $Manifest.Version -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "Publisher" -Value $Manifest.Publisher -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd") -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "InstallLocation" -Value $InstallRoot -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "DisplayIcon" -Value $ExecutablePath -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "EstimatedSize" -Value $installSizeKb -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "NoModify" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "NoRepair" -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "UninstallString" -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $registryPath -Name "QuietUninstallString" -Value "$uninstallCommand -Quiet" -PropertyType String -Force | Out-Null
}

$sourceRoot = Split-Path -Parent $PSCommandPath
$manifest = Get-InstallerManifest -SourceRoot $sourceRoot
$payloadRoot = Join-Path $sourceRoot "payload"

if (-not (Test-Path -LiteralPath $payloadRoot)) {
    throw "Missing published app payload at '$payloadRoot'."
}

$resolvedInstallRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    Join-Path $env:LOCALAPPDATA "Programs\$($manifest.InstallDirectory)"
}
else {
    [System.IO.Path]::GetFullPath($InstallRoot)
}

$resolvedProgramsFolder = if ([string]::IsNullOrWhiteSpace($ProgramsFolder)) {
    Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
}
else {
    [System.IO.Path]::GetFullPath($ProgramsFolder)
}

$installRoot = $resolvedInstallRoot
$installedExecutablePath = Join-Path $installRoot $manifest.ExecutableName
$uninstallScriptSource = Join-Path $sourceRoot "Uninstall-LogReader.ps1"
$manifestSource = Join-Path $sourceRoot "installer-manifest.json"
$shortcutPath = Join-Path $resolvedProgramsFolder "$($manifest.ShortcutName).lnk"

Stop-InstalledProcess -ExecutablePath $installedExecutablePath
Reset-Directory -Path $installRoot
Copy-DirectoryContents -SourcePath $payloadRoot -DestinationPath $installRoot
Copy-Item -LiteralPath $uninstallScriptSource -Destination (Join-Path $installRoot "Uninstall-LogReader.ps1") -Force
Copy-Item -LiteralPath $manifestSource -Destination (Join-Path $installRoot "installer-manifest.json") -Force

if (-not (Test-Path -LiteralPath $installedExecutablePath)) {
    throw "The installed executable '$installedExecutablePath' was not found after copying the payload."
}

New-StartMenuShortcut -ShortcutPath $shortcutPath `
    -TargetPath $installedExecutablePath `
    -WorkingDirectory $installRoot `
    -Description $manifest.ProductName

if (-not $SkipUninstallEntry) {
    Set-UninstallEntry -Manifest $manifest `
        -InstallRoot $installRoot `
        -ExecutablePath $installedExecutablePath `
        -UninstallScriptPath (Join-Path $installRoot "Uninstall-LogReader.ps1")
}

if (-not $SkipLaunch) {
    Start-Process -FilePath $installedExecutablePath -WorkingDirectory $installRoot
}

Write-Host "$($manifest.ProductName) $($manifest.Version) installed to $installRoot"
