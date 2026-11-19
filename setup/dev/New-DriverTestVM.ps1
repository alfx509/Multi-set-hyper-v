#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Creates the Hyper-V VM where MultiSeat drivers are tested (never test new drivers on your real Windows).

.DESCRIPTION
    - Generation 2, Secure Boot OFF (test-signed drivers need it off), checkpoints ON
    - Guest services on (Deploy-ToTestVM.ps1 copies files in with Copy-VMFile)
    - Boots the Windows ISO you give (Windows Server 2025 evaluation or Windows 11)

    After Windows is installed in the VM:
      1. Inside the VM, as Administrator:  C:\...\Enable-DriverTesting.ps1 -HostIp <this PC's IP>
         (or copy it in with Deploy-ToTestVM.ps1 -ToolsOnly)
      2. On this PC:  Checkpoint-VM -Name MultiSeatDriverTest -SnapshotName clean
         → after any blue screen:  Restore-VMCheckpoint -VMName MultiSeatDriverTest -Name clean -Confirm:$false

.EXAMPLE
    .\New-DriverTestVM.ps1 -IsoPath D:\ISO\WindowsServer2025_eval.iso
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$IsoPath,
    [string]$Name = 'MultiSeatDriverTest',
    [string]$Path = "$env:PUBLIC\Documents\Hyper-V\MultiSeat",
    [int]$MemoryGB = 6,
    [int]$CpuCount = 4,
    [int]$DiskGB = 80,
    [string]$SwitchName = 'Default Switch'
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $IsoPath)) { throw "ISO not found: $IsoPath" }
if (Get-VM -Name $Name -ErrorAction SilentlyContinue) { throw "VM $Name already exists." }

$vhd = Join-Path $Path "$Name.vhdx"
New-Item -ItemType Directory -Force -Path $Path | Out-Null
New-VHD -Path $vhd -SizeBytes ([uint64]$DiskGB * 1GB) -Dynamic | Out-Null
New-VM -Name $Name -Generation 2 -MemoryStartupBytes ([uint64]$MemoryGB * 1GB) -VHDPath $vhd -SwitchName $SwitchName -Path $Path | Out-Null
Set-VMProcessor -VMName $Name -Count $CpuCount
Set-VMMemory -VMName $Name -DynamicMemoryEnabled $false
Set-VMFirmware -VMName $Name -EnableSecureBoot Off
Set-VM -VMName $Name -CheckpointType Standard -AutomaticCheckpointsEnabled $false
Get-VMIntegrationService -VMName $Name | Where-Object { -not $_.Enabled } | Enable-VMIntegrationService
Add-VMDvdDrive -VMName $Name -Path $IsoPath
Set-VMFirmware -VMName $Name -FirstBootDevice (Get-VMDvdDrive -VMName $Name)

Write-Host "VM '$Name' created. Start it, install Windows, then follow the steps in this script's help:" -ForegroundColor Green
Write-Host "  Get-Help $PSCommandPath -Full"
Start-VM -Name $Name
vmconnect.exe localhost $Name
