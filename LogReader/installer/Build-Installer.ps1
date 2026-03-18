param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$SelfContained
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Get-ProjectVersion {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PropsPath
    )

    [xml]$props = Get-Content -LiteralPath $PropsPath -Raw
    $version = @($props.Project.PropertyGroup | ForEach-Object {
        $property = $_.PSObject.Properties["Version"]
        if ($null -ne $property) {
            $property.Value
        }
    }) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Unable to determine the product version from '$PropsPath'."
    }

    return $version.Trim()
}

function Get-AppExecutableName {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath,

        [Parameter(Mandatory = $true)]
        [string]$FallbackName
    )

    [xml]$project = Get-Content -LiteralPath $ProjectPath -Raw
    $assemblyName = @($project.Project.PropertyGroup | ForEach-Object {
        $property = $_.PSObject.Properties["AssemblyName"]
        if ($null -ne $property) {
            $property.Value
        }
    }) |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($assemblyName)) {
        $assemblyName = $FallbackName
    }

    return "$($assemblyName.Trim()).exe"
}

$installerRoot = Split-Path -Parent $PSCommandPath
$productRoot = [System.IO.Path]::GetFullPath((Join-Path $installerRoot ".."))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $productRoot ".."))
$artifactsRoot = Join-Path $repoRoot "artifacts\installer"
$publishRoot = Join-Path $artifactsRoot "publish"
$outputRoot = Join-Path $artifactsRoot "output"
$setupRoot = Join-Path $outputRoot "LogReader-Setup"
$propsPath = Join-Path $productRoot "Directory.Build.props"
$appProjectPath = Join-Path $productRoot "LogReader.App\LogReader.App.csproj"
$version = Get-ProjectVersion -PropsPath $propsPath
$executableName = Get-AppExecutableName -ProjectPath $appProjectPath -FallbackName "LogReader.App"
$versionedSetupRoot = Join-Path $outputRoot "LogReader-Setup-$version"

Reset-Directory -Path $publishRoot
Reset-Directory -Path $setupRoot

$restoreArguments = @(
    "restore"
    $appProjectPath
    "--ignore-failed-sources"
    "-nologo"
)

$publishArguments = @(
    "publish"
    $appProjectPath
    "-c"
    $Configuration
    "--no-restore"
    "-o"
    $publishRoot
    "-nologo"
)

if ($SelfContained) {
    if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
        throw "A runtime identifier is required when publishing a self-contained installer."
    }

    $restoreArguments += @(
        "-r"
        $RuntimeIdentifier
        "-p:SelfContained=true"
    )

    $publishArguments += @(
        "-r"
        $RuntimeIdentifier
        "--self-contained"
        "true"
        "-p:PublishSingleFile=false"
        "-p:PublishReadyToRun=false"
    )
}
else {
    $restoreArguments += @(
        "-p:SelfContained=false"
    )

    $publishArguments += @(
        "--self-contained"
        "false"
    )
}

& dotnet @restoreArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed with exit code $LASTEXITCODE."
}

& dotnet @publishArguments

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutablePath = Join-Path $publishRoot $executableName
if (-not (Test-Path -LiteralPath $publishedExecutablePath)) {
    throw "Expected published executable '$publishedExecutablePath' was not found."
}

$manifest = [ordered]@{
    ProductName         = "LogReader"
    Publisher           = "LogReader"
    Version             = $version
    InstallDirectory    = "LogReader"
    DataDirectory       = "LogReader"
    ShortcutName        = "LogReader"
    ExecutableName      = $executableName
    SupportFiles        = @(
        "installer-manifest.json"
        "Uninstall-LogReader.ps1"
    )
}

$manifestPath = Join-Path $setupRoot "installer-manifest.json"
$payloadRoot = Join-Path $setupRoot "payload"

New-Item -ItemType Directory -Path $payloadRoot | Out-Null

Get-ChildItem -LiteralPath $publishRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $payloadRoot -Recurse -Force
}

$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $manifestPath -Encoding ASCII
Copy-Item -LiteralPath (Join-Path $installerRoot "Setup.cmd") -Destination (Join-Path $setupRoot "Setup.cmd") -Force
Copy-Item -LiteralPath (Join-Path $installerRoot "Install-LogReader.ps1") -Destination (Join-Path $setupRoot "Install-LogReader.ps1") -Force
Copy-Item -LiteralPath (Join-Path $installerRoot "Uninstall-LogReader.ps1") -Destination (Join-Path $setupRoot "Uninstall-LogReader.ps1") -Force

if (Test-Path -LiteralPath $versionedSetupRoot) {
    Remove-Item -LiteralPath $versionedSetupRoot -Recurse -Force
}

Copy-Item -LiteralPath $setupRoot -Destination $versionedSetupRoot -Recurse

Write-Host "Installer files staged at: $setupRoot"
Write-Host "Versioned copy staged at: $versionedSetupRoot"
Write-Host "Run Setup.cmd from the staged folder to install LogReader $version."
