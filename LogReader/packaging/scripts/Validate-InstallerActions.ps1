param(
    [string]$InstallerActionsPath
)

$ErrorActionPreference = "Stop"

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot

if ([string]::IsNullOrWhiteSpace($InstallerActionsPath)) {
    $InstallerActionsPath = Join-Path $productRoot "LogReader.Setup\InstallerActions.vbs"
}

if (-not (Test-Path $InstallerActionsPath)) {
    throw "Installer actions script not found at '$InstallerActionsPath'."
}

$harnessPath = Join-Path ([System.IO.Path]::GetTempPath()) (
    "WeezTail.InstallerActions.{0}.vbs" -f [Guid]::NewGuid().ToString("N"))
$harness = @'
Dim actual
Dim expected

expected = "C:\Logs\" & ChrW(&H6E2C) & ChrW(&H8A66) & "-" & ChrW(&H0394)
actual = ExtractJsonStringValue( _
    "{""storageRootPath"":""C:\\Logs\\\u6E2C\u8A66-\u0394""}", _
    "storageRootPath")
AssertEqual expected, actual, "Unicode escape decoding"

expected = "C:\Literal\u6E2C"
actual = ExtractJsonStringValue( _
    "{""storageRootPath"":""C:\\Literal\\u6E2C""}", _
    "storageRootPath")
AssertEqual expected, actual, "Escaped literal Unicode sequence"

expected = "C:\Emoji\" & ChrW(-10179) & ChrW(-8576)
actual = ExtractJsonStringValue( _
    "{""storageRootPath"":""C:\\Emoji\\\uD83D\uDE80""}", _
    "storageRootPath")
AssertEqual expected, actual, "Surrogate-pair decoding"

actual = ExtractJsonStringValue( _
    "{""storageRootPath"":""C:\\Invalid\\\u12G4""}", _
    "storageRootPath")
AssertEqual "", actual, "Malformed Unicode escape rejection"

WScript.Echo "Installer action JSON parsing validated."

Sub AssertEqual(expectedValue, actualValue, scenario)
    If StrComp(expectedValue, actualValue, vbBinaryCompare) <> 0 Then
        WScript.Echo scenario & " failed."
        WScript.Quit 1
    End If
End Sub
'@

try {
    $source = [System.IO.File]::ReadAllText($InstallerActionsPath)
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText(
        $harnessPath,
        $source + [Environment]::NewLine + $harness,
        $utf8WithoutBom)

    & cscript.exe "//nologo" $harnessPath
    if ($LASTEXITCODE -ne 0) {
        throw "Installer action validation failed."
    }
}
finally {
    Remove-Item -LiteralPath $harnessPath -Force -ErrorAction SilentlyContinue
}
