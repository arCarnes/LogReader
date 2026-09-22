param(
    [string]$HarnessPath
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$packagingRoot = Split-Path -Parent $scriptRoot
$productRoot = Split-Path -Parent $packagingRoot
if ([string]::IsNullOrWhiteSpace($HarnessPath)) {
    $HarnessPath = Join-Path $productRoot 'artifacts\installer-actions\InstallerActionsHarness.exe'
}
if (-not (Test-Path -LiteralPath $HarnessPath)) {
    & (Join-Path $scriptRoot 'Build-InstallerActions.ps1') | Out-Host
}
if (-not (Test-Path -LiteralPath $HarnessPath)) {
    throw "Compiled installer action harness not found at '$HarnessPath'."
}

& $HarnessPath selftest-json
if ($LASTEXITCODE -ne 0) {
    throw "Compiled installer action validation failed with exit code $LASTEXITCODE."
}
