#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (Phase 2, B3): connects a MultiSeat Local seat, starts SeatSpinner inside the
    seat's session (as SYSTEM, on its sign-in desktop) and prints the frame rates.

.DESCRIPTION
    Needs: Install-ProtocolProvider.ps1, Install-RemoteIdd.ps1, SeatSpinner.exe next to this script.
    "spinner" = presents per second inside the seat (what the app asked for).
    "idd"     = frames DWM delivered to the seat's monitor (the real answer to B3).
#>
[CmdletBinding()]
param([int]$Seconds = 20)

$ErrorActionPreference = 'Stop'
$data = 'C:\ProgramData\MultiSeat'
$spinner = Join-Path $PSScriptRoot 'SeatSpinner.exe'
if (-not (Test-Path $spinner)) { throw "$spinner not found." }
Remove-Item "$data\idd.log", "$data\spinner.log" -ErrorAction SilentlyContinue
$before = (Get-Content "$data\protocol.log" -ErrorAction SilentlyContinue | Measure-Object -Line).Lines

$event = [System.Threading.EventWaitHandle]::OpenExisting('Global\MultiSeatLocalConnect')
[void]$event.Set(); $event.Dispose()
Write-Host 'Seat connect requested...' -ForegroundColor Cyan

$session = $null
for ($i = 0; $i -lt 40 -and -not $session; $i++) {
    Start-Sleep -Milliseconds 250
    $line = Get-Content "$data\protocol.log" | Select-Object -Skip $before | Select-String 'NotifySessionId -> session (\d+)' | Select-Object -Last 1
    if ($line) { $session = [int]$line.Matches[0].Groups[1].Value }
}
if (-not $session) { throw 'No seat session was created (see protocol.log).' }
Write-Host "Seat session: $session" -ForegroundColor Cyan
Start-Sleep -Seconds 2                                     # let the seat's display come up

$task = 'MultiSeatSpinner'
schtasks /Create /TN $task /RU SYSTEM /SC ONCE /ST 00:00 /F /TR "`"$spinner`" launch $session $Seconds" | Out-Null
schtasks /Run /TN $task | Out-Null
Start-Sleep -Seconds ($Seconds + 3)
schtasks /Delete /TN $task /F | Out-Null

Write-Host ''
Write-Host '---- spinner (inside the seat)' -ForegroundColor Cyan
Get-Content "$data\spinner.log" -ErrorAction SilentlyContinue
Write-Host '---- idd (frames delivered to the seat monitor)' -ForegroundColor Cyan
Get-Content "$data\idd.log" -ErrorAction SilentlyContinue | Select-String 'CommitModes|AssignSwapChain|SetDevice|Frames|ended'
Write-Host '---- sessions' -ForegroundColor Cyan
qwinsta
