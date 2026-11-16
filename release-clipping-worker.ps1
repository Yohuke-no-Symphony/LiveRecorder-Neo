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
$releaseArguments = @{}
if ($All)
{
    $releaseArguments.All = $true
}
else
{
    $releaseArguments.Runtime = $Runtime
}
if ($PSBoundParameters.ContainsKey('Version'))
{
    $releaseArguments.Version = $Version
}
& (Join-Path $PSScriptRoot 'release-worker.ps1') @releaseArguments
