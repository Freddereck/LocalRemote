param(
    [string]$ProgramPath = (Join-Path $PSScriptRoot 'LocalRemote.exe'),
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
try {
    $taskIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $taskPrincipal = [Security.Principal.WindowsPrincipal]::new($taskIdentity)
    if (-not $taskPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Запустите скрипт с правами администратора.'
    }
    $taskRuleName = 'LocalRemote-LAN-48930'
    $taskLegacyRule = Get-NetFirewallRule -Name 'NewAnyDesk-LAN-48930' -ErrorAction SilentlyContinue
    $taskExistingRule = Get-NetFirewallRule -Name $taskRuleName -ErrorAction SilentlyContinue
    if ($Remove) {
        if ($taskExistingRule) { $taskExistingRule | Remove-NetFirewallRule }
        if ($taskLegacyRule) { $taskLegacyRule | Remove-NetFirewallRule }
        Write-Output 'Правило LocalRemote удалено.'
        exit 0
    }
    $taskExecutable = (Resolve-Path -LiteralPath $ProgramPath).Path
    if ([IO.Path]::GetFileName($taskExecutable) -ne 'LocalRemote.exe') {
        throw 'Укажите путь к LocalRemote.exe.'
    }
    if ($taskExistingRule) { $taskExistingRule | Remove-NetFirewallRule }
    New-NetFirewallRule -Name $taskRuleName -DisplayName 'LocalRemote: домашняя сеть' `
        -Direction Inbound -Action Allow -Protocol TCP -LocalPort 48930 `
        -Program $taskExecutable -Profile Private -RemoteAddress LocalSubnet | Out-Null
    if ($taskLegacyRule) { $taskLegacyRule | Remove-NetFirewallRule }
    Write-Output 'Доступ разрешён только для локальной подсети в частной сети Windows.'
} catch {
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'LocalRemote: брандмауэр') | Out-Null
    exit 1
}
