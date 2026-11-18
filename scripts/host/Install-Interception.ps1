#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Downloads and installs the Interception input driver used by SeatRouter (router input mode).

.DESCRIPTION
    Interception (https://github.com/oblitum/Interception, by Francisco Lopes) is a filter driver
    that lets one program see WHICH keyboard or mouse produced each key/click and stop it from
    reaching the host. SeatRouter needs it to give each player their own keyboard and mouse.

    License: Interception is free for non-commercial use (LGPL); commercial use needs a license
    from its author. It is downloaded from its official GitHub release, not bundled here.

    A reboot is required after installing. Uninstall with -Uninstall.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Version = 'v1.0.1',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'MultiSeat.Common.psm1') -Force

$repo = Get-MultiSeatRepoRoot
$target = Join-Path $repo 'third_party'
$extracted = Join-Path $target 'Interception'
$installer = Join-Path $extracted 'command line installer\install-interception.exe'

if (-not (Test-Path $installer)) {
    $url = "https://github.com/oblitum/Interception/releases/download/$Version/Interception.zip"
    $zip = Join-Path $target 'Interception.zip'
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Write-Step "Downloading $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $target -Force
    Remove-Item $zip
    if (-not (Test-Path $installer)) { throw "Unexpected archive layout; installer not found at $installer" }
}

$action = if ($Uninstall) { '/uninstall' } else { '/install' }
if ($PSCmdlet.ShouldProcess('Interception driver', $action)) {
    & $installer $action
    if ($LASTEXITCODE -ne 0) { throw "install-interception.exe $action failed ($LASTEXITCODE)." }
}

if (-not $Uninstall) {
    $dll = Join-Path $extracted 'library\x64\interception.dll'
    $routerOut = Join-Path $repo 'out\SeatRouter'
    if (Test-Path $routerOut) {
        Copy-Item -Force $dll $routerOut
        Write-Ok "interception.dll copied to $routerOut"
    }
    Write-Ok 'Driver installed. REBOOT before starting SeatRouter.'
} else {
    Write-Ok 'Driver removed. Reboot to finish.'
}
