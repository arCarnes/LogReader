param(
    [Parameter(Mandatory = $true)][string]$Version,
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64',
    [string]$Repository = 'arCarnes/LogReader',
    [string]$PackagePath,
    [string]$ReleaseMetadataPath,
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleasePackaging.ps1')
Assert-ReleaseVersion $Version 'Pinned launcher version'
if ($Repository -notmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'Invalid GitHub repository.' }
if ($ReleaseMetadataPath -and -not $PackagePath) { throw 'Offline metadata requires a local PackagePath.' }
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $productRoot 'artifacts'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifactRoot 'publish\Launchers' }
$outputDir = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $outputDir.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Launcher output must remain within artifacts.'
}
$cacheDir = $outputDir + '.inputs'
New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
$stage = Join-Path $cacheDir ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$tag = "launchers-v$Version"
$assetName = "WeezTail-$Version-launchers-$Runtime.zip"

try {
    if ($ReleaseMetadataPath) {
        $metadata = Get-Content -LiteralPath $ReleaseMetadataPath -Raw | ConvertFrom-Json
    }
    else {
        try {
            $release = Invoke-ReleaseGitHub @('api', "repos/$Repository/releases/tags/$tag") | ConvertFrom-Json
            if ($release.tag_name -ne $tag -or $release.draft -ne $false -or
                $release.prerelease -ne $false -or -not $release.published_at) {
                throw "Expected published stable launcher release '$tag'."
            }
            $assets = @($release.assets | Where-Object { $_.name -ceq $assetName -and $_.state -eq 'uploaded' })
            if ($assets.Count -ne 1) { throw "Release '$tag' must contain exactly one '$assetName' asset." }
            $ref = Invoke-ReleaseGitHub @('api', "repos/$Repository/git/ref/tags/$tag") | ConvertFrom-Json
            if ($ref.object.type -ne 'tag') { throw 'Launcher release requires an annotated tag.' }
            $annotated = Invoke-ReleaseGitHub @('api', "repos/$Repository/git/tags/$($ref.object.sha)") | ConvertFrom-Json
            if ($annotated.tag -ne $tag -or $annotated.object.type -ne 'commit') { throw 'Invalid launcher release tag target.' }
            $metadata = [pscustomobject]@{
                schemaVersion = 1; repository = $Repository; tagName = $tag; version = $Version
                sourceCommit = $annotated.object.sha; releaseId = $release.id
                asset = [pscustomobject]@{ name = $assets[0].name; size = $assets[0].size; digest = $assets[0].digest }
            }
        }
        catch { throw "Cannot resolve pinned launcher release '$tag'. Publish and verify it before packaging this application. $($_.Exception.Message)" }
    }

    if ($metadata.schemaVersion -ne 1 -or $metadata.repository -cne $Repository -or
        $metadata.tagName -cne $tag -or $metadata.version -cne $Version -or
        $metadata.sourceCommit -notmatch '\A[0-9a-f]{40}\z' -or
        ($metadata.releaseId -isnot [long] -and $metadata.releaseId -isnot [int]) -or
        $metadata.releaseId -le 0 -or $metadata.asset.name -cne $assetName -or
        ($metadata.asset.size -isnot [long] -and $metadata.asset.size -isnot [int]) -or $metadata.asset.size -le 0 -or
        $metadata.asset.digest -notmatch '\Asha256:[0-9a-f]{64}\z') {
        throw "Missing or mismatched verification metadata for pinned launcher release '$tag'."
    }

    $stagedZip = Join-Path $stage $assetName
    if ($PackagePath) {
        Copy-Item -LiteralPath $PackagePath -Destination $stagedZip
    }
    else {
        $null = Invoke-ReleaseGitHub @('release', 'download', $tag, '--repo', $Repository,
            '--pattern', $assetName, '--dir', $stage)
    }
    $asset = Get-ReleaseAsset $stagedZip
    if ($asset.size -ne $metadata.asset.size -or "sha256:$($asset.sha256)" -cne $metadata.asset.digest) {
        throw "Launcher package size or SHA-256 mismatch for '$tag'."
    }

    # The validator rejects traversal, links and duplicate entries before extraction.
    & (Join-Path $PSScriptRoot 'Validate-SharedArtifact.ps1') -ZipPath $stagedZip -LauncherOnly | Out-Host
    $extracted = Join-Path $stage 'extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory($stagedZip, $extracted)
    Assert-LauncherExecutables $extracted $Version $metadata.sourceCommit

    $cachedZip = Join-Path $cacheDir $assetName
    $cachedMetadata = Join-Path $cacheDir "$assetName.release.json"
    Copy-Item -LiteralPath $stagedZip -Destination $cachedZip -Force
    $metadata | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $cachedMetadata -Encoding UTF8
    if (-not $outputDir.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Launcher output must remain within artifacts.'
    }
    if (Test-Path -LiteralPath $outputDir) { Remove-Item -LiteralPath $outputDir -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path -Parent $outputDir) -Force | Out-Null
    Move-Item -LiteralPath $extracted -Destination $outputDir
    [pscustomobject]@{
        version = $Version; sourceCommit = $metadata.sourceCommit
        repository = $Repository; tagName = $tag; releaseId = $metadata.releaseId
        packagePath = $cachedZip; metadataPath = $cachedMetadata; package = $asset
        executables = @(Get-ReleaseAsset (Join-Path $outputDir 'WeezTail.Launcher.exe');
            Get-ReleaseAsset (Join-Path $outputDir 'WeezTail.Mcp.Launcher.exe'))
    }
}
finally {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    if (-not $resolvedStage.StartsWith($cacheDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Launcher staging cleanup must remain within its cache.'
    }
    if (Test-Path -LiteralPath $resolvedStage) { Remove-Item -LiteralPath $resolvedStage -Recurse -Force }
}
