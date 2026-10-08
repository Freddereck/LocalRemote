param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\LocalRemote\LocalRemote.csproj'
$taskOutput = Join-Path $taskRoot 'dist\LocalRemote'
$taskSelfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
$taskFfmpeg = Join-Path $taskRoot 'third_party\ffmpeg'
if (-not (Test-Path -LiteralPath (Join-Path $taskFfmpeg 'ffmpeg.exe'))) { & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') }
& dotnet publish $taskProject -c Release -r win-x64 --self-contained $taskSelfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $taskOutput
if ($LASTEXITCODE -ne 0) { throw 'Сборка не выполнена.' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Configure-Firewall.ps1') -Destination $taskOutput -Force
foreach ($taskDoc in @('README.md','INSTALL.md','VERIFICATION.md','LICENSE','THIRD_PARTY.md')) { Copy-Item -LiteralPath (Join-Path $taskRoot $taskDoc) -Destination $taskOutput -Force }
Copy-Item -LiteralPath (Join-Path $taskRoot 'docs') -Destination $taskOutput -Recurse -Force
Copy-Item -LiteralPath (Join-Path $taskFfmpeg 'ffmpeg.exe') -Destination $taskOutput -Force
$taskLicenseOutput = Join-Path $taskOutput 'FFmpeg'
[IO.Directory]::CreateDirectory($taskLicenseOutput) | Out-Null
Get-ChildItem -LiteralPath $taskFfmpeg -File | Where-Object { $_.Extension -ne '.exe' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskLicenseOutput -Force }
if (-not $FrameworkDependent) {
    $taskAssets = Get-Content -LiteralPath (Join-Path $taskRoot 'src\LocalRemote\obj\project.assets.json') -Raw | ConvertFrom-Json
    foreach ($taskLibrary in $taskAssets.project.frameworks.PSObject.Properties.Value.downloadDependencies | Where-Object name -match '^Microsoft\.(NETCore|WindowsDesktop)\.App\.Runtime\.win-x64$') {
        $taskPackageVersion = ($taskLibrary.version -replace '[\[\]]','').Split(',')[0].Trim()
        foreach ($taskPackages in $taskAssets.packageFolders.PSObject.Properties.Name) {
            $taskPackageDirectory = Join-Path $taskPackages ($taskLibrary.name.ToLowerInvariant() + '\' + $taskPackageVersion)
            if (-not (Test-Path -LiteralPath $taskPackageDirectory)) { continue }
            $taskNotices = Join-Path $taskOutput ('DotNet\' + $taskLibrary.name + '-' + $taskPackageVersion)
            [IO.Directory]::CreateDirectory($taskNotices) | Out-Null
            Get-ChildItem -LiteralPath $taskPackageDirectory -File | Where-Object Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $taskNotices -Force }
        }
    }
}
Write-Output "Готово: $taskOutput"
