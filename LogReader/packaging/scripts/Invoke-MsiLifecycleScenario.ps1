param(
    [Parameter(Mandatory = $true)][ValidateSet('Install', 'Repair', 'Upgrade', 'Uninstall')][string]$Scenario,
    [Parameter(Mandatory = $true)][string]$MsiPath,
    [Parameter(Mandatory = $true)][string]$FixtureRoot,
    [string[]]$Property = @(),
    [switch]$Execute,
    [switch]$DisposableGuest
)

$ErrorActionPreference = 'Stop'
$package = (Resolve-Path -LiteralPath $MsiPath).Path
$root = [IO.Path]::GetFullPath($FixtureRoot).TrimEnd('\')
if ($root.Length -le 3 -or $root.StartsWith('\\')) { throw 'Use a dedicated local fixture directory.' }
$log = Join-Path $root ('logs\' + $Scenario + '-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff'))
$verb = switch ($Scenario) { 'Repair' { '/fa' } 'Uninstall' { '/x' } default { '/i' } }
$arguments = @($verb, $package, '/qn', '/norestart', '/l*v', ($log + '.log')) + $Property
foreach ($value in $arguments) {
    if ($value.Contains('"') -or $value.Contains("`r") -or $value.Contains("`n")) { throw 'Invalid MSI argument.' }
}
$commandArguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$plan = [ordered]@{
    Scenario = $Scenario
    Msi = $package
    Sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
    Arguments = $commandArguments
    FixtureRoot = $root
    Executed = $false
}
if (-not $Execute) { return [pscustomobject]$plan }
if (-not $DisposableGuest) { throw 'Execution requires explicit -DisposableGuest in a disposable VM.' }
$machine = Get-CimInstance Win32_ComputerSystem
if (($machine.Model + ' ' + $machine.Manufacturer) -notmatch 'Virtual Machine|VMware|VirtualBox|QEMU|KVM|Parallels') {
    throw 'Refusing lifecycle mutation outside a recognized virtual machine.'
}
if (-not (Test-Path -LiteralPath (Join-Path $root '.disposable-msi-fixture') -PathType Leaf)) {
    throw 'Create .disposable-msi-fixture in the dedicated guest fixture root after taking a VM snapshot.'
}
$ancestor = $root
while ($ancestor) {
    if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Fixture root has a redirected ancestor.'
    }
    $ancestor = Split-Path -Parent $ancestor
}
[IO.Directory]::CreateDirectory((Split-Path -Parent $log)) | Out-Null
function Get-FixtureManifest {
    $items = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
    if ($items.Where({ $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw 'Use the dedicated redirected-path scenario before scanning a fixture containing reparse points.'
    }
    @($items | Where-Object { -not $_.PSIsContainer -and -not $_.FullName.StartsWith((Join-Path $root 'logs') + '\') } |
        ForEach-Object { [pscustomobject]@{ Path = $_.FullName.Substring($root.Length); Length = $_.Length; Sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash } })
}
$plan['Before'] = @(Get-FixtureManifest)
$plan['Machine'] = $machine.Model
$plan['Windows'] = [Environment]::OSVersion.VersionString
$plan['User'] = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$plan['StartedUtc'] = [DateTime]::UtcNow.ToString('o')
$plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($log + '.json') -Encoding UTF8
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $env:SystemRoot 'System32\msiexec.exe'
$start.Arguments = $commandArguments
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    $plan['Executed'] = $true
    $plan['ExitCode'] = $process.ExitCode
    $plan['RebootRequired'] = $process.ExitCode -in @(1641, 3010)
    $plan['After'] = @(Get-FixtureManifest)
    $plan['FinishedUtc'] = [DateTime]::UtcNow.ToString('o')
    $plan | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath ($log + '.json') -Encoding UTF8
    [pscustomobject]$plan
    # Expected injected failures are evidence, not successful MSI outcomes.
    if ($process.ExitCode -notin @(0, 1641, 3010)) { throw "MSI returned $($process.ExitCode); inspect $log.log and $log.json." }
}
finally { $process.Dispose() }
