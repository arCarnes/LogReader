param(
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$installer = $null
$database = $null
function Release-ComObject($value) {
    if ($null -ne $value -and [Runtime.InteropServices.Marshal]::IsComObject($value)) {
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($value) | Out-Null
    }
}
function Read-MsiTable([string]$table, [string[]]$columns) {
    $view = $null
    $rows = @()
    try {
        $quoted = ($columns | ForEach-Object { '`' + $_ + '`' }) -join ','
        $query = 'SELECT ' + $quoted + ' FROM `' + $table + '`'
        $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($query))
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        while ($null -ne ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null))) {
            try {
                $row = [ordered]@{}
                for ($i = 0; $i -lt $columns.Count; $i++) {
                    $row[$columns[$i]] = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($i + 1))
                }
                $rows += [pscustomobject]$row
            }
            finally { Release-ComObject $record }
        }
    }
    finally {
        if ($null -ne $view) {
            $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
            Release-ComObject $view
        }
    }
    return $rows
}

try {
    $resolvedMsi = (Resolve-Path -LiteralPath $MsiPath).Path
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($resolvedMsi, 0))
    $sequence = @(Read-MsiTable 'InstallExecuteSequence' @('Action', 'Condition', 'Sequence'))
    $actions = @(Read-MsiTable 'CustomAction' @('Action', 'Type', 'Source', 'Target'))
    $initialize = $sequence | Where-Object Action -eq 'InstallInitialize'
    $removeOld = $sequence | Where-Object Action -eq 'RemoveExistingProducts'
    $report = [ordered]@{
        MsiPath = $resolvedMsi
        Sha256 = (Get-FileHash -LiteralPath $resolvedMsi -Algorithm SHA256).Hash
        CapturedUtc = [DateTime]::UtcNow.ToString('o')
        Properties = @(Read-MsiTable 'Property' @('Property', 'Value'))
        Upgrade = @(Read-MsiTable 'Upgrade' @('UpgradeCode', 'VersionMin', 'VersionMax', 'Attributes', 'ActionProperty'))
        CustomActions = $actions
        ExecuteSequence = $sequence
        UiSequence = @(Read-MsiTable 'InstallUISequence' @('Action', 'Condition', 'Sequence'))
        OldProductRemovalInsideTransaction = ([int]$removeOld.Sequence -gt [int]$initialize.Sequence)
        VbScriptActionCount = @($actions | Where-Object { ([int]$_.Type -band 7) -eq 6 }).Count
        LifecycleDemonstrated = $false
    }
    $target = [IO.Path]::GetFullPath($OutputPath)
    if ($target -eq $resolvedMsi) { throw 'Evidence output cannot overwrite the MSI.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::WriteAllText($target, ($report | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    Write-Host "MSI table evidence saved to $target. Actual lifecycle behavior has not been demonstrated by this command."
}
finally {
    Release-ComObject $database
    Release-ComObject $installer
}
