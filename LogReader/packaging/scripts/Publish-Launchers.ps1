param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'ReleasePackaging.ps1')
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$version = Get-ReleaseVersion $productRoot
$source = Get-ReleaseSource $productRoot
$outputDir = [System.IO.Path]::GetFullPath((Join-Path $productRoot "artifacts\publish\Launchers"))
$publishRoot = [System.IO.Path]::GetFullPath((Join-Path $productRoot "artifacts\publish"))
if (-not $outputDir.StartsWith($publishRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Launcher output must remain within '$publishRoot'."
}
if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

foreach ($projectName in @("LogReader.Launcher.Desktop", "LogReader.Launcher.Mcp")) {
    $projectPath = Join-Path $productRoot "$projectName\$projectName.csproj"
    & dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true `
        /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:DebugType=None /p:DebugSymbols=false /p:NuGetAudit=false -o $outputDir
    if ($LASTEXITCODE -ne 0) {
        throw "Launcher publish failed for '$projectName'."
    }
}

@{ version = $version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDir "current.example.json") -Encoding UTF8
Assert-LauncherExecutables $outputDir $version $source.commit
& (Join-Path $PSScriptRoot "Validate-SharedArtifact.ps1") -PublishDirectory $outputDir -LauncherOnly
$zipPath = Join-Path $publishRoot "WeezTail-$version-launchers-$Runtime.zip"
New-ReleaseZip $outputDir $zipPath
& (Join-Path $PSScriptRoot 'Validate-SharedArtifact.ps1') -ZipPath $zipPath -LauncherOnly
[ordered]@{
    schemaVersion = 1; stream = 'application'; applicationVersion = $version
    source = $source; tag = "v$version"; assets = @(Get-ReleaseAsset $zipPath)
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $publishRoot 'launcher-build-evidence.json') -Encoding UTF8
Write-Host "Launchers published to $outputDir"
Write-Host "Launcher ZIP created at $zipPath"
