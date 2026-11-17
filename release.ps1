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
$runtimes = @($Runtime)

if ($All)
{
    $runtimes = @('linux-x64', 'win-x64')
}

$publishParameters = @{}

if ($PSBoundParameters.ContainsKey('Version'))
{
    $publishParameters.Version = $Version
}

foreach ($runtimeIdentifier in $runtimes)
{
    & (Join-Path $PSScriptRoot 'scripts/publish.ps1') -Runtime $runtimeIdentifier @publishParameters
    $publishParameters.NoWebUIBuild = $true
}
