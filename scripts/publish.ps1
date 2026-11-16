[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'linux-x64',

    [Alias('-version')]
    [ValidateNotNullOrEmpty()]
    [string]$Version,

    [switch]$NoWebUIBuild
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot 'Sources'
$projectPath = Join-Path $sourceRoot 'Yohuke.LiveRecorder.Server/Yohuke.LiveRecorder.Server.csproj'
$Version = Get-ReleaseVersion -ProjectPath $projectPath -Runtime $Runtime -Version $Version
$outputDirectory = Join-Path $repoRoot "Artifacts/server/$Version/$Runtime"
$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', 'Release',
    '--runtime', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=embedded',
    '-p:RuntimeFrameworkVersion=10.0.13',
    "-p:Version=$Version",
    '--output', $outputDirectory
)

if ($NoWebUIBuild)
{
    $publishArguments += '-p:BuildWebUI=false'
}

& dotnet @publishArguments

if ($LASTEXITCODE -ne 0)
{
    throw "Server publish failed for $Runtime."
}

Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $sourceRoot 'README.md') -Destination $outputDirectory
Copy-Item -LiteralPath (Join-Path $sourceRoot 'ThirdParty/BililiveRecorder.Flv/NOTICE') -Destination (Join-Path $outputDirectory 'THIRD-PARTY-NOTICES.txt')
Compress-ReleaseArtifact -OutputDirectory $outputDirectory -Component 'server' -Version $Version -Runtime $Runtime
