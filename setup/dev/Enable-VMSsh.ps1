#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run ONCE inside the TEST VM: turns on the built-in OpenSSH server so the host can run tests
    (ssh / scp) with a key instead of a password, and without UAC prompts on the host.

.DESCRIPTION
    - Installs/starts the Windows OpenSSH server (sshd), automatic start.
    - Trusts only the given public key for Administrators (C:\ProgramData\ssh\administrators_authorized_keys).
    - Password login over SSH is turned off; only that key works.
    - Firewall: SSH (TCP 22) allowed from the local subnet only (the Hyper-V Default Switch).
    - PowerShell is the default SSH shell.
    TEST VM ONLY. Undo: Stop-Service sshd; Set-Service sshd -StartupType Disabled.

.EXAMPLE
    .\Enable-VMSsh.ps1                      # uses multiseat_testvm.pub next to this script
    .\Enable-VMSsh.ps1 -PublicKey 'ssh-ed25519 AAAA... multiseat-testvm'
#>
[CmdletBinding()]
param([string]$PublicKey)

$ErrorActionPreference = 'Stop'
if (-not $PublicKey) {
    $file = Join-Path $PSScriptRoot 'multiseat_testvm.pub'
    if (-not (Test-Path $file)) { throw "No -PublicKey given and $file not found." }
    $PublicKey = (Get-Content $file -TotalCount 1).Trim()
}
if ($PublicKey -notmatch '^ssh-(ed25519|rsa) ') { throw 'PublicKey must be one line starting with ssh-ed25519 or ssh-rsa.' }

if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    Write-Host '  Installing OpenSSH server...'
    Add-WindowsCapability -Online -Name OpenSSH.Server~~~~0.0.1.0 | Out-Null
}

Set-Service sshd -StartupType Automatic
Start-Service sshd                                   # first start creates C:\ProgramData\ssh and host keys

$sshDir = 'C:\ProgramData\ssh'
$keys = Join-Path $sshDir 'administrators_authorized_keys'
Set-Content -Path $keys -Value $PublicKey -Encoding ascii
icacls $keys /inheritance:r /grant '*S-1-5-32-544:F' /grant '*S-1-5-18:F' | Out-Null   # Administrators, SYSTEM only

# Key login only.
$config = Join-Path $sshDir 'sshd_config'
$text = Get-Content $config -Raw
if ($text -notmatch '(?m)^PasswordAuthentication no') {
    $text = $text -replace '(?m)^#?PasswordAuthentication .*$', 'PasswordAuthentication no'
    if ($text -notmatch '(?m)^PasswordAuthentication no') { $text = "PasswordAuthentication no`r`n" + $text }
    Set-Content -Path $config -Value $text -Encoding ascii
}

New-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell -Force -PropertyType String `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" | Out-Null

Get-NetFirewallRule -Name 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -Name 'MultiSeat-SSH-In' -DisplayName 'MultiSeat test VM SSH (local subnet)' -Direction Inbound `
    -Protocol TCP -LocalPort 22 -RemoteAddress LocalSubnet -Action Allow -ErrorAction SilentlyContinue | Out-Null

Restart-Service sshd
$ip = (Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.InterfaceAlias -notlike 'Loopback*' -and $_.IPAddress -notlike '169.254*' }).IPAddress
Write-Host ''
Write-Host "SSH is on. VM address: $($ip -join ', ')" -ForegroundColor Green
Write-Host 'The host can now connect: ssh -i ~\.ssh\multiseat_testvm Administrator@<VM address>'
