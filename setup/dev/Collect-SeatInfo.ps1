#Requires -Version 5.1
<#
.SYNOPSIS
    Run on the TEST PC: saves what Windows reports about sessions, displays, drivers and input,
    so a clean Windows (baseline) can be compared with Windows running ASTER (black-box, rule 4).

.DESCRIPTION
    Writes C:\MultiSeatDev\observations\<label>-s<session>-<time>\ with one text file per topic.
    Run as Administrator in each session you want to compare (console, seat 2, ...).
    Read-only: changes nothing on the PC.

.EXAMPLE
    .\Collect-SeatInfo.ps1 -Label baseline
    .\Collect-SeatInfo.ps1 -Label aster-seat2
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Label,
    [string]$OutRoot = 'C:\MultiSeatDev\observations'
)

$ErrorActionPreference = 'Continue'
$session = (Get-Process -Id $PID).SessionId
$out = Join-Path $OutRoot ("{0}-s{1}-{2:yyyyMMdd-HHmmss}" -f $Label, $session, (Get-Date))
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Save([string]$name, [scriptblock]$block) {
    Write-Host "  $name"
    try { & $block 2>&1 | Out-String -Width 400 | Set-Content -Path (Join-Path $out "$name.txt") -Encoding UTF8 }
    catch { "ERROR: $_" | Set-Content -Path (Join-Path $out "$name.txt") }
}

Write-Host "Collecting into $out" -ForegroundColor Cyan

Save 'os'          { Get-CimInstance Win32_OperatingSystem | Format-List Caption, Version, BuildNumber, OSArchitecture; whoami; "Session: $session" }
Save 'sessions'    { qwinsta; ''; query user }
Save 'session-procs' { Get-Process | Where-Object { $_.ProcessName -in 'dwm', 'csrss', 'winlogon', 'explorer', 'LogonUI', 'fontdrvhost' } |
                       Sort-Object SessionId, ProcessName | Format-Table SessionId, ProcessName, Id, StartTime -AutoSize }
Save 'gpu'         { Get-CimInstance Win32_VideoController | Format-List Name, DriverVersion, DriverDate, VideoModeDescription, CurrentRefreshRate, PNPDeviceID }
Save 'monitors'    { Get-CimInstance -Namespace root\wmi WmiMonitorID -ErrorAction SilentlyContinue |
                       ForEach-Object { $name = -join ($_.UserFriendlyName | Where-Object { $_ } | ForEach-Object { [char]$_ }); "$($_.InstanceName)  $name" }
                     Get-CimInstance -Namespace root\wmi WmiMonitorConnectionParams -ErrorAction SilentlyContinue | Format-Table InstanceName, VideoOutputTechnology, Active }
Save 'screens'     { Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.Screen]::AllScreens | Format-List DeviceName, Primary, Bounds, WorkingArea }
Save 'display-devices' { pnputil /enum-devices /class Display /drivers; pnputil /enum-devices /class Monitor }
Save 'input-devices' { pnputil /enum-devices /class Keyboard; pnputil /enum-devices /class Mouse; pnputil /enum-devices /class HIDClass }
Save 'audio-devices' { pnputil /enum-devices /class MEDIA; pnputil /enum-devices /class AudioEndpoint }
Save 'class-filters' { foreach ($g in '{4D36E96B-E325-11CE-BFC1-08002BE10318}', '{4D36E96F-E325-11CE-BFC1-08002BE10318}', '{4D36E968-E325-11CE-BFC1-08002BE10318}', '{36FC9E60-C465-11CF-8056-444553540000}') {
                         $k = Get-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$g" -ErrorAction SilentlyContinue
                         "$g  Upper=$($k.UpperFilters -join ',')  Lower=$($k.LowerFilters -join ',')" } }
Save 'drivers'     { driverquery /v /fo csv }
Save 'driver-files' { Get-ChildItem "$env:SystemRoot\System32\drivers" -Filter *.sys | Sort-Object LastWriteTime -Descending | Select-Object -First 40 Name, LastWriteTime, Length | Format-Table -AutoSize }
Save 'minifilters' { fltmc; ''; fltmc instances }
Save 'services'    { Get-CimInstance Win32_Service | Where-Object { $_.PathName -notmatch '\\Windows\\' -or $_.Name -match 'term|umrdp|sessionenv|audio' } |
                       Format-Table Name, State, StartMode, PathName -AutoSize }
Save 'terminal-server' { Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' -ErrorAction SilentlyContinue | Format-List
                         Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSChildName }
Save 'code-integrity' { bcdedit /enum '{current}'
                        Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard Win32_DeviceGuard -ErrorAction SilentlyContinue |
                          Format-List SecurityServicesRunning, VirtualizationBasedSecurityStatus, CodeIntegrityPolicyEnforcementStatus }

$probe = Join-Path $PSScriptRoot 'SeatProbe.exe'
if (Test-Path $probe) { Save 'seatprobe' { & $probe } } else { Write-Host '  (SeatProbe.exe not next to this script - skipped)' }

Write-Host "Done: $out" -ForegroundColor Green
