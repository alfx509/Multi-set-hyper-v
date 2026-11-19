#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (Windows Server): installs the "MultiSeat Local" protocol provider (Phase 2, B1).

.DESCRIPTION
    What Windows needs before it loads a third-party protocol provider (found in B1):
    - The RD Session Host role (RDS-RD-Server). Without it TermService silently ignores
      non-Microsoft listeners. Installing the role needs a restart.
    - Remote Desktop on (fDenyTSConnections = 0), otherwise no listener starts.
    - A protocol definition under Terminal Server\Wds\<name> (copied from rdpwd) whose
      LoadableProtocol_Object is our CLSID, and a listener under Terminal Server\WinStations\MultiSeat-Local
      (copied from RDP-Tcp) whose WdName points to it. TermService watches these keys live.

    The DLL is copied while TermService is STOPPED (a running TermService keeps the old DLL loaded).
    Log: C:\ProgramData\MultiSeat\protocol.log. Then: Connect-LocalSeat.ps1.
    -Uninstall removes all of it again (the role stays). TEST VM ONLY.
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$clsid = '{2372A2FF-3F7C-4CFD-B896-1FA8163924FF}'
$ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
$listenerKey = "$ts\WinStations\MultiSeat-Local"
$wdKey = "$ts\Wds\multiseat"
$wdName = 'MultiSeat Local'
$installDir = Join-Path $env:ProgramFiles 'MultiSeat\Protocol'
$dll = Join-Path $installDir 'MultiSeatProtocol.dll'
$logDir = 'C:\ProgramData\MultiSeat'

function Stop-RemoteDesktop {
    Write-Host '  Stopping Remote Desktop Services...'
    Stop-Service UmRdpService -Force -ErrorAction SilentlyContinue
    Stop-Service TermService -Force
}

function Start-RemoteDesktop {
    Write-Host '  Starting Remote Desktop Services...'
    Start-Service TermService
    Start-Service UmRdpService -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
}

if ($Uninstall) {
    Stop-RemoteDesktop
    foreach ($k in $listenerKey, $wdKey) { if (Test-Path $k) { Remove-Item $k -Recurse -Force } }
    if (Test-Path $dll) { & regsvr32.exe /s /u $dll; Remove-Item $dll -Force -ErrorAction SilentlyContinue }
    Start-RemoteDesktop
    Write-Host 'MultiSeat Local provider removed.' -ForegroundColor Green
    return
}

$source = Join-Path $PSScriptRoot 'MultiSeatProtocol.dll'
if (-not (Test-Path $source)) { throw "$source not found." }

if (-not (Get-WindowsFeature RDS-RD-Server).Installed) {
    Write-Host '  Installing the RD Session Host role (needed for third-party protocols)...'
    Install-WindowsFeature RDS-RD-Server | Out-Null
    Write-Warning 'Restart the VM (Restart-Computer), then run this script again.'
    return
}

if ((Get-ItemProperty $ts).fDenyTSConnections -ne 0) {
    Set-ItemProperty $ts -Name fDenyTSConnections -Value 0
    Write-Host '  Remote Desktop turned on (fDenyTSConnections = 0) so listeners start.'
}

New-Item -ItemType Directory -Force -Path $installDir, $logDir | Out-Null
icacls $logDir /grant '*S-1-5-20:(OI)(CI)M' | Out-Null      # NETWORK SERVICE (TermService) may write the log

Stop-RemoteDesktop
Copy-Item -Force $source $dll
& regsvr32.exe /s $dll
if ($LASTEXITCODE -ne 0) { throw "regsvr32 failed ($LASTEXITCODE)" }

# Protocol definition (Wds) - a copy of rdpwd that loads our protocol manager.
if (-not (Test-Path $wdKey)) { Copy-Item "$ts\Wds\rdpwd" $wdKey -Recurse }
Set-ItemProperty $wdKey LoadableProtocol_Object $clsid
Set-ItemProperty $wdKey WdName $wdName
Set-ItemProperty $wdKey WdPrefix 'MSL'
Set-ItemProperty $wdKey StartupPrograms ''

# Listener (WinStation) - a copy of RDP-Tcp that uses our protocol definition.
if (-not (Test-Path $listenerKey)) { Copy-Item "$ts\WinStations\RDP-Tcp" $listenerKey -Recurse }
foreach ($n in 'WebSocketListenerPort', 'WebSocketTlsListenerPort') {
    Remove-ItemProperty $listenerKey -Name $n -ErrorAction SilentlyContinue
}
Set-ItemProperty $listenerKey LoadableProtocol_Object $clsid
Set-ItemProperty $listenerKey WdName $wdName
Set-ItemProperty $listenerKey WdPrefix 'MSL'
Set-ItemProperty $listenerKey PortNumber 3390                 # never the same port as RDP-Tcp
Set-ItemProperty $listenerKey MaxInstanceCount 8
Set-ItemProperty $listenerKey fEnableWinStation 1
Write-Host "  Listener key: $listenerKey"

Clear-Content -Path (Join-Path $logDir 'protocol.log') -ErrorAction SilentlyContinue
Start-RemoteDesktop

Write-Host ''
Write-Host 'Provider log:' -ForegroundColor Cyan
Get-Content (Join-Path $logDir 'protocol.log') -ErrorAction SilentlyContinue
qwinsta | Select-String -Pattern 'multiseat'
Write-Host ''
Write-Host 'Next: C:\MultiSeatDev\protocol\Connect-LocalSeat.ps1' -ForegroundColor Green
