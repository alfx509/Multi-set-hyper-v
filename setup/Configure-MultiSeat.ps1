#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Prepares Windows for MultiSeat. MultiSeat-Setup.exe runs this for you; you normally never run it by hand.

.DESCRIPTION
    1. Starts the player agent at every sign-in (scheduled task "MultiSeat Agent")
    2. Installs the Interception keyboard/mouse driver (shipped in <AppDir>\interception)
    3. Turns on Remote Desktop for this PC only (no firewall port is opened)
    4. Lets player sessions use the graphics card, at 60 fps, with sound, microphone and USB devices
    5. Lets the saved player passwords be used for the local (127.0.0.x) player connections
    6. Turns on the Windows Audio service (off by default on Windows Server)
    7. Optional: installs the Remote Desktop Session Host role (Windows Server, needed for 4-8 players)

    Log: C:\ProgramData\MultiSeat\setup.log. Exit code 0 = OK, 1 = failed (see log).

.EXAMPLE
    .\Configure-MultiSeat.ps1 -AppDir 'C:\Program Files\MultiSeat'
    .\Configure-MultiSeat.ps1 -AppDir 'C:\Program Files\MultiSeat' -InstallSessionHost
    .\Configure-MultiSeat.ps1 -AppDir 'C:\Program Files\MultiSeat' -Uninstall
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppDir,
    [switch]$InstallSessionHost,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:ProgramData 'MultiSeat'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
Start-Transcript -Path (Join-Path $dataDir 'setup.log') -Append | Out-Null

function Step([string]$m) { Write-Host "==> $m" }
function Ok([string]$m) { Write-Host "    [ OK ] $m" }
function Warn([string]$m) { Write-Host "    [WARN] $m" }

function Set-Reg([string]$Path, [string]$Name, $Value, [string]$Type = 'DWord') {
    if (-not (Test-Path $Path)) { New-Item -Path $Path -Force | Out-Null }
    New-ItemProperty -Path $Path -Name $Name -Value $Value -PropertyType $Type -Force | Out-Null
}

$agentExe = Join-Path $AppDir 'Agent\SeatAgent.exe'
$interceptionDir = Join-Path $AppDir 'interception'
$interceptionInstaller = Join-Path $interceptionDir 'command line installer\install-interception.exe'
$tsPolicy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$taskName = 'MultiSeat Agent'

try {
    if ($Uninstall) {
        Step 'Removing MultiSeat from Windows'
        Get-Process MultiSeat, SeatAgent -ErrorAction SilentlyContinue | Stop-Process -Force
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName 'MultiSeat' -Confirm:$false -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $runKey -Name 'MultiSeatAgent' -ErrorAction SilentlyContinue
        if (Test-Path $interceptionInstaller) {
            & $interceptionInstaller /uninstall | Out-Null
            Ok 'Keyboard/mouse driver removed (after restart)'
        }
        Ok "Done. Player accounts and $dataDir were kept."
        exit 0
    }

    $os = Get-CimInstance Win32_OperatingSystem
    Step "Windows: $($os.Caption) (build $($os.BuildNumber))"
    if ($os.ProductType -eq 1) {
        Warn 'Windows 10/11 normally allows one signed-in person at a time. Windows Server supports several players.'
    }

    # ---------------------------------------------------------------- agent
    Step 'Player agent'
    if (-not (Test-Path $agentExe)) { throw "$agentExe is missing." }
    Get-Process SeatAgent -ErrorAction SilentlyContinue | Stop-Process -Force
    Remove-ItemProperty -Path $runKey -Name 'MultiSeatAgent' -ErrorAction SilentlyContinue
    $task = @{
        TaskName    = $taskName
        Action      = New-ScheduledTaskAction -Execute $agentExe
        Trigger     = New-ScheduledTaskTrigger -AtLogOn
        Principal   = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited
        Settings    = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
                        -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances Parallel
        Description = 'Gives each MultiSeat player their own keyboard and mouse.'
    }
    Register-ScheduledTask @task -Force | Out-Null
    Ok 'Starts at every sign-in (exits at once for users that are not players)'

    # Player settings folder: users may open it; each player's file is readable only by that player.
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

    # ---------------------------------------------------------------- keyboard/mouse driver
    Step 'Keyboard/mouse driver (Interception)'
    if (-not (Test-Path $interceptionInstaller)) { throw "$interceptionInstaller is missing." }
    & $interceptionInstaller /install | Out-Null
    $dll = Join-Path $interceptionDir 'library\x64\interception.dll'
    if ((Test-Path $dll) -and -not (Test-Path (Join-Path $AppDir 'interception.dll'))) {
        Copy-Item -Force $dll $AppDir
    }
    if (Test-Path (Join-Path $env:SystemRoot 'System32\drivers\keyboard.sys')) {
        Ok 'Installed - active after restart'
    } else {
        Warn 'The driver installer ran but the driver files were not found. See the log.'
    }

    # ---------------------------------------------------------------- sessions, graphics, sound
    Step 'Player sessions'
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
    Ok 'Sound, microphone and USB devices allowed for players'

    # Saved passwords for TERMSRV/127.0.0.x (NTLM-only, because it is this same PC).
    $cd = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation'
    Set-Reg $cd 'AllowSavedCredentialsWhenNTLMOnly' 1
    Set-Reg $cd 'ConcatenateDefaults_AllowSavedNTLMOnly' 1
    Set-Reg "$cd\AllowSavedCredentialsWhenNTLMOnly" '1' 'TERMSRV/127.0.0.*' 'String'
    Ok 'Players sign in with their saved password'

    Step 'Windows Audio'
    foreach ($svc in 'AudioEndpointBuilder', 'Audiosrv') {
        Set-Service -Name $svc -StartupType Automatic
        Start-Service -Name $svc -ErrorAction SilentlyContinue
    }
    Ok 'Audio services on'

    # ---------------------------------------------------------------- 4-8 players
    if ($InstallSessionHost) {
        Step 'Remote Desktop Session Host (for 4-8 players)'
        if ($os.ProductType -eq 1) {
            Warn 'Only available on Windows Server - skipped.'
        } elseif ((Get-WindowsFeature RDS-RD-Server).Installed) {
            Ok 'already installed'
        } else {
            Install-WindowsFeature RDS-RD-Server -IncludeManagementTools | Out-Null
            Ok 'Installed. Works 120 days without RDS licenses; after that add RDS licenses.'
        }
    }

    Step 'Done. Restart the PC, then open MultiSeat.'
    exit 0
} catch {
    Write-Host "    [FAIL] $($_.Exception.Message)"
    exit 1
} finally {
    Stop-Transcript | Out-Null
}
