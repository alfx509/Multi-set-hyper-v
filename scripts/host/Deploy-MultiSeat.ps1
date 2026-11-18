#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates every seat in config\seats.json: VMs, GPU partitions, USB controllers, guest files.

.DESCRIPTION
    Safe to run again: seats that already exist are skipped. Typical first run:

        .\build.ps1                                # builds SeatRouter / SeatAgent
        .\scripts\host\Test-HostReadiness.ps1
        .\scripts\host\Deploy-MultiSeat.ps1
        # in each VM, once:  C:\MultiSeat\Initialize-SeatGuest.ps1
        .\scripts\host\Start-MultiSeat.ps1

.EXAMPLE
    .\Deploy-MultiSeat.ps1
#>
[CmdletBinding()]
param(
    [string]$ConfigPath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force
Assert-Administrator
Assert-HyperV

$config = Import-MultiSeatConfig -Path $ConfigPath

Write-Step 'Checking host'
& (Join-Path $PSScriptRoot 'Test-HostReadiness.ps1') -ConfigPath $config.__path
if ($LASTEXITCODE -gt 0) {
    $answer = Read-Host 'Problems were found. Continue anyway? (y/N)'
    if ($answer -notmatch '^[yY]') { return }
}

foreach ($seat in $config.seats) {
    Write-Host ''
    & (Join-Path $PSScriptRoot 'New-GamingSeat.ps1') -SeatName $seat.name -ConfigPath $config.__path

    if ((Get-ConfigValue $seat 'input' 'router') -eq 'usb-dda') {
        $vmName = Get-SeatVmName $seat
        $location = $seat.usbControllerLocationPath
        $assigned = Get-VMAssignableDevice -VMName $vmName -ErrorAction SilentlyContinue |
            Where-Object { $_.LocationPath -eq $location }
        if (-not $assigned) {
            & (Join-Path $PSScriptRoot 'Add-SeatUsbController.ps1') -SeatName $seat.name -ConfigPath $config.__path
        }
    }
}

Write-Host ''
Write-Step 'Seats'
$config.seats | ForEach-Object {
    $vm = Get-VM -Name (Get-SeatVmName $_) -ErrorAction SilentlyContinue
    [pscustomobject]@{
        Seat     = $_.name
        VM       = if ($vm) { $vm.Name } else { '(missing)' }
        State    = if ($vm) { $vm.State } else { '' }
        GpuShare = '{0:P0}' -f [double]$_.gpuShare
        Input    = Get-ConfigValue $_ 'input' 'router'
        Monitor  = Get-ConfigValue $_ 'monitor' ''
    }
} | Format-Table -AutoSize

Write-Host 'Next steps:' -ForegroundColor Green
Write-Host '  1. Start each VM, log in, and run as Administrator:'
Write-Host '       powershell -ExecutionPolicy Bypass -File C:\MultiSeat\Initialize-SeatGuest.ps1 -InstallSunshine'
Write-Host '  2. Pair Moonlight with each seat once (PIN shown by Moonlight, entered in Sunshine web UI).'
Write-Host '  3. .\Start-MultiSeat.ps1'
