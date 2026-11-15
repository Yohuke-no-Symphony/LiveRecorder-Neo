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
$workerProjectDirectory = Join-Path $PSScriptRoot 'Sources/Yohuke.LiveRecorder.Transcoding.Worker'
$workerRuntimes = @($Runtime)

if ($All)
{
    $workerRuntimes = @('linux-x64', 'linux-arm64', 'win-x64', 'osx-x64', 'osx-arm64')
}

foreach ($workerRuntime in $workerRuntimes)
{
    $workerOutputDirectory = Join-Path $PSScriptRoot "Artifacts/worker/$workerRuntime"
    $workerPublishArguments = @(
        'publish',
        (Join-Path $workerProjectDirectory 'Yohuke.LiveRecorder.Transcoding.Worker.csproj'),
        '--configuration', 'Release',
        '--runtime', $workerRuntime,
        '--self-contained', 'true',
        '-p:RuntimeFrameworkVersion=10.0.13',
        '--output', $workerOutputDirectory
    )

    if ($PSBoundParameters.ContainsKey('Version'))
    {
        $workerPublishArguments += "-p:Version=$Version"
    }

    & dotnet @workerPublishArguments

    if ($LASTEXITCODE -ne 0)
    {
        throw "Worker publish failed for $workerRuntime."
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $workerOutputDirectory
    Copy-Item -LiteralPath (Join-Path $workerProjectDirectory 'README.md') -Destination $workerOutputDirectory
}
