#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM: allows test-signed drivers and kernel debugging over the network.

.DESCRIPTION
    - bcdedit testsigning on (shows "Test Mode" on the desktop - expected)
    - network kernel debugging to the host PC (WinDbg: File → Attach to kernel → Net, port + key below)
    - full memory dumps on blue screens (C:\Windows\MEMORY.DMP) for analysis
    Restart the VM afterwards. NEVER run this on a normal PC.

.EXAMPLE
    .\Enable-DriverTesting.ps1 -HostIp 172.20.48.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$HostIp,
    [int]$Port = 50000,
    [string]$Key = '1.2.3.4'
)

$ErrorActionPreference = 'Stop'
bcdedit /set testsigning on
bcdedit /debug on
bcdedit /dbgsettings net hostip:$HostIp port:$Port key:$Key
Set-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\CrashControl' -Name CrashDumpEnabled -Value 1
New-Item -ItemType Directory -Force -Path C:\MultiSeatDev | Out-Null
Write-Host ''
Write-Host "Done. Restart the VM. On the host: WinDbg → Attach to kernel → Net → port $Port, key $Key" -ForegroundColor Green
