param(
    [string]$InstallerActionsPath = (Join-Path $PSScriptRoot '..\..\LogReader.Setup\InstallerActions.vbs'),
    [switch]$RequireSafeguards
)

$ErrorActionPreference = 'Stop'
$fixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('MsiCleanupFixture-' + [Guid]::NewGuid().ToString('N'))
$fixtureRoot = [IO.Path]::GetFullPath($fixtureRoot)
[IO.Directory]::CreateDirectory($fixtureRoot) | Out-Null
$harnessPath = Join-Path $fixtureRoot 'actions.vbs'

# Instrument the two deletion boundaries in a temporary copy. The production
# checks still execute, but even a defective action cannot delete outside fixtures.
$source = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $InstallerActionsPath))
$source = $source.Replace('MsgBox', 'FixtureMsgBox')
foreach ($call in @('fileSystem.DeleteFolder folderPath, True', 'fileSystem.DeleteFile filePath, True')) {
    if ([regex]::Matches($source, [regex]::Escape($call)).Count -ne 1) {
        throw "Deletion boundary changed; review fixture instrumentation: $call"
    }
    $argument = if ($call.Contains('DeleteFolder')) { 'folderPath' } else { 'filePath' }
    $source = $source.Replace($call, "AssertFixtureMutation $argument`r`n    $call")
}
$harness = @'

Class FixtureSession
    Private values
    Private Sub Class_Initialize()
        Set values = CreateObject("Scripting.Dictionary")
    End Sub
    Public Property Get Property(name)
        Property = ""
        If values.Exists(name) Then Property = values(name)
    End Property
    Public Property Let Property(name, value)
        values(name) = value
    End Property
    Public Sub Log(message)
        WScript.Echo message
    End Sub
End Class

Sub AssertFixtureMutation(path)
    Dim fs, fullPath, root, ancestor, folder
    Set fs = CreateObject("Scripting.FileSystemObject")
    root = fs.GetAbsolutePathName(WScript.Arguments(0)) & "\"
    fullPath = fs.GetAbsolutePathName(path)
    If StrComp(Left(fullPath, Len(root)), root, vbTextCompare) <> 0 Then WScript.Quit 77
    ancestor = fullPath
    Do While ancestor <> ""
        If fs.FolderExists(ancestor) Then
            If (fs.GetFolder(ancestor).Attributes And 1024) <> 0 Then WScript.Quit 77
        ElseIf fs.FileExists(ancestor) Then
            If (fs.GetFile(ancestor).Attributes And 1024) <> 0 Then WScript.Quit 77
        End If
        ancestor = fs.GetParentFolderName(ancestor)
    Loop
    If fs.FolderExists(fullPath) Then AssertFixtureTree fs.GetFolder(fullPath)
End Sub

Sub AssertFixtureTree(folder)
    Dim child
    If (folder.Attributes And 1024) <> 0 Then WScript.Quit 77
    For Each child In folder.Files
        If (child.Attributes And 1024) <> 0 Then WScript.Quit 77
    Next
    For Each child In folder.SubFolders
        AssertFixtureTree child
    Next
End Sub

Dim Session, result
Set Session = New FixtureSession
If WScript.Arguments(1) = "guard" Then
    AssertFixtureMutation WScript.Arguments(2)
    WScript.Quit 78
End If
Session.Property("UILevel") = "2"
Session.Property("REMOVE") = "ALL"
If WScript.Arguments.Count > 6 Then Session.Property("REMOVE") = WScript.Arguments(6)
Session.Property("REMOVELOGREADERDATA") = WScript.Arguments(1)
Session.Property("LOGREADERDATAROOT") = WScript.Arguments(2)
Session.Property("LOGREADERUSERSELECTIONPATH") = WScript.Arguments(3)
Session.Property("INSTALLFOLDER") = WScript.Arguments(4)
Session.Property("UPGRADINGPRODUCTCODE") = WScript.Arguments(5)
If WScript.Arguments(1) = "prompt" Then
    Session.Property("UILevel") = WScript.Arguments(7)
    result = PromptRemoveData()
End If
result = RemoveDataFolders()
WScript.Echo "ActionResult=" & result
If result <> msiDoActionStatusSuccess Then WScript.Quit 1

Function FixtureMsgBox(message, style, title)
    FixtureMsgBox = vbNo
    If WScript.Arguments.Count > 8 Then
        If WScript.Arguments(8) = "yes" Then FixtureMsgBox = vbYes
    End If
End Function
'@
[IO.File]::WriteAllText($harnessPath, $source + "`r`n" + $harness, [Text.UTF8Encoding]::new($false))

function Invoke-FixtureAction {
    param([string[]]$ActionArguments, [int]$ExpectedExitCode = 0)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $env:SystemRoot 'System32\cscript.exe'
    $arguments = @('//nologo', '//B', '//T:20', $harnessPath, $fixtureRoot) + $ActionArguments
    if ($arguments.Where({ $_.Contains('"') }).Count) { throw 'Unexpected quote in fixture argument.' }
    $start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
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
