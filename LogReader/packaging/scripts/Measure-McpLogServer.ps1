param(
    [string]$ExecutablePath = ".\artifacts\publish\Portable\WeezTail.Mcp.exe",
    [ValidateRange(1, 2000)]
    [int]$FileCount = 10,
    [ValidateRange(100, 500000)]
    [int]$LinesPerFile = 10000,
    [ValidateRange(1, 30000)]
    [int]$TimeoutMilliseconds = 30000,
    [ValidateNotNullOrEmpty()]
    [string]$SearchQuery = "needle",
    [switch]$IncludeStatistics
)

$ErrorActionPreference = "Stop"
$script:responseWireBytes = @{}
$utf8 = New-Object System.Text.UTF8Encoding($false)
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = Join-Path $productRoot "artifacts\measurements"
$runName = "mcp-headless-{0}files-{1}" -f (
    $FileCount,
    [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss-fff")
)
$runRoot = Join-Path $artifactRoot $runName
$dataDirectory = Join-Path $runRoot "Data"
$cacheDirectory = Join-Path $runRoot "Cache"
$logDirectory = Join-Path $runRoot "Logs"
$copiedExecutable = Join-Path $runRoot "WeezTail.Mcp.exe"
$sourceExecutable = (Resolve-Path $ExecutablePath).Path

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Value
    )

    $json = $Value | ConvertTo-Json -Depth 20
    [System.IO.File]::WriteAllText($Path, $json, $utf8)
}

function Write-Envelope {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [object]$Data
    )

    Write-JsonFile $Path ([ordered]@{ schemaVersion = 1; data = $Data })
}

function Send-Message {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [object]$Message
    )

    $Process.StandardInput.WriteLine(($Message | ConvertTo-Json -Depth 20 -Compress))
    $Process.StandardInput.Flush()
}

function Read-Response {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [int]$RequestId,
        [Parameter(Mandatory = $true)]
        [int]$Timeout
    )

    $deadline = [DateTime]::UtcNow.AddMilliseconds($Timeout)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        $readTask = $Process.StandardOutput.ReadLineAsync()
        if (-not $readTask.Wait($remaining)) {
            throw "Timed out waiting for MCP response $RequestId."
        }

        $line = $readTask.Result
        if ($null -eq $line) {
            throw "MCP stdout closed before response $RequestId."
        }

        try {
            $message = $line | ConvertFrom-Json
        }
        catch {
            throw "MCP stdout contained a non-JSON line."
        }

        if ($null -ne $message.id -and [int]$message.id -eq $RequestId) {
            $script:responseWireBytes[$RequestId] = [Text.Encoding]::UTF8.GetByteCount($line)
            return $message
        }
    }

    throw "Timed out waiting for MCP response $RequestId."
}

function Invoke-ToolMeasurement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [int]$RequestId,
        [Parameter(Mandatory = $true)]
        [string]$Name,
        [Parameter(Mandatory = $true)]
        [object]$Arguments,
        [Parameter(Mandatory = $true)]
        [int]$Timeout
    )

    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Send-Message $Process ([ordered]@{
        jsonrpc = "2.0"
        id = $RequestId
        method = "tools/call"
        params = [ordered]@{ name = $Name; arguments = $Arguments }
    })
    $response = Read-Response $Process $RequestId $Timeout
    $watch.Stop()
    if ($null -ne $response.error) {
        throw "Tool '$Name' returned JSON-RPC error $($response.error.code)."
    }
    if ($response.result.isError -eq $true) {
        throw "Tool '$Name' returned a tool error."
    }

    $Process.Refresh()
    return [pscustomobject]@{
        Name = $Name
        Milliseconds = [Math]::Round($watch.Elapsed.TotalMilliseconds, 2)
        IsPartial = $response.result.structuredContent.isPartial
        IsTruncated = $response.result.structuredContent.isTruncated
        WorkingSetBytes = $Process.WorkingSet64
        PrivateBytes = $Process.PrivateMemorySize64
        PeakWorkingSetBytes = $Process.PeakWorkingSet64
        ProtocolResponseBytes = $script:responseWireBytes[$RequestId]
        StructuredResponseBytes = [Text.Encoding]::UTF8.GetByteCount(
            ($response.result.structuredContent | ConvertTo-Json -Depth 30 -Compress))
        Response = $response
    }
}

function Invoke-PagedSearchMeasurement {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [int]$RequestIdBase,
        [Parameter(Mandatory = $true)]
        [object]$Arguments,
        [Parameter(Mandatory = $true)]
        [int]$Timeout
    )

    $cursor = $null
    $pageCount = 0
    $pageReturnedFileRecordCounts = @()
    $pageOmittedZeroHitFileCounts = @()
    $totalOmittedZeroHitFileCount = 0
    $totalMilliseconds = 0.0
    $totalResponseBytes = 0L
    $maximumPageResponseBytes = 0L
    $maximumCursorCharacters = 0
    $maximumWorkingSetBytes = 0L
    $maximumPrivateBytes = 0L
    $maximumPeakWorkingSetBytes = 0L
    $isPartial = $false
    $isTruncated = $false
    $totalBytesEvaluated = 0L
    $totalFilesStarted = 0
    $totalFilesCompleted = 0
    $peakDiskOperations = 0
    $peakUncOperations = 0
    $hasTraversalStatistics = $true
    $lastMeasurement = $null
    do {
        $pageArguments = [ordered]@{}
        foreach ($entry in $Arguments.GetEnumerator()) {
            $pageArguments[$entry.Key] = $entry.Value
        }
        if (-not [string]::IsNullOrWhiteSpace($cursor)) {
            $pageArguments.cursor = $cursor
        }

        $lastMeasurement = Invoke-ToolMeasurement `
            $Process `
            ($RequestIdBase + $pageCount) `
            "search_logs" `
            $pageArguments `
            $Timeout
        $result = $lastMeasurement.Response.result.structuredContent.result
        if ($null -eq $result) {
            throw "Paged search returned no structured result."
        }

        $pageCount++
        $pageReturnedFileRecordCounts += @($result.files).Count
        $pageOmittedZeroHitFileCounts += $result.pageOmittedZeroHitFileCount
        $totalOmittedZeroHitFileCount += $result.pageOmittedZeroHitFileCount
        $totalMilliseconds += $lastMeasurement.Milliseconds
        $pageResponseBytes = [Text.Encoding]::UTF8.GetByteCount(($lastMeasurement.Response | ConvertTo-Json -Depth 30 -Compress))
        $totalResponseBytes += $pageResponseBytes
        $maximumPageResponseBytes = [Math]::Max($maximumPageResponseBytes, $pageResponseBytes)
        $maximumWorkingSetBytes = [Math]::Max($maximumWorkingSetBytes, $lastMeasurement.WorkingSetBytes)
        $maximumPrivateBytes = [Math]::Max($maximumPrivateBytes, $lastMeasurement.PrivateBytes)
        $maximumPeakWorkingSetBytes = [Math]::Max($maximumPeakWorkingSetBytes, $lastMeasurement.PeakWorkingSetBytes)
        $isPartial = $isPartial -or $lastMeasurement.IsPartial
        $isTruncated = $isTruncated -or $lastMeasurement.IsTruncated
        if ($null -eq $result.statistics) {
            # Compact responses omit instrumentation. Do not report missing data as zero.
            $hasTraversalStatistics = $false
        } else {
            $totalBytesEvaluated += $result.statistics.bytesEvaluated
            $totalFilesStarted += $result.statistics.filesStarted
            $totalFilesCompleted += $result.statistics.filesCompleted
            $peakDiskOperations = [Math]::Max($peakDiskOperations, $result.statistics.peakConcurrentDiskOperations)
            $peakUncOperations = [Math]::Max($peakUncOperations, $result.statistics.peakConcurrentUncOperations)
        }
        $cursor = $result.nextCursor
        if (-not [string]::IsNullOrWhiteSpace($cursor)) {
            $maximumCursorCharacters = [Math]::Max($maximumCursorCharacters, $cursor.Length)
        }
        if ($pageCount -gt [Math]::Ceiling($FileCount / 1.0) + 1) {
            throw "Paged search did not converge."
        }
    } while (-not [string]::IsNullOrWhiteSpace($cursor))

    if ($lastMeasurement.Response.result.structuredContent.result.isQueryComplete -ne $true) {
        throw "Paged search exhausted cursors without reporting query completion."
    }

    return [pscustomobject]@{
        Name = "search_logs"
        Milliseconds = [Math]::Round($totalMilliseconds, 2)
        IsPartial = $isPartial
        IsTruncated = $isTruncated
        WorkingSetBytes = $maximumWorkingSetBytes
        PrivateBytes = $maximumPrivateBytes
        PeakWorkingSetBytes = $maximumPeakWorkingSetBytes
        Response = $lastMeasurement.Response
        ResponseBytes = $totalResponseBytes
        MaximumPageResponseBytes = $maximumPageResponseBytes
        MaximumCursorCharacters = $maximumCursorCharacters
        PageCount = $pageCount
        PageReturnedFileRecordCounts = $pageReturnedFileRecordCounts
        PageOmittedZeroHitFileCounts = $pageOmittedZeroHitFileCounts
        TotalOmittedZeroHitFileCount = $totalOmittedZeroHitFileCount
        TraversalStatistics = $(if ($hasTraversalStatistics) {
            [ordered]@{
                bytesEvaluated = $totalBytesEvaluated
                filesStarted = $totalFilesStarted
                filesCompleted = $totalFilesCompleted
                peakConcurrentDiskOperations = $peakDiskOperations
                peakConcurrentUncOperations = $peakUncOperations
            }
        } else { $null })
    }
}

New-Item -ItemType Directory -Force -Path $dataDirectory, $cacheDirectory, $logDirectory | Out-Null
Copy-Item -LiteralPath $sourceExecutable -Destination $copiedExecutable -Force
Write-JsonFile (Join-Path $runRoot "WeezTail.install.json") ([ordered]@{
    installMode = "Portable"
    storageMode = "ExeDirectory"
})

$fileEntries = @()
$fileIds = @()
for ($fileIndex = 0; $fileIndex -lt $FileCount; $fileIndex++) {
    $fileId = "measurement-file-{0:D3}" -f $fileIndex
    $filePath = Join-Path $logDirectory ("measurement-{0:D3}.log" -f $fileIndex)
    $writer = New-Object System.IO.StreamWriter($filePath, $false, $utf8, 65536)
    try {
        for ($lineNumber = 1; $lineNumber -le $LinesPerFile; $lineNumber++) {
            $marker = if ($lineNumber % 250 -eq 0) { " needle" } else { "" }
            $writer.WriteLine("2026-08-05 12:00:{0:D2} file={1:D3} line={2:D7}{3}", ($lineNumber % 60), $fileIndex, $lineNumber, $marker)
        }
    }
    finally {
        $writer.Dispose()
    }

    $fileIds += $fileId
    $fileEntries += [ordered]@{
        id = $fileId
        filePath = $filePath
        lastOpenedAt = [DateTime]::UtcNow.ToString("O")
    }
}

$groups = @(
    [ordered]@{
        id = "measurement-folder"
        name = "Measurement"
        sortOrder = 0
        parentGroupId = $null
        kind = "branch"
        fileIds = @()
    },
    [ordered]@{
        id = "measurement-dashboard"
        name = "Measurement Dashboard"
        sortOrder = 0
        parentGroupId = "measurement-folder"
        kind = "dashboard"
        fileIds = $fileIds
    }
)
Write-Envelope (Join-Path $dataDirectory "loggroups.json") $groups
Write-Envelope (Join-Path $dataDirectory "logfiles.json") $fileEntries
Write-Envelope (Join-Path $dataDirectory "settings.json") ([ordered]@{ dateRollingPatterns = @() })

$mcpProcess = $null
$mcpStarted = $false
$measurements = @()
$startupWatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $copiedExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true
    $mcpProcess = New-Object System.Diagnostics.Process
    $mcpProcess.StartInfo = $startInfo
    [void]$mcpProcess.Start()
    $mcpStarted = $true

    Send-Message $mcpProcess ([ordered]@{
        jsonrpc = "2.0"
        id = 1
        method = "initialize"
        params = [ordered]@{
            protocolVersion = "2025-11-25"
            capabilities = [ordered]@{}
            clientInfo = [ordered]@{ name = "weeztail-measurement"; version = "1.0" }
        }
    })
    $initialize = Read-Response $mcpProcess 1 $TimeoutMilliseconds
    $startupWatch.Stop()
    if ($null -ne $initialize.error) {
        throw "MCP initialize failed."
    }
    Send-Message $mcpProcess ([ordered]@{
        jsonrpc = "2.0"
        method = "notifications/initialized"
        params = [ordered]@{}
    })

    $measurements += Invoke-ToolMeasurement $mcpProcess 2 "server_status" ([ordered]@{}) $TimeoutMilliseconds
    $measurements += Invoke-ToolMeasurement $mcpProcess 3 "list_log_tree" ([ordered]@{ maxNodes = 500 }) $TimeoutMilliseconds
    $searchArguments = [ordered]@{
        includeStatistics = [bool]$IncludeStatistics
        targets = @([ordered]@{ kind = "dashboard"; id = "measurement-dashboard" })
        query = $SearchQuery
        resultMode = "countsOnly"
        maxFiles = [Math]::Min(50, $FileCount)
        maxHitsPerFile = 50
        maxTotalHits = 500
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $measurement = Invoke-PagedSearchMeasurement $mcpProcess 1000 $searchArguments $TimeoutMilliseconds
    $measurement.Name = "search_logs_cold"
    $measurements += $measurement
    $measurement = Invoke-PagedSearchMeasurement $mcpProcess 2000 $searchArguments $TimeoutMilliseconds
    $measurement.Name = "search_logs_warm"
    $measurements += $measurement
    $countArguments = [ordered]@{
        includeStatistics = [bool]$IncludeStatistics
        targets = @([ordered]@{ kind = "dashboard"; id = "measurement-dashboard" })
        query = $SearchQuery
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $measurement = Invoke-ToolMeasurement $mcpProcess 2500 "count_logs" $countArguments $TimeoutMilliseconds
    $measurement.Name = "count_logs_cold"
    $measurements += $measurement
    $measurement = Invoke-ToolMeasurement $mcpProcess 2501 "count_logs" $countArguments $TimeoutMilliseconds
    $measurement.Name = "count_logs_warm"
    $measurements += $measurement
    $bucketedCountArguments = [ordered]@{
        includeStatistics = [bool]$IncludeStatistics
        targets = @([ordered]@{ kind = "dashboard"; id = "measurement-dashboard" })
        query = $SearchQuery
        startTimestamp = "12:00:00"
        endTimestamp = "12:00:59"
        bucketSize = "minute"
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $measurement = Invoke-ToolMeasurement $mcpProcess 2502 "count_logs" $bucketedCountArguments $TimeoutMilliseconds
    $measurement.Name = "count_logs_bucketed"
    $measurements += $measurement
    $readArguments = [ordered]@{
        fileId = $fileIds[0]
        startLine = [Math]::Max(1, $LinesPerFile - 20)
        count = 20
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $measurement = Invoke-ToolMeasurement $mcpProcess 3000 "read_log_lines" $readArguments $TimeoutMilliseconds
    $measurement.Name = "read_log_lines_cold"
    $measurements += $measurement
    $measurement = Invoke-ToolMeasurement $mcpProcess 3001 "read_log_lines" $readArguments $TimeoutMilliseconds
    $measurement.Name = "read_log_lines_warm"
    $measurements += $measurement
    $tailArguments = [ordered]@{
        fileId = $fileIds[0]
        maxLines = 20
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $tailInitial = Invoke-ToolMeasurement $mcpProcess 3002 "read_log_tail" $tailArguments $TimeoutMilliseconds
    $tailInitial.Name = "read_log_tail_initial"
    $measurements += $tailInitial
    $filteredTailArguments = [ordered]@{
        fileId = $fileIds[0]
        query = "needle"
        maxLines = 20
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $filteredTailInitial = Invoke-ToolMeasurement $mcpProcess 3004 "read_log_tail" $filteredTailArguments $TimeoutMilliseconds
    $filteredTailInitial.Name = "read_log_tail_filtered_initial"
    $measurements += $filteredTailInitial
    $tailCursor = $tailInitial.Response.result.structuredContent.result.nextCursor
    $filteredTailCursor = $filteredTailInitial.Response.result.structuredContent.result.nextCursor
    if (-not [string]::IsNullOrEmpty($tailCursor) -and
        -not [string]::IsNullOrEmpty($filteredTailCursor)) {
        $tailArguments['cursor'] = $tailCursor
        $filteredTailArguments['cursor'] = $filteredTailCursor
        $measurement = Invoke-ToolMeasurement $mcpProcess 3005 "read_log_tail" $tailArguments $TimeoutMilliseconds
        $measurement.Name = "read_log_tail_idle"
        $measurements += $measurement
        $measurement = Invoke-ToolMeasurement $mcpProcess 3006 "read_log_tail" $filteredTailArguments $TimeoutMilliseconds
        $measurement.Name = "read_log_tail_filtered_idle"
        $measurements += $measurement

        $appendPath = Join-Path $logDirectory "measurement-000.log"
        [System.IO.File]::AppendAllText($appendPath,
            "2026-08-05 12:00:01 append=nonmatch`n2026-08-05 12:00:02 append=needle`n2026-08-05 12:00:03 append=nonmatch`n",
            $utf8)
        $tailArguments['cursor'] = $measurements[-2].Response.result.structuredContent.result.nextCursor
        $filteredTailArguments['cursor'] = $measurements[-1].Response.result.structuredContent.result.nextCursor
        $measurement = Invoke-ToolMeasurement $mcpProcess 3007 "read_log_tail" $tailArguments $TimeoutMilliseconds
        $measurement.Name = "read_log_tail_append"
        $measurements += $measurement
        $measurement = Invoke-ToolMeasurement $mcpProcess 3008 "read_log_tail" $filteredTailArguments $TimeoutMilliseconds
        $measurement.Name = "read_log_tail_filtered_append"
        $measurements += $measurement
    }
    $measurement = Invoke-ToolMeasurement $mcpProcess 3003 "server_status" ([ordered]@{}) $TimeoutMilliseconds
    $measurement.Name = "server_status_after_reads"
    $measurements += $measurement

    $unexpectedResults = @($measurements | Where-Object {
        ($_.Name -like "server_status*" -or $_.Name -like "search_logs*") -and
        ($_.IsPartial -or $_.IsTruncated)
    })
    if ($unexpectedResults.Count -gt 0) {
        $unexpectedNames = $unexpectedResults.Name -join ", "
        throw "Measurement operations returned unexpected partial or truncated results: $unexpectedNames."
    }
    $invalidCountResults = @($measurements | Where-Object {
        $_.Name -like "count_logs*" -and
        ($_.IsPartial -or $_.Response.result.structuredContent.result.isComplete -ne $true)
    })
    if ($invalidCountResults.Count -gt 0) {
        throw "Count measurements did not return exact complete totals."
    }

    $cancelId = 4000
    $cancelWatch = [System.Diagnostics.Stopwatch]::StartNew()
    Send-Message $mcpProcess ([ordered]@{
        jsonrpc = "2.0"
        id = $cancelId
        method = "tools/call"
        params = [ordered]@{
            name = "search_logs"
            arguments = [ordered]@{
                targets = @([ordered]@{ kind = "dashboard"; id = "measurement-dashboard" })
                query = "never-present-$([Guid]::NewGuid().ToString('N'))"
                includeStatistics = [bool]$IncludeStatistics
                resultMode = "countsOnly"
                maxFiles = [Math]::Min(50, $FileCount)
                timeoutMilliseconds = $TimeoutMilliseconds
            }
        }
    })
    Start-Sleep -Milliseconds 20
    $cancelIssuedAt = $cancelWatch.Elapsed.TotalMilliseconds
    Send-Message $mcpProcess ([ordered]@{
        jsonrpc = "2.0"
        method = "notifications/cancelled"
        params = [ordered]@{ requestId = $cancelId; reason = "measurement" }
    })
    # MCP cancellation is a notification. A compliant server may omit the original
    # request's response once the client has said it is no longer interested. Probe
    # the same process with a fresh light request instead: its completion
    # proves the cancelled heavy request released the request gate.
    $cancelProbeId = 4001
    Send-Message $mcpProcess ([ordered]@{
        jsonrpc = "2.0"
        id = $cancelProbeId
        method = "tools/call"
        params = [ordered]@{ name = "server_status"; arguments = [ordered]@{} }
    })
    $cancelProbeResponse = Read-Response $mcpProcess $cancelProbeId $TimeoutMilliseconds
    $cancelWatch.Stop()
    if ($null -ne $cancelProbeResponse.error -or $cancelProbeResponse.result.isError -eq $true) {
        throw "The post-cancellation status probe failed."
    }
    $cancellation = [pscustomobject]@{
        CancelIssuedAtMilliseconds = [Math]::Round($cancelIssuedAt, 2)
        ProbeResponseAtMilliseconds = [Math]::Round($cancelWatch.Elapsed.TotalMilliseconds, 2)
        LatencyAfterCancelMilliseconds = [Math]::Round($cancelWatch.Elapsed.TotalMilliseconds - $cancelIssuedAt, 2)
    }

    $mcpProcess.Refresh()
    $finalMcpWorkingSetBytes = $mcpProcess.WorkingSet64
    $finalMcpPrivateBytes = $mcpProcess.PrivateMemorySize64
    $finalMcpPeakWorkingSetBytes = $mcpProcess.PeakWorkingSet64
    $shutdownWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $mcpProcess.StandardInput.Close()
    if (-not $mcpProcess.WaitForExit($TimeoutMilliseconds)) {
        throw "MCP process did not exit after stdin closure."
    }
    $shutdownWatch.Stop()
    if ($mcpProcess.ExitCode -ne 0) {
        throw "MCP process exited with code $($mcpProcess.ExitCode)."
    }
    $stderr = $mcpProcess.StandardError.ReadToEnd()

    $report = [ordered]@{
        schemaVersion = 5
        measuredAtUtc = [DateTime]::UtcNow.ToString("O")
        mode = "headless"
        executableBytes = (Get-Item $copiedExecutable).Length
        fileCount = $FileCount
        includeStatistics = [bool]$IncludeStatistics
        searchQuery = $SearchQuery
        linesPerFile = $LinesPerFile
        totalLogBytes = (Get-ChildItem $logDirectory -File | Measure-Object Length -Sum).Sum
        initializeMilliseconds = [Math]::Round($startupWatch.Elapsed.TotalMilliseconds, 2)
        shutdownMilliseconds = [Math]::Round($shutdownWatch.Elapsed.TotalMilliseconds, 2)
        exitCode = $mcpProcess.ExitCode
        stderrWasEmpty = [string]::IsNullOrWhiteSpace($stderr)
        cancellation = $cancellation
        measurements = @($measurements | ForEach-Object {
            [ordered]@{
                name = $_.Name
                milliseconds = $_.Milliseconds
                isPartial = $_.IsPartial
                isTruncated = $_.IsTruncated
                responseBytes = $(if ($null -ne $_.ResponseBytes) {
                    $_.ResponseBytes
                } else {
                    [Text.Encoding]::UTF8.GetByteCount(($_.Response | ConvertTo-Json -Depth 30 -Compress))
                })
                protocolResponseBytes = $_.ProtocolResponseBytes
                structuredResponseBytes = $_.StructuredResponseBytes
                maximumPageResponseBytes = $_.MaximumPageResponseBytes
                maximumCursorCharacters = $_.MaximumCursorCharacters
                pageCount = $_.PageCount
                pageReturnedFileRecordCounts = $(if ($null -ne $_.PageReturnedFileRecordCounts) {
                    @($_.PageReturnedFileRecordCounts)
                } else { $null })
                pageOmittedZeroHitFileCounts = $(if ($null -ne $_.PageOmittedZeroHitFileCounts) {
                    @($_.PageOmittedZeroHitFileCounts)
                } else { $null })
                totalOmittedZeroHitFileCount = $_.TotalOmittedZeroHitFileCount
                traversalStatistics = $_.TraversalStatistics
                errorCodes = @($_.Response.result.structuredContent.errors | ForEach-Object { $_.code })
                fileErrorCodes = @($(
                    @(
                        $_.Response.result.structuredContent.result.files | ForEach-Object { $_.error.code }
                        $_.Response.result.structuredContent.result.file | ForEach-Object { $_.error.code }
                    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
                ))
                status = $(if ($_.Name -like "server_status*") {
                    $_.Response.result.structuredContent.result
                } else {
                    $null
                })
                search = $(if ($_.Name -like "search_logs*") {
                    $searchResult = $_.Response.result.structuredContent.result
                    [ordered]@{
                        resultMode = $searchResult.resultMode
                        selectedFileCount = $searchResult.selectedFileCount
                        searchedFileCount = $searchResult.searchedFileCount
                        skippedFileCount = $searchResult.skippedFileCount
                        failedFileCount = $searchResult.failedFileCount
                        remainingFileCount = $searchResult.remainingFileCount
                        matchedFileCount = $searchResult.matchedFileCount
                        pageOmittedZeroHitFileCount = $searchResult.pageOmittedZeroHitFileCount
                        returnedHitCount = $searchResult.returnedHitCount
                        matchingLineCount = $searchResult.matchingLineCount
                        matchOccurrenceCount = $searchResult.matchOccurrenceCount
                        isPageComplete = $searchResult.isPageComplete
                        isQueryComplete = $searchResult.isQueryComplete
                        incompleteReasons = @($searchResult.incompleteReasons | Where-Object { $null -ne $_ })
                        statistics = $searchResult.statistics
                    }
                } else {
                    $null
                })
                count = $(if ($_.Name -like "count_logs*") {
                    $countResult = $_.Response.result.structuredContent.result
                    [ordered]@{
                        selectedFileCount = $countResult.selectedFileCount
                        searchedFileCount = $countResult.searchedFileCount
                        skippedFileCount = $countResult.skippedFileCount
                        failedFileCount = $countResult.failedFileCount
                        remainingFileCount = $countResult.remainingFileCount
                        matchedFileCount = $countResult.matchedFileCount
                        matchingLineCount = $countResult.matchingLineCount
                        matchOccurrenceCount = $countResult.matchOccurrenceCount
                        isComplete = $countResult.isComplete
                        incompleteReasons = @($countResult.incompleteReasons | Where-Object { $null -ne $_ })
                        bucketSize = $countResult.bucketSize
                        bucketCount = @($countResult.buckets).Count
                        fileRecordTotalCount = $countResult.fileRecordTotalCount
                        returnedFileRecordCount = $countResult.returnedFileRecordCount
                        isFileRecordTruncated = $countResult.isFileRecordTruncated
                        statistics = $countResult.statistics
                    }
                } else {
                    $null
                })
                tail = $(if ($_.Name -like "read_log_tail*") {
                    $tailResult = $_.Response.result.structuredContent.result
                    [ordered]@{
                        returnedLineCount = @($tailResult.file.lines).Count
                        examinedLineCount = $tailResult.examinedLineCount
                        skippedLineCount = $tailResult.skippedLineCount
                        remainingLineCount = $tailResult.remainingLineCount
                        removedLineNumber = $tailResult.removedLineNumber
                    }
                } else {
                    $null
                })
                workingSetBytes = $_.WorkingSetBytes
                privateBytes = $_.PrivateBytes
                peakWorkingSetBytes = $_.PeakWorkingSetBytes
            }
        })
        finalMcpWorkingSetBytes = $finalMcpWorkingSetBytes
        finalMcpPrivateBytes = $finalMcpPrivateBytes
        finalMcpPeakWorkingSetBytes = $finalMcpPeakWorkingSetBytes
        outputDirectory = $runRoot
    }
    $reportPath = Join-Path $runRoot "measurement.json"
    Write-JsonFile $reportPath $report
    Write-Host "MCP measurement completed: $reportPath"
    Get-Content -LiteralPath $reportPath -Raw
}
finally {
    if ($null -ne $mcpProcess -and $mcpStarted) {
        if (-not $mcpProcess.HasExited) {
            $mcpProcess.StandardInput.Close()
            if (-not $mcpProcess.WaitForExit(5000)) {
                $mcpProcess.Kill()
                $mcpProcess.WaitForExit()
            }
        }
        $mcpProcess.Dispose()
    }
    elseif ($null -ne $mcpProcess) {
        $mcpProcess.Dispose()
    }
}
