#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Installs everything needed on YOUR PC to build and test MultiSeat and its drivers.

.DESCRIPTION
    - Visual Studio 2022 Community with C++ (desktop + Spectre libs), .NET desktop, Windows 11 SDK 26100
    - Windows Driver Kit (WDK) 10.0.26100 (+ its Visual Studio extension)
    - .NET 8 SDK, Git, Inno Setup, WinDbg (kernel debugger)
    - Hyper-V (for the driver test VM)
    Safe to run again. A restart is needed after Hyper-V is enabled.
#>
[CmdletBinding()]
param([switch]$SkipVisualStudio, [switch]$SkipHyperV)

$ErrorActionPreference = 'Stop'
function Step([string]$m) { Write-Host "==> $m" -ForegroundColor Cyan }
function Winget([string]$id, [string[]]$extra = @()) {
    Step "winget install $id"
    # winget.exe, not winget: this function is also called 'Winget' and would call itself.
    & winget.exe install --id $id -e --accept-package-agreements --accept-source-agreements --silent @extra
    if ($LASTEXITCODE -ne 0 -and $LASTEXITCODE -ne -1978335189) {   # -1978335189 = already installed
        Write-Warning "${id}: winget exit code $LASTEXITCODE"
    }
}

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    throw 'winget is missing. Install "App Installer" from the Microsoft Store (or on Server: https://github.com/microsoft/winget-cli/releases).'
}

if (-not $SkipVisualStudio) {
    $vsComponents = @(
        'Microsoft.VisualStudio.Workload.NativeDesktop',
        'Microsoft.VisualStudio.Workload.ManagedDesktop',
        'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
        'Microsoft.VisualStudio.Component.VC.Runtimes.x86.x64.Spectre',
        'Microsoft.VisualStudio.Component.Windows11SDK.26100',
        'Component.Microsoft.Windows.DriverKit'                    # WDK Visual Studio extension
    ) | ForEach-Object { "--add $_" }
    $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
    $vsPath = if (Test-Path $vswhere) { & $vswhere -latest -products * -property installationPath } else { $null }
    $vsComplete = $vsPath -and (& $vswhere -latest -products * -requires ($vsComponents -replace '^--add ', '') -property installationPath)
    if ($vsComplete) {
        Step 'Visual Studio already has every needed component'
    } elseif ($vsPath) {
        # winget skips packages that are already installed, so add missing components with the VS installer.
        Step "Visual Studio found at $vsPath - adding missing components"
        $setup = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\setup.exe"
        $p = Start-Process $setup -ArgumentList (@('modify', '--installPath', "`"$vsPath`"", '--passive', '--norestart', '--includeRecommended') + ($vsComponents -join ' ').Split(' ')) -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010) { Write-Warning "Visual Studio installer exit code $($p.ExitCode)" }
    } else {
        Winget 'Microsoft.VisualStudio.2022.Community' @('--override', "--passive --wait --includeRecommended $($vsComponents -join ' ')")
    }
    Winget 'Microsoft.WindowsSDK.10.0.26100'
    Winget 'Microsoft.WindowsWDK.10.0.26100'
    Write-Host '    If the WDK Visual Studio extension is missing: Visual Studio Installer -> Modify -> Individual components ->'
    Write-Host '    "Windows Driver Kit", or see https://learn.microsoft.com/windows-hardware/drivers/download-the-wdk'
}

Winget 'Microsoft.DotNet.SDK.8'
Winget 'Git.Git'
Winget 'JRSoftware.InnoSetup'
Winget 'Microsoft.WinDbg'

if (-not $SkipHyperV) {
    Step 'Hyper-V'
    $os = Get-CimInstance Win32_OperatingSystem
    if ($os.ProductType -eq 1) {
        $f = Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
        if ($f.State -ne 'Enabled') { Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All -All -NoRestart | Out-Null; Write-Host '    Enabled - restart needed' }
        else { Write-Host '    already enabled' }
    } else {
        if (-not (Get-WindowsFeature Hyper-V).Installed) { Install-WindowsFeature Hyper-V -IncludeManagementTools | Out-Null; Write-Host '    Installed - restart needed' }
        else { Write-Host '    already installed' }
    }
}

Write-Host ''
Write-Host 'Done. Restart if Hyper-V was just enabled. Next: setup\dev\New-DriverTestVM.ps1' -ForegroundColor Green
