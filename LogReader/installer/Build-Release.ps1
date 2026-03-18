param(
    [string]$Configuration = "Release",
    [string]$RuntimeIdentifier = "win-x64",
    [string]$PublishRoot,
    [string]$ArtifactsRoot
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

function Copy-DirectoryContents {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourcePath,

        [Parameter(Mandatory = $true)]
        [string]$DestinationPath
    )

    Get-ChildItem -LiteralPath $SourcePath -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $DestinationPath -Recurse -Force
    }
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

function Write-RuntimeConfiguration {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$StorageRoot
    )

    $payload = [ordered]@{
        storageRoot = $StorageRoot
    }

    $payload |
        ConvertTo-Json -Depth 2 |
        Set-Content -LiteralPath $Path -Encoding ASCII
}

$installerRoot = Split-Path -Parent $PSCommandPath
$productRoot = [System.IO.Path]::GetFullPath((Join-Path $installerRoot ".."))
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $productRoot ".."))
$resolvedArtifactsRoot = if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    Join-Path $repoRoot "artifacts\release"
}
else {
    [System.IO.Path]::GetFullPath($ArtifactsRoot)
}

$propsPath = Join-Path $productRoot "Directory.Build.props"
$appProjectPath = Join-Path $productRoot "LogReader.App\LogReader.App.csproj"
$setupProjectPath = Join-Path $installerRoot "LogReader.Setup\LogReader.Setup.wixproj"
$version = Get-ProjectVersion -PropsPath $propsPath
$executableName = Get-AppExecutableName -ProjectPath $appProjectPath -FallbackName "LogReader.App"

$publishArtifactName = "LogReader-$version-$RuntimeIdentifier-publish"
$portableArtifactName = "LogReader-$version-$RuntimeIdentifier-portable"
$msiArtifactBaseName = "LogReader-$version-$RuntimeIdentifier"

$publishArtifactRoot = Join-Path $resolvedArtifactsRoot "publish\$publishArtifactName"
$msiPayloadRoot = Join-Path $resolvedArtifactsRoot "msi\payload"
$portableStageRoot = Join-Path $resolvedArtifactsRoot "portable\$portableArtifactName"
$outputRoot = Join-Path $resolvedArtifactsRoot "output"
$portableZipPath = Join-Path $outputRoot "$portableArtifactName.zip"
$finalMsiPath = Join-Path $outputRoot "$msiArtifactBaseName.msi"
$setupSourceDir = Join-Path $installerRoot "LogReader.Setup\SourceDir"

Reset-Directory -Path $outputRoot
Reset-Directory -Path $msiPayloadRoot
Reset-Directory -Path $portableStageRoot

$resolvedPublishRoot = if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    Reset-Directory -Path $publishArtifactRoot

    $publishArguments = @(
        "publish"
        $appProjectPath
        "-c"
        $Configuration
        "-r"
        $RuntimeIdentifier
        "--self-contained"
        "true"
        "-p:PublishSingleFile=false"
        "-p:PublishReadyToRun=false"
        "-o"
        $publishArtifactRoot
        "-nologo"
    )

    & dotnet @publishArguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $publishArtifactRoot
}
else {
    [System.IO.Path]::GetFullPath($PublishRoot)
}

if (-not (Test-Path -LiteralPath $resolvedPublishRoot)) {
    throw "The publish root '$resolvedPublishRoot' does not exist."
}

$publishedExecutablePath = Join-Path $resolvedPublishRoot $executableName
if (-not (Test-Path -LiteralPath $publishedExecutablePath)) {
    throw "Expected published executable '$publishedExecutablePath' was not found."
}

Copy-DirectoryContents -SourcePath $resolvedPublishRoot -DestinationPath $msiPayloadRoot
Copy-DirectoryContents -SourcePath $resolvedPublishRoot -DestinationPath $portableStageRoot

Write-RuntimeConfiguration -Path (Join-Path $msiPayloadRoot "LogReader.runtime.json") -StorageRoot "__MSI_STORAGE_ROOT__"
Write-RuntimeConfiguration -Path (Join-Path $portableStageRoot "LogReader.runtime.json") -StorageRoot ".\LogReaderData"
Reset-Directory -Path $setupSourceDir
Copy-DirectoryContents -SourcePath $msiPayloadRoot -DestinationPath $setupSourceDir

$buildArguments = @(
    "build"
    $setupProjectPath
    "-c"
    $Configuration
    "-nologo"
    "-o"
    $outputRoot
    "-p:InstallerPlatform=x64"
    "-p:Platform=x64"
    "-p:PublishDir=$msiPayloadRoot"
    "-p:ProductVersion=$version"
    "-p:ExecutableName=$executableName"
    "-p:OutputName=$msiArtifactBaseName"
)

try {
    & dotnet @buildArguments

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build for MSI packaging failed with exit code $LASTEXITCODE."
    }
}
finally {
    if (Test-Path -LiteralPath $setupSourceDir) {
        Remove-Item -LiteralPath $setupSourceDir -Recurse -Force
    }
}

$builtMsi = Get-ChildItem -LiteralPath $outputRoot -Filter "$msiArtifactBaseName.msi" -Recurse | Select-Object -First 1
if ($null -eq $builtMsi) {
    throw "The MSI output '$msiArtifactBaseName.msi' was not found under '$outputRoot'."
}

if ($builtMsi.FullName -ne $finalMsiPath) {
    Copy-Item -LiteralPath $builtMsi.FullName -Destination $finalMsiPath -Force
}

if (Test-Path -LiteralPath $portableZipPath) {
    Remove-Item -LiteralPath $portableZipPath -Force
}

Compress-Archive -LiteralPath $portableStageRoot -DestinationPath $portableZipPath

Write-Host "Publish payload: $resolvedPublishRoot"
Write-Host "MSI payload: $msiPayloadRoot"
Write-Host "Portable staging folder: $portableStageRoot"
Write-Host "MSI artifact: $finalMsiPath"
Write-Host "Portable artifact: $portableZipPath"
