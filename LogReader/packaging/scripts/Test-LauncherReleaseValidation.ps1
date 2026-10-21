param([string]$LauncherDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleasePackaging.ps1')
. (Join-Path $PSScriptRoot 'LauncherPublication.ps1')
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$versions = Get-ReleaseVersions $productRoot
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts'))
$fixtureRoot = Join-Path $artifactRoot ('validation\launcher-release-' + [Guid]::NewGuid().ToString('N'))
$resolver = Join-Path $PSScriptRoot 'Get-LauncherRelease.ps1'
$validator = Join-Path $PSScriptRoot 'Validate-SharedArtifact.ps1'
$metadataPath = Join-Path $fixtureRoot 'published.json'
$output = Join-Path $fixtureRoot 'Imported'
$repository = 'fixture/launchers'
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

function Write-FixtureMetadata {
    param([string]$ZipPath)
    $asset = Get-ReleaseAsset $ZipPath
    $script:metadata = [pscustomobject]@{
        schemaVersion = 1; repository = $repository; tagName = $tag; version = $version
        sourceCommit = $sourceCommit; releaseId = 123
        asset = [pscustomobject]@{ name = $assetName; size = $asset.size; digest = "sha256:$($asset.sha256)" }
    }
    $script:metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath
    if (Get-Variable WeezTailLauncherFixture -Scope Global -ErrorAction SilentlyContinue) {
        $global:WeezTailLauncherFixture.Metadata = $script:metadata
    }
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    # Check evaluated properties rather than text matching the import statements.
    foreach ($project in @('LogReader.App', 'LogReader.Mcp', 'LogReader.Launcher', 'LogReader.Launcher.Desktop', 'LogReader.Launcher.Mcp')) {
        $projectPath = Join-Path $productRoot "$project\$project.csproj"
        $properties = & dotnet msbuild $projectPath -nologo -getProperty:Version,AssemblyVersion,FileVersion,InformationalVersion
        if ($LASTEXITCODE -ne 0) { throw "MSBuild property evaluation failed for $project." }
        $evaluated = ($properties | Out-String | ConvertFrom-Json).Properties
        $expected = if ($project -in @('LogReader.Launcher', 'LogReader.Launcher.Desktop', 'LogReader.Launcher.Mcp')) {
            $versions.launcher
        } else { $versions.application }
        Assert-Fixture ($evaluated.Version -eq $expected -and $evaluated.AssemblyVersion -eq "$expected.0" -and
            $evaluated.FileVersion -eq "$expected.0" -and $evaluated.InformationalVersion -eq $expected) "Wrong evaluated versions for $project."
    }

    $payload = Join-Path $fixtureRoot 'Payload'
    New-Item -ItemType Directory -Path $payload | Out-Null
    foreach ($pair in @(@('LogReader.Launcher.Desktop', 'net10.0-windows', 'WeezTail.Launcher.exe'),
        @('LogReader.Launcher.Mcp', 'net10.0', 'WeezTail.Mcp.Launcher.exe'))) {
        $binary = if ($LauncherDirectory) { Join-Path $LauncherDirectory $pair[2] } else {
            Join-Path $productRoot "$($pair[0])\bin\Release\$($pair[1])\$($pair[2])"
        }
        Copy-Item -LiteralPath $binary -Destination $payload
    }
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'WeezTail.Launcher.exe'))
    $version = ($info.FileVersion -replace '\.0$', '')
    $sourceCommit = ($info.ProductVersion -split '\+', 2)[1]
    Assert-LauncherExecutables $payload $version $sourceCommit
    # This intentionally differs from both the launcher version and shared application version.
    Set-Content -LiteralPath (Join-Path $payload 'current.example.json') -Value '{"version":"9.8.7"}'
    $tag = "launchers-v$version"
    $assetName = "WeezTail-$version-launchers-win-x64.zip"
    $zipPath = Join-Path $fixtureRoot $assetName
    New-ReleaseZip $payload $zipPath
    Write-FixtureMetadata $zipPath

    # Substitute the GitHub boundary only. Every archive/hash/PE check is real.
    $githubRequests = [Collections.Generic.List[string]]::new()
    $global:WeezTailLauncherFixture = [pscustomobject]@{
        Mode = 'valid'; Requests = $githubRequests; ZipPath = $zipPath; AssetName = $assetName
        Tag = $tag; SourceCommit = $sourceCommit; Metadata = $metadata; Published = $false
    }
    function global:gh {
        $global:LASTEXITCODE = 0
        $global:WeezTailLauncherFixture.Requests.Add(($args -join ' '))
        if ($global:WeezTailLauncherFixture.Mode -eq 'offline') { throw 'Offline resolution attempted network access.' }
        if ($global:WeezTailLauncherFixture.Mode -eq 'missing') { $global:LASTEXITCODE = 1; return }
        if ($global:WeezTailLauncherFixture.Mode -like 'publish*') {
            if ($args[0] -eq 'release') {
                $global:WeezTailLauncherFixture.Published = $args[1] -eq 'edit'
                return
            }
            if ($args[1] -like '*/releases/latest') {
                $latest = if ($global:WeezTailLauncherFixture.Mode -eq 'publish-latest-changed') { 'v0.0.0' } else { 'v4.5.6' }
                @{ tag_name = $latest } | ConvertTo-Json
                return
            }
            $digest = if ($global:WeezTailLauncherFixture.Mode -eq 'publish-bad-digest') { 'sha256:' + ('0' * 64) } else {
                $global:WeezTailLauncherFixture.Metadata.asset.digest
            }
            @{ tag_name = $global:WeezTailLauncherFixture.Tag; draft = -not $global:WeezTailLauncherFixture.Published; prerelease = $false
                assets = @(@{ name = $global:WeezTailLauncherFixture.AssetName; size = $global:WeezTailLauncherFixture.Metadata.asset.size
                    digest = $digest; state = 'uploaded' }) } | ConvertTo-Json -Depth 8
            return
        }
        if ($args[0] -eq 'release' -and $args[1] -eq 'download') {
            $directory = $args[[Array]::IndexOf($args, '--dir') + 1]
            Copy-Item -LiteralPath $global:WeezTailLauncherFixture.ZipPath -Destination (Join-Path $directory $global:WeezTailLauncherFixture.AssetName)
            return
        }
        if ($args[1] -like '*/releases/tags/*') {
            $releaseAssets = if ($global:WeezTailLauncherFixture.Mode -eq 'missing-asset') { @() } else {
                @(@{ name = $global:WeezTailLauncherFixture.AssetName; size = $global:WeezTailLauncherFixture.Metadata.asset.size; digest = $global:WeezTailLauncherFixture.Metadata.asset.digest; state = 'uploaded' })
            }
            @{ id = 123; tag_name = $global:WeezTailLauncherFixture.Tag; draft = ($global:WeezTailLauncherFixture.Mode -eq 'draft');
                prerelease = ($global:WeezTailLauncherFixture.Mode -eq 'prerelease'); published_at = '2026-10-18'; assets = $releaseAssets } | ConvertTo-Json -Depth 8
        }
        elseif ($args[1] -like '*/git/ref/tags/*') {
            @{ object = @{ type = 'tag'; sha = ('a' * 40) } } | ConvertTo-Json
        }
        elseif ($args[1] -like '*/git/tags/*') {
            @{ tag = $global:WeezTailLauncherFixture.Tag; object = @{ type = 'commit'; sha = $global:WeezTailLauncherFixture.SourceCommit } } | ConvertTo-Json
        }
        else { throw "Unexpected GitHub request: $args" }
    }

    $resolved = & $resolver -Version $version -Repository $repository -OutputDirectory $output
    Assert-Fixture ($resolved.version -eq $version) 'Exact published version was not resolved.'
    Assert-Fixture (@($githubRequests | Where-Object { $_ -match 'latest' }).Count -eq 0) 'Resolver queried latest.'
    Assert-Fixture ($githubRequests[0] -ceq "api repos/$repository/releases/tags/$tag") 'Resolver did not query the exact pinned tag.'
    $global:WeezTailLauncherFixture.Mode = 'offline'
    $resolved = & $resolver -Version $version -Repository $repository -PackagePath $resolved.packagePath `
        -ReleaseMetadataPath $resolved.metadataPath -OutputDirectory $output
    foreach ($name in @('WeezTail.Launcher.exe', 'WeezTail.Mcp.Launcher.exe')) {
        Assert-Fixture ((Get-ReleaseAsset (Join-Path $output $name)).sha256 -eq
            (Get-ReleaseAsset (Join-Path $payload $name)).sha256) 'Offline import changed executable bytes.'
    }
    $validHash = (Get-ReleaseAsset (Join-Path $output 'WeezTail.Launcher.exe')).sha256

    foreach ($mode in @('missing', 'missing-asset', 'draft', 'prerelease')) {
        $global:WeezTailLauncherFixture.Mode = $mode
        Expect-Rejection { & $resolver -Version $version -Repository $repository -OutputDirectory $output } '*Cannot resolve pinned launcher release*'
    }
    $global:WeezTailLauncherFixture.Mode = 'offline'
    foreach ($field in @('digest', 'size', 'name')) {
        Write-FixtureMetadata $zipPath
        switch ($field) {
            'digest' { $metadata.asset.digest = 'sha256:' + ('0' * 64) }
            'size' { $metadata.asset.size++ }
            'name' { $metadata.asset.name = 'other.zip' }
        }
        $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath
        Expect-Rejection { & $resolver -Version $version -Repository $repository -PackagePath $zipPath `
            -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*mismatch*'
    }
    Write-FixtureMetadata $zipPath
    $metadata.asset.digest = $null
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath
    Expect-Rejection { & $resolver -Version $version -Repository $repository -PackagePath $zipPath `
        -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*verification metadata*'
    Write-FixtureMetadata $zipPath
    Expect-Rejection { & $resolver -Version '8.7.6' -Repository $repository -PackagePath $zipPath `
        -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*verification metadata*'
    $metadata.sourceCommit = 'b' * 40
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $metadataPath
    Expect-Rejection { & $resolver -Version $version -Repository $repository -PackagePath $zipPath `
        -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*metadata mismatch*'

    Copy-Item -LiteralPath (Join-Path $productRoot 'LogReader.App\bin\Release\net10.0-windows\WeezTail.exe') `
        -Destination (Join-Path $payload 'WeezTail.Mcp.Launcher.exe') -Force
    New-ReleaseZip $payload $zipPath
    Write-FixtureMetadata $zipPath
    Expect-Rejection { & $resolver -Version $version -Repository $repository -PackagePath $zipPath `
        -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*metadata mismatch*'
    Copy-Item -LiteralPath (Join-Path $output 'WeezTail.Mcp.Launcher.exe') -Destination $payload -Force

    foreach ($entryName in @('../escape.exe', 'WEezTail.Launcher.exe', 'link.exe')) {
        New-ReleaseZip $payload $zipPath
        $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Update)
        try {
            $entry = $archive.CreateEntry($entryName)
            if ($entryName -eq 'link.exe') { $entry.ExternalAttributes = -1577058304 } # Unix symlink type.
        } finally { $archive.Dispose() }
        Write-FixtureMetadata $zipPath
        Expect-Rejection { & $resolver -Version $version -Repository $repository -PackagePath $zipPath `
            -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*unsafe*'
    }
    New-ReleaseZip $payload $zipPath
    Remove-Item -LiteralPath (Join-Path $payload 'WeezTail.Mcp.Launcher.exe')
    New-ReleaseZip $payload $zipPath
    Write-FixtureMetadata $zipPath
    Expect-Rejection { & $resolver -Version $version -Repository $repository -PackagePath $zipPath `
        -ReleaseMetadataPath $metadataPath -OutputDirectory $output } '*missing*'
    Assert-Fixture ((Get-ReleaseAsset (Join-Path $output 'WeezTail.Launcher.exe')).sha256 -eq $validHash) 'Rejected input changed the previously validated output.'

    Copy-Item -LiteralPath (Join-Path $output 'WeezTail.Mcp.Launcher.exe') -Destination $payload
    New-ReleaseZip $payload $zipPath
    Write-FixtureMetadata $zipPath
    $global:WeezTailLauncherFixture.Mode = 'publish-valid'
    $githubRequests.Clear()
    $null = Publish-VerifiedLauncherAsset $repository $version $zipPath 'fixture-notes.md' 'v4.5.6'
    $mutations = @($githubRequests | Where-Object { $_ -like 'release *' })
    Assert-Fixture ($mutations.Count -eq 2 -and
        @($mutations | Where-Object { $_ -notlike '*--latest=false*' }).Count -eq 0) 'Launcher publication could change application latest.'
    Assert-Fixture ($mutations[0] -like '*--verify-tag*--draft*') 'Publication did not start with a verified-tag draft.'
    $global:WeezTailLauncherFixture.Mode = 'publish-bad-digest'
    $githubRequests.Clear()
    Expect-Rejection { Publish-VerifiedLauncherAsset $repository $version $zipPath 'fixture-notes.md' 'v4.5.6' } '*verification failed*'
    Assert-Fixture (@($githubRequests | Where-Object { $_ -like 'release edit *' }).Count -eq 0) 'Unverified draft was published.'
    $global:WeezTailLauncherFixture.Mode = 'publish-latest-changed'
    Expect-Rejection { Publish-VerifiedLauncherAsset $repository $version $zipPath 'fixture-notes.md' 'v4.5.6' } '*Latest application selection changed*'

    $application = Join-Path $fixtureRoot 'Application'
    New-Item -ItemType Directory -Path $application | Out-Null
    Set-Content -LiteralPath (Join-Path $application 'WeezTail.exe') -Value 'app fixture'
    Set-Content -LiteralPath (Join-Path $application 'WeezTail.Mcp.exe') -Value 'mcp fixture'
    Set-Content -LiteralPath (Join-Path $application 'WeezTail.install.json') -Value '{"installMode":"Portable","storageMode":"LocalAppData"}'
    $shared = Join-Path $fixtureRoot 'Shared'
    New-SharedRelease $shared $application $output '4.5.6'
    & $validator -PublishDirectory $shared
    $pointer = Get-Content -LiteralPath (Join-Path $shared 'current.json') -Raw | ConvertFrom-Json
    Assert-Fixture ($pointer.version -eq '4.5.6') 'Shared pointer used the launcher version or example pointer.'
    Assert-Fixture ((Get-ReleaseAsset (Join-Path $shared 'WeezTail.Launcher.exe')).sha256 -eq $validHash) 'Shared assembly changed launcher bytes.'
    Write-Host "Launcher release validation passed ($script:checks checks)."
}
finally {
    if (Test-Path Function:\gh) { Remove-Item Function:\gh }
    Remove-Variable WeezTailLauncherFixture -Scope Global -ErrorAction SilentlyContinue
    $resolvedRoot = [IO.Path]::GetFullPath($fixtureRoot)
    if (-not $resolvedRoot.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture cleanup must remain within artifacts.'
    }
    if (Test-Path -LiteralPath $resolvedRoot) { Remove-Item -LiteralPath $resolvedRoot -Recurse -Force }
}
