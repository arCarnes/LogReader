param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,
    [int]$TimeoutMilliseconds = 10000
)

$ErrorActionPreference = "Stop"

function Read-McpResponse {
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
            throw "MCP process closed stdout before response $RequestId."
        }

        try {
            $message = $line | ConvertFrom-Json
        }
        catch {
            throw "MCP stdout contained a non-JSON protocol line."
        }

        if ($null -ne $message.id -and [int]$message.id -eq $RequestId) {
            if ($null -ne $message.error) {
                throw "MCP request $RequestId returned error code $($message.error.code)."
            }

            return $message
        }
    }

    throw "Timed out waiting for MCP response $RequestId."
}

function Send-McpMessage {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory = $true)]
        [string]$Json
    )

    $Process.StandardInput.WriteLine($Json)
    $Process.StandardInput.Flush()
}

$resolvedExecutablePath = (Resolve-Path $ExecutablePath).Path
$startInfo = New-Object System.Diagnostics.ProcessStartInfo
$startInfo.FileName = $resolvedExecutablePath
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo

try {
    if (-not $process.Start()) {
        throw "Could not start the published WeezTail MCP executable."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"weeztail-packaging-smoke","version":"1.0"}}}'
    $initialize = Read-McpResponse $process 1 $TimeoutMilliseconds
    if ($initialize.result.serverInfo.name -ne "weeztail") {
        throw "MCP initialize returned an unexpected server name."
    }
    if ([string]::IsNullOrWhiteSpace($initialize.result.instructions) -or $initialize.result.instructions.Length -gt 512 -or
        $initialize.result.instructions -notlike '*untrusted*') {
        throw "MCP initialize did not return bounded shared investigation guidance."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
    Send-McpMessage $process '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    $toolsResponse = Read-McpResponse $process 2 $TimeoutMilliseconds
    $toolNames = @($toolsResponse.result.tools | ForEach-Object { $_.name } | Sort-Object)
    $expectedToolNames = @("count_logs", "list_log_tree", "read_log_lines", "read_log_tail", "search_logs", "server_status")
    if (($toolNames -join "|") -ne ($expectedToolNames -join "|")) {
        throw "MCP tools/list did not return the expected tool surface."
    }
    foreach ($tool in ($toolsResponse.result.tools | Where-Object { $_.name -in @("search_logs", "count_logs", "read_log_lines", "read_log_tail") })) {
        if (($tool.inputSchema.properties.provenanceMode.enum -join '|') -ne 'shared|inline' -or
            $tool.inputSchema.properties.provenanceMode.default -ne 'shared') {
            throw "MCP $($tool.name) did not advertise shared/inline provenance."
        }
        if ($null -eq $tool.outputSchema.'$defs'.provenance) {
            throw "MCP $($tool.name) did not advertise a typed shared provenance definition."
        }
    }
    $countTool = $toolsResponse.result.tools | Where-Object { $_.name -eq "count_logs" }
    if (($countTool.inputSchema.properties.bucketMode.enum -join '|') -ne 'sparse|dense' -or
        $countTool.inputSchema.properties.bucketMode.default -ne 'sparse') {
        throw "MCP count_logs did not advertise sparse/dense bucket presentation."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"server_status","arguments":{}}}'
    $statusResponse = Read-McpResponse $process 3 $TimeoutMilliseconds
    if ($statusResponse.result.isError -eq $true) {
        throw "MCP server_status returned an error."
    }

    if ([int]$statusResponse.result.structuredContent.schemaVersion -ne 4) {
        throw "MCP server_status returned an unexpected schema version."
    }

    if ($statusResponse.result.structuredContent.result.transport -ne "stdio") {
        throw "MCP server_status returned an unexpected transport."
    }

    $limits = $statusResponse.result.structuredContent.result.queryBackend.limits
    $expectedLimits = [ordered]@{
        searchWorkMilliseconds = 5000
        searchScanBytes = 268435456
        maximumFiles = 200
        maximumHitsPerFile = 200
        maximumTotalHits = 2000
        maximumQueryHits = 10000
        maximumResponseCharacters = 800000
        defaultTimeoutMilliseconds = 30000
    }
    foreach ($entry in $expectedLimits.GetEnumerator()) {
        if ($limits.($entry.Key) -ne $entry.Value) {
            throw "MCP server_status returned an unexpected $($entry.Key) limit."
        }
    }
    $searchTool = $toolsResponse.result.tools | Where-Object { $_.name -eq "search_logs" }
    if ($null -eq $searchTool.inputSchema.properties.maxQueryHits) {
        throw "MCP search tool did not advertise the query-wide hit allowance."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"count_logs","arguments":{"targets":[{"kind":"logFile","id":"packaging-smoke-missing"}],"query":"needle"}}}'
    $countResponse = Read-McpResponse $process 4 $TimeoutMilliseconds
    if ($countResponse.result.isError -eq $true) {
        throw "MCP count_logs returned a protocol tool error."
    }
    if ([int]$countResponse.result.structuredContent.schemaVersion -ne 4) {
        throw "MCP count_logs returned an unexpected envelope schema version."
    }
    foreach ($sample in @(
        @{ response = $statusResponse; tool = ($toolsResponse.result.tools | Where-Object { $_.name -eq 'server_status' }) },
        @{ response = $countResponse; tool = $countTool }
    )) {
        $text = @($sample.response.result.content | Where-Object { $_.type -eq 'text' })[0].text
        if (($text | ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress) -cne
            ($sample.response.result.structuredContent | ConvertTo-Json -Depth 100 -Compress)) {
            throw "MCP structured/text content differ."
        }
        if ($null -ne (Get-Command Test-Json -ErrorAction SilentlyContinue)) {
            if (-not (Test-Json -Json $text -Schema ($sample.tool.outputSchema | ConvertTo-Json -Depth 100 -Compress) -ErrorAction Stop)) {
                throw "MCP output does not satisfy its advertised schema."
            }
        }
    }
    $requestId = 6
    foreach ($name in @('search_logs', 'count_logs', 'read_log_lines', 'read_log_tail')) {
        $arguments = @{ provenanceMode = 'smoke-invalid' }
        if ($name -in @('search_logs', 'count_logs')) {
            $arguments.targets = @(@{ kind = 'logFile'; id = 'packaging-smoke-missing' })
            $arguments.query = 'needle'
        } else {
            $arguments.fileId = 'packaging-smoke-missing'
        }
        $message = @{ jsonrpc = '2.0'; id = $requestId; method = 'tools/call'; params = @{ name = $name; arguments = $arguments } }
        Send-McpMessage $process ($message | ConvertTo-Json -Depth 10 -Compress)
        $invalid = Read-McpResponse $process $requestId $TimeoutMilliseconds
        if ($invalid.result.isError -ne $true -or $null -ne $invalid.result.structuredContent -or
            $invalid.result.content[0].text -ne 'provenanceMode must be one of: shared, inline.') {
            throw "MCP $name did not reject an invalid presentation mode safely."
        }
        $requestId++
    }

    $process.StandardInput.Close()
    if (-not $process.WaitForExit($TimeoutMilliseconds)) {
        throw "MCP process did not exit after stdin closed."
    }

    if ($process.ExitCode -ne 0) {
        throw "MCP process exited with code $($process.ExitCode)."
    }

    $unexpectedOutput = $process.StandardOutput.ReadToEnd()
    if (-not [string]::IsNullOrWhiteSpace($unexpectedOutput)) {
        throw "MCP stdout contained unexpected output after the final response."
    }

    Write-Host "MCP stdio artifact smoke test passed: $resolvedExecutablePath"
}
finally {
    if (-not $process.HasExited) {
        $process.Kill()
        $process.WaitForExit()
    }

    $process.Dispose()
}
