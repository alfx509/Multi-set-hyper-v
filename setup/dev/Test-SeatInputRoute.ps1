#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (Phase 2, B4): the VM's real keyboard is captured by MultiSeatInput and routed to a
    MultiSeat Local seat. SeatAgent runs as SYSTEM inside the seat session (seat mode) and types it there.

.DESCRIPTION
    1. Agent settings with a random key: C:\ProgramData\MultiSeat\agents\seat-test.json (Administrators/SYSTEM only).
    2. Connects a seat, starts "SeatAgent --seat" in that session (SeatSpinner run).
    3. Finds the VMBus keyboard (or, with -Device mouse, the Hyper-V mouse) and runs "InputProbe route <id> <settings> <seconds>".
    4. While it runs, type on the VM keyboard (host: Hyper-V Msvm_Keyboard.TypeText, or the VMConnect window
       in BASIC mode). The text should appear on the seat's sign-in screen, not on the console.
    Writes C:\ProgramData\MultiSeat\route.ready when routing has started. Snapshot: route-after.bmp
#>
[CmdletBinding()]
param([int]$Seconds = 90, [int]$Port = 27101, [int]$SnapAfter = 15, [ValidateSet('keyboard', 'mouse')][string]$Device = 'keyboard')

$ErrorActionPreference = 'Stop'
$data = 'C:\ProgramData\MultiSeat'
$dev = 'C:\MultiSeatDev'
Remove-Item "$data\route.ready", "$data\route.log", "$data\route-*.bmp", "$data\seat.bmp", "$data\snap.request", "$data\agent-s*.log" -ErrorAction SilentlyContinue

# 0. sign out old seat sessions (test VM: every session except services/console; only the test seat may
#    answer the snapshot request)
$console = (Get-Process -Id $PID).SessionId
foreach ($line in (qwinsta 2>$null | Select-Object -Skip 1)) {
    $id = ($line -split '\s+' | Where-Object { $_ -match '^\d+$' } | Select-Object -First 1)
    if ($id -and [int]$id -gt 1 -and [int]$id -lt 65536 -and [int]$id -ne $console) {
        "Signing out old session $id"; logoff $id
    }
}
Start-Sleep -Seconds 3

# 1. agent settings
$agents = "$data\agents"; New-Item -ItemType Directory -Force $agents | Out-Null
$settings = "$agents\seat-test.json"
$key = [Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Maximum 256 }) -as [byte[]])
@{ seat = 'Seat test'; port = $Port; key = $key; watchdogMs = 2000 } | ConvertTo-Json | Set-Content $settings -Encoding ascii
icacls $settings /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null

# 2. seat + agent
$before = (Get-Content "$data\protocol.log" | Measure-Object -Line).Lines
$e = [System.Threading.EventWaitHandle]::OpenExisting('Global\MultiSeatLocalConnect'); [void]$e.Set(); $e.Dispose()
$session = $null
for ($i = 0; $i -lt 40 -and -not $session; $i++) {
    Start-Sleep -Milliseconds 250
    $l = Get-Content "$data\protocol.log" | Select-Object -Skip $before | Select-String 'NotifySessionId -> session (\d+)' | Select-Object -Last 1
    if ($l) { $session = [int]$l.Matches[0].Groups[1].Value }
}
if (-not $session) { throw 'No seat session was created.' }
"Seat session: $session"
Start-Sleep -Seconds 3
$task = "MultiSeatAgent-$session"
schtasks /Create /TN $task /RU SYSTEM /SC ONCE /ST 23:59 /F /TR "`"$dev\spinner\SeatSpinner.exe`" run $session $dev\agent\SeatAgent.exe --seat $settings" 2>&1 | Out-Null
schtasks /Run /TN $task | Out-Null
Start-Sleep -Seconds 3
"Agent: " + ((Get-Process SeatAgent -ErrorAction SilentlyContinue | Where-Object SessionId -eq $session | ForEach-Object { "pid $($_.Id) in session $($_.SessionId)" }) -join ', ')

# 3. route the VM keyboard (VMBus) or mouse (Hyper-V HID mouse)
$list = & "$dev\InputProbe\InputProbe.exe" list
$pattern = if ($Device -eq 'keyboard') { 'id\s+(\d+)\s+keyboard\s+\S+\s+VMBUS' } else { 'id\s+(\d+)\s+mouse\s' }
$found = $list | Select-String $pattern | Select-Object -First 1
if (-not $found) { $list; throw "VM $Device not found." }
$id = $found.Matches[0].Groups[1].Value
"Routing $Device id $id for $Seconds s"
$probe = Start-Process "$dev\InputProbe\InputProbe.exe" -ArgumentList "route $id $settings $Seconds" -RedirectStandardOutput "$data\route.log" -PassThru -WindowStyle Hidden
Set-Content "$data\route.ready" $session

# 4. wait, then take a snapshot while routing is still on
Start-Sleep -Seconds $SnapAfter
Set-Content "$data\snap.request" 'x'
for ($i = 0; $i -lt 60 -and (Test-Path "$data\snap.request"); $i++) { Start-Sleep -Milliseconds 100 }
if (Test-Path "$data\seat.bmp") { Move-Item "$data\seat.bmp" "$data\route-after.bmp" -Force; 'snapshot -> route-after.bmp' }
$probe.WaitForExit(($Seconds - $SnapAfter + 15) * 1000) | Out-Null
schtasks /Delete /TN $task /F 2>&1 | Out-Null
Remove-Item "$data\route.ready" -ErrorAction SilentlyContinue

'---- route'; Get-Content "$data\route.log" -ErrorAction SilentlyContinue | Select-Object -First 3; Get-Content "$data\route.log" -ErrorAction SilentlyContinue | Select-Object -Last 1
'---- agent'; Get-Content "$data\agent-s$session.log" -ErrorAction SilentlyContinue
