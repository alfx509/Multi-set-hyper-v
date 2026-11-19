#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM: installs Microsoft's IndirectDisplay sample driver (Phase 2, step A).

.DESCRIPTION
    Trusts the WDK test certificate (TEST VM ONLY), adds the driver package, then starts
    IddSampleApp.exe, which creates the virtual monitor while it runs (press x in it to remove it).
    -Uninstall removes the driver package again.

.EXAMPLE
    C:\MultiSeatDev\idd\Install-IddSample.ps1
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

if ($Uninstall) {
    $pkg = pnputil /enum-drivers | Out-String
    $oem = [regex]::Matches($pkg, 'Published Name:\s+(oem\d+\.inf)\s+Original Name:\s+iddsampledriver\.inf', 'IgnoreCase') |
        ForEach-Object { $_.Groups[1].Value }
    foreach ($o in $oem) { pnputil /delete-driver $o /uninstall /force }
    Write-Host 'IddSample driver removed.' -ForegroundColor Green
    return
}

$cer = Join-Path $dir 'IddSampleDriver.cer'
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Write-Host '  Trusted the WDK test certificate (test VM only).'

pnputil /add-driver (Join-Path $dir 'IddSampleDriver.inf') /install
if ($LASTEXITCODE -notin 0, 3010) { throw "pnputil failed ($LASTEXITCODE)" }

Write-Host ''
Write-Host 'Starting IddSampleApp.exe. Keep its window open: the virtual monitor exists while it runs.' -ForegroundColor Green
Write-Host 'Then open Settings > System > Display. Press x in the app window to remove the monitor.'
Start-Process (Join-Path $dir 'IddSampleApp.exe')
