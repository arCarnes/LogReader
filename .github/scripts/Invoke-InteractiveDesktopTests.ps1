param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repositoryRoot "LogReader\LogReader.Tests\LogReader.Tests.csproj"
$resultsDirectory = Join-Path $repositoryRoot ("artifacts\test-results\interactive-desktop\" + [Guid]::NewGuid().ToString("N"))

& dotnet test $projectPath -c $Configuration --no-build --no-restore -m:1 `
    --filter "Category=InteractiveDesktop" --logger trx --results-directory $resultsDirectory
if ($LASTEXITCODE -ne 0) {
    throw "Interactive desktop tests failed. Results: $resultsDirectory"
}

$results = @(Get-ChildItem -LiteralPath $resultsDirectory -Filter *.trx)
if ($results.Count -eq 0) { throw "No interactive desktop test results were produced." }
$executed = 0
$passed = 0
$failed = 0
$notExecuted = 0
foreach ($result in $results) {
    [xml]$report = Get-Content -LiteralPath $result.FullName -Raw
    $counters = $report.TestRun.ResultSummary.Counters
    $executed += [int]$counters.executed
    $passed += [int]$counters.passed
    $failed += [int]$counters.failed
    $notExecuted += [int]$counters.notExecuted
}
if ($executed -eq 0 -or $passed -ne $executed -or $failed -ne 0 -or $notExecuted -ne 0) {
    throw "Interactive lane requires actual passing tests: executed=$executed passed=$passed failed=$failed notExecuted=$notExecuted. Results: $resultsDirectory"
}
Write-Output "Interactive desktop lane: $passed/$executed passed, none skipped. Results: $resultsDirectory"
