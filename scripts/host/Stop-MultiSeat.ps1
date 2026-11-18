#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Ends a multi-seat session: closes viewers, stops SeatRouter, shuts down the seat VMs.

.EXAMPLE
    .\Stop-MultiSeat.ps1            # graceful guest shutdown
    .\Stop-MultiSeat.ps1 -KeepVMs   # only close viewers and router
    .\Stop-MultiSeat.ps1 -Force     # power off immediately
#>
[CmdletBinding()]
param(
    [string]$ConfigPath,
    [switch]$KeepVMs,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force

$config = Import-MultiSeatConfig -Path $ConfigPath
$runtime = Join-Path (Get-MultiSeatRepoRoot) 'runtime'

Write-Step 'Stopping SeatRouter (all keyboards and mice return to the host)'
Get-Process SeatRouter -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Step 'Closing viewers'
$viewerPath = Get-ConfigValue (Get-ConfigValue $config 'viewer') 'path'
$viewerName = if ($viewerPath) { [IO.Path]::GetFileNameWithoutExtension($viewerPath) } else { $null }
if ($viewerName) {
    Get-Process -Name $viewerName -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and ($_.Path -like "$runtime\viewers\*" -or $_.Path -eq $viewerPath)
    } | Stop-Process -Force
}

if ($KeepVMs) { return }

Assert-HyperV
Write-Step 'Shutting down seat VMs'
foreach ($seat in $config.seats) {
    $vm = Get-VM -Name (Get-SeatVmName $seat) -ErrorAction SilentlyContinue
    if ($vm -and $vm.State -eq 'Running') {
        if ($Force) { Stop-VM -VM $vm -TurnOff -Force } else { Stop-VM -VM $vm -Force }
        Write-Ok "$($vm.Name) stopped"
    }
}
