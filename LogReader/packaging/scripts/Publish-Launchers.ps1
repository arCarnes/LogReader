param(
    [string]$Configuration = "Release",
    [ValidateSet("win-x64")]
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$productRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
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

[xml]$props = Get-Content (Join-Path $productRoot "Directory.Build.props")
$version = [string]$props.Project.PropertyGroup.Version
if ($version -notmatch '\A[0-9]+\.[0-9]+\.[0-9]+\z') {
    throw "Product version must have three numeric fields."
}
@{ version = $version } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDir "current.example.json") -Encoding UTF8
& (Join-Path $PSScriptRoot "Validate-SharedArtifact.ps1") -PublishDirectory $outputDir -LauncherOnly
Write-Host "Launchers published to $outputDir"
