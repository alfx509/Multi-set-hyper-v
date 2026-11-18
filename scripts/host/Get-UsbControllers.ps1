#Requires -Version 5.1
<#
.SYNOPSIS
    Lists PCIe USB controllers and what is plugged into them, to pick one controller per seat (usb-dda mode).

.DESCRIPTION
    For usb-dda input mode each seat needs its own USB *controller* (not just a port or a hub):
    typically one PCIe USB card per seat. Plug the seat's keyboard, mouse and headset into that card,
    run this script, and copy the LocationPath into config\seats.json -> usbControllerLocationPath.

    Never assign the controller that your host keyboard/mouse use.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

$controllers = Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -like 'PCI\*' -and ($_.Class -eq 'USB' -or $_.FriendlyName -match 'USB.*(Host|Controller)')
}

if (-not $controllers) {
    Write-Host 'No PCI USB controllers found.' -ForegroundColor Yellow
    return
}

function Get-DescendantNames([string]$InstanceId, [int]$Depth = 0) {
    if ($Depth -gt 6) { return }
    $children = (Get-PnpDeviceProperty -InstanceId $InstanceId -KeyName 'DEVPKEY_Device_Children' -ErrorAction SilentlyContinue).Data
    foreach ($child in @($children)) {
        if (-not $child) { continue }
        $dev = Get-PnpDevice -InstanceId $child -ErrorAction SilentlyContinue
        if ($dev -and $dev.Class -in @('Keyboard', 'Mouse', 'HIDClass', 'AudioEndpoint', 'MEDIA', 'XnaComposite', 'Image')) {
            "$($dev.Class): $($dev.FriendlyName)"
        }
        Get-DescendantNames $child ($Depth + 1)
    }
}

foreach ($c in $controllers) {
    $location = (Get-PnpDeviceProperty -InstanceId $c.InstanceId -KeyName 'DEVPKEY_Device_LocationPaths' -ErrorAction SilentlyContinue).Data |
        Where-Object { $_ -like 'PCIROOT*' } | Select-Object -First 1
    Write-Host ''
    Write-Host $c.FriendlyName -ForegroundColor Cyan
    Write-Host "  Status       : $($c.Status)"
    Write-Host "  InstanceId   : $($c.InstanceId)"
    Write-Host "  LocationPath : $location" -ForegroundColor Green
    $attached = @(Get-DescendantNames $c.InstanceId | Sort-Object -Unique)
    if ($attached) {
        Write-Host '  Devices      :'
        $attached | ForEach-Object { Write-Host "    - $_" }
    } else {
        Write-Host '  Devices      : (none detected)'
    }
}

Write-Host ''
Write-Host 'Tip: chipset/CPU USB controllers are usually shared with other ports and often cannot be' -ForegroundColor DarkGray
Write-Host '     passed through. Use one add-in PCIe USB card per seat.' -ForegroundColor DarkGray
