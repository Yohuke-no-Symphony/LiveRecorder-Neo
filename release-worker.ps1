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
$workerProjects = @(
    @{
        ProjectName = 'Yohuke.LiveRecorder.Transcoding.Worker'
        DirectoryName = 'transcoding'
        ExecutableName = 'ylr-tc-worker'
    },
    @{
        ProjectName = 'Yohuke.LiveRecorder.Clipping.Worker'
        DirectoryName = 'clip'
        ExecutableName = 'ylr-clip-worker'
    },
    @{
        ProjectName = 'Yohuke.LiveRecorder.Recording.Worker'
        DirectoryName = 'recording'
        ExecutableName = 'ylr-rec-worker'
    },
    @{
        ProjectName = 'Yohuke.LiveRecorder.AI.Worker'
        DirectoryName = 'ai'
        ExecutableName = 'ylr-ai-worker'
    }
)
$workerVersionProject = Join-Path $PSScriptRoot "Sources/Worker/$($workerProjects[0].ProjectName)/$($workerProjects[0].ProjectName).csproj"
$workerRuntimes = @($Runtime)

if ($All)
{
    $workerRuntimes = @('linux-x64', 'win-x64', 'osx-arm64')
}

foreach ($workerRuntime in $workerRuntimes)
{
    $workerVersion = Get-ReleaseVersion -ProjectPath $workerVersionProject -Runtime $workerRuntime -Version $Version
    $workerVersionDirectory = Join-Path $PSScriptRoot "Artifacts/workers/$workerVersion"
    $workerStagingDirectory = Join-Path $workerVersionDirectory ".$workerRuntime-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $workerStagingDirectory -Force | Out-Null
    try
    {
        foreach ($workerProject in $workerProjects)
        {
            $workerProjectDirectory = Join-Path $PSScriptRoot "Sources/Worker/$($workerProject.ProjectName)"
            $workerProjectPath = Join-Path $workerProjectDirectory "$($workerProject.ProjectName).csproj"
            $workerOutputDirectory = Join-Path $workerStagingDirectory $workerProject.DirectoryName
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
                throw "$($workerProject.ProjectName) publish failed for $workerRuntime."
            }
            $workerExecutableName = $workerProject.ExecutableName
            if ($workerRuntime.StartsWith('win-', [System.StringComparison]::Ordinal))
            {
                $workerExecutableName += '.exe'
            }
            if (-not (Test-Path -LiteralPath (Join-Path $workerOutputDirectory $workerExecutableName) -PathType Leaf))
            {
                throw "Worker publish did not produce $workerExecutableName for $workerRuntime."
            }
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $workerOutputDirectory
            Copy-Item -LiteralPath (Join-Path $workerProjectDirectory 'README.md') -Destination $workerOutputDirectory
            Copy-Item -LiteralPath (Join-Path $workerProjectDirectory 'settings.example.json') -Destination $workerOutputDirectory
        }
        Compress-ReleaseArtifact -OutputDirectory $workerStagingDirectory -Component 'workers' -Version $workerVersion -Runtime $workerRuntime
        $workerRuntimeDirectory = Join-Path $workerVersionDirectory $workerRuntime
        New-Item -ItemType Directory -Path $workerRuntimeDirectory -Force | Out-Null
        foreach ($workerProject in $workerProjects)
        {
            Copy-Item -LiteralPath (Join-Path $workerStagingDirectory $workerProject.DirectoryName) -Destination $workerRuntimeDirectory -Recurse -Force
        }
        Write-Host "Published worker files to $workerRuntimeDirectory"
    }
    finally
    {
        if (Test-Path -LiteralPath $workerStagingDirectory)
        {
            Remove-Item -LiteralPath $workerStagingDirectory -Recurse -Force
        }
    }
}
