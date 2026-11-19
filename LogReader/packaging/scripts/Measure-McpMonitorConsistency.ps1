# Private helper for Measure-McpLogServer.ps1. Uses its bounded protocol/schema measurement functions.
function Invoke-MonitorConsistencyMeasurement {
    param(
        [System.Diagnostics.Process]$Process,
        [string[]]$FileIds,
        [string]$LogDirectory,
        [int]$Timeout,
        [System.Text.Encoding]$Encoding,
        [bool]$UsesUnc = $false
    )
    if ($FileIds.Count -gt 100) { throw 'Monitor consistency measurements support at most 100 physical files.' }
    $state = @{ requestId = 10000; cursor = $null; rows = [System.Collections.Generic.List[object]]::new() }
    function Invoke-MonitorSample([string]$Scenario, [object]$Arguments) {
        $state.requestId++
        $tool = if ($Arguments.Contains('targets')) { 'start_log_monitor' } elseif ($Arguments.Contains('handoffCursor')) { 'start_log_monitor_from_snapshot' } elseif ($Arguments.release) { 'stop_log_monitor' } else { 'poll_log_monitor' }
        if ($Arguments.Contains('release')) { $Arguments.Remove('release') }
        $sample = Invoke-ToolMeasurement $Process $state.requestId $tool $Arguments $Timeout
        $result = $sample.Response.result.structuredContent.data
        if ($null -eq $result) { throw "Monitor scenario $Scenario returned no result." }
        $state.rows.Add([ordered]@{
            scenario = $Scenario; milliseconds = $sample.Milliseconds
            protocolResponseBytes = $sample.ProtocolResponseBytes; structuredResponseBytes = $sample.StructuredResponseBytes
            workingSetBytes = $sample.WorkingSetBytes; privateBytes = $sample.PrivateBytes
            checkedFiles = $result.checkedFileCount; uncheckedFiles = $result.uncheckedFileCount
            backlogFiles = $result.backlogFileCount; bytesEvaluated = $result.bytesEvaluated
            examinedLines = $result.examinedLineCount; isScopeChecked = $result.isScopeChecked
            isCaughtUp = $result.isCaughtUp; isIdle = $result.isIdle; state = $result.state
            eventCount = @($result.files.events | Where-Object { $null -ne $_ }).Count
        })
        return $sample
    }
    function Invoke-MonitorRound([string]$Scenario, [bool]$ReplayFirst = $false) {
        $events = [System.Collections.Generic.List[object]]::new()
        $checked = 0
        for ($poll = 0; $poll -lt 100; $poll++) {
            $submitted = $state.cursor
            $sample = Invoke-MonitorSample $Scenario @{ cursor = $submitted; maxFiles = 10; timeoutMilliseconds = $Timeout }
            $result = $sample.Response.result.structuredContent.data
            $checked += $result.checkedFileCount
            foreach ($file in $result.files) {
                foreach ($event in $file.events) { $events.Add(@{ fileId = $file.fileId; generation = $file.generation; event = $event }) }
            }
            if ($ReplayFirst -and $poll -eq 0) {
                $replay = Invoke-MonitorSample 'replay' @{ cursor = $submitted; maxFiles = 10; timeoutMilliseconds = $Timeout }
                if (($sample.Response.result.structuredContent | ConvertTo-Json -Depth 100 -Compress) -cne
                    ($replay.Response.result.structuredContent | ConvertTo-Json -Depth 100 -Compress)) {
                    throw 'Monitor replay changed its committed batch.'
                }
            }
            $state.cursor = $sample.Response.result.structuredContent.continuation.cursor
            if ($result.isScopeChecked) {
                if ($checked -ne $FileIds.Count) { throw 'A monitor round did not check every deduplicated file exactly once.' }
                return @{ result = $result; events = $events.ToArray() }
            }
            if ($result.isIdle -or $result.isCaughtUp) { throw 'Unchecked scope falsely reported idle/caught-up.' }
        }
        throw 'Monitor round exceeded its bounded fixture poll allowance.'
    }
    function Add-MonitorLines([string]$Text, [int]$FileIndex) {
        [IO.File]::AppendAllText((Join-Path $LogDirectory ('measurement-{0:D3}.log' -f $FileIndex)), $Text, $Encoding)
    }

    $initial = Invoke-MonitorSample 'initialize' @{
        targets = @(@{ kind = 'folder'; id = 'measurement-folder' },
            @{ kind = 'dashboard'; id = 'measurement-dashboard' }, @{ kind = 'logFile'; id = $FileIds[0] })
        query = 'monitor-needle'; excludeQuery = 'drop'; timeoutMilliseconds = $Timeout
    }
    $initialResult = $initial.Response.result.structuredContent.data
    if ($initialResult.selectedFileCount -ne $FileIds.Count -or @($initialResult.files.events | Where-Object { $null -ne $_ }).Count -ne 0) {
        throw 'Monitor initialization did not deduplicate configured routes or unexpectedly returned history.'
    }
    $state.cursor = $initial.Response.result.structuredContent.continuation.cursor
    $idle = Invoke-MonitorRound 'idle'
    if (-not $idle.result.isIdle) { throw 'A stable checked scope did not become idle.' }

    for ($index = 0; $index -lt $FileIds.Count; $index++) { Add-MonitorLines "monitor-other`n" $index }
    $nonmatch = Invoke-MonitorRound 'nonmatching_append'
    if ($nonmatch.events.Count -ne 0 -or $nonmatch.result.isIdle) { throw 'A nonmatching append was mistaken for idle or emitted matches.' }
    $settled = Invoke-MonitorRound 'idle_after_nonmatch'
    if (-not $settled.result.isIdle) { throw 'Nonmatch advancement did not settle.' }

    for ($index = 0; $index -lt $FileIds.Count; $index++) { Add-MonitorLines "monitor-needle $index`n" $index }
    $matching = Invoke-MonitorRound 'matching_append' $true
    if ($matching.events.Count -ne $FileIds.Count) { throw 'Matching monitor events were lost or duplicated.' }
    $identities = @($matching.events | ForEach-Object { "$($_.fileId):$($_.generation):$($_.event.byteOffset)" } | Sort-Object -Unique)
    if ($identities.Count -ne $FileIds.Count) { throw 'Monitor event identities were not unique.' }

    $hotQuietVerified = $null
    if ($FileIds.Count -gt 1) {
        Add-MonitorLines (('x' * 600000) + "`n") 0
        Add-MonitorLines "monitor-needle quiet`n" ($FileIds.Count - 1)
        $mixed = Invoke-MonitorRound 'hot_quiet_first_round'
        if ($mixed.events.Count -ne 1 -or $mixed.result.backlogFileCount -ne 1 -or $mixed.result.isCaughtUp) {
            throw 'Hot-file backlog starved the quiet file or falsely reported caught-up.'
        }
        $hotQuietVerified = $true
        for ($round = 0; $round -lt 4 -and $mixed.result.backlogFileCount -gt 0; $round++) { $mixed = Invoke-MonitorRound 'hot_file_drain' }
        if (-not $mixed.result.isCaughtUp) { throw 'Bounded hot-file polls did not drain.' }
    }
    $released = Invoke-MonitorSample 'release' @{ cursor = $state.cursor; release = $true }
    if ($released.Response.result.structuredContent.data.state -ne 'released') { throw 'Monitor release was not terminal.' }

    function Complete-MonitorSnapshot([object]$Sample, [int]$RequestIdBase) {
        $pages = 1
        $envelope = $Sample.Response.result.structuredContent
        while ($null -ne $envelope.continuation -and $pages -lt 20) {
            $arguments = @{ cursor = $envelope.continuation.cursor; timeoutMilliseconds = $Timeout }
            $pages++
            $Sample = Invoke-ToolMeasurement $Process ($RequestIdBase + $pages) $envelope.continuation.tool $arguments $Timeout
            $envelope = $Sample.Response.result.structuredContent
        }
        if ($null -ne $envelope.continuation) { throw 'Snapshot exceeded its bounded fixture page allowance.' }
        return @{ envelope = $envelope; pages = $pages }
    }

    # A continuation excludes appended bytes while retaining the growth caveat.
    Add-MonitorLines "handoff-needle one`nhandoff-needle two`nsource trailing evidence`n" 0
    $searchArguments = @{
        targets = @(@{ kind = 'logFile'; id = $FileIds[0] }); query = 'handoff-needle'
        enableHandoff = $true; maxTotalHits = 1; maxHitsPerFile = 1; timeoutMilliseconds = $Timeout
    }
    $source = Invoke-ToolMeasurement $Process 11000 'search_logs' $searchArguments $Timeout
    if ([string]::IsNullOrEmpty($source.Response.result.structuredContent.continuation.cursor)) { throw 'Snapshot fixture did not establish a continuation before the append.' }
    Add-MonitorLines "handoff-needle after frozen extent`n" 0
    $growth = Complete-MonitorSnapshot $source 11000
    $growthEnvelope = $growth.envelope
    $snapshot = $growthEnvelope.data
    if ($snapshot.matchingLineCount -ne 2 -or -not $snapshot.isTraversalComplete -or $snapshot.scanComplete -or
        $growthEnvelope.status -ne 'partial' -or $growthEnvelope.issues.code -notcontains 'file_grew_during_scan' -or
        $null -ne $growthEnvelope.continuation -or $null -ne $snapshot.handoffCursor) {
        throw "Snapshot extent/terminal uncertainty differed: matches=$($snapshot.matchingLineCount), traversal=$($snapshot.isTraversalComplete), exact=$($snapshot.scanComplete), reasons=$($snapshot.incompleteReasons -join ',')."
    }

    # Finish an independent stable snapshot before writing the first observation event.
    Add-MonitorLines "verified-needle before handoff`n" 0
    $verified = Invoke-ToolMeasurement $Process 11100 'search_logs' @{
        targets = $searchArguments.targets; query = 'verified-needle'; enableHandoff = $true; timeoutMilliseconds = $Timeout
    } $Timeout
    $boundary = (Complete-MonitorSnapshot $verified 11100).envelope.data
    if (-not $boundary.scanComplete -or $boundary.matchingLineCount -ne 1 -or [string]::IsNullOrEmpty($boundary.handoffCursor)) {
        throw 'Stable snapshot did not return a verified handoff boundary.'
    }
    Add-MonitorLines "verified-needle after frozen extent`n" 0
    $handoff = Invoke-MonitorSample 'explicit_handoff' @{ handoffCursor = $boundary.handoffCursor; timeoutMilliseconds = $Timeout }
    $handoffResult = $handoff.Response.result.structuredContent.data
    $handoffEvents = @($handoffResult.files.events | Where-Object { $null -ne $_ })
    if ($handoffEvents.Count -ne 1 -or $handoffEvents[0].text -ne 'verified-needle after frozen extent') {
        throw 'Explicit handoff did not deliver exactly the post-snapshot append.'
    }
    $null = Invoke-MonitorSample 'handoff_release' @{ cursor = $handoff.Response.result.structuredContent.continuation.cursor; release = $true }
    return [ordered]@{
        physicalFiles = $FileIds.Count; duplicateRoutesDeduplicated = $true; replayVerified = $true
        hotQuietVerified = $hotQuietVerified; frozenExtentAndGrowthUncertaintyVerified = $true
        explicitHandoffVerified = $true; sourcePages = $growth.pages; rows = $state.rows.ToArray()
        unc = $(if ($UsesUnc) { 'Real UNC synthetic fixture under the supplied test root; parent/sibling directories were not enumerated.' } else { 'Local fixture; real UNC evidence is recorded in separate supplied-root runs.' })
    }
}
