#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    For developers: install MultiSeat from a local build (.\out) without the setup program.

.DESCRIPTION
    Most people should just run MultiSeat-Setup.exe. This script does the same from source:
    copies .\out to C:\Program Files\MultiSeat, adds the keyboard/mouse driver files, creates
    shortcuts and runs Configure-MultiSeat.ps1.

.EXAMPLE
    .\build.ps1
    .\setup\Install-MultiSeat.ps1
    .\setup\Install-MultiSeat.ps1 -InstallSessionHost     # Windows Server, 4-8 players
    .\setup\Install-MultiSeat.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [switch]$InstallSessionHost,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'out'
$appDir = Join-Path $env:ProgramFiles 'MultiSeat'
$configure = Join-Path $PSScriptRoot 'Configure-MultiSeat.ps1'
$shortcuts = @(
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'MultiSeat.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'MultiSeat.lnk')
)

if ($Uninstall) {
    & $configure -AppDir $appDir -Uninstall
    Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue
    $shortcuts | ForEach-Object { Remove-Item -Force $_ -ErrorAction SilentlyContinue }
    return
}

foreach ($p in 'MultiSeat\MultiSeat.exe', 'SeatAgent\SeatAgent.exe') {
    if (-not (Test-Path (Join-Path $out $p))) { throw "$out\$p not found. Run .\build.ps1 first." }
}

& (Join-Path $PSScriptRoot 'Get-Interception.ps1')

Get-Process MultiSeat, SeatAgent -ErrorAction SilentlyContinue | Stop-Process -Force
New-Item -ItemType Directory -Force -Path $appDir, "$appDir\Agent", "$appDir\interception" | Out-Null
Copy-Item -Recurse -Force (Join-Path $out 'MultiSeat\*') $appDir
Copy-Item -Recurse -Force (Join-Path $out 'SeatAgent\*') "$appDir\Agent"
Copy-Item -Recurse -Force (Join-Path $repo 'third_party\Interception\*') "$appDir\interception"

$shell = New-Object -ComObject WScript.Shell
foreach ($path in $shortcuts) {
    $lnk = $shell.CreateShortcut($path)
    $lnk.TargetPath = Join-Path $appDir 'MultiSeat.exe'
    $lnk.WorkingDirectory = $appDir
    $lnk.Save()
}

$configureArgs = @{ AppDir = $appDir }
if ($InstallSessionHost) { $configureArgs.InstallSessionHost = $true }
& $configure @configureArgs
if ($LASTEXITCODE -ne 0) { throw 'Setup failed. See C:\ProgramData\MultiSeat\setup.log' }
Write-Host 'Installed. RESTART the PC, then open MultiSeat from the desktop.' -ForegroundColor Green
