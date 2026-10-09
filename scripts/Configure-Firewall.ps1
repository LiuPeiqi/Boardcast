#Requires -RunAsAdministrator
param([int]$Port = 45831)
$ErrorActionPreference = 'Stop'
$daemonPath = Join-Path $PSScriptRoot 'Boardcast.Daemon.exe'
if (-not (Test-Path -LiteralPath $daemonPath)) { throw 'Run this script from the extracted release folder.' }
if ($Port -lt 1024 -or $Port -gt 65535) { throw 'Invalid port.' }
# Updates use a separate dynamically assigned TCP port. Keep all TCP access
# limited to this daemon, private Windows networks, and the local subnet.
$tcpRuleName = 'Boardcast-TCP'
foreach ($ruleName in @("Boardcast-TCP-$Port", $tcpRuleName, "Boardcast-UDP-$Port")) {
    $existing = Get-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    if ($existing) { Remove-NetFirewallRule -Name $ruleName }
}
New-NetFirewallRule -Name $tcpRuleName -DisplayName 'Boardcast LAN TCP (files and updates)' -Direction Inbound -Action Allow -Program $daemonPath -Protocol TCP -LocalPort Any -Profile Private -RemoteAddress LocalSubnet | Out-Null
New-NetFirewallRule -Name "Boardcast-UDP-$Port" -DisplayName 'Boardcast LAN UDP discovery' -Direction Inbound -Action Allow -Program $daemonPath -Protocol UDP -LocalPort $Port -Profile Private -RemoteAddress LocalSubnet | Out-Null
Write-Host 'Boardcast files, updates, and discovery are allowed on private local networks only.'
