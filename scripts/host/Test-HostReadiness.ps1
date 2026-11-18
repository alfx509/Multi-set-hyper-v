#Requires -Version 5.1
<#
.SYNOPSIS
    Checks whether this PC can run Multi-Seat Hyper-V gaming, and how many seats it can afford.

.DESCRIPTION
    Read-only. Checks Windows edition, virtualization, Hyper-V, partitionable GPUs (GPU-P),
    CPU / RAM budget for the configured seats, the Interception driver (router input mode),
    DDA support (usb-dda input mode) and the viewer program.

.EXAMPLE
    .\Test-HostReadiness.ps1
    .\Test-HostReadiness.ps1 -ConfigPath ..\..\config\seats.json
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force

$failures = 0
function Fail([string]$m) { Write-Bad $m; $script:failures++ }

Write-Step 'Windows'
$os = Get-CimInstance Win32_OperatingSystem
$isServer = $os.ProductType -ne 1
Write-Ok "$($os.Caption) build $($os.BuildNumber)"
if (-not $isServer -and $os.Caption -match 'Home') {
    Fail 'Windows Home has no Hyper-V. Use Windows 10/11 Pro, Enterprise, Education or Windows Server.'
}
if ([int]$os.BuildNumber -lt 19041) {
    Fail 'GPU partitioning needs Windows 10 20H1 (19041) or newer; Windows 11 is recommended.'
}

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Ok 'Running as Administrator'
} else {
    Write-Note 'Not elevated: some checks (Hyper-V, GPU list) may be incomplete. Re-run as Administrator.'
}

Write-Step 'Virtualization and Hyper-V'
$cs = Get-CimInstance Win32_ComputerSystem
if ($cs.HypervisorPresent) {
    Write-Ok 'Hypervisor is running'
} else {
    $cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
    if ($cpu.VirtualizationFirmwareEnabled) {
        Fail 'Virtualization is enabled in firmware but Hyper-V is not running. Enable Hyper-V and reboot.'
    } else {
        Fail 'Enable Intel VT-x / AMD-V (SVM) and VT-d / AMD-Vi (IOMMU) in BIOS/UEFI, then enable Hyper-V.'
    }
}
$hyperVReady = [bool](Get-Command Get-VM -ErrorAction SilentlyContinue)
if ($hyperVReady) { Write-Ok 'Hyper-V PowerShell module present' } else { Fail 'Hyper-V PowerShell module missing.' }

Write-Step 'GPU partitioning (GPU-P)'
$gpus = @()
if ($hyperVReady) {
    try { $gpus = Get-HostPartitionableGpus } catch { Write-Note "Could not query GPUs: $($_.Exception.Message)" }
}
if ($gpus.Count -eq 0) {
    Fail 'No partitionable GPU. Install the latest NVIDIA/AMD/Intel driver (WDDM 2.5+). Some laptops with hybrid graphics do not support GPU-P.'
}
foreach ($g in $gpus) {
    $vramGB = [math]::Round($g.TotalVRAM / 1GB, 1)
    Write-Ok ("{0}  (VRAM {1} GB, partitions {2})" -f (Get-GpuFriendlyName $g.Name), $vramGB, $g.PartitionCount)
    Write-Host "           instancePath: $($g.Name)" -ForegroundColor DarkGray
}

Write-Step 'Configuration'
$config = $null
try {
    $config = Import-MultiSeatConfig -Path $ConfigPath
    Write-Ok "Loaded $($config.__path) ($(@($config.seats).Count) seats)"
} catch {
    Write-Note $_.Exception.Message
}

$logical = (Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
$ramGB = [math]::Round($cs.TotalPhysicalMemory / 1GB)
Write-Ok "Host: $logical logical CPUs, $ramGB GB RAM"

if ($config) {
    $cpuSum = 0; $ramSum = 0
    foreach ($seat in $config.seats) {
        $cpuSum += [int](Get-SeatSetting $config $seat 'cpuCount' 4)
        $ramSum += [int](Get-SeatSetting $config $seat 'memoryGB' 8)
    }
    if ($cpuSum -ge $logical) {
        Write-Note "Seats use $cpuSum vCPUs of $logical. Leave 2+ threads for the host (encoding, routing)."
    } else {
        Write-Ok "Seats use $cpuSum of $logical vCPUs"
    }
    if ($ramSum + 6 -gt $ramGB) {
        Fail "Seats need $ramSum GB RAM + ~6 GB for the host, but only $ramGB GB is installed."
    } else {
        Write-Ok "Seats use $ramSum GB of $ramGB GB RAM"
    }

    if ($gpus.Count -gt 0) {
        $vramGB = [math]::Round($gpus[0].TotalVRAM / 1GB, 1)
        foreach ($seat in $config.seats) {
            $seatVram = [math]::Round($vramGB * [double]$seat.gpuShare, 1)
            $msg = "$($seat.name): ~$seatVram GB VRAM"
            if ($seatVram -lt 3) { Write-Note "$msg (low - modern games want 4 GB+)" } else { Write-Ok $msg }
        }
    }

    $modes = @($config.seats | ForEach-Object { Get-ConfigValue $_ 'input' 'router' } | Sort-Object -Unique)

    if ($modes -contains 'router') {
        Write-Step 'Router input mode (Interception driver)'
        $drivers = Join-Path $env:SystemRoot 'System32\drivers'
        if ((Test-Path (Join-Path $drivers 'keyboard.sys')) -and (Test-Path (Join-Path $drivers 'mouse.sys'))) {
            Write-Ok 'Interception driver installed'
        } else {
            Fail 'Interception driver not installed. Run Install-Interception.ps1 and reboot.'
        }
        $router = Join-Path (Get-MultiSeatRepoRoot) 'out\SeatRouter\SeatRouter.exe'
        if (Test-Path $router) { Write-Ok "SeatRouter built: $router" } else { Write-Note 'SeatRouter not built yet. Run .\build.ps1' }
    }

    if ($modes -contains 'usb-dda') {
        Write-Step 'USB controller passthrough (DDA)'
        if ($isServer -and (Get-Command Dismount-VMHostAssignableDevice -ErrorAction SilentlyContinue)) {
            Write-Ok 'Discrete Device Assignment available'
        } else {
            Fail 'usb-dda needs Windows Server 2016+ (DDA is not supported on Windows 10/11). Use input "router" or "usbip".'
        }
    }

    Write-Step 'Viewer'
    $viewerPath = Get-ConfigValue (Get-ConfigValue $config 'viewer') 'path'
    if ($viewerPath -and (Test-Path $viewerPath)) {
        Write-Ok "Viewer found: $viewerPath"
    } else {
        Write-Note "Viewer not found ($viewerPath). Install Moonlight (https://moonlight-stream.org) or set viewer.path."
    }

    Write-Step 'Switch'
    if ($hyperVReady) {
        $sw = Get-ConfigValue $config 'switchName' 'Default Switch'
        if (Get-VMSwitch -Name $sw -ErrorAction SilentlyContinue) { Write-Ok "Virtual switch '$sw' exists" }
        else { Fail "Virtual switch '$sw' not found. Create it in Hyper-V Manager or set switchName." }
    }
}

Write-Host ''
if ($failures -eq 0) {
    Write-Host 'Host looks ready.' -ForegroundColor Green
} else {
    Write-Host "$failures problem(s) found - fix them before Deploy-MultiSeat.ps1." -ForegroundColor Red
}
exit $failures
