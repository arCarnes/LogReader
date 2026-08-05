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
$startInfo.Arguments = "--mcp-stdio"
$startInfo.UseShellExecute = $false
$startInfo.RedirectStandardInput = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.CreateNoWindow = $true

$process = New-Object System.Diagnostics.Process
$process.StartInfo = $startInfo

try {
    if (-not $process.Start()) {
        throw "Could not start the published WeezTail executable."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"weeztail-packaging-smoke","version":"1.0"}}}'
    $initialize = Read-McpResponse $process 1 $TimeoutMilliseconds
    if ($initialize.result.serverInfo.name -ne "weeztail") {
        throw "MCP initialize returned an unexpected server name."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}'
    Send-McpMessage $process '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'
    $toolsResponse = Read-McpResponse $process 2 $TimeoutMilliseconds
    $toolNames = @($toolsResponse.result.tools | ForEach-Object { $_.name } | Sort-Object)
    $expectedToolNames = @("list_log_tree", "read_log_lines", "read_log_tail", "search_logs", "server_status")
    if (($toolNames -join "|") -ne ($expectedToolNames -join "|")) {
        throw "MCP tools/list did not return the expected tool surface."
    }

    Send-McpMessage $process '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"server_status","arguments":{}}}'
    $statusResponse = Read-McpResponse $process 3 $TimeoutMilliseconds
    if ($statusResponse.result.isError -eq $true) {
        throw "MCP server_status returned an error."
    }

    if ([int]$statusResponse.result.structuredContent.schemaVersion -ne 1) {
        throw "MCP server_status returned an unexpected schema version."
    }

    if ($statusResponse.result.structuredContent.result.transport -ne "stdio") {
        throw "MCP server_status returned an unexpected transport."
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
