#Requires -Version 5.1
<#
.SYNOPSIS
    Downloads the Interception keyboard/mouse driver into third_party\Interception (used by the build).

.DESCRIPTION
    Interception by Francisco Lopes: https://github.com/oblitum/Interception
    Free for non-commercial use (LGPL); commercial use needs a license from its author.
#>
[CmdletBinding()]
param([string]$Version = 'v1.0.1')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$target = Join-Path $repo 'third_party'
$installer = Join-Path $target 'Interception\command line installer\install-interception.exe'
if (Test-Path $installer) { return }

New-Item -ItemType Directory -Force -Path $target | Out-Null
$zip = Join-Path $target 'Interception.zip'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Invoke-WebRequest -UseBasicParsing -OutFile $zip -Uri "https://github.com/oblitum/Interception/releases/download/$Version/Interception.zip"
Expand-Archive -Force -Path $zip -DestinationPath $target
Remove-Item $zip
if (-not (Test-Path $installer)) { throw "Unexpected Interception archive layout: $installer not found." }
Write-Host "Interception $Version downloaded to $target\Interception"
