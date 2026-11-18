#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Passes a whole PCIe USB controller to a seat VM with Discrete Device Assignment (usb-dda mode).

.DESCRIPTION
    Everything plugged into that controller (keyboard, mouse, USB headset, gamepad) then belongs
    to the VM only - true hardware isolation, including the headset microphone.

    Requirements: Windows Server 2016 or newer, IOMMU (VT-d / AMD-Vi) enabled, a controller that
    supports DDA (most add-in PCIe USB 3 cards). The VM must be off.

.EXAMPLE
    .\Add-SeatUsbController.ps1 -SeatName Seat1
    .\Add-SeatUsbController.ps1 -SeatName Seat1 -Remove
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string]$SeatName,
    [string]$LocationPath,
    [string]$ConfigPath,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-HyperV

if (-not (Get-Command Dismount-VMHostAssignableDevice -ErrorAction SilentlyContinue)) {
    throw 'Discrete Device Assignment is not available. usb-dda needs Windows Server; use input "router" or "usbip" instead.'
}

$config = Import-MultiSeatConfig -Path $ConfigPath
$seat = Get-Seat $config $SeatName
$vmName = Get-SeatVmName $seat
if (-not $LocationPath) { $LocationPath = Get-ConfigValue $seat 'usbControllerLocationPath' }
if (-not $LocationPath) { throw "No LocationPath given and $SeatName has no usbControllerLocationPath." }

$vm = Get-VM -Name $vmName
if ($vm.State -ne 'Off') { throw "$vmName must be turned off (it is $($vm.State))." }

$device = Get-PnpDevice | Where-Object {
    (Get-PnpDeviceProperty -InstanceId $_.InstanceId -KeyName 'DEVPKEY_Device_LocationPaths' -ErrorAction SilentlyContinue).Data -contains $LocationPath
} | Select-Object -First 1

if ($Remove) {
    Write-Step "Returning USB controller $LocationPath from $vmName to the host"
    if ($PSCmdlet.ShouldProcess($vmName, 'Remove assigned USB controller')) {
        Remove-VMAssignableDevice -VMName $vmName -LocationPath $LocationPath
        Mount-VMHostAssignableDevice -LocationPath $LocationPath
        if ($device) { Enable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false }
        Write-Ok 'Controller is back on the host'
    }
    return
}

Write-Step "Assigning USB controller to $vmName"
if ($device) { Write-Ok "Device: $($device.FriendlyName)" }
Write-Note 'Everything plugged into this controller will disappear from the host.'

if ($PSCmdlet.ShouldProcess($LocationPath, "Dismount from host and assign to $vmName")) {
    Set-VM -VMName $vmName -AutomaticStopAction TurnOff
    if ($device) { Disable-PnpDevice -InstanceId $device.InstanceId -Confirm:$false }
    Dismount-VMHostAssignableDevice -LocationPath $LocationPath -Force
    Add-VMAssignableDevice -VMName $vmName -LocationPath $LocationPath
    Write-Ok 'USB controller assigned. Plug the seat keyboard, mouse and headset into this card.'
}
