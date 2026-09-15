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
Session.Property("REMOVELOGREADERDATA") = WScript.Arguments(1)
Session.Property("LOGREADERDATAROOT") = WScript.Arguments(2)
Session.Property("LOGREADERUSERSELECTIONPATH") = WScript.Arguments(3)
Session.Property("INSTALLFOLDER") = WScript.Arguments(4)
Session.Property("UPGRADINGPRODUCTCODE") = WScript.Arguments(5)
result = RemoveDataFolders()
WScript.Echo "ActionResult=" & result
If result <> msiDoActionStatusSuccess Then WScript.Quit 1
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

    $unsafe = New-CleanupFixture 'unrelated'
    Invoke-FixtureAction -ActionArguments @('1', $unsafe, (Join-Path $unsafe 'unrelated.txt'), $unsafe, '')
    $bypassClosed = Test-Path -LiteralPath (Join-Path $unsafe 'Data\sentinel.txt')
    $selectionClosed = Test-Path -LiteralPath (Join-Path $unsafe 'unrelated.txt')

    $upgrade = New-CleanupFixture 'WeezTail-upgrade'
    Invoke-FixtureAction -ActionArguments @('1', $upgrade, '', $upgrade, '{FIXTURE-UPGRADE}')
    $upgradeRetained = Test-Path -LiteralPath (Join-Path $upgrade 'Data\sentinel.txt')
    $report = [pscustomobject]@{
        FixtureContainment = 'Passed'
        DefaultRetention = 'Passed'
        SupportedCleanup = 'Passed'
        UnsafeRootRejected = $bypassClosed
        ArbitrarySelectionPreserved = $selectionClosed
        UpgradeActionRetainsData = $upgradeRetained
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
