param(
    [Parameter(Mandatory = $true)][string]$NotesFile,
    [string]$Repository = 'arCarnes/LogReader',
    [ValidateSet('win-x64')][string]$Runtime = 'win-x64'
)

# Explicit publication entry point. Building packages alone never invokes this script.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleasePackaging.ps1')
. (Join-Path $PSScriptRoot 'LauncherPublication.ps1')
if ($Repository -notmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'Invalid GitHub repository.' }
if (-not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw 'Release notes file was not found.' }
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$versions = Get-ReleaseVersions $productRoot
$source = Get-ReleaseSource $productRoot
if ($source.dirty) { throw 'Publish launcher releases only from a clean, committed source tree.' }
$tag = "launchers-v$($versions.launcher)"
$tagType = & git -C $productRoot cat-file -t "refs/tags/$tag"
if ($LASTEXITCODE -ne 0 -or $tagType -ne 'tag') { throw "Create the annotated '$tag' tag after validation first." }
$tagCommit = & git -C $productRoot rev-parse "$tag^{}"
if ($LASTEXITCODE -ne 0 -or $tagCommit -ne $source.commit) { throw 'Launcher tag must identify the current source commit.' }
$remote = Invoke-ReleaseGitHub @('api', "repos/$Repository/git/ref/tags/$tag") | ConvertFrom-Json
$localTag = & git -C $productRoot rev-parse "refs/tags/$tag"
if ($LASTEXITCODE -ne 0 -or $remote.object.type -ne 'tag' -or $remote.object.sha -ne $localTag) {
    throw 'Push the exact annotated launcher tag before publication.'
}
$existing = Invoke-ReleaseGitHub @('api', "repos/$Repository/releases?per_page=100") | ConvertFrom-Json
if (@($existing | Where-Object tag_name -eq $tag).Count -ne 0) { throw "Release '$tag' already exists; never replace published launcher assets." }
$latestBefore = Invoke-ReleaseGitHub @('api', "repos/$Repository/releases/latest") | ConvertFrom-Json
if ($latestBefore.tag_name -notmatch '\Av[0-9]+\.[0-9]+\.[0-9]+\z') { throw 'Expected an application as the latest stable release.' }

& (Join-Path $PSScriptRoot 'Publish-Launchers.ps1') -Runtime $Runtime
$afterBuild = Get-ReleaseSource $productRoot
if ($afterBuild.dirty -or $afterBuild.commit -ne $source.commit) { throw 'Source changed during launcher packaging.' }
$zip = Join-Path $productRoot "artifacts\publish\WeezTail-$($versions.launcher)-launchers-$Runtime.zip"
$asset = Get-ReleaseAsset $zip
$published = Publish-VerifiedLauncherAsset $Repository $versions.launcher $zip $NotesFile $latestBefore.tag_name
[ordered]@{ tag = $tag; sourceCommit = $source.commit; asset = $asset; latestApplication = $latestBefore.tag_name } |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $productRoot 'artifacts\publish\launcher-publication-evidence.json') -Encoding UTF8
Write-Host "Published $($published.html_url); application latest remains $($latestBefore.tag_name)."
