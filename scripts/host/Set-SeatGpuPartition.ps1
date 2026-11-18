#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Gives a seat VM its slice of the shared GPU (Hyper-V GPU partitioning / GPU-P).

.DESCRIPTION
    Removes any existing GPU partition adapter from the VM, adds a new one on the configured
    GPU, and sizes VRAM / encode / decode / compute to the seat's gpuShare (0..1).
    The VM must be turned off.

.EXAMPLE
    .\Set-SeatGpuPartition.ps1 -SeatName Seat1
    .\Set-SeatGpuPartition.ps1 -SeatName Seat2 -GpuShare 0.33
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$SeatName,
    [double]$GpuShare,
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-HyperV

$config = Import-MultiSeatConfig -Path $ConfigPath
$seat = Get-Seat $config $SeatName
$vmName = Get-SeatVmName $seat
if (-not $PSBoundParameters.ContainsKey('GpuShare')) { $GpuShare = [double]$seat.gpuShare }
if ($GpuShare -le 0 -or $GpuShare -gt 1) { throw 'GpuShare must be > 0 and <= 1.' }

$vm = Get-VM -Name $vmName
if ($vm.State -ne 'Off') { throw "$vmName must be turned off (it is $($vm.State))." }

$gpu = Get-MultiSeatGpu $config
Write-Step ("{0}: {1:P0} of {2}" -f $vmName, $GpuShare, (Get-GpuFriendlyName $gpu.Name))

$partitionCount = [int](Get-ConfigValue (Get-ConfigValue $config 'gpu') 'partitionCount' 0)
if ($partitionCount -gt 0 -and (Get-Command Set-VMHostPartitionableGpu -ErrorAction SilentlyContinue)) {
    try {
        Set-VMHostPartitionableGpu -Name $gpu.Name -PartitionCount $partitionCount
        Write-Ok "GPU partition count set to $partitionCount"
    } catch {
        Write-Note "Could not set partition count: $($_.Exception.Message)"
    }
}

if ($PSCmdlet.ShouldProcess($vmName, 'Replace GPU partition adapter')) {
    Get-VMGpuPartitionAdapter -VMName $vmName -ErrorAction SilentlyContinue | Remove-VMGpuPartitionAdapter

    $addParams = @{ VMName = $vmName }
    if ((Get-Command Add-VMGpuPartitionAdapter).Parameters.ContainsKey('InstancePath')) {
        $addParams.InstancePath = $gpu.Name
    }
    Add-VMGpuPartitionAdapter @addParams

    # Equal min/max/optimal pins the slice; the GPU scheduler still time-slices between seats.
    $set = @{ VMName = $vmName }
    foreach ($resource in 'VRAM', 'Encode', 'Decode', 'Compute') {
        $max = [double]$gpu."MaxPartition$resource"
        $value = [uint64][math]::Floor($max * $GpuShare)
        if ($value -lt 1) { $value = 1 }
        $set["MinPartition$resource"] = $value
        $set["MaxPartition$resource"] = $value
        $set["OptimalPartition$resource"] = $value
    }
    Set-VMGpuPartitionAdapter @set

    # GPU-P needs write-combining and a large MMIO window in the guest.
    $low = [int](Get-SeatSetting $config $seat 'lowMmioGB' 1)
    $high = [int](Get-SeatSetting $config $seat 'highMmioGB' 32)
    Set-VM -VMName $vmName -GuestControlledCacheTypes $true `
        -LowMemoryMappedIoSpace ([uint64]$low * 1GB) -HighMemoryMappedIoSpace ([uint64]$high * 1GB)

    Write-Ok ("GPU partition assigned (~{0:N1} GB VRAM)" -f ($gpu.TotalVRAM * $GpuShare / 1GB))
}
