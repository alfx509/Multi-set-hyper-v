#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM / test PC (Phase 2, B4): installs the "MultiSeat seat agents" service (MultiSeatSeats).
    It starts SeatAgent (seat mode) by itself in every MultiSeat Local seat session.

.DESCRIPTION
    Copies SeatService.exe and the published SeatAgent to C:\Program Files\MultiSeat (Administrators only: a SYSTEM
    service must never start a program from a folder normal users can change), registers the service as
    LocalSystem, automatic start, restart on failure.
    Seat settings: C:\ProgramData\MultiSeat\agents\local-*.json, one per seat (Administrators/SYSTEM only).
    -TestSeat writes local-1.json (seat "Seat 1", port 27101, new random key) if it does not exist.

.EXAMPLE
    .\Install-SeatService.ps1 -Source C:\MultiSeatDev -TestSeat
    .\Install-SeatService.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string]$Source = 'C:\MultiSeatDev',   # contains seatsvc\SeatService.exe and agent\ (published SeatAgent)
    [switch]$TestSeat,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$name = 'MultiSeatSeats'
$target = "$env:ProgramFiles\MultiSeat"
$agents = 'C:\ProgramData\MultiSeat\agents'

if (Get-Service $name -ErrorAction SilentlyContinue) {
    Stop-Service $name -Force -ErrorAction SilentlyContinue
    if ($Uninstall) {
        sc.exe delete $name | Out-Null
        Remove-Item 'HKLM:\SOFTWARE\MultiSeat\SeatService' -Recurse -ErrorAction SilentlyContinue
        Remove-Item "$target\SeatService", "$target\SeatAgent" -Recurse -Force -ErrorAction SilentlyContinue
        'MultiSeat seat agents service removed (seat settings in agents\ are kept).'
        return
    }
}
elseif ($Uninstall) { 'Not installed.'; return }

# 1. files (Program Files: Administrators/SYSTEM can write, users can only read)
New-Item -ItemType Directory -Force "$target\SeatService", "$target\SeatAgent" | Out-Null
Copy-Item "$Source\seatsvc\SeatService.exe" "$target\SeatService\" -Force
Copy-Item "$Source\agent\*" "$target\SeatAgent\" -Recurse -Force
New-Item -Force 'HKLM:\SOFTWARE\MultiSeat\SeatService' | Out-Null
Set-ItemProperty 'HKLM:\SOFTWARE\MultiSeat\SeatService' AgentPath "$target\SeatAgent\SeatAgent.exe"

# 2. seat settings folder (holds the per-seat keys)
New-Item -ItemType Directory -Force $agents | Out-Null
icacls $agents /inheritance:r /grant '*S-1-5-32-544:(OI)(CI)F' /grant '*S-1-5-18:(OI)(CI)F' | Out-Null
if ($TestSeat -and -not (Test-Path "$agents\local-1.json")) {
    $key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
    @{ seat = 'Seat 1'; port = 27101; key = $key; watchdogMs = 2000 } | ConvertTo-Json | Set-Content "$agents\local-1.json" -Encoding ascii
    'Test seat settings: local-1.json (port 27101)'
}

# 3. service
$bin = "`"$target\SeatService\SeatService.exe`""
if (-not (Get-Service $name -ErrorAction SilentlyContinue)) {
    sc.exe create $name binPath= $bin start= auto obj= LocalSystem DisplayName= 'MultiSeat seat agents' | Out-Null
}
else {
    sc.exe config $name binPath= $bin start= auto obj= LocalSystem | Out-Null
}
sc.exe description $name 'Starts the MultiSeat input agent in every MultiSeat Local seat session.' | Out-Null
sc.exe failure $name reset= 86400 actions= restart/2000/restart/5000/restart/30000 | Out-Null
Start-Service $name
Get-Service $name | Format-Table -AutoSize Name, Status, StartType
'Log: C:\ProgramData\MultiSeat\seatsvc.log'
