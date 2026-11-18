#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Starts a multi-seat session: boots the seat VMs, starts SeatRouter, and opens one viewer per monitor.

.DESCRIPTION
    1. Starts every seat VM and waits for its IP address.
    2. Writes runtime\router.json and starts SeatRouter (for seats with input = "router").
       The first time, each player presses a key and clicks their mouse to claim them.
    3. For every seat, launches the viewer (Moonlight by default) borderless on the seat's monitor.
       Monitors are numbered left to right, starting at 1.
    4. Optionally sends each viewer's sound to the seat's headset (needs NirSoft SoundVolumeView).

.EXAMPLE
    .\Start-MultiSeat.ps1
    .\Start-MultiSeat.ps1 -Pair            # re-assign keyboards and mice
    .\Start-MultiSeat.ps1 -SeatName Seat2  # only one seat
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [string[]]$SeatName,
    [switch]$Pair,
    [switch]$NoRouter,
    [switch]$NoViewers
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-HyperV

$config = Import-MultiSeatConfig -Path $ConfigPath
$repo = Get-MultiSeatRepoRoot
$runtime = Join-Path $repo 'runtime'
New-Item -ItemType Directory -Force -Path $runtime | Out-Null

$seats = @($config.seats)
if ($SeatName) { $seats = @($seats | Where-Object { $SeatName -contains $_.name }) }
if ($seats.Count -eq 0) { throw 'No matching seats.' }

# ---------------------------------------------------------------- 1. VMs
Write-Step 'Starting seat VMs'
foreach ($seat in $seats) {
    $vm = Get-VM -Name (Get-SeatVmName $seat)
    if ($vm.State -ne 'Running') { Start-VM -VM $vm }
}

$addresses = @{}
foreach ($seat in $seats) {
    $vmName = Get-SeatVmName $seat
    $ip = Get-VmIPv4 -VMName $vmName -TimeoutSeconds 240
    if (-not $ip) { throw "$vmName did not report an IP address. Is it logged in and on the network?" }
    $addresses[$seat.name] = $ip
    Write-Ok "$vmName -> $ip"
}

# ---------------------------------------------------------------- 2. Router
$routerSeats = @($seats | Where-Object { (Get-ConfigValue $_ 'input' 'router') -eq 'router' })
if ($routerSeats.Count -gt 0 -and -not $NoRouter) {
    Write-Step 'Starting SeatRouter'
    $routerConfig = [ordered]@{
        pairingFile = Join-Path $runtime 'pairing.json'
        seats       = @($routerSeats | ForEach-Object {
            [ordered]@{
                name    = $_.name
                address = $addresses[$_.name]
                port    = [int](Get-SeatSetting $config $_ 'agentPort' 47800)
                secret  = Get-SeatSecret $config $_
            }
        })
    }
    $routerJson = Join-Path $runtime 'router.json'
    $routerConfig | ConvertTo-Json -Depth 5 | Set-Content -Path $routerJson -Encoding UTF8

    # router.json holds the seat secrets: Administrators and SYSTEM only.
    $acl = Get-Acl $routerJson
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($id in @('BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'Allow')))
    }
    Set-Acl -Path $routerJson -AclObject $acl

    $routerExe = Join-Path $repo 'out\SeatRouter\SeatRouter.exe'
    if (-not (Test-Path $routerExe)) { throw "SeatRouter not built: $routerExe. Run .\build.ps1" }

    Get-Process SeatRouter -ErrorAction SilentlyContinue | Stop-Process -Force
    $routerArgs = @('--config', "`"$routerJson`"")
    if ($Pair) { $routerArgs += '--pair' }
    Start-Process -FilePath $routerExe -ArgumentList $routerArgs -WorkingDirectory (Split-Path $routerExe)
    Write-Ok 'SeatRouter started in its own window. Follow the pairing prompts there if shown.'
}

# ---------------------------------------------------------------- 3. Viewers
if ($NoViewers) { return }

$viewer = Get-ConfigValue $config 'viewer'
$viewerPath = Get-ConfigValue $viewer 'path'
if (-not $viewerPath -or -not (Test-Path $viewerPath)) {
    Write-Note "Viewer not found ($viewerPath). Skipping viewers."
    return
}
$template = Get-ConfigValue $viewer 'arguments' 'stream {address} Desktop --display-mode borderless'
$perSeatCopies = [bool](Get-ConfigValue $viewer 'perSeatCopies' $true)
$svv = Get-ConfigValue (Get-ConfigValue $config 'tools') 'soundVolumeView'

Add-Type -AssemblyName System.Windows.Forms
if (-not ('MultiSeat.Win32' -as [type])) {
    Add-Type -Namespace MultiSeat -Name Win32 -MemberDefinition @'
[DllImport("user32.dll", SetLastError = true)]
public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
'@
}
$screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { $_.Bounds.X }, { $_.Bounds.Y })
Write-Step "Launching viewers ($($screens.Count) monitor(s) found, numbered left to right)"

foreach ($seat in $seats) {
    $monitor = [int](Get-ConfigValue $seat 'monitor' 0)
    if ($monitor -lt 1 -or $monitor -gt $screens.Count) {
        Write-Note "$($seat.name): monitor $monitor does not exist - skipping viewer."
        continue
    }
    $bounds = $screens[$monitor - 1].Bounds

    $resolution = Get-SeatSetting $config $seat 'resolution' 'native'
    if ($resolution -match '^(\d+)x(\d+)$') {
        $width = [int]$Matches[1]; $height = [int]$Matches[2]
    } else {
        $width = $bounds.Width; $height = $bounds.Height
    }

    # Windows remembers the audio output device per program path, so each seat gets its own copy
    # of the viewer: Seat1's viewer can play on Headset 1, Seat2's on Headset 2.
    $exe = $viewerPath
    if ($perSeatCopies) {
        $copyDir = Join-Path $runtime "viewers\$($seat.name)"
        $exe = Join-Path $copyDir (Split-Path $viewerPath -Leaf)
        if (-not (Test-Path $exe)) {
            New-Item -ItemType Directory -Force -Path $copyDir | Out-Null
            Copy-Item -Recurse -Force -Path (Join-Path (Split-Path $viewerPath) '*') -Destination $copyDir
        }
    }

    $audio = Get-ConfigValue $seat 'audioOutput'
    if ($audio -and $svv -and (Test-Path $svv)) {
        & $svv /SetAppDefault $audio all $exe
        Write-Ok "$($seat.name): sound -> '$audio'"
    } elseif ($audio) {
        Write-Note "$($seat.name): set the viewer's output to '$audio' in Settings > Sound > Volume mixer (or configure tools.soundVolumeView)."
    }

    $fps = Get-SeatSetting $config $seat 'fps' 60
    $arguments = $template.Replace('{address}', $addresses[$seat.name]).Replace('{seat}', $seat.name)
    $arguments = $arguments.Replace('{width}', "$width").Replace('{height}', "$height").Replace('{fps}', "$fps")
    $extra = Get-ConfigValue $seat 'viewerExtraArguments'
    if ($extra) { $arguments = "$arguments $extra" }

    $process = Start-Process -FilePath $exe -ArgumentList $arguments -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    while ($process.MainWindowHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Milliseconds 300
        $process.Refresh()
    }
    if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
        # SWP_NOZORDER | SWP_SHOWWINDOW
        [void][MultiSeat.Win32]::SetWindowPos($process.MainWindowHandle, [IntPtr]::Zero,
            $bounds.X, $bounds.Y, $bounds.Width, $bounds.Height, 0x0044)
        Write-Ok "$($seat.name): viewer on monitor $monitor ($($bounds.Width)x$($bounds.Height)), stream ${width}x${height}"
    } else {
        Write-Note "$($seat.name): viewer window did not appear; move it to monitor $monitor by hand."
    }
}

Write-Host ''
Write-Host 'Session running. Ctrl+Alt+F12 on a seat keyboard gives that keyboard/mouse back to the host.' -ForegroundColor Green
Write-Host 'Stop everything with .\Stop-MultiSeat.ps1'
