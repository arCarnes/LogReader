# Shared by the release entry points and their offline regression fixtures.
function Assert-ReleaseVersion {
    param([string]$Version, [string]$Name = 'Version')
    $parsed = $null
    if ($Version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z' -or
        -not [Version]::TryParse($Version, [ref]$parsed)) {
        throw "$Name must have three numeric version fields."
    }
}

function Get-ReleaseVersions {
    param([string]$ProductRoot)
    [xml]$application = Get-Content -LiteralPath (Join-Path $ProductRoot 'Directory.Build.props')
    [xml]$launcher = Get-Content -LiteralPath (Join-Path $ProductRoot 'Launcher.Version.props')
    $versions = [ordered]@{
        application = [string]$application.Project.PropertyGroup.Version
        launcher = [string]$launcher.Project.PropertyGroup.LauncherVersion
        bundledLauncher = [string]$application.Project.PropertyGroup.BundledLauncherVersion
    }
    foreach ($name in $versions.Keys) { Assert-ReleaseVersion $versions[$name] $name }
    return [pscustomobject]$versions
}

function Get-ReleaseSource {
    param([string]$ProductRoot)
    $commit = & git -C $ProductRoot rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '\A[0-9a-f]{40}\z') {
        throw 'Could not resolve release source commit.'
    }
    $changes = @(& git -C $ProductRoot status --porcelain)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect release source state.' }
    return [pscustomobject]@{ commit = $commit; dirty = $changes.Count -ne 0 }
}

function Get-ReleaseAsset {
    param([string]$Path)
    $file = Get-Item -LiteralPath $Path -ErrorAction Stop
    return [pscustomobject]@{
        name = $file.Name
        size = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}

function New-ReleaseZip {
    param([string]$SourceDirectory, [string]$DestinationPath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $DestinationPath) { Remove-Item -LiteralPath $DestinationPath -Force }
    [IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path -LiteralPath $SourceDirectory).Path, $DestinationPath,
        [IO.Compression.CompressionLevel]::Optimal, $false)
}

function Assert-ReleaseZipEntries {
    param([System.IO.Compression.ZipArchive]$Zip)
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        $unixType = ($entry.ExternalAttributes -shr 16) -band 0xF000
        if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name.Contains(':') -or
            @($name.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' }).Count -ne 0 -or
            -not $seen.Add($name) -or $unixType -eq 0xA000 -or
            ($entry.ExternalAttributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Release ZIP contains an unsafe, redirected or duplicate entry '$name'."
        }
    }
}

function Assert-LauncherExecutables {
    param([string]$Directory, [string]$Version, [string]$SourceCommit)
    Assert-ReleaseVersion $Version 'Launcher version'
    if ($SourceCommit -notmatch '\A[0-9a-f]{40}\z') { throw 'Launcher source commit must be a full Git SHA.' }
    foreach ($name in @('WeezTail.Launcher.exe', 'WeezTail.Mcp.Launcher.exe')) {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Directory $name))
        if ($info.FileVersion -ne "$Version.0" -or $info.ProductVersion -ne "$Version+$SourceCommit") {
            throw "Launcher metadata mismatch for '$name': expected $Version at $SourceCommit, found '$($info.ProductVersion)'."
        }
    }
}

function New-SharedRelease {
    param([string]$OutputDirectory, [string]$ApplicationDirectory,
        [string]$LauncherDirectory, [string]$ApplicationVersion)
    Assert-ReleaseVersion $ApplicationVersion 'Application version'
    $release = Join-Path $OutputDirectory "releases\$ApplicationVersion"
    New-Item -ItemType Directory -Path $release -Force | Out-Null
    foreach ($name in @('WeezTail.Launcher.exe', 'WeezTail.Mcp.Launcher.exe')) {
        Copy-Item -LiteralPath (Join-Path $LauncherDirectory $name) -Destination $OutputDirectory
    }
    @{ version = $ApplicationVersion } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $OutputDirectory 'current.json') -Encoding UTF8
    Get-ChildItem -LiteralPath $ApplicationDirectory -File | Copy-Item -Destination $release
}

function Invoke-ReleaseGitHub {
    param([string[]]$Arguments)
    $output = & gh @Arguments | Out-String
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: gh $($Arguments -join ' ')" }
    return $output
}
