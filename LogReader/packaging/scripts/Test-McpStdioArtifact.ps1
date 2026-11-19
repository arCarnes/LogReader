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
    $expectedToolNames = @('continue_log_count', 'continue_log_search', 'count_logs', 'list_log_tree',
        'poll_log_monitor', 'read_log_lines', 'search_logs', 'server_status', 'start_log_monitor',
        'start_log_monitor_from_snapshot', 'stop_log_monitor')
    if (($toolNames -join '|') -ne ($expectedToolNames -join '|')) {
        throw 'MCP tools/list did not return the eleven-tool cutover surface.'
    }
    foreach ($tool in $toolsResponse.result.tools) {
        if ($tool.inputSchema.type -ne 'object' -or $tool.inputSchema.additionalProperties -ne $false -or
            $null -ne $tool.inputSchema.anyOf -or $null -ne $tool.inputSchema.oneOf -or $null -ne $tool.inputSchema.allOf) {
            throw "MCP $($tool.name) did not advertise a closed ordinary-object input schema."
        }
    }
    foreach ($name in @('count_logs', 'search_logs', 'start_log_monitor')) {
        $tool = $toolsResponse.result.tools | Where-Object { $_.name -eq $name }
        if ($tool.inputSchema.required -notcontains 'targets' -or
            $tool.inputSchema.properties.targets.items.required -notcontains 'kind' -or
            $tool.inputSchema.properties.targets.items.required -notcontains 'id') {
            throw "MCP $name lost its required configured selectors."
        }
    }
    $cases = @(
        @{ name = 'server_status'; arguments = @{}; status = 'success' },
        @{ name = 'count_logs'; arguments = @{ targets = @(@{ kind = 'logFile'; id = 'packaging-smoke-missing' }); query = 'needle' }; status = 'error' },
        @{ name = 'start_log_monitor'; arguments = @{ targets = @(@{ kind = 'logFile'; id = 'packaging-smoke-missing' }) }; status = 'error' },
        @{ name = 'count_logs'; arguments = @{ query = 'needle' }; status = 'error'; path = '/targets' },
        @{ name = 'server_status'; arguments = @{ unexpected = $true }; status = 'error'; path = '/unexpected' }
    )
    $requestId = 3
    foreach ($case in $cases) {
        $message = @{ jsonrpc = '2.0'; id = $requestId; method = 'tools/call'; params = @{ name = $case.name; arguments = $case.arguments } }
        Send-McpMessage $process ($message | ConvertTo-Json -Depth 20 -Compress)
        $sample = Read-McpResponse $process $requestId $TimeoutMilliseconds
        $body = $sample.result.structuredContent
        if ($body.schemaVersion -ne 5 -or $body.status -ne $case.status -or
            $sample.result.isError -ne ($case.status -eq 'error')) {
            throw "MCP $($case.name) returned an inconsistent outcome."
        }
        if ($case.path -and $body.issues.argumentPath -notcontains $case.path) {
            throw "MCP $($case.name) did not explain its invalid argument."
        }
        $text = @($sample.result.content | Where-Object { $_.type -eq 'text' })[0].text
        if (($text | ConvertFrom-Json | ConvertTo-Json -Depth 100 -Compress) -cne ($body | ConvertTo-Json -Depth 100 -Compress)) {
            throw 'MCP structured/text content differ.'
        }
        if ($null -ne (Get-Command Test-Json -ErrorAction SilentlyContinue)) {
            $tool = $toolsResponse.result.tools | Where-Object { $_.name -eq $case.name }
            if (-not (Test-Json -Json $text -Schema ($tool.outputSchema | ConvertTo-Json -Depth 100 -Compress) -ErrorAction Stop)) {
                throw 'MCP output does not satisfy its advertised schema.'
            }
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
