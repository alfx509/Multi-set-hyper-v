#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates one gaming seat: a Hyper-V VM with its own slice of the shared GPU.

.DESCRIPTION
    - Generation 2 VM, Secure Boot + virtual TPM (Windows 11 ready)
    - Static memory, no checkpoints (GPU-P does not support them), TurnOff on host shutdown
    - Disk: differencing disk on top of config.baseVhd (recommended: a prepared Windows image
      with Steam/launchers installed), or a new empty disk + installIso.
    - GPU partition sized by the seat's gpuShare (Set-SeatGpuPartition.ps1)
    - If Windows is already on the disk: copies GPU driver + SeatAgent (Update-SeatGuest.ps1)

.EXAMPLE
    .\New-GamingSeat.ps1 -SeatName Seat1
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$SeatName,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-HyperV

$config = Import-MultiSeatConfig -Path $ConfigPath
$seat = Get-Seat $config $SeatName
$vmName = Get-SeatVmName $seat
$paths = Get-SeatPaths $config $seat

if (Get-VM -Name $vmName -ErrorAction SilentlyContinue) {
    Write-Note "$vmName already exists - skipping creation (use Remove-GamingSeat.ps1 to rebuild)."
    return
}

$cpu = [int](Get-SeatSetting $config $seat 'cpuCount' 4)
$memGB = [int](Get-SeatSetting $config $seat 'memoryGB' 8)
$diskGB = [int](Get-SeatSetting $config $seat 'diskGB' 256)
$switch = Get-ConfigValue $config 'switchName' 'Default Switch'
$baseVhd = Get-ConfigValue $config 'baseVhd'
$iso = Get-ConfigValue $config 'installIso'

if (-not (Get-VMSwitch -Name $switch -ErrorAction SilentlyContinue)) {
    throw "Virtual switch '$switch' not found."
}

Write-Step "Creating $vmName ($cpu vCPU, $memGB GB RAM)"
if (-not $PSCmdlet.ShouldProcess($vmName, 'Create VM')) { return }

New-Item -ItemType Directory -Force -Path $paths.Directory | Out-Null

$windowsOnDisk = $false
if ($baseVhd) {
    if (-not (Test-Path $baseVhd)) { throw "baseVhd not found: $baseVhd" }
    New-VHD -Path $paths.Vhd -ParentPath $baseVhd -Differencing | Out-Null
    $windowsOnDisk = $true
    Write-Ok "Differencing disk on $baseVhd"
} else {
    New-VHD -Path $paths.Vhd -SizeBytes ([uint64]$diskGB * 1GB) -Dynamic | Out-Null
    Write-Ok "New $diskGB GB dynamic disk"
}

New-VM -Name $vmName -Generation 2 -MemoryStartupBytes ([uint64]$memGB * 1GB) `
    -VHDPath $paths.Vhd -SwitchName $switch -Path $config.vmRoot | Out-Null

Set-VMProcessor -VMName $vmName -Count $cpu
Set-VMMemory -VMName $vmName -DynamicMemoryEnabled $false
Set-VM -VMName $vmName -CheckpointType Disabled -AutomaticStopAction TurnOff -AutomaticStartAction Nothing
Set-VMFirmware -VMName $vmName -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
Set-VMKeyProtector -VMName $vmName -NewLocalKeyProtector
Enable-VMTPM -VMName $vmName
Get-VMIntegrationService -VMName $vmName | Where-Object { -not $_.Enabled } | Enable-VMIntegrationService
Write-Ok 'VM created (Gen 2, Secure Boot, vTPM, no checkpoints, static RAM)'

if (-not $windowsOnDisk -and $iso) {
    Add-VMDvdDrive -VMName $vmName -Path $iso
    $dvd = Get-VMDvdDrive -VMName $vmName
    Set-VMFirmware -VMName $vmName -FirstBootDevice $dvd
    Write-Ok "Install ISO attached: $iso"
}

& (Join-Path $PSScriptRoot 'Set-SeatGpuPartition.ps1') -SeatName $SeatName -ConfigPath $config.__path

# Create the per-seat secret now so SeatRouter and SeatAgent agree on it.
Get-SeatSecret $config $seat | Out-Null

if ($windowsOnDisk) {
    & (Join-Path $PSScriptRoot 'Update-SeatGuest.ps1') -SeatName $SeatName -ConfigPath $config.__path
} else {
    Write-Host ''
    Write-Host "Next: start $vmName, install Windows, shut it down, then run:" -ForegroundColor Yellow
    Write-Host "  .\Update-SeatGuest.ps1 -SeatName $SeatName"
}
