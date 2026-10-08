param([string]$ArchivePath, [switch]$Force)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = Join-Path $taskRoot 'third_party\ffmpeg'
$taskBinary = Join-Path $taskOutput 'ffmpeg.exe'
if ((Test-Path -LiteralPath $taskBinary) -and -not $Force) {
    Write-Output "FFmpeg already available: $taskBinary"
    return
}
$taskHeaders = @{ 'User-Agent' = 'LocalRemote-build'; 'Accept' = 'application/vnd.github+json' }
$taskRelease = Invoke-RestMethod -Uri 'https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest' -Headers $taskHeaders
$taskAsset = @($taskRelease.assets | Where-Object name -eq 'ffmpeg-n8.1-latest-win64-gpl-8.1.zip')
if ($taskAsset.Count -ne 1 -or $taskAsset[0].digest -notmatch '^sha256:([a-fA-F0-9]{64})$') {
    throw 'The FFmpeg release must provide a SHA-256 digest.'
}
$taskExpected = $Matches[1]
$taskDownload = $taskAsset[0].browser_download_url
if ($taskDownload -notlike 'https://github.com/BtbN/FFmpeg-Builds/releases/download/*') { throw 'Unexpected download URL.' }
if (-not $ArchivePath) {
    $taskCache = Join-Path $taskRoot '.cache\ffmpeg'
    [IO.Directory]::CreateDirectory($taskCache) | Out-Null
    $ArchivePath = Join-Path $taskCache $taskAsset[0].name
    if (-not (Test-Path -LiteralPath $ArchivePath) -or (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $taskExpected) {
        Invoke-WebRequest -Uri $taskDownload -OutFile $ArchivePath -UseBasicParsing -TimeoutSec 600
    }
}
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $taskExpected) { throw 'FFmpeg archive checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$taskZip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $ArchivePath).Path)
try {
    $taskEntry = @($taskZip.Entries | Where-Object FullName -match '/bin/ffmpeg\.exe$')
    if ($taskEntry.Count -ne 1) { throw 'The archive must contain exactly one ffmpeg.exe.' }
    [IO.Directory]::CreateDirectory($taskOutput) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry[0], $taskBinary, $true)
} finally { $taskZip.Dispose() }
$taskVersion = & $taskBinary -version | Select-Object -First 1
if ($LASTEXITCODE -ne 0) { throw 'FFmpeg did not start.' }
if ($taskVersion -notmatch '-g([a-f0-9]+)-') { throw 'Cannot identify the FFmpeg source revision.' }
$taskCommit = Invoke-RestMethod -Uri ("https://api.github.com/repos/FFmpeg/FFmpeg/commits/" + $Matches[1]) -Headers $taskHeaders
$taskSourceArchive = "https://github.com/FFmpeg/FFmpeg/archive/$($taskCommit.sha).tar.gz"
$taskSources = @(
    "Build: $($taskAsset[0].name)", "Version: $taskVersion", "Download: $taskDownload",
    "Archive SHA256: $taskExpected", 'Source and build scripts: https://github.com/BtbN/FFmpeg-Builds',
    "FFmpeg source: $taskSourceArchive"
)
[IO.File]::WriteAllLines((Join-Path $taskOutput 'SOURCE.txt'), $taskSources, [Text.UTF8Encoding]::new($false))
Write-Output "FFmpeg verified and installed: $taskBinary"
