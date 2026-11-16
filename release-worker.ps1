[CmdletBinding(DefaultParameterSetName = 'Runtime')]
param(
    [Parameter(ParameterSetName = 'Runtime')]
    [Alias('-runtime')]
    [ValidateSet('linux-x64', 'linux-arm64', 'win-x64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime = 'linux-x64',

    [Parameter(ParameterSetName = 'All', Mandatory)]
    [Alias('-all')]
    [switch]$All,

    [Alias('-version')]
    [ValidateNotNullOrEmpty()]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts/Release.Common.ps1')
$workerProjectDirectory = Join-Path $PSScriptRoot 'Sources/Yohuke.LiveRecorder.Transcoding.Worker'
$workerProjectPath = Join-Path $workerProjectDirectory 'Yohuke.LiveRecorder.Transcoding.Worker.csproj'
$workerRuntimes = @($Runtime)

if ($All)
{
    $workerRuntimes = @('linux-x64', 'linux-arm64', 'win-x64', 'osx-x64', 'osx-arm64')
}

foreach ($workerRuntime in $workerRuntimes)
{
    $workerVersion = Get-ReleaseVersion -ProjectPath $workerProjectPath -Runtime $workerRuntime -Version $Version
    $workerOutputDirectory = Join-Path $PSScriptRoot "Artifacts/worker/$workerVersion/$workerRuntime"
    $workerPublishArguments = @(
        'publish',
        $workerProjectPath,
        '--configuration', 'Release',
        '--runtime', $workerRuntime,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded',
        '-p:RuntimeFrameworkVersion=10.0.13',
        "-p:Version=$workerVersion",
        '--output', $workerOutputDirectory
    )

    & dotnet @workerPublishArguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "Worker publish failed for $workerRuntime."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $workerOutputDirectory
    Copy-Item -LiteralPath (Join-Path $workerProjectDirectory 'README.md') -Destination $workerOutputDirectory
    Copy-Item -LiteralPath (Join-Path $workerProjectDirectory 'settings.example.json') -Destination $workerOutputDirectory
    Compress-ReleaseArtifact -OutputDirectory $workerOutputDirectory -Component 'worker' -Version $workerVersion -Runtime $workerRuntime
}
