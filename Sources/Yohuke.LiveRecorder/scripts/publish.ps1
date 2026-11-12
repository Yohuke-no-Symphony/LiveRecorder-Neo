param(
    [ValidateSet('linux-x64','linux-arm64','win-x64','osx-x64','osx-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputDirectory = Join-Path $projectRoot "artifacts/$Runtime"
dotnet publish (Join-Path $projectRoot 'Yohuke.LiveRecorder.Server/Yohuke.LiveRecorder.Server.csproj') --configuration Release --runtime $Runtime --self-contained true -p:RuntimeFrameworkVersion=10.0.13 --output $outputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
Copy-Item (Join-Path $projectRoot 'LICENSE') $outputDirectory
Copy-Item (Join-Path $projectRoot 'README.md') $outputDirectory
Copy-Item (Join-Path $projectRoot 'ThirdParty/BililiveRecorder.Flv/NOTICE') (Join-Path $outputDirectory 'THIRD-PARTY-NOTICES.txt')
