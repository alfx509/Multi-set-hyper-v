#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time setup INSIDE a seat VM. Run as Administrator after Update-SeatGuest.ps1 copied files in.

.DESCRIPTION
    - Checks that the partitioned GPU is visible
    - Registers SeatAgent to start at logon (it must run in the user's desktop session)
    - Opens the agent's UDP port for the local subnet only
    - Turns off mouse acceleration so routed mouse movement is 1:1
    - High-performance power plan, no sleep / screen off
    - Optional: installs Sunshine (the game-stream server Moonlight connects to) via winget

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File C:\MultiSeat\Initialize-SeatGuest.ps1 -InstallSunshine
#>
[CmdletBinding()]
param(
    [string]$ConfigPath = 'C:\MultiSeat\seat.json',
    [switch]$InstallSunshine
)

$ErrorActionPreference = 'Stop'
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok([string]$m) { Write-Host "    [ OK ] $m" -ForegroundColor Green }
function Warn([string]$m) { Write-Host "    [WARN] $m" -ForegroundColor Yellow }

if (-not (Test-Path $ConfigPath)) { throw "$ConfigPath not found. Run Update-SeatGuest.ps1 on the host (VM off) first." }
$seat = Get-Content -Raw $ConfigPath | ConvertFrom-Json
Step "Setting up seat '$($seat.name)'"

# ------------------------------------------------------------------ GPU
Step 'GPU'
$gpus = @(Get-PnpDevice -Class Display -PresentOnly | Where-Object { $_.FriendlyName -notmatch 'Hyper-V|Basic Display' })
if ($gpus) {
    foreach ($g in $gpus) {
        if ($g.Status -eq 'OK') { Ok "$($g.FriendlyName)" } else { Warn "$($g.FriendlyName): $($g.Status) (re-run Update-SeatGuest.ps1 on the host)" }
    }
} else {
    Warn 'No partitioned GPU visible. On the host: check Set-SeatGpuPartition.ps1 and Update-SeatGuest.ps1.'
}

# ------------------------------------------------------------------ Agent
Step 'SeatAgent'
$agent = 'C:\MultiSeat\Agent\SeatAgent.exe'
if (-not (Test-Path $agent)) {
    Warn "$agent missing. Build on the host (.\build.ps1) and re-run Update-SeatGuest.ps1."
} else {
    $action = New-ScheduledTaskAction -Execute $agent -Argument "--config `"$ConfigPath`""
    $trigger = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal -GroupId 'BUILTIN\Users' -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'MultiSeat Agent' -Action $action -Trigger $trigger -Principal $principal `
        -Settings $settings -Description 'Receives this seat keyboard/mouse from SeatRouter on the host.' -Force | Out-Null
    Ok 'Scheduled task "MultiSeat Agent" runs at logon (elevated, so it can control admin windows too)'

    $port = [int]$seat.port
    Get-NetFirewallRule -DisplayName 'MultiSeat Agent' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'MultiSeat Agent' -Direction Inbound -Protocol UDP -LocalPort $port `
        -RemoteAddress LocalSubnet -Program $agent -Action Allow | Out-Null
    Ok "Firewall: UDP $port allowed from the local subnet only"

    Start-ScheduledTask -TaskName 'MultiSeat Agent'
}

# ------------------------------------------------------------------ Mouse
Step 'Mouse: disable acceleration (1:1 movement)'
$mouse = 'HKCU:\Control Panel\Mouse'
Set-ItemProperty $mouse -Name MouseSpeed -Value '0'
Set-ItemProperty $mouse -Name MouseThreshold1 -Value '0'
Set-ItemProperty $mouse -Name MouseThreshold2 -Value '0'
Set-ItemProperty $mouse -Name MouseSensitivity -Value '10'
Ok 'Done (takes effect after sign-out)'

# ------------------------------------------------------------------ Power
Step 'Power'
powercfg /setactive 8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c 2>$null | Out-Null   # High performance
powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
powercfg /change hibernate-timeout-ac 0
Ok 'High performance, never sleep'

# ------------------------------------------------------------------ Sunshine
if ($InstallSunshine) {
    Step 'Installing Sunshine (game stream server)'
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        winget install --id LizardByte.Sunshine -e --accept-package-agreements --accept-source-agreements
        Ok 'Sunshine installed. Open https://localhost:47990 in this VM to create its login, then pair Moonlight.'
    } else {
        Warn 'winget not found. Download Sunshine from https://github.com/LizardByte/Sunshine/releases'
    }
}

Write-Host ''
Write-Host 'Remaining manual steps in this VM:' -ForegroundColor Green
Write-Host '  1. Install a virtual display driver so the seat has a real desktop at any resolution/refresh rate'
Write-Host '     (e.g. https://github.com/VirtualDrivers/Virtual-Display-Driver), and set it as the main display.'
Write-Host '  2. Enable automatic sign-in (Sysinternals Autologon) so the agent and games start without a login screen.'
Write-Host '  3. Sign in to Steam / your launcher with THIS player''s own account.'
Write-Host '  4. If the seat uses usbip mode, install the usbip-win2 / VirtualHere client and attach this seat''s devices.'
