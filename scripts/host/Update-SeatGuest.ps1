#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Copies the host GPU driver, SeatAgent and the seat's config into a seat VM's disk.

.DESCRIPTION
    GPU-P guests do not install a normal GPU driver. They load the HOST's driver from
    C:\Windows\System32\HostDriverStore inside the VM. This script mounts the seat VHDX
    (VM must be off), and:

      1. Copies the GPU driver package folder(s) from the host DriverStore to
         <guest>\Windows\System32\HostDriverStore\FileRepository\...
      2. Copies the driver's non-DriverStore files (e.g. nvapi64.dll) to the same path in the guest.
      3. Copies SeatAgent (out\SeatAgent) and the guest setup script to <guest>\MultiSeat\
      4. Writes <guest>\MultiSeat\seat.json (name, UDP port, per-seat secret).

    Run it again after EVERY host GPU driver update, or the seat's GPU will stop working.

.EXAMPLE
    .\Update-SeatGuest.ps1 -SeatName Seat1
    .\Update-SeatGuest.ps1 -All
#>
[CmdletBinding(DefaultParameterSetName = 'One')]
param(
    [Parameter(Mandatory, ParameterSetName = 'One')][string]$SeatName,
    [Parameter(Mandatory, ParameterSetName = 'All')][switch]$All,
    [string]$ConfigPath,
    [switch]$SkipDrivers
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-HyperV

$config = Import-MultiSeatConfig -Path $ConfigPath
$repo = Get-MultiSeatRepoRoot
$seats = if ($All) { @($config.seats) } else { @(Get-Seat $config $SeatName) }

function Get-GpuDriverFiles {
    <# Every file that belongs to the GPU's signed driver, as reported by WMI. #>
    param([string]$InstanceId)
    $driver = Get-CimInstance Win32_PnPSignedDriver | Where-Object { $_.DeviceID -eq $InstanceId }
    if (-not $driver) { throw "No signed driver found for $InstanceId." }
    Write-Ok "Host driver: $($driver.DeviceName) $($driver.DriverVersion) ($($driver.InfName))"

    $files = Get-CimInstance Win32_PnPSignedDriverCIMDataFile |
        Where-Object { $_.Antecedent.DeviceID -eq $InstanceId } |
        ForEach-Object { $_.Dependent.Name }
    return @($files | Where-Object { $_ } | Sort-Object -Unique)
}

$gpu = $null
$driverFiles = @()
if (-not $SkipDrivers) {
    Write-Step 'Collecting host GPU driver files'
    $gpu = Get-MultiSeatGpu $config
    $driverFiles = Get-GpuDriverFiles (ConvertTo-PnpInstanceId $gpu.Name)
    Write-Ok "$($driverFiles.Count) driver files"
}

$agentDir = Join-Path $repo 'out\SeatAgent'
if (-not (Test-Path (Join-Path $agentDir 'SeatAgent.exe'))) {
    Write-Note "SeatAgent not built ($agentDir). Run .\build.ps1 first; skipping agent copy."
    $agentDir = $null
}

$hostStore = Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository'

foreach ($seat in $seats) {
    $vmName = Get-SeatVmName $seat
    $paths = Get-SeatPaths $config $seat
    Write-Step "Updating $vmName"

    $vm = Get-VM -Name $vmName
    if ($vm.State -ne 'Off') { throw "$vmName must be turned off (it is $($vm.State))." }
    $vhd = (Get-VMHardDiskDrive -VMName $vmName | Select-Object -First 1).Path
    if (-not $vhd) { $vhd = $paths.Vhd }

    $root = Mount-SeatVhdWindows $vhd
    try {
        if (-not $SkipDrivers) {
            $guestStore = Join-Path $root 'Windows\System32\HostDriverStore\FileRepository'
            New-Item -ItemType Directory -Force -Path $guestStore | Out-Null

            $packageDirs = @{}
            foreach ($file in $driverFiles) {
                if ($file.StartsWith($hostStore, [StringComparison]::OrdinalIgnoreCase)) {
                    # ...\FileRepository\<package folder>\...  -> copy the whole package folder once
                    $relative = $file.Substring($hostStore.Length).TrimStart('\')
                    $packageDirs[($relative -split '\\')[0]] = $true
                } elseif (Test-Path $file) {
                    $target = Join-Path $root ($file.Substring(3))   # strip "C:\"
                    New-Item -ItemType Directory -Force -Path (Split-Path $target) | Out-Null
                    Copy-Item -Path $file -Destination $target -Force
                }
            }
            foreach ($dir in $packageDirs.Keys) {
                $target = Join-Path $guestStore $dir
                if (Test-Path $target) { Remove-Item -Recurse -Force $target }
                Copy-Item -Recurse -Force -Path (Join-Path $hostStore $dir) -Destination $target
            }
            Write-Ok "GPU driver copied ($($packageDirs.Count) package folder(s))"
        }

        $guestDir = Join-Path $root 'MultiSeat'
        New-Item -ItemType Directory -Force -Path $guestDir | Out-Null

        if ($agentDir) {
            $guestAgent = Join-Path $guestDir 'Agent'
            New-Item -ItemType Directory -Force -Path $guestAgent | Out-Null
            Copy-Item -Recurse -Force -Path (Join-Path $agentDir '*') -Destination $guestAgent
            Write-Ok 'SeatAgent copied'
        }
        Copy-Item -Force -Path (Join-Path $repo 'scripts\guest\Initialize-SeatGuest.ps1') -Destination $guestDir

        $seatConfig = [ordered]@{
            name        = $seat.name
            bindAddress = '0.0.0.0'
            port        = [int](Get-SeatSetting $config $seat 'agentPort' 47800)
            secret      = Get-SeatSecret $config $seat
            watchdogMs  = 2000
        }
        $seatJson = Join-Path $guestDir 'seat.json'
        $seatConfig | ConvertTo-Json | Set-Content -Path $seatJson -Encoding UTF8
        Write-Ok "Wrote $($seatJson.Substring(2)) inside the VM"
    } finally {
        Dismount-VHD -Path $vhd
    }
}

Write-Host ''
Write-Host 'Done. Inside each VM run (as Administrator, once):' -ForegroundColor Green
Write-Host '  powershell -ExecutionPolicy Bypass -File C:\MultiSeat\Initialize-SeatGuest.ps1'
