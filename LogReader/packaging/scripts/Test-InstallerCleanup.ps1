param(
    [string]$InstallerActionsHarnessPath,
    [switch]$RequireSafeguards
)

$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($InstallerActionsHarnessPath)) {
    $InstallerActionsHarnessPath = Join-Path $productRoot 'artifacts\installer-actions\InstallerActionsHarness.exe'
}
if (-not (Test-Path -LiteralPath $InstallerActionsHarnessPath)) {
    & (Join-Path $PSScriptRoot 'Build-InstallerActions.ps1') | Out-Host
}
if (-not (Test-Path -LiteralPath $InstallerActionsHarnessPath)) {
    throw "Compiled installer action harness not found at '$InstallerActionsHarnessPath'."
}

$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('WeezTail-MsiCleanupFixture-' + [Guid]::NewGuid().ToString('N'))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null

function ConvertTo-NativeArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Invoke-FixtureAction {
    param([string[]]$ActionArguments, [int]$ExpectedExitCode = 0)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $InstallerActionsHarnessPath
    $arguments = @($fixtureRoot) + $ActionArguments
    $start.Arguments = ($arguments | ForEach-Object { ConvertTo-NativeArgument $_ }) -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['LOCALAPPDATA'] = Join-Path $fixtureRoot 'Local'
    $start.EnvironmentVariables['APPDATA'] = Join-Path $fixtureRoot 'Roaming'
    $start.EnvironmentVariables['USERPROFILE'] = Join-Path $fixtureRoot 'Profile'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEnd()
        $errorOutput = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne $ExpectedExitCode) {
            throw "Fixture action exited $($process.ExitCode), expected $ExpectedExitCode. $output $errorOutput"
        }
    }
    finally { $process.Dispose() }
}

function New-CleanupFixture {
    param([string]$Name)
    $path = Join-Path $fixtureRoot $Name
    [IO.Directory]::CreateDirectory((Join-Path $path 'Data')) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $path 'Cache')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $path 'Data\sentinel.txt'), 'data')
    [IO.File]::WriteAllText((Join-Path $path 'Cache\sentinel.txt'), 'cache')
    $currentCache = Join-Path $fixtureRoot 'Local\WeezTail\Cache'
    [IO.Directory]::CreateDirectory($currentCache) | Out-Null
    [IO.File]::WriteAllText((Join-Path $currentCache 'current-cache.txt'), 'current cache')
    [IO.File]::WriteAllText((Join-Path $path 'unrelated.txt'), 'preserve')
    [IO.File]::WriteAllText((Join-Path $path 'WeezTail.install.json'),
        (@{ installMode = 'Msi'; storageMode = 'Absolute'; storageRootPath = $path } | ConvertTo-Json))
    return $path
}

try {
    Invoke-FixtureAction -ActionArguments @('guard', [IO.Path]::GetPathRoot($fixtureRoot)) -ExpectedExitCode 77
    $retained = New-CleanupFixture 'WeezTail-retained'
    Invoke-FixtureAction -ActionArguments @('0', $retained, '', $retained, '')
    if (-not (Test-Path -LiteralPath (Join-Path $retained 'Data\sentinel.txt'))) { throw 'Default retention failed.' }

    $valid = New-CleanupFixture 'WeezTail-valid'
    Invoke-FixtureAction -ActionArguments @('1', $valid, '', $valid, '')
    if (Test-Path -LiteralPath (Join-Path $valid 'Data')) { throw 'Supported cleanup did not delete fixture data.' }
    if (-not (Test-Path -LiteralPath (Join-Path $valid 'unrelated.txt'))) { throw 'Supported cleanup deleted sibling data.' }
    if (Test-Path -LiteralPath (Join-Path $fixtureRoot 'Local\WeezTail\Cache')) { throw 'Current-user cache was not removed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $valid 'Cache\sentinel.txt'))) { throw 'Historical cache with uncertain ownership was deleted.' }

    $default = New-CleanupFixture 'Local\WeezTail'
    Invoke-FixtureAction -ActionArguments @('1', $default, '', $default, '')
    if ((Test-Path -LiteralPath (Join-Path $default 'Data')) -or (Test-Path -LiteralPath (Join-Path $default 'Cache'))) { throw 'Default-root data/cache cleanup failed.' }

    $unsafe = New-CleanupFixture 'unrelated'
    Invoke-FixtureAction -ActionArguments @('1', $unsafe, (Join-Path $unsafe 'unrelated.txt'), $unsafe, '')
    $bypassClosed = Test-Path -LiteralPath (Join-Path $unsafe 'Data\sentinel.txt')
    $selectionClosed = Test-Path -LiteralPath (Join-Path $unsafe 'unrelated.txt')

    $upgrade = New-CleanupFixture 'WeezTail-upgrade'
    Invoke-FixtureAction -ActionArguments @('1', $upgrade, '', $upgrade, '{FIXTURE-UPGRADE}')
    $upgradeRetained = Test-Path -LiteralPath (Join-Path $upgrade 'Data\sentinel.txt')
    foreach ($consent in @('yes', 'no')) {
        foreach ($uiLevel in @('2', '3', '5')) {
            $prompted = New-CleanupFixture "WeezTail-prompt-$consent-$uiLevel"
            Invoke-FixtureAction -ActionArguments @('prompt', '', '', $prompted, '', 'ALL', $uiLevel, $consent)
            $removed = -not (Test-Path -LiteralPath (Join-Path $prompted 'Data\sentinel.txt'))
            if ($removed -ne ($consent -eq 'yes' -and $uiLevel -eq '5')) { throw 'UI consent matrix failed.' }
        }
    }

    $invalid = New-CleanupFixture 'WeezTail-invalid-overrides'
    foreach ($override in @('.', 'C:', 'C:\', '\\server\share', '\\?\C:\data',
            ($invalid + '\..\WeezTail-invalid-overrides'), ($invalid + ':stream'), ($invalid + '.'), ($invalid + ' '))) {
        Invoke-FixtureAction -ActionArguments @('1', $override, '', $invalid, '')
        if (-not (Test-Path -LiteralPath (Join-Path $invalid 'Data\sentinel.txt'))) { throw "Unsafe override removed data: $override" }
    }
    Invoke-FixtureAction -ActionArguments @('1', $invalid, '', $invalid, '', 'MainFeature')
    if (-not (Test-Path -LiteralPath (Join-Path $invalid 'Data\sentinel.txt'))) { throw 'Partial removal deleted data.' }

    $selectionOverride = New-CleanupFixture 'WeezTail-selection-override'
    Invoke-FixtureAction -ActionArguments @('1', $selectionOverride, (Join-Path $selectionOverride 'unrelated.txt'), $selectionOverride, '')
    if (-not (Test-Path -LiteralPath (Join-Path $selectionOverride 'unrelated.txt'))) { throw 'Selection override removed unrelated data.' }

    $canonical = New-CleanupFixture 'WeezTail-canonical'
    Invoke-FixtureAction -ActionArguments @('1', ($canonical.ToUpperInvariant().Replace('\', '/') + '/'), '', $canonical, '')
    if (Test-Path -LiteralPath (Join-Path $canonical 'Data')) { throw 'Supported case/separator normalization failed.' }

    $perUser = New-CleanupFixture 'WeezTail-per-user'
    $selectionDirectory = Join-Path $fixtureRoot 'Local\WeezTailSetup'
    [IO.Directory]::CreateDirectory($selectionDirectory) | Out-Null
    $selection = Join-Path $selectionDirectory 'WeezTail.msi-user.json'
    [IO.File]::WriteAllText($selection, (@{ storageRootPath = $perUser } | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $perUser 'WeezTail.install.json'), '{"installMode":"Msi","storageMode":"PerUserChoice"}')
    Invoke-FixtureAction -ActionArguments @('1', $perUser, $selection, $perUser, '')
    if ((Test-Path -LiteralPath $selection) -or (Test-Path -LiteralPath (Join-Path $perUser 'Data'))) { throw 'Eligible per-user cleanup failed.' }

    $redirected = New-CleanupFixture 'WeezTail-redirected'
    $destination = New-CleanupFixture 'WeezTail-link-destination'
    $junction = Join-Path $redirected 'Data\redirect'
    New-Item -ItemType Junction -Path $junction -Target (Join-Path $destination 'Data') | Out-Null
    try {
        # Product validation must reject the link before the instrumented mutation
        # guard. Exit 77 here would expose a missing product check, not a pass.
        Invoke-FixtureAction -ActionArguments @('1', $redirected, '', $redirected, '')
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'Data\sentinel.txt'))) { throw 'Redirect target was modified.' }
        if (-not (Test-Path -LiteralPath (Join-Path $redirected 'Data\sentinel.txt'))) { throw 'Redirected plan was partially deleted.' }
    }
    finally {
        # Directory.Delete on the junction itself unlinks it without recursion.
        if (Test-Path -LiteralPath $junction) { [IO.Directory]::Delete($junction) }
    }
    $ancestorLink = Join-Path $fixtureRoot 'WeezTail-ancestor'
    New-Item -ItemType Junction -Path $ancestorLink -Target $destination | Out-Null
    try {
        [IO.File]::WriteAllText((Join-Path $destination 'WeezTail.install.json'),
            (@{ installMode = 'Msi'; storageMode = 'Absolute'; storageRootPath = $ancestorLink } | ConvertTo-Json))
        Invoke-FixtureAction -ActionArguments @('1', $ancestorLink, '', $ancestorLink, '')
        if (-not (Test-Path -LiteralPath (Join-Path $destination 'Data\sentinel.txt'))) { throw 'Redirected root was followed.' }
    }
    finally { [IO.Directory]::Delete($ancestorLink) }

    $currentSelectionDirectory = Join-Path $fixtureRoot 'Local\WeezTailSetup'
    $currentSelection = Join-Path $currentSelectionDirectory 'WeezTail.msi-user.json'
    $legacySelectionDirectory = Join-Path $fixtureRoot 'Local\LogReaderSetup'
    $legacySelection = Join-Path $legacySelectionDirectory 'LogReader.msi-user.json'
    [IO.Directory]::CreateDirectory($currentSelectionDirectory) | Out-Null
    [IO.Directory]::CreateDirectory($legacySelectionDirectory) | Out-Null
    [IO.File]::WriteAllText($currentSelection, '{"storageRootPath":"C:\\Current"}')
    [IO.File]::WriteAllText($legacySelection, '{"storageRootPath":"C:\\Legacy"}')
    Invoke-FixtureAction -ActionArguments @('migrate', '')
    if ([IO.File]::ReadAllText($currentSelection) -notmatch 'Current') { throw 'Migration replaced an existing current selection.' }

    Remove-Item -LiteralPath $currentSelection -Force
    Invoke-FixtureAction -ActionArguments @('migrate', '')
    if ([IO.File]::ReadAllText($currentSelection) -ne [IO.File]::ReadAllText($legacySelection)) { throw 'Legacy selection copy failed.' }

    Remove-Item -LiteralPath $currentSelection -Force
    Remove-Item -LiteralPath $legacySelection -Force
    $legacyInstall = Join-Path $fixtureRoot 'WeezTail-legacy-install'
    [IO.Directory]::CreateDirectory($legacyInstall) | Out-Null
    $legacyExecutable = Join-Path $legacyInstall 'LogReader.exe'
    [IO.File]::WriteAllText($legacyExecutable, '')
    $unicodeFolderName = 'WeezTail-migrated-' + [char]0x6e2c + [char]0x8a66
    $adoptedRoot = Join-Path $fixtureRoot $unicodeFolderName
    [IO.File]::WriteAllText(
        (Join-Path $legacyInstall 'LogReader.install.json'),
        (@{ installMode = 'Msi'; storageMode = 'Absolute'; storageRootPath = $adoptedRoot } | ConvertTo-Json -Compress))
    Invoke-FixtureAction -ActionArguments @('migrate', $legacyExecutable)
    $migrated = [IO.File]::ReadAllText($currentSelection, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($migrated.storageRootPath -ne $adoptedRoot) {
        throw "Legacy absolute-root migration failed. Expected '$adoptedRoot'; actual '$($migrated.storageRootPath)'."
    }

    Remove-Item -LiteralPath $currentSelection -Force
    Invoke-FixtureAction -ActionArguments @('migrate-rollback', $legacyExecutable)
    if (Test-Path -LiteralPath $currentSelection) { throw 'Migration rollback left the new selection behind.' }
    if (@(Get-ChildItem -LiteralPath $currentSelectionDirectory -Filter 'WeezTail.msi-user.json.migration-*').Count) {
        throw 'Migration rollback left transaction files behind.'
    }

    $legacyDefault = Join-Path $fixtureRoot 'Local\LogReader'
    [IO.Directory]::CreateDirectory($legacyDefault) | Out-Null
    Remove-Item -LiteralPath (Join-Path $legacyInstall 'LogReader.install.json') -Force
    Invoke-FixtureAction -ActionArguments @('migrate', $legacyExecutable)
    $migrated = [IO.File]::ReadAllText($currentSelection, [Text.Encoding]::UTF8) | ConvertFrom-Json
    if ($migrated.storageRootPath -ne $legacyDefault) { throw 'Legacy default-root migration failed.' }

    Remove-Item -LiteralPath $currentSelection -Force
    [IO.Directory]::Delete($legacyDefault)
    [IO.File]::WriteAllText(
        (Join-Path $legacyInstall 'LogReader.install.json'),
        '{"installMode":"Msi","storageMode":"Absolute","storageRootPath":"C:\\Windows"}')
    Invoke-FixtureAction -ActionArguments @('migrate', $legacyExecutable)
    if (Test-Path -LiteralPath $currentSelection) { throw 'Migration accepted a protected storage root.' }

    $report = [pscustomobject]@{
        FixtureContainment = 'Passed'
        DefaultRetention = 'Passed'
        SupportedCleanup = 'Passed'
        UnsafeRootRejected = $bypassClosed
        ArbitrarySelectionPreserved = $selectionClosed
        UpgradeActionRetainsData = $upgradeRetained
        InvalidOverridesPreserved = 'Passed'
        EligibleSelectionCleanup = 'Passed'
        ReparseTargetPreserved = 'Passed'
        UiConsentMatrix = 'Passed'
        CurrentUserCacheCleanup = 'Passed'
        HistoricalCachePreserved = 'Passed'
        MigrationMatrix = 'Passed'
        MigrationRollback = 'Passed'
        ProductSafetyGatePassed = ($bypassClosed -and $selectionClosed -and $upgradeRetained)
    }
    $report
    if ($RequireSafeguards -and -not $report.ProductSafetyGatePassed) { throw 'MSI-002 safeguards are not complete.' }
    if (-not $report.ProductSafetyGatePassed) { Write-Warning 'Harness passed; known MSI-002 product defects were reproduced. This is not a product-safety pass.' }
}
finally {
    # Do not recursively remove a computed directory without rechecking containment
    # and redirects. Retain evidence instead if the fixture was unexpectedly changed.
    $parent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $fixtureRoot.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture cleanup escaped TEMP.' }
    $items = @(Get-Item -LiteralPath $fixtureRoot) + @(Get-ChildItem -LiteralPath $fixtureRoot -Recurse -Force)
    if ($items.Where({ $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        Write-Warning "Fixture contains a reparse point; retained at $fixtureRoot."
    }
    else { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
}
