# Loaded only by the explicit publisher and mocked publication fixtures.
function Assert-UploadedLauncherAsset {
    param($Release, [string]$Tag, $Asset, [bool]$Draft)
    $uploaded = @($Release.assets | Where-Object { $_.name -ceq $Asset.name })
    if ($Release.tag_name -cne $Tag -or $Release.draft -ne $Draft -or $Release.prerelease -ne $false -or
        $Release.assets.Count -ne 1 -or $uploaded.Count -ne 1 -or $uploaded[0].state -ne 'uploaded' -or
        $uploaded[0].size -ne $Asset.size -or $uploaded[0].digest -cne "sha256:$($Asset.sha256)") {
        throw 'Launcher release asset verification failed; inspect the release before continuing.'
    }
}

function Publish-VerifiedLauncherAsset {
    param([string]$Repository, [string]$Version, [string]$ZipPath,
        [string]$NotesFile, [string]$LatestApplicationTag)
    $tag = "launchers-v$Version"
    $asset = Get-ReleaseAsset $ZipPath
    $null = Invoke-ReleaseGitHub @('release', 'create', $tag, $ZipPath, '--repo', $Repository, '--verify-tag',
        '--draft', '--latest=false', '--title', "WeezTail Launchers $Version", '--notes-file', $NotesFile)
    $draft = Invoke-ReleaseGitHub @('api', "repos/$Repository/releases/tags/$tag") | ConvertFrom-Json
    Assert-UploadedLauncherAsset $draft $tag $asset $true
    $null = Invoke-ReleaseGitHub @('release', 'edit', $tag, '--repo', $Repository, '--draft=false', '--latest=false')
    $published = Invoke-ReleaseGitHub @('api', "repos/$Repository/releases/tags/$tag") | ConvertFrom-Json
    Assert-UploadedLauncherAsset $published $tag $asset $false
    $latest = Invoke-ReleaseGitHub @('api', "repos/$Repository/releases/latest") | ConvertFrom-Json
    if ($latest.tag_name -ne $LatestApplicationTag) {
        throw 'Latest application selection changed during launcher publication; inspect GitHub releases.'
    }
    return $published
}
