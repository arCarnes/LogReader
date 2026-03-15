param(
    [Parameter(Mandatory = $true)]
    [string]$TargetPath,

    [Parameter(Mandatory = $true)]
    [string]$ProcessName
)

$resolvedTargetPath = [System.IO.Path]::GetFullPath($TargetPath)
$processes = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue)

foreach ($process in $processes) {
    try {
        $processPath = $process.Path
    }
    catch {
        continue
    }

    if (-not $processPath) {
        continue
    }

    if ([System.IO.Path]::GetFullPath($processPath) -ne $resolvedTargetPath) {
        continue
    }

    try {
        Stop-Process -Id $process.Id -Force -ErrorAction Stop
    }
    catch [System.InvalidOperationException] {
        # The process may exit between enumeration and the stop attempt.
    }
}
