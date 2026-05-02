param(
    [string]$MsiPath,
    [string]$VersionPropsPath
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $productRoot "artifacts\installer\LogReader.Setup.msi"
}

if ([string]::IsNullOrWhiteSpace($VersionPropsPath)) {
    $VersionPropsPath = Join-Path $productRoot "Directory.Build.props"
}

$expectedUpgradeCode = "{93530218-C7A8-4BC1-B4C0-8A670BA3776A}"
$sameVersionProperty = "LOGREADER_SAME_VERSION_DETECTED"
$sameVersionLaunchCondition = "Installed OR NOT $sameVersionProperty"
$onlyDetectAttribute = 2
$versionMinInclusiveAttribute = 256
$versionMaxInclusiveAttribute = 512

if (-not (Test-Path $MsiPath)) {
    throw "MSI not found at '$MsiPath'."
}

if (-not (Test-Path $VersionPropsPath)) {
    throw "Version props file not found at '$VersionPropsPath'."
}

[xml]$versionProps = Get-Content $VersionPropsPath
$expectedVersion = [string]$versionProps.Project.PropertyGroup.Version

if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
    throw "Could not resolve the expected product version from '$VersionPropsPath'."
}

$versionParts = $expectedVersion.Split(".")
if ($versionParts.Count -ne 3) {
    throw "MSI release versions must use exactly three version fields. Found '$expectedVersion'."
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

$properties = @{}
foreach ($row in Get-MsiRows $database "SELECT ``Property``,``Value`` FROM ``Property``" 2) {
    $properties[$row[0]] = $row[1]
}

if ($properties["ProductVersion"] -ne $expectedVersion) {
    throw "MSI ProductVersion '$($properties["ProductVersion"])' does not match Directory.Build.props Version '$expectedVersion'."
}

if ([string]::IsNullOrWhiteSpace($properties["ProductCode"])) {
    throw "MSI ProductCode is missing."
}

$upgradeRows = Get-MsiRows $database "SELECT ``UpgradeCode``,``VersionMin``,``VersionMax``,``Attributes``,``ActionProperty`` FROM ``Upgrade``" 5
$sameVersionRows = @(
    $upgradeRows | Where-Object {
        $_[0].Equals($expectedUpgradeCode, [System.StringComparison]::OrdinalIgnoreCase) -and
        $_[1] -eq $expectedVersion -and
        $_[2] -eq $expectedVersion -and
        $_[4] -eq $sameVersionProperty
    }
)

if ($sameVersionRows.Count -ne 1) {
    throw "Expected exactly one same-version Upgrade row for $sameVersionProperty, found $($sameVersionRows.Count)."
}

$sameVersionAttributes = [int]$sameVersionRows[0][3]
foreach ($requiredAttribute in @($onlyDetectAttribute, $versionMinInclusiveAttribute, $versionMaxInclusiveAttribute)) {
    if (($sameVersionAttributes -band $requiredAttribute) -ne $requiredAttribute) {
        throw "Same-version Upgrade row is missing required attribute flag $requiredAttribute. Attributes: $sameVersionAttributes."
    }
}

$launchConditionRows = Get-MsiRows $database "SELECT ``Condition``,``Description`` FROM ``LaunchCondition``" 2
$sameVersionLaunchRows = @(
    $launchConditionRows | Where-Object {
        $_[0] -eq $sameVersionLaunchCondition -and
        $_[1] -like "*already installed*"
    }
)

if ($sameVersionLaunchRows.Count -ne 1) {
    throw "Expected exactly one same-version LaunchCondition '$sameVersionLaunchCondition', found $($sameVersionLaunchRows.Count)."
}

Write-Host "MSI identity validated: ProductVersion=$($properties["ProductVersion"]), ProductCode=$($properties["ProductCode"]), UpgradeCode=$expectedUpgradeCode"
