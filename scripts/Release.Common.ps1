function Get-ReleaseVersion
{
    param(
        [string]$ProjectPath,
        [string]$Runtime,
        [string]$Version
    )

    if ([string]::IsNullOrEmpty($Version))
    {
        $versionOutput = & dotnet msbuild $ProjectPath -nologo '-p:Configuration=Release' "-p:RuntimeIdentifier=$Runtime" '-getProperty:Version'

        if ($LASTEXITCODE -ne 0)
        {
            throw "Failed to read the project version from $ProjectPath."
        }

        $Version = ($versionOutput -join [Environment]::NewLine).Trim()
    }

    if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z.+-]*$')
    {
        throw "Invalid release version: $Version. Use letters, digits, dots, plus signs or hyphens."
    }

    return $Version
}

function Compress-ReleaseArtifact
{
    param(
        [string]$OutputDirectory,
        [string]$Component,
        [string]$Version,
        [string]$Runtime
    )

    $versionDirectory = Split-Path -Parent $OutputDirectory
    $archivePath = Join-Path $versionDirectory "$Component-$Version-$Runtime.zip"
    $temporaryArchivePath = Join-Path $versionDirectory ".$([guid]::NewGuid().ToString('N')).zip"

    try
    {
        [System.IO.Compression.ZipFile]::CreateFromDirectory(
            $OutputDirectory,
            $temporaryArchivePath,
            [System.IO.Compression.CompressionLevel]::Optimal,
            $false
        )
        [System.IO.File]::Move($temporaryArchivePath, $archivePath, $true)
    }
    finally
    {
        if (Test-Path -LiteralPath $temporaryArchivePath)
        {
            Remove-Item -LiteralPath $temporaryArchivePath -Force
        }
    }

    Write-Host "Created $archivePath"
}
