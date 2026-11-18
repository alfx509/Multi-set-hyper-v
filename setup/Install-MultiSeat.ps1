#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time setup of this PC for MultiSeat. Run as Administrator, then restart the PC.

.DESCRIPTION
    1. Installs MultiSeat to C:\Program Files\MultiSeat (from .\out, made by build.ps1)
    2. Starts the seat agent in every user session (HKLM Run key)
    3. Downloads and installs the Interception input driver (tells keyboards/mice apart)
    4. Turns on Remote Desktop for this PC only (no firewall port is opened)
    5. Lets seat sessions use the graphics card, at 60 fps, with sound, microphone and USB devices
    6. Allows the saved seat passwords to be used for the local (127.0.0.x) seat connections
    7. Starts the Windows Audio service (it is off by default on Windows Server)
    8. Optionally creates the Windows users for the players (-CreateUsers player2,player3)

.EXAMPLE
    .\setup\Install-MultiSeat.ps1 -CreateUsers player2,player3
    .\setup\Install-MultiSeat.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string[]]$CreateUsers,
    [string]$SourceDir,
    [switch]$SkipInterception,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Ok([string]$m) { Write-Host "    [ OK ] $m" -ForegroundColor Green }
function Warn([string]$m) { Write-Host "    [WARN] $m" -ForegroundColor Yellow }

function Set-Reg([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord') {
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

$repo = Split-Path $PSScriptRoot -Parent
if (-not $SourceDir) { $SourceDir = Join-Path $repo 'out' }
$installDir = Join-Path $env:ProgramFiles 'MultiSeat'
$agentExe = Join-Path $installDir 'Agent\SeatAgent.exe'
$dataDir = Join-Path $env:ProgramData 'MultiSeat'
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$tsPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$interceptionDir = Join-Path $repo 'third_party\Interception'
$interceptionInstaller = Join-Path $interceptionDir 'command line installer\install-interception.exe'

if ($Uninstall) {
    Step 'Removing MultiSeat'
    Get-Process MultiSeat, SeatAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-ItemProperty -Path $runKey -Name 'MultiSeatAgent' -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $installDir -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'MultiSeat.lnk') -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'MultiSeat.lnk') -ErrorAction SilentlyContinue
    if (Test-Path $interceptionInstaller) { & $interceptionInstaller /uninstall | Out-Null; Ok 'Interception driver removed' }
    Ok "Removed. Settings in $dataDir and Windows policies were left as they are. Restart the PC."
    return
}

# ------------------------------------------------------------------ Windows edition
Step 'Windows edition'
$os = Get-CimInstance Win32_OperatingSystem
Ok "$($os.Caption) (build $($os.BuildNumber))"
if ($os.ProductType -eq 1) {
    Warn 'Windows 10/11 allows only ONE signed-in person at a time, so extra seats will not work here.'
    Warn 'Use Windows Server 2019/2022/2025 (the free 180-day evaluation is fine for testing).'
}

# ------------------------------------------------------------------ Program files
Step "Installing to $installDir"
foreach ($p in 'MultiSeat\MultiSeat.exe', 'SeatAgent\SeatAgent.exe') {
    if (-not (Test-Path (Join-Path $SourceDir $p))) { throw "$SourceDir\$p not found. Run .\build.ps1 first." }
}
Get-Process MultiSeat -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process SeatAgent -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $installDir, "$installDir\Agent" | Out-Null
Copy-Item -Recurse -Force (Join-Path $SourceDir 'MultiSeat\*') $installDir
Copy-Item -Recurse -Force (Join-Path $SourceDir 'SeatAgent\*') "$installDir\Agent"
Set-Reg $runKey 'MultiSeatAgent' "`"$agentExe`"" 'String'
Ok 'Seat agent starts in every session (it exits at once for users that are not seats)'

# Agent settings folder: users may open it, but each seat file is readable only by its own user.
$agents = Join-Path $dataDir 'agents'
New-Item -ItemType Directory -Force -Path $agents | Out-Null
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
$none = [Security.AccessControl.PropagationFlags]::None
foreach ($sid in 'S-1-5-32-544', 'S-1-5-18') {
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
        (New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', $inherit, $none, 'Allow')))
}
$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule(
    (New-Object Security.Principal.SecurityIdentifier 'S-1-5-32-545'), 'ReadAndExecute', 'None', $none, 'Allow')))
Set-Acl -Path $agents -AclObject $acl

$shell = New-Object -ComObject WScript.Shell
foreach ($folder in [Environment]::GetFolderPath('CommonDesktopDirectory'), [Environment]::GetFolderPath('CommonPrograms')) {
    $lnk = $shell.CreateShortcut((Join-Path $folder 'MultiSeat.lnk'))
    $lnk.TargetPath = Join-Path $installDir 'MultiSeat.exe'
    $lnk.WorkingDirectory = $installDir
    $lnk.Save()
}
Ok 'Shortcuts on the desktop and in the Start menu'

# ------------------------------------------------------------------ Interception
if (-not $SkipInterception) {
    Step 'Interception input driver'
    if (-not (Test-Path $interceptionInstaller)) {
        $zip = Join-Path $env:TEMP 'Interception.zip'
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -OutFile $zip -Uri 'https://github.com/oblitum/Interception/releases/download/v1.0.1/Interception.zip'
        New-Item -ItemType Directory -Force -Path (Split-Path $interceptionDir) | Out-Null
        Expand-Archive -Force -Path $zip -DestinationPath (Split-Path $interceptionDir)
        Remove-Item $zip
    }
    & $interceptionInstaller /install | Out-Null
    Copy-Item -Force (Join-Path $interceptionDir 'library\x64\interception.dll') $installDir
    Ok 'Installed (free for non-commercial use; see https://github.com/oblitum/Interception)'
}

# ------------------------------------------------------------------ Remote Desktop + graphics
Step 'Seat sessions'
Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server' 'fDenyTSConnections' 0
Ok 'Remote Desktop on (used only inside this PC; no firewall port opened)'

Set-Reg $tsPolicy 'bEnumerateHWBeforeSW' 1          # use the graphics card in every session
Set-Reg $tsPolicy 'AVC444ModePreferred' 1           # sharp colours
Set-Reg $tsPolicy 'AVCHardwareEncodePreferred' 1    # GPU video encoder
Set-Reg $tsPolicy 'fEnableWddmDriver' 1
Set-Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations' 'DWMFRAMEINTERVAL' 15   # 60 fps
Ok 'Graphics card, 60 fps, hardware video encoding'

Set-Reg $tsPolicy 'fDisableAudioCapture' 0          # microphones
Set-Reg $tsPolicy 'fDisableCam' 0
Set-Reg $tsPolicy 'fDisablePNPRedir' 0              # USB devices (gamepads etc., experimental)
Set-Reg "$tsPolicy\Client" 'fUsbRedirectionEnableMode' 2
Ok 'Sound, microphone and USB devices allowed in seats'

# Saved passwords for TERMSRV/127.0.0.x (NTLM-only, because it is this same PC).
$cd = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation'
Set-Reg $cd 'AllowSavedCredentialsWhenNTLMOnly' 1
Set-Reg $cd 'ConcatenateDefaults_AllowSavedNTLMOnly' 1
Set-Reg "$cd\AllowSavedCredentialsWhenNTLMOnly" '1' 'TERMSRV/127.0.0.*' 'String'
Ok 'Seats sign in with their saved password'

Step 'Windows Audio'
foreach ($svc in 'AudioEndpointBuilder', 'Audiosrv') {
    Set-Service -Name $svc -StartupType Automatic
    Start-Service -Name $svc -ErrorAction SilentlyContinue
}
Ok 'Audio services on'

# ------------------------------------------------------------------ Users
foreach ($user in $CreateUsers) {
    Step "User $user"
    if (Get-LocalUser -Name $user -ErrorAction SilentlyContinue) {
        Ok 'already exists'
    } else {
        $password = Read-Host "Password for $user" -AsSecureString
        New-LocalUser -Name $user -Password $password -PasswordNeverExpires -FullName $user | Out-Null
        Ok 'created (type the same password in MultiSeat when you add the seat)'
    }
    Add-LocalGroupMember -Group (Get-LocalGroup -SID 'S-1-5-32-555') -Member $user -ErrorAction SilentlyContinue  # Remote Desktop Users
}

Write-Host ''
Write-Host 'Done. RESTART the PC, then open MultiSeat from the desktop.' -ForegroundColor Green
