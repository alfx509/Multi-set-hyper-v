#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (Phase 2, B4): connects a MultiSeat Local seat, saves a snapshot of its screen,
    injects a click + text into its sign-in screen (SendInput from SYSTEM inside the seat session), saves
    a second snapshot. Snapshots: C:\ProgramData\MultiSeat\seat-1-before.bmp / seat-2-after.bmp.
#>
[CmdletBinding()]
param([string]$Text = 'multiseat')

$ErrorActionPreference = 'Stop'
$data = 'C:\ProgramData\MultiSeat'
$tool = Join-Path $PSScriptRoot 'SeatSpinner.exe'
Remove-Item "$data\idd.log", "$data\spinner.log", "$data\seat*.bmp", "$data\snap.request" -ErrorAction SilentlyContinue
$before = (Get-Content "$data\protocol.log" -ErrorAction SilentlyContinue | Measure-Object -Line).Lines

function Get-Snapshot([string]$name) {
    Set-Content "$data\snap.request" 'x'
    for ($i = 0; $i -lt 40 -and (Test-Path "$data\snap.request"); $i++) { Start-Sleep -Milliseconds 250 }
    if (Test-Path "$data\seat.bmp") { Move-Item "$data\seat.bmp" "$data\$name" -Force; "  snapshot -> $name" }
    else { Remove-Item "$data\snap.request" -ErrorAction SilentlyContinue; "  snapshot ${name}: no new frame" }
}

function Start-InSeat([int]$session, [string]$arguments) {
    schtasks /Create /TN MultiSeatSeatTool /RU SYSTEM /SC ONCE /ST 23:59 /F /TR "`"$tool`" launch $session $arguments" 2>&1 | Out-Null
    schtasks /Run /TN MultiSeatSeatTool | Out-Null
}

$event = [System.Threading.EventWaitHandle]::OpenExisting('Global\MultiSeatLocalConnect')
[void]$event.Set(); $event.Dispose()
$session = $null
for ($i = 0; $i -lt 40 -and -not $session; $i++) {
    Start-Sleep -Milliseconds 250
    $line = Get-Content "$data\protocol.log" | Select-Object -Skip $before | Select-String 'NotifySessionId -> session (\d+)' | Select-Object -Last 1
    if ($line) { $session = [int]$line.Matches[0].Groups[1].Value }
}
if (-not $session) { throw 'No seat session was created (see protocol.log).' }
Write-Host "Seat session: $session" -ForegroundColor Cyan
Start-Sleep -Seconds 4

# Frames only arrive when the screen changes: the first one after the injected mouse move is "before".
Set-Content "$data\snap.request" 'x'
Start-InSeat $session "input $Text"
for ($i = 0; $i -lt 40 -and (Test-Path "$data\snap.request"); $i++) { Start-Sleep -Milliseconds 100 }
if (Test-Path "$data\seat.bmp") { Move-Item "$data\seat.bmp" "$data\seat-1-before.bmp" -Force; '  snapshot -> seat-1-before.bmp' }
Start-Sleep -Seconds 5
Get-Snapshot 'seat-2-after.bmp'                # the text caret blinks, so new frames keep coming
schtasks /Delete /TN MultiSeatSeatTool /F 2>&1 | Out-Null

Write-Host '---- tool log' -ForegroundColor Cyan
Get-Content "$data\spinner.log" -ErrorAction SilentlyContinue | Select-String -NotMatch 'presents'
Write-Host '---- idd' -ForegroundColor Cyan
Get-Content "$data\idd.log" -ErrorAction SilentlyContinue | Select-String 'Snapshot|CommitModes'
