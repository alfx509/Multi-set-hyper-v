#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Deletes a seat VM, returns any passed-through USB controller to the host, optionally deletes its disk.

.EXAMPLE
    .\Remove-GamingSeat.ps1 -SeatName Seat2
    .\Remove-GamingSeat.ps1 -SeatName Seat2 -DeleteFiles
#>
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)][string]$SeatName,
    [string]$ConfigPath,
    [switch]$DeleteFiles
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-HyperV

$config = Import-MultiSeatConfig -Path $ConfigPath
$seat = Get-Seat $config $SeatName
$vmName = Get-SeatVmName $seat
$paths = Get-SeatPaths $config $seat

$vm = Get-VM -Name $vmName -ErrorAction SilentlyContinue
if ($vm -and $PSCmdlet.ShouldProcess($vmName, 'Remove VM')) {
    if ($vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Force }

    foreach ($dev in @(Get-VMAssignableDevice -VMName $vmName -ErrorAction SilentlyContinue)) {
        Remove-VMAssignableDevice -VMName $vmName -LocationPath $dev.LocationPath
        Mount-VMHostAssignableDevice -LocationPath $dev.LocationPath
        Write-Ok "Returned $($dev.LocationPath) to the host"
    }

    Remove-VM -VM $vm -Force
    Write-Ok "$vmName removed"
}

if ($DeleteFiles -and (Test-Path $paths.Directory) -and $PSCmdlet.ShouldProcess($paths.Directory, 'Delete seat disk and secret')) {
    Remove-Item -Recurse -Force $paths.Directory
    Write-Ok "Deleted $($paths.Directory)"
}
