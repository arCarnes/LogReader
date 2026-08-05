param(
    [string]$ExecutablePath = ".\artifacts\publish\Portable\WeezTail.exe",
    [ValidateRange(1, 50)]
    [int]$FileCount = 10,
    [ValidateRange(100, 500000)]
    [int]$LinesPerFile = 10000,
    [switch]$UseLiveUi,
    [ValidateRange(0, 2)]
    [int]$AdditionalIdleClients = 0,
    [ValidateRange(1, 30000)]
    [int]$TimeoutMilliseconds = 30000
)

$ErrorActionPreference = "Stop"
$utf8 = New-Object System.Text.UTF8Encoding($false)
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = Join-Path $productRoot "artifacts\measurements"
$runName = "mcp-{0}-{1}files-{2}" -f (
    $(if ($UseLiveUi) { "live" } else { "headless" })),
    $FileCount,
    [DateTime]::UtcNow.ToString("yyyyMMdd-HHmmss-fff")
$runRoot = Join-Path $artifactRoot $runName
$dataDirectory = Join-Path $runRoot "Data"
$cacheDirectory = Join-Path $runRoot "Cache"
$logDirectory = Join-Path $runRoot "Logs"
$copiedExecutable = Join-Path $runRoot "WeezTail.exe"
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
        Backend = $response.result.structuredContent.backend
        IsPartial = $response.result.structuredContent.isPartial
        IsTruncated = $response.result.structuredContent.isTruncated
        WorkingSetBytes = $Process.WorkingSet64
        PrivateBytes = $Process.PrivateMemorySize64
        PeakWorkingSetBytes = $Process.PeakWorkingSet64
        Response = $response
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
            $marker = if ($lineNumber % 1000 -eq 0) { " needle" } else { "" }
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

$uiProcess = $null
$mcpProcess = $null
$mcpStarted = $false
$additionalMcpProcesses = @()
$measurements = @()
$startupWatch = [System.Diagnostics.Stopwatch]::StartNew()
try {
    if ($UseLiveUi) {
        $uiProcess = Start-Process -FilePath $copiedExecutable -WindowStyle Hidden -PassThru
        $uiDeadline = [DateTime]::UtcNow.AddSeconds(15)
        while (-not $uiProcess.HasExited -and $uiProcess.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $uiDeadline) {
            Start-Sleep -Milliseconds 10
            $uiProcess.Refresh()
        }
        if ($uiProcess.HasExited -or $uiProcess.MainWindowHandle -eq 0) {
            throw "The measurement UI did not open."
        }
        Start-Sleep -Milliseconds 500
    }

    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $copiedExecutable
    $startInfo.Arguments = "--mcp-stdio"
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

    if ($AdditionalIdleClients -gt 0 -and -not $UseLiveUi) {
        throw "Additional idle clients require -UseLiveUi."
    }
    for ($clientIndex = 0; $clientIndex -lt $AdditionalIdleClients; $clientIndex++) {
        $additionalStartInfo = New-Object System.Diagnostics.ProcessStartInfo
        $additionalStartInfo.FileName = $copiedExecutable
        $additionalStartInfo.Arguments = "--mcp-stdio"
        $additionalStartInfo.UseShellExecute = $false
        $additionalStartInfo.RedirectStandardInput = $true
        $additionalStartInfo.RedirectStandardOutput = $true
        $additionalStartInfo.RedirectStandardError = $true
        $additionalStartInfo.CreateNoWindow = $true
        $additionalProcess = New-Object System.Diagnostics.Process
        $additionalProcess.StartInfo = $additionalStartInfo
        [void]$additionalProcess.Start()
        $additionalMcpProcesses += $additionalProcess

        Send-Message $additionalProcess ([ordered]@{
            jsonrpc = "2.0"
            id = 1
            method = "initialize"
            params = [ordered]@{
                protocolVersion = "2025-11-25"
                capabilities = [ordered]@{}
                clientInfo = [ordered]@{ name = "weeztail-measurement-extra"; version = "1.0" }
            }
        })
        $additionalInitialize = Read-Response $additionalProcess 1 $TimeoutMilliseconds
        if ($null -ne $additionalInitialize.error) {
            throw "Additional MCP client initialize failed."
        }
        Send-Message $additionalProcess ([ordered]@{
            jsonrpc = "2.0"
            method = "notifications/initialized"
            params = [ordered]@{}
        })
        Send-Message $additionalProcess ([ordered]@{
            jsonrpc = "2.0"
            id = 2
            method = "tools/call"
            params = [ordered]@{ name = "server_status"; arguments = [ordered]@{} }
        })
        $additionalStatus = Read-Response $additionalProcess 2 $TimeoutMilliseconds
        if ($additionalStatus.result.structuredContent.backend -ne "liveUi") {
            throw "Additional MCP client did not select the live UI backend."
        }
    }

    $measurements += Invoke-ToolMeasurement $mcpProcess 2 "server_status" ([ordered]@{}) $TimeoutMilliseconds
    $measurements += Invoke-ToolMeasurement $mcpProcess 3 "list_log_tree" ([ordered]@{ maxNodes = 500 }) $TimeoutMilliseconds
    $searchArguments = [ordered]@{
        targets = @([ordered]@{ kind = "dashboard"; id = "measurement-dashboard" })
        query = "needle"
        maxFiles = $FileCount
        maxHitsPerFile = 50
        maxTotalHits = 500
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $measurement = Invoke-ToolMeasurement $mcpProcess 4 "search_logs" $searchArguments $TimeoutMilliseconds
    $measurement.Name = "search_logs_cold"
    $measurements += $measurement
    $measurement = Invoke-ToolMeasurement $mcpProcess 5 "search_logs" $searchArguments $TimeoutMilliseconds
    $measurement.Name = "search_logs_warm"
    $measurements += $measurement
    $readArguments = [ordered]@{
        fileId = $fileIds[0]
        startLine = [Math]::Max(1, $LinesPerFile - 20)
        count = 20
        timeoutMilliseconds = $TimeoutMilliseconds
    }
    $measurement = Invoke-ToolMeasurement $mcpProcess 6 "read_log_lines" $readArguments $TimeoutMilliseconds
    $measurement.Name = "read_log_lines_cold"
    $measurements += $measurement
    $measurement = Invoke-ToolMeasurement $mcpProcess 7 "read_log_lines" $readArguments $TimeoutMilliseconds
    $measurement.Name = "read_log_lines_warm"
    $measurements += $measurement
    $measurements += Invoke-ToolMeasurement $mcpProcess 8 "read_log_tail" ([ordered]@{
        fileId = $fileIds[0]
        maxLines = 20
        timeoutMilliseconds = $TimeoutMilliseconds
    }) $TimeoutMilliseconds
    $measurement = Invoke-ToolMeasurement $mcpProcess 9 "server_status" ([ordered]@{}) $TimeoutMilliseconds
    $measurement.Name = "server_status_after_reads"
    $measurements += $measurement

    $cancelId = 10
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
                maxFiles = $FileCount
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
    # the same serialized backend with a fresh light request instead: its completion
    # proves the cancelled heavy request released the request gate.
    $cancelProbeId = 11
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
        ProbeBackend = $cancelProbeResponse.result.structuredContent.backend
    }

    $concurrency = $null
    if ($additionalMcpProcesses.Count -gt 0) {
        $concurrentQuery = "never-present-concurrent-$([Guid]::NewGuid().ToString('N'))"
        foreach ($additionalProcess in $additionalMcpProcesses) {
            Send-Message $additionalProcess ([ordered]@{
                jsonrpc = "2.0"
                id = 3
                method = "tools/call"
                params = [ordered]@{
                    name = "search_logs"
                    arguments = [ordered]@{
                        targets = @([ordered]@{ kind = "dashboard"; id = "measurement-dashboard" })
                        query = $concurrentQuery
                        maxFiles = $FileCount
                        timeoutMilliseconds = $TimeoutMilliseconds
                    }
                }
            })
        }
        Start-Sleep -Milliseconds 20

        $lightProbeWatch = [System.Diagnostics.Stopwatch]::StartNew()
        Send-Message $mcpProcess ([ordered]@{
            jsonrpc = "2.0"
            id = 12
            method = "tools/call"
            params = [ordered]@{ name = "server_status"; arguments = [ordered]@{} }
        })
        $lightProbeResponse = Read-Response $mcpProcess 12 $TimeoutMilliseconds
        $lightProbeWatch.Stop()

        $drainWatch = [System.Diagnostics.Stopwatch]::StartNew()
        foreach ($additionalProcess in $additionalMcpProcesses) {
            Send-Message $additionalProcess ([ordered]@{
                jsonrpc = "2.0"
                method = "notifications/cancelled"
                params = [ordered]@{ requestId = 3; reason = "concurrency measurement" }
            })
            Send-Message $additionalProcess ([ordered]@{
                jsonrpc = "2.0"
                id = 4
                method = "tools/call"
                params = [ordered]@{ name = "server_status"; arguments = [ordered]@{} }
            })
        }
        $probeBackends = @()
        foreach ($additionalProcess in $additionalMcpProcesses) {
            $drainResponse = Read-Response $additionalProcess 4 $TimeoutMilliseconds
            $probeBackends += $drainResponse.result.structuredContent.backend
        }
        $drainWatch.Stop()
        $concurrency = [ordered]@{
            clientCount = 1 + $additionalMcpProcesses.Count
            queuedHeavyRequestCount = $additionalMcpProcesses.Count
            lightStatusMilliseconds = [Math]::Round($lightProbeWatch.Elapsed.TotalMilliseconds, 2)
            lightStatusBackend = $lightProbeResponse.result.structuredContent.backend
            cancelDrainMilliseconds = [Math]::Round($drainWatch.Elapsed.TotalMilliseconds, 2)
            postCancelBackends = $probeBackends
        }
    }

    $mcpProcess.Refresh()
    if ($null -ne $uiProcess) { $uiProcess.Refresh() }
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

    $additionalClientReports = @()
    foreach ($additionalProcess in $additionalMcpProcesses) {
        $additionalProcess.Refresh()
        $additionalWorkingSet = $additionalProcess.WorkingSet64
        $additionalPrivateBytes = $additionalProcess.PrivateMemorySize64
        $additionalPeakWorkingSet = $additionalProcess.PeakWorkingSet64
        $additionalProcess.StandardInput.Close()
        if (-not $additionalProcess.WaitForExit($TimeoutMilliseconds)) {
            throw "Additional MCP process did not exit after stdin closure."
        }
        $additionalStderr = $additionalProcess.StandardError.ReadToEnd()
        $additionalClientReports += [ordered]@{
            exitCode = $additionalProcess.ExitCode
            stderrWasEmpty = [string]::IsNullOrWhiteSpace($additionalStderr)
            workingSetBytes = $additionalWorkingSet
            privateBytes = $additionalPrivateBytes
            peakWorkingSetBytes = $additionalPeakWorkingSet
        }
    }

    $report = [ordered]@{
        schemaVersion = 1
        measuredAtUtc = [DateTime]::UtcNow.ToString("O")
        mode = $(if ($UseLiveUi) { "live_ui" } else { "headless" })
        executableBytes = (Get-Item $copiedExecutable).Length
        fileCount = $FileCount
        linesPerFile = $LinesPerFile
        totalLogBytes = (Get-ChildItem $logDirectory -File | Measure-Object Length -Sum).Sum
        initializeMilliseconds = [Math]::Round($startupWatch.Elapsed.TotalMilliseconds, 2)
        shutdownMilliseconds = [Math]::Round($shutdownWatch.Elapsed.TotalMilliseconds, 2)
        exitCode = $mcpProcess.ExitCode
        stderrWasEmpty = [string]::IsNullOrWhiteSpace($stderr)
        cancellation = $cancellation
        concurrency = $concurrency
        measurements = @($measurements | ForEach-Object {
            [ordered]@{
                name = $_.Name
                milliseconds = $_.Milliseconds
                backend = $_.Backend
                isPartial = $_.IsPartial
                isTruncated = $_.IsTruncated
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
                workingSetBytes = $_.WorkingSetBytes
                privateBytes = $_.PrivateBytes
                peakWorkingSetBytes = $_.PeakWorkingSetBytes
            }
        })
        finalMcpWorkingSetBytes = $finalMcpWorkingSetBytes
        finalMcpPrivateBytes = $finalMcpPrivateBytes
        finalMcpPeakWorkingSetBytes = $finalMcpPeakWorkingSetBytes
        additionalClients = $additionalClientReports
        uiWorkingSetBytes = $(if ($null -ne $uiProcess) { $uiProcess.WorkingSet64 } else { 0 })
        uiPrivateBytes = $(if ($null -ne $uiProcess) { $uiProcess.PrivateMemorySize64 } else { 0 })
        outputDirectory = $runRoot
    }
    $reportPath = Join-Path $runRoot "measurement.json"
    Write-JsonFile $reportPath $report
    Write-Host "MCP measurement completed: $reportPath"
    Get-Content -LiteralPath $reportPath -Raw
}
finally {
    foreach ($additionalProcess in $additionalMcpProcesses) {
        if (-not $additionalProcess.HasExited) {
            $additionalProcess.StandardInput.Close()
            if (-not $additionalProcess.WaitForExit(5000)) {
                $additionalProcess.Kill()
                $additionalProcess.WaitForExit()
            }
        }
        $additionalProcess.Dispose()
    }

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

    if ($null -ne $uiProcess) {
        if (-not $uiProcess.HasExited) {
            [void]$uiProcess.CloseMainWindow()
            if (-not $uiProcess.WaitForExit(5000)) {
                Stop-Process -Id $uiProcess.Id -Force
                $uiProcess.WaitForExit()
            }
        }
        $uiProcess.Dispose()
    }
}
