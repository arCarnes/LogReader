param(
    [switch]$Quiet,
    [switch]$RemoveData,
    [string]$InstallRoot,
    [string]$ProgramsFolder,
    [string]$DataRoot,
    [switch]$SkipUninstallEntryRemoval
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-InstallerManifest {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ManifestPath
    )

    if (-not (Test-Path -LiteralPath $ManifestPath)) {
        throw "Missing installer manifest at '$ManifestPath'."
    }

    return Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
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

function Remove-StartMenuShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ShortcutPath
    )

    if (Test-Path -LiteralPath $ShortcutPath) {
        Remove-Item -LiteralPath $ShortcutPath -Force
    }
}

function Remove-UninstallEntry {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallDirectoryName
    )

    $registryPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$InstallDirectoryName"
    if (Test-Path -LiteralPath $registryPath) {
        Remove-Item -LiteralPath $registryPath -Recurse -Force
    }
}

function Start-CleanupProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstallRoot,

        [Parameter(Mandatory = $true)]
        [bool]$RemoveData,

        [Parameter(Mandatory = $true)]
        [string]$DataRoot
    )

    $escapedInstallRoot = $InstallRoot.Replace("'", "''")
    $escapedDataRoot = $DataRoot.Replace("'", "''")
    $removeDataLiteral = if ($RemoveData) { '$true' } else { '$false' }
    $cleanupScriptDirectory = Split-Path -Parent $InstallRoot
    $cleanupScriptPath = Join-Path $cleanupScriptDirectory ("cleanup-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    $escapedCleanupScriptPath = $cleanupScriptPath.Replace("'", "''")
    $cleanupScript = @"
Start-Sleep -Seconds 1
for (`$attempt = 0; `$attempt -lt 10; `$attempt++) {
    if (-not (Test-Path -LiteralPath '$escapedInstallRoot')) {
        break
    }

    try {
        Remove-Item -LiteralPath '$escapedInstallRoot' -Recurse -Force -ErrorAction Stop
    }
    catch {
        Start-Sleep -Seconds 1
    }
}
if ($removeDataLiteral -and (Test-Path -LiteralPath '$escapedDataRoot')) {
    Remove-Item -LiteralPath '$escapedDataRoot' -Recurse -Force -ErrorAction SilentlyContinue
}
Remove-Item -LiteralPath '$escapedCleanupScriptPath' -Force -ErrorAction SilentlyContinue
"@

    Set-Content -LiteralPath $cleanupScriptPath -Value $cleanupScript -Encoding ASCII
    Start-Process -FilePath "cmd.exe" `
        -ArgumentList @(
            "/c"
            "start"
            '""'
            "/b"
            "powershell.exe"
            "-NoProfile"
            "-ExecutionPolicy"
            "Bypass"
            "-WindowStyle"
            "Hidden"
            "-File"
            $cleanupScriptPath
        ) | Out-Null
}

$resolvedInstallRoot = if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
    Split-Path -Parent $PSCommandPath
}
else {
    [System.IO.Path]::GetFullPath($InstallRoot)
}

$manifest = Get-InstallerManifest -ManifestPath (Join-Path $resolvedInstallRoot "installer-manifest.json")
$installedExecutablePath = Join-Path $resolvedInstallRoot $manifest.ExecutableName
$resolvedProgramsFolder = if ([string]::IsNullOrWhiteSpace($ProgramsFolder)) {
    Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
}
else {
    [System.IO.Path]::GetFullPath($ProgramsFolder)
}

$resolvedDataRoot = if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    Join-Path $env:LOCALAPPDATA $manifest.DataDirectory
}
else {
    [System.IO.Path]::GetFullPath($DataRoot)
}

$shortcutPath = Join-Path $resolvedProgramsFolder "$($manifest.ShortcutName).lnk"

if (-not $Quiet) {
    $answer = Read-Host "Uninstall $($manifest.ProductName)? Enter Y to continue"
    if ($answer -notin @("Y", "y")) {
        Write-Host "Uninstall cancelled."
        return
    }
}

Stop-InstalledProcess -ExecutablePath $installedExecutablePath
Remove-StartMenuShortcut -ShortcutPath $shortcutPath
if (-not $SkipUninstallEntryRemoval) {
    Remove-UninstallEntry -InstallDirectoryName $manifest.InstallDirectory
}

Start-CleanupProcess -InstallRoot $resolvedInstallRoot -RemoveData:$RemoveData -DataRoot $resolvedDataRoot

Write-Host "$($manifest.ProductName) uninstall started."
