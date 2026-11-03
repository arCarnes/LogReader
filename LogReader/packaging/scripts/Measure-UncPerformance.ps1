param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Prepare', 'Baseline', 'Compare')]
    [string]$Mode,
    [Parameter(Mandatory = $true)]
    [string]$RunId,
    [string]$FixtureId,
    [string]$BaselineDirectory,
    [string]$CandidateDirectory,
    [ValidateSet('Components', 'Mcp', 'Ui')]
    [string]$Workflow = 'Components',
    [string]$McpExecutable,
    [string]$CandidateMcpExecutable,
    [switch]$IncludeUnc
)

$ErrorActionPreference = 'Stop'
if ($RunId -notmatch '^[a-zA-Z0-9-]+$' -or ($FixtureId -and $FixtureId -notmatch '^[a-zA-Z0-9-]+$')) {
    throw 'Run and fixture IDs must contain only letters, digits and hyphens.'
}
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..'))
$artifactRoot = Join-Path $repository 'artifacts\measurements\unc-performance'
$runRoot = Join-Path $artifactRoot $RunId
if (Test-Path -LiteralPath $runRoot) { throw 'Use a new run ID; measurement evidence cannot be overwritten.' }
$harnessProject = Join-Path $repository 'LogReader\LogReader.Core.Tests\UncPerformanceHarness\UncPerformanceHarness.csproj'
$utf8 = [Text.UTF8Encoding]::new($false)
$harnessExecutable = if ($Workflow -eq 'Ui') { 'LogReader.Tests.exe' } else { 'LogReader.Core.Tests.exe' }

function Write-Json([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 30), $utf8)
}
function Invoke-Harness([string]$Directory, [string[]]$Arguments) {
    # Direct, awaited foreground invocation: fresh process per sample, no startup sleeps.
    & (Join-Path $Directory $harnessExecutable) @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Measurement process failed with exit code $LASTEXITCODE." }
}
function Get-LocalBundle([string]$Directory) {
    if ([string]::IsNullOrWhiteSpace($Directory)) { throw 'A preserved harness output directory is required.' }
    $fullPath = [IO.Path]::GetFullPath($Directory)
    if ($fullPath.StartsWith('\\')) { throw 'Executable bundles must be local.' }
    if (-not (Test-Path -LiteralPath (Join-Path $fullPath $harnessExecutable))) { throw 'Harness executable is missing.' }
    return $fullPath
}

if ($Mode -eq 'Prepare') {
    if ($Workflow -ne 'Components') { throw 'Prepare fixtures with the Components workflow before measuring UI or MCP.' }
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    $bundle = Join-Path $runRoot 'baseline-components'
    & dotnet publish $harnessProject -c Release -m:1 -p:UseSharedCompilation=false -o $bundle
    if ($LASTEXITCODE -ne 0) { throw 'Harness build/publish failed.' }
    $head = & git -C $repository rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Cannot identify baseline HEAD.' }
    $status = @(& git -C $repository status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot capture working tree status.' }
    Write-Json (Join-Path $runRoot 'build.json') ([ordered]@{
        head = $head; workingTree = $status; sdk = (& dotnet --version)
        configuration = 'Release'; serialMSBuild = $true; sharedCompilation = $false
        note = 'Preserved component output; runtime changes require separate candidate output. Full UI/MCP baseline artifacts and entry-point acceptance remain separate.'
        artifacts = @(Get-ChildItem -LiteralPath $bundle -File | Sort-Object Name | ForEach-Object {
            [ordered]@{ name = $_.Name; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        })
    })
    Invoke-Harness $bundle @('prepare', (Join-Path $runRoot 'fixtures'), $(if ($IncludeUnc) { 'unc' } else { 'local' }))
    return
}

if (-not $FixtureId) { throw 'Baseline/Compare require the ID of a previously prepared fixture.' }
$manifest = Join-Path (Join-Path $artifactRoot $FixtureId) 'fixtures\manifest.private.json'
if (-not (Test-Path -LiteralPath $manifest)) { throw 'Private fixture manifest is missing.' }
$baseline = Get-LocalBundle $BaselineDirectory
$candidate = if ($Mode -eq 'Compare') { Get-LocalBundle $CandidateDirectory } else { $null }
if ($Workflow -eq 'Mcp') {
    if (-not $McpExecutable) { throw 'MCP workflow requires a preserved local McpExecutable.' }
    $McpExecutable = [IO.Path]::GetFullPath($McpExecutable)
    if ($Mode -eq 'Compare' -and -not $CandidateMcpExecutable) { throw 'MCP comparison requires CandidateMcpExecutable.' }
    if ($CandidateMcpExecutable) { $CandidateMcpExecutable = [IO.Path]::GetFullPath($CandidateMcpExecutable) }
    if ($Mode -eq 'Compare') {
        foreach ($name in @('LogReader.Core.Tests.dll', 'LogReader.Core.dll', 'LogReader.Infrastructure.dll', 'LogReader.Core.Tests.runtimeconfig.json')) {
            if ((Get-FileHash -LiteralPath (Join-Path $baseline $name) -Algorithm SHA256).Hash -cne
                (Get-FileHash -LiteralPath (Join-Path $candidate $name) -Algorithm SHA256).Hash) {
                throw 'MCP comparison requires identical measurement bundles; vary only the server executables.'
            }
        }
    }
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$rows = [Collections.Generic.List[object]]::new()
$processSamples = 0
for ($session = 1; $session -le 2; $session++) {
    for ($pair = 1; $pair -le 7; $pair++) {
        $variants = if ($Mode -eq 'Baseline') { @('A') } elseif ($pair % 2 -eq 1) { @('A', 'B') } else { @('B', 'A') }
        $reports = @{}
        foreach ($variant in $variants) {
            $reportPath = Join-Path $runRoot ("session-{0}-pair-{1}-{2}.json" -f $session, $pair, $variant)
            $arguments = if ($Workflow -eq 'Mcp') {
                @('mcp-sample', $manifest, $reportPath, $(if ($variant -eq 'A') { $McpExecutable } else { $CandidateMcpExecutable }))
            } elseif ($Workflow -eq 'Ui') { @('ui-sample', $manifest, $reportPath) }
            else { @('sample', $manifest, $reportPath) }
            Invoke-Harness $(if ($variant -eq 'A') { $baseline } else { $candidate }) $arguments
            $processSamples++
            $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
            if (@($report.samples).Count -eq 0) { throw 'Zero samples cannot pass.' }
            $reports[$variant] = $report
            foreach ($sample in $report.samples) {
                $rows.Add([ordered]@{ session = $session; pair = $pair; variant = $variant; sample = $sample })
            }
        }
        if ($Mode -eq 'Compare') {
            if ($Workflow -eq 'Ui' -and $reports.A.measurementRecipe -cne $reports.B.measurementRecipe) {
                throw 'UI measurement recipes differ between paired variants.'
            }
            if ($Workflow -eq 'Mcp' -and ($reports.A.toolSchemaSha256 -cne $reports.B.toolSchemaSha256 -or
                ($reports.A.limits | ConvertTo-Json -Depth 10 -Compress) -cne ($reports.B.limits | ConvertTo-Json -Depth 10 -Compress))) {
                throw 'MCP public schemas or effective limits changed between paired variants.'
            }
            if ($reports.A.manifestHash -cne $reports.B.manifestHash -or @($reports.A.samples).Count -ne @($reports.B.samples).Count) {
                throw 'Paired reports used different fixtures or scenario counts.'
            }
            for ($i = 0; $i -lt @($reports.A.samples).Count; $i++) {
                $a = $reports.A.samples[$i]
                $b = $reports.B.samples[$i]
                if ($a.Fixture -cne $b.Fixture -or $a.Operation -cne $b.Operation -or $a.Units -ne $b.Units -or
                    $a.ExactResultSha256 -cne $b.ExactResultSha256 -or $a.SourceBytes -ne $b.SourceBytes) {
                    throw 'Paired exact results differ; performance cannot excuse a correctness failure.'
                }
            }
        }
    }
}
function Get-Median([double[]]$Values) {
    $ordered = @($Values | Sort-Object)
    if ($ordered.Count -eq 0) { throw 'No samples.' }
    return $ordered[[int][Math]::Floor($ordered.Count / 2)]
}
$summary = @($rows | Group-Object { "$($_.session)|$($_.sample.Fixture)|$($_.sample.Operation)" } | ForEach-Object {
    $group = $_.Group
    $a = @($group | Where-Object variant -eq 'A' | Sort-Object pair)
    $b = @($group | Where-Object variant -eq 'B' | Sort-Object pair)
    if ($a.Count -ne 7 -or ($Mode -eq 'Compare' -and $b.Count -ne 7)) { throw 'A session requires seven complete samples per variant.' }
    $aMedian = Get-Median @($a | ForEach-Object { $_.sample.Milliseconds })
    $bMedian = if ($b.Count) { Get-Median @($b | ForEach-Object { $_.sample.Milliseconds }) } else { $null }
    [ordered]@{
        session = $a[0].session; fixture = $a[0].sample.Fixture; operation = $a[0].sample.Operation
        baselineMedianMs = $aMedian
        baselineMinMs = ($a.sample.Milliseconds | Measure-Object -Minimum).Minimum
        baselineMaxMs = ($a.sample.Milliseconds | Measure-Object -Maximum).Maximum
        candidateMedianMs = $bMedian
        candidateMinMs = if ($b.Count) { ($b.sample.Milliseconds | Measure-Object -Minimum).Minimum } else { $null }
        candidateMaxMs = if ($b.Count) { ($b.sample.Milliseconds | Measure-Object -Maximum).Maximum } else { $null }
        pairedMedianRatio = if ($b.Count) { Get-Median @(0..6 | ForEach-Object { $b[$_].sample.Milliseconds / $a[$_].sample.Milliseconds }) } else { $null }
        exactUnits = $a[0].sample.Units; sourceBytes = $a[0].sample.SourceBytes
    }
})
Write-Json (Join-Path $runRoot 'summary.json') ([ordered]@{
    schemaVersion = 1; mode = $Mode; workflow = $Workflow; cache = 'warm/cache-uncontrolled'; sessions = 2; pairsPerSession = 7
    processSamples = $processSamples
    note = 'Workflow is recorded explicitly. Candidate acceptance also requires protected workflows, ownership/resource and desktop checks.'
    summary = $summary
})
Write-Output ("Recorded {0} {1} measurements in {2}." -f $rows.Count, $Workflow, $RunId)
