#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (Phase 2, B4): connects a MultiSeat Local seat, signs in the test user on the seat's
    sign-in screen (SendInput from SYSTEM inside the seat session) and checks the session over time.
    Credentials come from C:\ProgramData\MultiSeat\testuser-seat1.txt (Administrators/SYSTEM only) and are never printed.
    Snapshots: C:\ProgramData\MultiSeat\signin-*.bmp
#>
[CmdletBinding()]
param([int]$WatchSeconds = 60)

$ErrorActionPreference = 'Stop'
$data = 'C:\ProgramData\MultiSeat'
$tool = Join-Path $PSScriptRoot 'SeatSpinner.exe'
$secret = "$data\testuser-seat1.txt"
Remove-Item "$data\idd.log", "$data\spinner.log", "$data\signin-*.bmp", "$data\seat.bmp", "$data\snap.request" -ErrorAction SilentlyContinue
$before = (Get-Content "$data\protocol.log" -ErrorAction SilentlyContinue | Measure-Object -Line).Lines

function Start-InSeat([string]$arguments) {
    schtasks /Create /TN MultiSeatSeatTool /RU SYSTEM /SC ONCE /ST 23:59 /F /TR "`"$tool`" launch $session $arguments" 2>&1 | Out-Null
    schtasks /Run /TN MultiSeatSeatTool | Out-Null
}

# A still screen sends no frames: "nudge" moves the seat's mouse 1 px so the next frame can be saved.
function Save-Snapshot([string]$name, [int]$waitMs = 6000) {
    Set-Content "$data\snap.request" 'x'
    Start-InSeat 'nudge x'
    for ($i = 0; $i -lt ($waitMs / 100) -and (Test-Path "$data\snap.request"); $i++) { Start-Sleep -Milliseconds 100 }
    if (Test-Path "$data\seat.bmp") { Move-Item "$data\seat.bmp" "$data\$name" -Force; "  snapshot -> $name" }
    else { Remove-Item "$data\snap.request" -ErrorAction SilentlyContinue; "  snapshot ${name}: no new frame" }
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
$start = Get-Date
Write-Host "Seat session: $session" -ForegroundColor Cyan
Start-Sleep -Seconds 4

Save-Snapshot 'signin-1-before.bmp'
Start-InSeat "signin $secret"
Start-Sleep -Seconds 8
Save-Snapshot 'signin-2-after.bmp'
Write-Host '---- sessions after sign-in' -ForegroundColor Cyan
qwinsta

while (((Get-Date) - $start).TotalSeconds -lt $WatchSeconds) { Start-Sleep -Seconds 2 }
Save-Snapshot 'signin-3-later.bmp'
schtasks /Delete /TN MultiSeatSeatTool /F 2>&1 | Out-Null
Write-Host "---- sessions after $WatchSeconds s" -ForegroundColor Cyan
qwinsta

Write-Host '---- tool log' -ForegroundColor Cyan
Get-Content "$data\spinner.log" -ErrorAction SilentlyContinue
Write-Host '---- provider (logon / disconnect)' -ForegroundColor Cyan
Get-Content "$data\protocol.log" | Select-Object -Skip $before | Select-String 'Logon|IsUserAllowed|SessionArbitration|PreDisconnect|DisconnectNotify|Close|SetErrorInfo'
Write-Host '---- idd' -ForegroundColor Cyan
Get-Content "$data\idd.log" -ErrorAction SilentlyContinue | Select-String 'Snapshot|CommitModes|ended'
