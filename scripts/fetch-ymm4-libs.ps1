param(
    [string]$OutputDir = "libs/YMM4"
)

$ErrorActionPreference = 'Stop'

$tag = 'v4.57.0.1'
$asset = 'YukkuriMovieMaker_v4.57.0.1_Lite.zip'
$archiveSha256 = '47837E0AA514533BDEB3D018FF28FBAC8D2A57E8D6A5665DDFB72E9329EC0553'
$source = "https://github.com/manju-summoner/YukkuriMovieMaker4/releases/download/$tag/$asset"
$requiredDlls = @(
    'YukkuriMovieMaker.Plugin.dll',
    'YukkuriMovieMaker.dll',
    'YukkuriMovieMaker.Controls.dll'
)

$output = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $output) {
    throw "Choose a new output directory for the pinned YMM4 references: $output"
}
$parent = Split-Path -Parent $output
[IO.Directory]::CreateDirectory($parent) | Out-Null
$staging = Join-Path $parent ('.ymm4-refs-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging | Out-Null

$download = [IO.Directory]::CreateTempSubdirectory('ymm4-fetch-').FullName
$zipPath = Join-Path $download $asset
$metadataName = 'YMM4-reference.json'
try {
    Invoke-WebRequest -Uri $source -OutFile $zipPath
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $zipPath).Hash -ne $archiveSha256) {
        throw 'The downloaded YMM4 archive does not match the pinned SHA-256.'
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $references = foreach ($dllName in $requiredDlls) {
            $entries = @($archive.Entries | Where-Object Name -CEQ $dllName)
            if ($entries.Count -ne 1) {
                throw "Expected exactly one entry for $dllName in the pinned archive."
            }
            $destination = Join-Path $staging $dllName
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $destination)
            [ordered]@{
                name = $dllName
                fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($destination).FileVersion
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash
            }
        }
    }
    finally {
        $archive.Dispose()
    }

    [ordered]@{
        release = $tag
        source = $source
        archiveSha256 = $archiveSha256
        files = $references
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $staging $metadataName) -Encoding utf8

    [IO.Directory]::Move($staging, $output)
    Write-Host "YMM4 references prepared at $output ($tag)"
}
finally {
    if ([IO.Directory]::Exists($staging)) {
        foreach ($name in ($requiredDlls + $metadataName)) {
            [IO.File]::Delete((Join-Path $staging $name))
        }
        [IO.Directory]::Delete($staging)
    }
    [IO.File]::Delete($zipPath)
    [IO.Directory]::Delete($download)
}
