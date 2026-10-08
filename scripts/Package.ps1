param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'Build.ps1') }
$taskInput = Join-Path $taskRoot 'dist\LocalRemote'
foreach ($taskRequired in @('LocalRemote.exe','ffmpeg.exe','README.md','INSTALL.md','LICENSE','FFmpeg\LICENSE.txt','FFmpeg\SOURCE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskInput $taskRequired) -PathType Leaf)) { throw "Missing release file: $taskRequired" }
}
$taskOutput = Join-Path $taskRoot 'artifacts\release'
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskArchive = Join-Path $taskOutput 'LocalRemote-win-x64.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$taskStream = [IO.File]::Open($taskArchive, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$taskZip = $null
try {
    $taskZip = [IO.Compression.ZipArchive]::new($taskStream, [IO.Compression.ZipArchiveMode]::Create)
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskInput -File -Recurse | Where-Object Extension -ne '.pdb') {
        $taskRelative = $taskFile.FullName.Substring($taskInput.Length + 1).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip, $taskFile.FullName, ('LocalRemote/' + $taskRelative), [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { if ($taskZip) { $taskZip.Dispose() }; $taskStream.Dispose() }
$taskHash = (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $taskOutput 'SHA256SUMS.txt'), "$taskHash  LocalRemote-win-x64.zip`n", [Text.UTF8Encoding]::new($false))
Write-Output "Release archive: $taskArchive"
Write-Output "SHA256: $taskHash"
