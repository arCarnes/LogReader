param(
    [string]$MsiPath
)

$ErrorActionPreference = "Stop"

$expectedShortcuts = @(
    @{
        Shortcut = "StartMenuShortcut"
        Component = "StartMenuShortcutComponent"
        Directory = "LogReaderProgramMenuFolder"
        RegistryName = "StartMenuShortcut"
        Feature = "StartMenuShortcutFeature"
    },
    @{
        Shortcut = "DesktopShortcut"
        Component = "DesktopShortcutComponent"
        Directory = "DesktopFolder"
        RegistryName = "DesktopShortcut"
        Feature = "DesktopShortcutFeature"
    }
)
$hkcuRoot = "1"
$expectedRegistryKey = "Software\LogReader\Installer"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $productRoot "artifacts\installer\LogReader.Setup.msi"
}

if (-not (Test-Path $MsiPath)) {
    throw "MSI not found at '$MsiPath'."
}

function Get-RecordString {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Record,
        [Parameter(Mandatory = $true)]
        [int]$Index
    )

    return [string]$Record.GetType().InvokeMember("StringData", "GetProperty", $null, $Record, @($Index))
}

function Get-MsiRows {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,
        [Parameter(Mandatory = $true)]
        [string]$Query,
        [Parameter(Mandatory = $true)]
        [int]$ColumnCount
    )

    $view = $Database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $Database, @($Query))
    $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null

    $rows = @()
    while ($true) {
        $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
        if ($null -eq $record) {
            break
        }

        $row = @()
        for ($i = 1; $i -le $ColumnCount; $i++) {
            $row += Get-RecordString $record $i
        }

        $rows += ,$row
    }

    $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null
    return $rows
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember("OpenDatabase", "InvokeMethod", $null, $installer, @((Resolve-Path $MsiPath).Path, 0))

$componentRows = Get-MsiRows $database "SELECT ``Component``,``Directory_``,``KeyPath`` FROM ``Component``" 3
$shortcutRows = Get-MsiRows $database "SELECT ``Shortcut``,``Directory_``,``Component_``,``Target`` FROM ``Shortcut``" 4
$registryRows = Get-MsiRows $database "SELECT ``Registry``,``Root``,``Key``,``Name``,``Component_`` FROM ``Registry``" 5
$featureComponentRows = Get-MsiRows $database "SELECT ``Feature_``,``Component_`` FROM ``FeatureComponents``" 2

foreach ($expected in $expectedShortcuts) {
    $shortcut = @($shortcutRows | Where-Object { $_[0] -eq $expected.Shortcut })
    if ($shortcut.Count -ne 1) {
        throw "Expected exactly one Shortcut row for '$($expected.Shortcut)', found $($shortcut.Count)."
    }

    if ($shortcut[0][1] -ne $expected.Directory -or $shortcut[0][2] -ne $expected.Component) {
        throw "Shortcut '$($expected.Shortcut)' is not attached to expected directory/component."
    }

    if ($shortcut[0][3] -ne "[INSTALLFOLDER]LogReader.exe") {
        throw "Shortcut '$($expected.Shortcut)' target '$($shortcut[0][3])' does not match '[INSTALLFOLDER]LogReader.exe'."
    }

    $registry = @($registryRows | Where-Object {
        $_[1] -eq $hkcuRoot -and
        $_[2] -eq $expectedRegistryKey -and
        $_[3] -eq $expected.RegistryName -and
        $_[4] -eq $expected.Component
    })
    if ($registry.Count -ne 1) {
        throw "Expected exactly one HKCU Registry row for shortcut component '$($expected.Component)', found $($registry.Count)."
    }

    $component = @($componentRows | Where-Object { $_[0] -eq $expected.Component })
    if ($component.Count -ne 1) {
        throw "Expected exactly one Component row for '$($expected.Component)', found $($component.Count)."
    }

    if ($component[0][2] -ne $registry[0][0]) {
        throw "Component '$($expected.Component)' KeyPath '$($component[0][2])' does not point to registry key '$($registry[0][0])'."
    }

    $featureComponent = @($featureComponentRows | Where-Object {
        $_[0] -eq $expected.Feature -and $_[1] -eq $expected.Component
    })
    if ($featureComponent.Count -ne 1) {
        throw "Feature '$($expected.Feature)' does not reference component '$($expected.Component)'."
    }
}

$hklmShortcutRows = @($registryRows | Where-Object {
    $_[1] -eq "2" -and
    $_[2] -eq $expectedRegistryKey -and
    $_[4] -in @("StartMenuShortcutComponent", "DesktopShortcutComponent")
})
if ($hklmShortcutRows.Count -gt 0) {
    throw "Shortcut components contain HKLM key paths, but non-advertised ProgramMenuFolder/DesktopFolder shortcuts must use HKCU key paths."
}

Write-Host "MSI shortcut authoring validated: per-user shortcut components use HKCU key paths and expected shortcut rows."
