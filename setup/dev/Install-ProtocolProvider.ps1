#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (Windows Server): installs the "MultiSeat Local" protocol provider (Phase 2, B1).

.DESCRIPTION
    - Copies MultiSeatProtocol.dll to C:\Program Files\MultiSeat\Protocol and registers the COM class.
    - Lets TermService (NETWORK SERVICE) write its log to C:\ProgramData\MultiSeat\protocol.log.
    - Adds the listener key Terminal Server\WinStations\MultiSeat-Local (LoadableProtocol_Object).
    - Restarts Remote Desktop Services, which loads the provider.
    Use VMConnect BASIC mode (View > uncheck Enhanced session): restarting TermService drops RDP sessions.
    Then: Connect-LocalSeat.ps1

    -Uninstall removes all of it again. TEST VM ONLY.
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$clsid = '{2372A2FF-3F7C-4CFD-B896-1FA8163924FF}'
$listenerKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\MultiSeat-Local'
$installDir = Join-Path $env:ProgramFiles 'MultiSeat\Protocol'
$dll = Join-Path $installDir 'MultiSeatProtocol.dll'
$logDir = 'C:\ProgramData\MultiSeat'

function Restart-RemoteDesktop {
    Write-Host '  Restarting Remote Desktop Services...'
    Restart-Service TermService -Force
    Start-Sleep -Seconds 3
    (Get-Service TermService).Status
}

if ($Uninstall) {
    if (Test-Path $listenerKey) { Remove-Item $listenerKey -Recurse -Force }
    Restart-RemoteDesktop                       # unloads the DLL
    if (Test-Path $dll) { & regsvr32.exe /s /u $dll; Remove-Item $dll -Force -ErrorAction SilentlyContinue }
    Write-Host 'MultiSeat Local provider removed.' -ForegroundColor Green
    return
}

$source = Join-Path $PSScriptRoot 'MultiSeatProtocol.dll'
if (-not (Test-Path $source)) { throw "$source not found." }

New-Item -ItemType Directory -Force -Path $installDir, $logDir | Out-Null
icacls $logDir /grant '*S-1-5-20:(OI)(CI)M' | Out-Null      # NETWORK SERVICE (TermService) may write the log
if (Test-Path $listenerKey) { Restart-RemoteDesktop }        # release an older DLL before replacing it
Copy-Item -Force $source $dll
& regsvr32.exe /s $dll
if ($LASTEXITCODE -ne 0) { throw "regsvr32 failed ($LASTEXITCODE)" }

New-Item -Path $listenerKey -Force | Out-Null
New-ItemProperty -Path $listenerKey -Name LoadableProtocol_Object -Value $clsid -PropertyType String -Force | Out-Null
New-ItemProperty -Path $listenerKey -Name fEnableWinStation -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $listenerKey -Name MaxInstanceCount -Value 8 -PropertyType DWord -Force | Out-Null
Write-Host "  Listener key: $listenerKey"

Clear-Content -Path (Join-Path $logDir 'protocol.log') -ErrorAction SilentlyContinue
Restart-RemoteDesktop

Write-Host ''
Write-Host 'Provider log:' -ForegroundColor Cyan
Get-Content (Join-Path $logDir 'protocol.log') -ErrorAction SilentlyContinue
Write-Host ''
Write-Host 'Next: C:\MultiSeatDev\protocol\Connect-LocalSeat.ps1' -ForegroundColor Green
