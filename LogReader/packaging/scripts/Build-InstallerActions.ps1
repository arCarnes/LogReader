param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'

if ($Configuration -ne 'Release') {
    throw "Installer actions currently support only the statically linked Release build."
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$productRoot = Split-Path -Parent (Split-Path -Parent $scriptRoot)
$sourceRoot = Join-Path $productRoot 'LogReader.SetupActions'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $productRoot 'artifacts\installer-actions'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw 'Visual Studio Installer discovery tool (vswhere.exe) was not found.'
}
$visualStudioRoot = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath | Select-Object -First 1)
if ([string]::IsNullOrWhiteSpace($visualStudioRoot)) {
    throw 'Visual Studio 2022 C++ x64 build tools are required to build installer actions.'
}
$devShellModule = Join-Path $visualStudioRoot 'Common7\Tools\Microsoft.VisualStudio.DevShell.dll'
if (-not (Test-Path -LiteralPath $devShellModule)) {
    throw "Visual Studio Developer PowerShell module was not found under '$visualStudioRoot'."
}

Import-Module $devShellModule
Enter-VsDevShell -VsInstallPath $visualStudioRoot -SkipAutomaticLocation -Arch amd64 -HostArch amd64
$compiler = (Get-Command cl.exe -ErrorAction Stop).Source
$dependencyTool = (Get-Command dumpbin.exe -ErrorAction Stop).Source

$commonArguments = @(
    '/nologo',
    '/std:c++20',
    '/O2',
    '/W4',
    '/WX',
    '/EHsc',
    '/GR-',
    '/MT',
    '/DUNICODE',
    '/D_UNICODE',
    '/DWIN32_LEAN_AND_MEAN',
    '/DNOMINMAX'
)
$coreSource = Join-Path $sourceRoot 'InstallerActionsCore.cpp'
$dllSource = Join-Path $sourceRoot 'InstallerActions.cpp'
$harnessSource = Join-Path $sourceRoot 'InstallerActionsHarness.cpp'
$dllPath = Join-Path $OutputDirectory 'InstallerActions.dll'
$harnessPath = Join-Path $OutputDirectory 'InstallerActionsHarness.exe'

Push-Location $OutputDirectory
try {
    & $compiler @commonArguments '/LD' $coreSource $dllSource "/Fe:$dllPath" '/link' '/NOLOGO' '/INCREMENTAL:NO'
    if ($LASTEXITCODE -ne 0) {
        throw "Native installer action DLL compilation failed with exit code $LASTEXITCODE."
    }

    & $compiler @commonArguments $coreSource $harnessSource "/Fe:$harnessPath" '/link' '/NOLOGO' '/INCREMENTAL:NO'
    if ($LASTEXITCODE -ne 0) {
        throw "Native installer action harness compilation failed with exit code $LASTEXITCODE."
    }

    $inspection = (& $dependencyTool /headers /dependents /exports $dllPath | Out-String)
    if ($LASTEXITCODE -ne 0) {
        throw "Native installer action dependency inspection failed with exit code $LASTEXITCODE."
    }
}
finally {
    Pop-Location
}

if ($inspection -notmatch '8664 machine \(x64\)') {
    throw 'InstallerActions.dll is not an x64 PE image.'
}
foreach ($export in @(
        'CaptureLegacyStorageSelection',
        'ApplyLegacyStorageSelection',
        'RollbackLegacyStorageSelection',
        'CommitLegacyStorageSelection',
        'PromptRemoveData',
        'RemoveDataFolders')) {
    if ($inspection -notmatch "(?m)^\s+\d+\s+\w+\s+[0-9A-F]+\s+$export\s*$") {
        throw "InstallerActions.dll is missing the undecorated '$export' export."
    }
}
foreach ($forbiddenDependency in @('VCRUNTIME', 'MSVCP', 'ucrtbase', 'hostfxr', 'hostpolicy', 'coreclr')) {
    if ($inspection -match [regex]::Escape($forbiddenDependency)) {
        throw "InstallerActions.dll unexpectedly depends on '$forbiddenDependency'."
    }
}

$inspectionPath = Join-Path $OutputDirectory 'InstallerActions.dependencies.txt'
[IO.File]::WriteAllText($inspectionPath, $inspection, [Text.UTF8Encoding]::new($false))

[pscustomobject]@{
    Compiler = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    Dll = $dllPath
    Harness = $harnessPath
    DependencyEvidence = $inspectionPath
}
