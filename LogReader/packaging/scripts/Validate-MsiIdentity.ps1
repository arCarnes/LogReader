param(
    [string]$MsiPath,
    [string]$VersionPropsPath
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot

if ([string]::IsNullOrWhiteSpace($MsiPath)) {
    $MsiPath = Join-Path $productRoot "artifacts\installer\WeezTail.Setup.msi"
}

if ([string]::IsNullOrWhiteSpace($VersionPropsPath)) {
    $VersionPropsPath = Join-Path $productRoot "Directory.Build.props"
}

$expectedUpgradeCode = "{93530218-C7A8-4BC1-B4C0-8A670BA3776A}"
$upgradeDetectedProperty = "WIX_UPGRADE_DETECTED"
$sameVersionProperty = "LOGREADER_SAME_VERSION_DETECTED"
$sameVersionLaunchCondition = "Installed OR NOT $sameVersionProperty"
$storageMigrationAction = "CaptureLegacyStorageSelection"
$storageMigrationCondition = "NOT Installed AND $upgradeDetectedProperty"
$expectedStorageMigrationActionType = 1
$transactionalMigrationCondition = "$storageMigrationCondition AND LOGREADERMIGRATIONPLANNED = `"1`""
$transactionalMigrationActions = @(
    @{ Action = "RollbackLegacyStorageSelection"; Type = 9473 },
    @{ Action = "ApplyLegacyStorageSelection"; Type = 9217 },
    @{ Action = "CommitLegacyStorageSelection"; Type = 9729 }
)
$cleanupPlanAction = "PlanDataCleanup"
$cleanupPlanCondition = 'REMOVE = "ALL" AND NOT UPGRADINGPRODUCTCODE AND REMOVELOGREADERDATA = "1"'
$transactionalCleanupCondition = $cleanupPlanCondition + ' AND LOGREADERCLEANUPPLANNED = "1"'
$transactionalCleanupActions = @(
    @{ Action = "RecoverInterruptedDataCleanup"; Type = 9217 },
    @{ Action = "RollbackDataCleanup"; Type = 9473 },
    @{ Action = "StageDataCleanup"; Type = 9217 },
    @{ Action = "CommitDataCleanup"; Type = 9729 }
)
$onlyDetectAttribute = 2
$versionMinInclusiveAttribute = 256
$versionMaxInclusiveAttribute = 512

function Release-ComObject {
    param(
        [AllowNull()]
        [object]$ComObject
    )

    if ($null -ne $ComObject -and [System.Runtime.InteropServices.Marshal]::IsComObject($ComObject)) {
        [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($ComObject) | Out-Null
    }
}

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

    $rows = @()
    $view = $null

    try {
        $view = $Database.GetType().InvokeMember("OpenView", "InvokeMethod", $null, $Database, @($Query))
        $view.GetType().InvokeMember("Execute", "InvokeMethod", $null, $view, $null) | Out-Null

        while ($true) {
            $record = $view.GetType().InvokeMember("Fetch", "InvokeMethod", $null, $view, $null)
            if ($null -eq $record) {
                break
            }

            try {
                $row = @()
                for ($i = 1; $i -le $ColumnCount; $i++) {
                    $row += Get-RecordString $record $i
                }

                $rows += ,$row
            }
            finally {
                Release-ComObject $record
            }
        }
    }
    finally {
        if ($null -ne $view) {
            $view.GetType().InvokeMember("Close", "InvokeMethod", $null, $view, $null) | Out-Null
            Release-ComObject $view
        }
    }

    return $rows
}

$installer = $null
$database = $null

try {
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

    if (-not $properties["UpgradeCode"].Equals($expectedUpgradeCode, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MSI UpgradeCode '$($properties["UpgradeCode"])' does not match the expected upgrade lineage '$expectedUpgradeCode'."
    }

    if ($properties["ALLUSERS"] -ne "1") {
        throw "MSI must remain per-machine (ALLUSERS=1). Found '$($properties["ALLUSERS"])'."
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

    $majorUpgradeRows = @(
        $upgradeRows | Where-Object {
            $_[0].Equals($expectedUpgradeCode, [System.StringComparison]::OrdinalIgnoreCase) -and
            $_[4] -eq $upgradeDetectedProperty
        }
    )

    if ($majorUpgradeRows.Count -ne 1) {
        throw "Expected exactly one major-upgrade detection row for $upgradeDetectedProperty, found $($majorUpgradeRows.Count)."
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

    $customActionRows = Get-MsiRows $database "SELECT ``Action``,``Type``,``Source``,``Target`` FROM ``CustomAction``" 4
    $storageMigrationRows = @(
        $customActionRows | Where-Object {
            $_[0] -eq $storageMigrationAction -and
            $_[2] -eq "InstallerActionsDll" -and
            $_[3] -eq $storageMigrationAction
        }
    )

    if ($storageMigrationRows.Count -ne 1) {
        throw "Expected exactly one $storageMigrationAction custom action backed by InstallerActionsDll, found $($storageMigrationRows.Count)."
    }

    if ([int]$storageMigrationRows[0][1] -ne $expectedStorageMigrationActionType) {
        throw "$storageMigrationAction must be an immediate, synchronous Binary-table DLL action. Type: $($storageMigrationRows[0][1])."
    }

    foreach ($expectedAction in $transactionalMigrationActions) {
        $matchingActions = @(
            $customActionRows | Where-Object {
                $_[0] -eq $expectedAction.Action -and
                $_[2] -eq "InstallerActionsDll" -and
                $_[3] -eq $expectedAction.Action
            }
        )
        if ($matchingActions.Count -ne 1) {
            throw "Expected exactly one $($expectedAction.Action) action backed by InstallerActionsDll, found $($matchingActions.Count)."
        }
        if ([int]$matchingActions[0][1] -ne $expectedAction.Type) {
            throw "$($expectedAction.Action) has unexpected type $($matchingActions[0][1]); expected $($expectedAction.Type)."
        }
    }

    $cleanupPlanRows = @(
        $customActionRows | Where-Object {
            $_[0] -eq $cleanupPlanAction -and
            $_[2] -eq "InstallerActionsDll" -and
            $_[3] -eq $cleanupPlanAction
        }
    )
    if ($cleanupPlanRows.Count -ne 1 -or [int]$cleanupPlanRows[0][1] -ne 1) {
        throw "$cleanupPlanAction must be one immediate Binary-table DLL action."
    }
    if ((@($customActionRows | Where-Object { $_[0] -eq 'RemoveDataFolders' })).Count -ne 0) {
        throw 'The immediate RemoveDataFolders action must not remain in the package.'
    }
    foreach ($expectedAction in $transactionalCleanupActions) {
        $matchingActions = @(
            $customActionRows | Where-Object {
                $_[0] -eq $expectedAction.Action -and
                $_[2] -eq "InstallerActionsDll" -and
                $_[3] -eq $expectedAction.Action
            }
        )
        if ($matchingActions.Count -ne 1 -or [int]$matchingActions[0][1] -ne $expectedAction.Type) {
            throw "$($expectedAction.Action) is missing or has an unexpected custom-action type."
        }
    }

    $hiddenProperties = @($properties["MsiHiddenProperties"].Split(';'))
    foreach ($expectedAction in $transactionalMigrationActions) {
        if ($expectedAction.Action -notin $hiddenProperties) {
            throw "$($expectedAction.Action) must be listed in MsiHiddenProperties."
        }
    }
    foreach ($expectedAction in $transactionalCleanupActions) {
        if ($expectedAction.Action -notin $hiddenProperties) {
            throw "$($expectedAction.Action) must be listed in MsiHiddenProperties."
        }
    }

    $executeSequenceRows = Get-MsiRows $database "SELECT ``Action``,``Condition``,``Sequence`` FROM ``InstallExecuteSequence``" 3
    $storageMigrationSequenceRows = @(
        $executeSequenceRows | Where-Object {
            $_[0] -eq $storageMigrationAction -and
            $_[1] -eq $storageMigrationCondition
        }
    )
    $removeExistingProductRows = @(
        $executeSequenceRows | Where-Object { $_[0] -eq "RemoveExistingProducts" }
    )
    $installInitializeRows = @(
        $executeSequenceRows | Where-Object { $_[0] -eq "InstallInitialize" }
    )

    if ($storageMigrationSequenceRows.Count -ne 1) {
        throw "Expected exactly one $storageMigrationAction sequence row with condition '$storageMigrationCondition', found $($storageMigrationSequenceRows.Count)."
    }

    if ($removeExistingProductRows.Count -ne 1) {
        throw "Expected exactly one RemoveExistingProducts sequence row, found $($removeExistingProductRows.Count)."
    }

    if ($installInitializeRows.Count -ne 1) {
        throw "Expected exactly one InstallInitialize sequence row, found $($installInitializeRows.Count)."
    }

    if ([int]$removeExistingProductRows[0][2] -le [int]$installInitializeRows[0][2]) {
        throw "RemoveExistingProducts must run after InstallInitialize so a failed upgrade can roll back old-product removal."
    }

    $betweenInitializeAndRemoval = @(
        $executeSequenceRows | Where-Object {
            [int]$_[2] -gt [int]$installInitializeRows[0][2] -and
            [int]$_[2] -lt [int]$removeExistingProductRows[0][2]
        }
    )
    if ($betweenInitializeAndRemoval.Count -ne 0) {
        throw "No action may run between InstallInitialize and RemoveExistingProducts. Found: $($betweenInitializeAndRemoval[0][0])."
    }

    if ([int]$storageMigrationSequenceRows[0][2] -ge [int]$installInitializeRows[0][2]) {
        throw "$storageMigrationAction must capture legacy metadata before InstallInitialize."
    }

    $previousSequence = [int]$removeExistingProductRows[0][2]
    foreach ($expectedAction in $transactionalMigrationActions) {
        $matchingSequence = @(
            $executeSequenceRows | Where-Object {
                $_[0] -eq $expectedAction.Action -and $_[1] -eq $transactionalMigrationCondition
            }
        )
        if ($matchingSequence.Count -ne 1) {
            throw "Expected exactly one $($expectedAction.Action) sequence row with the transactional migration condition."
        }
        if ([int]$matchingSequence[0][2] -le $previousSequence) {
            throw "$($expectedAction.Action) must follow the preceding migration/removal action."
        }
        $previousSequence = [int]$matchingSequence[0][2]
    }

    if ([int]$storageMigrationSequenceRows[0][2] -ge [int]$removeExistingProductRows[0][2]) {
        throw "$storageMigrationAction must run before RemoveExistingProducts."
    }

    $cleanupPlanSequence = @(
        $executeSequenceRows | Where-Object {
            $_[0] -eq $cleanupPlanAction -and $_[1] -eq $cleanupPlanCondition
        }
    )
    if ($cleanupPlanSequence.Count -ne 1 -or
        [int]$cleanupPlanSequence[0][2] -ge [int]$installInitializeRows[0][2]) {
        throw "$cleanupPlanAction must run exactly once before InstallInitialize."
    }
    $removeFilesRows = @($executeSequenceRows | Where-Object { $_[0] -eq 'RemoveFiles' })
    if ($removeFilesRows.Count -ne 1) {
        throw 'Expected exactly one RemoveFiles sequence row.'
    }
    $previousCleanupSequence = [int]$installInitializeRows[0][2]
    foreach ($expectedAction in $transactionalCleanupActions) {
        $matchingSequence = @(
            $executeSequenceRows | Where-Object {
                $_[0] -eq $expectedAction.Action -and $_[1] -eq $transactionalCleanupCondition
            }
        )
        if ($matchingSequence.Count -ne 1) {
            throw "Expected exactly one $($expectedAction.Action) sequence row with the transactional cleanup condition."
        }
        if ([int]$matchingSequence[0][2] -le $previousCleanupSequence) {
            throw "$($expectedAction.Action) must follow the preceding cleanup action."
        }
        $previousCleanupSequence = [int]$matchingSequence[0][2]
    }
    $stageSequence = @($executeSequenceRows | Where-Object { $_[0] -eq 'StageDataCleanup' })
    if ([int]$stageSequence[0][2] -ge [int]$removeFilesRows[0][2]) {
        throw 'StageDataCleanup must run before RemoveFiles.'
    }

    Write-Host "MSI identity validated: ProductVersion=$($properties["ProductVersion"]), ProductCode=$($properties["ProductCode"]), UpgradeCode=$expectedUpgradeCode"
}
finally {
    Release-ComObject $database
    Release-ComObject $installer
}
