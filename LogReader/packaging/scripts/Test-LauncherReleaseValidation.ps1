param([string]$LauncherDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleasePackaging.ps1')
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$version = Get-ReleaseVersion $productRoot
$source = Get-ReleaseSource $productRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts'))
$fixtureRoot = Join-Path $artifactRoot ('validation\launcher-release-' + [Guid]::NewGuid().ToString('N'))
$validator = Join-Path $PSScriptRoot 'Validate-SharedArtifact.ps1'
$script:checks = 0

function Assert-Fixture {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
    $script:checks++
}

function Expect-Rejection {
    param([scriptblock]$Action, [string]$MessagePattern)
    $errorMessage = $null
    try { & $Action | Out-Null } catch { $errorMessage = $_.Exception.Message }
    Assert-Fixture ($null -ne $errorMessage -and $errorMessage -like $MessagePattern) "Expected rejection '$MessagePattern', got '$errorMessage'."
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    # Evaluate all production entry points and the launcher library to catch version overrides.
    foreach ($project in @('LogReader.App', 'LogReader.Mcp', 'LogReader.Launcher', 'LogReader.Launcher.Desktop', 'LogReader.Launcher.Mcp')) {
        $projectPath = Join-Path $productRoot "$project\$project.csproj"
        $properties = & dotnet msbuild $projectPath -nologo -getProperty:Version,AssemblyVersion,FileVersion,InformationalVersion
        if ($LASTEXITCODE -ne 0) { throw "MSBuild property evaluation failed for $project." }
        $evaluated = ($properties | Out-String | ConvertFrom-Json).Properties
        Assert-Fixture ($evaluated.Version -eq $version -and $evaluated.AssemblyVersion -eq "$version.0" -and
            $evaluated.FileVersion -eq "$version.0" -and $evaluated.InformationalVersion -eq $version) "Wrong evaluated versions for $project."
    }

    foreach ($binary in @('LogReader.App\bin\Release\net10.0-windows\WeezTail.exe',
        'LogReader.Mcp\bin\Release\net10.0\WeezTail.Mcp.exe',
        'LogReader.Launcher\bin\Release\net10.0\LogReader.Launcher.dll')) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $productRoot $binary))
        Assert-Fixture ($info.FileVersion -eq "$version.0" -and $info.ProductVersion -eq "$version+$($source.commit)") "Wrong built version or source commit for $binary."
    }

    $payload = Join-Path $fixtureRoot 'Launchers'
    New-Item -ItemType Directory -Path $payload | Out-Null
    foreach ($pair in @(@('LogReader.Launcher.Desktop', 'net10.0-windows', 'WeezTail.Launcher.exe'),
        @('LogReader.Launcher.Mcp', 'net10.0', 'WeezTail.Mcp.Launcher.exe'))) {
        $binary = if ($LauncherDirectory) { Join-Path $LauncherDirectory $pair[2] } else {
            Join-Path $productRoot "$($pair[0])\bin\Release\$($pair[1])\$($pair[2])"
        }
        Copy-Item -LiteralPath $binary -Destination $payload
    }
    Assert-LauncherExecutables $payload $version $source.commit
    $wrongVersion = if ($version -eq '9.8.7') { '9.8.6' } else { '9.8.7' }
    Expect-Rejection { Assert-LauncherExecutables $payload $wrongVersion $source.commit } '*metadata mismatch*'
    $wrongCommit = if ($source.commit -eq ('b' * 40)) { 'c' * 40 } else { 'b' * 40 }
    Expect-Rejection { Assert-LauncherExecutables $payload $version $wrongCommit } '*metadata mismatch*'
    Expect-Rejection { Assert-LauncherExecutables $payload $version 'invalid' } '*full Git SHA*'

    # A stale maintenance example must never select the shared application's release.
    @{ version = $wrongVersion } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $payload 'current.example.json')
    & $validator -PublishDirectory $payload -LauncherOnly
    $zipPath = Join-Path $fixtureRoot "WeezTail-$version-launchers-win-x64.zip"
    New-ReleaseZip $payload $zipPath
    & $validator -ZipPath $zipPath -LauncherOnly
    foreach ($entryName in @('../escape.exe', 'WEezTail.Launcher.exe', 'link.exe')) {
        New-ReleaseZip $payload $zipPath
        $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = $archive.CreateEntry($entryName)
            if ($entryName -eq 'link.exe') { $entry.ExternalAttributes = -1577058304 } # Unix symlink type.
        } finally { $archive.Dispose() }
        Expect-Rejection { & $validator -ZipPath $zipPath -LauncherOnly } '*unsafe*'
    }

    $application = Join-Path $fixtureRoot 'Application'
    New-Item -ItemType Directory -Path $application | Out-Null
    Set-Content -LiteralPath (Join-Path $application 'WeezTail.exe') -Value 'app fixture'
    Set-Content -LiteralPath (Join-Path $application 'WeezTail.Mcp.exe') -Value 'mcp fixture'
    Set-Content -LiteralPath (Join-Path $application 'WeezTail.install.json') -Value '{"installMode":"Portable","storageMode":"LocalAppData"}'
    $shared = Join-Path $fixtureRoot 'Shared'
    New-SharedRelease $shared $application $payload $version
    & $validator -PublishDirectory $shared
    $pointer = Get-Content -LiteralPath (Join-Path $shared 'current.json') -Raw | ConvertFrom-Json
    Assert-Fixture ($pointer.version -eq $version) 'Shared pointer did not select the application version.'
    foreach ($name in @('WeezTail.Launcher.exe', 'WeezTail.Mcp.Launcher.exe')) {
        Assert-Fixture ((Get-ReleaseAsset (Join-Path $shared $name)).sha256 -eq
            (Get-ReleaseAsset (Join-Path $payload $name)).sha256) "Shared assembly changed $name bytes."
    }
    Assert-Fixture (-not (Test-Path -LiteralPath (Join-Path $shared 'current.example.json'))) 'Shared assembly copied the maintenance example.'
    $sharedZip = Join-Path $fixtureRoot "WeezTail-$version-shared-win-x64.zip"
    New-ReleaseZip $shared $sharedZip
    & $validator -ZipPath $sharedZip
    Remove-Item -LiteralPath (Join-Path $payload 'WeezTail.Mcp.Launcher.exe')
    Expect-Rejection { & $validator -PublishDirectory $payload -LauncherOnly } '*missing*'
    Write-Host "Unified launcher release validation passed ($script:checks checks)."
}
finally {
    $resolvedRoot = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture cleanup must remain within artifacts.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
