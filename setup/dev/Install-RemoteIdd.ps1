#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM: adds the MultiSeatRemoteIdd driver package (Phase 2, B2).

.DESCRIPTION
    - Trusts the WDK test certificate the package is signed with (TEST VM ONLY).
    - Lets the driver host (LOCAL SERVICE) write C:\ProgramData\MultiSeat\idd.log.
    - Adds the package to the driver store. No device exists yet: Remote Desktop Services creates one
      (hardware id MultiSeatRemoteIdd) for each MultiSeat Local seat session.
    -Uninstall removes the package.
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

$published = [regex]::Matches((pnputil /enum-drivers | Out-String),
    'Published Name:\s+(oem\d+\.inf)\s+Original Name:\s+multiseatremoteidd\.inf', 'IgnoreCase') | ForEach-Object { $_.Groups[1].Value }
foreach ($oem in $published) { pnputil /delete-driver $oem /uninstall /force | Out-Null; Write-Host "  removed $oem" }
if ($Uninstall) { Write-Host 'MultiSeatRemoteIdd removed.' -ForegroundColor Green; return }

foreach ($store in 'Root', 'TrustedPublisher') {
    certutil -addstore -f $store (Join-Path $dir 'MultiSeatRemoteIdd.cer') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "certutil -addstore $store failed ($LASTEXITCODE)" }
}

New-Item -ItemType Directory -Force -Path C:\ProgramData\MultiSeat | Out-Null
icacls C:\ProgramData\MultiSeat /grant '*S-1-5-19:(OI)(CI)M' | Out-Null     # LOCAL SERVICE (UMDF driver host)

pnputil /add-driver (Join-Path $dir 'MultiSeatRemoteIdd.inf')
if ($LASTEXITCODE -notin 0, 3010) { throw "pnputil failed ($LASTEXITCODE)" }
Write-Host 'MultiSeatRemoteIdd package added. Next: Connect-LocalSeat.ps1' -ForegroundColor Green
