#Requires -Version 5.1
<#
.SYNOPSIS
    Builds MultiSeat into .\out, and with -Installer also the all-in-one out\installer\MultiSeat-Setup.exe.

.DESCRIPTION
    Needs the .NET 8 SDK:       winget install Microsoft.DotNet.SDK.8
    For -Installer, Inno Setup: winget install JRSoftware.InnoSetup
#>
[CmdletBinding()]
param(
    [switch]$Installer,
    [switch]$SkipTests,
    [string]$Version = '0.3.0'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Exe $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}

if (-not $SkipTests) { Invoke-Native dotnet @('test', 'tests\MultiSeat.Core.Tests', '-c', 'Release') }

foreach ($p in 'MultiSeat', 'SeatAgent') {
    Invoke-Native dotnet @('publish', "src\$p\$p.csproj", '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        "-p:Version=$Version", '-o', "out\$p")
}

if ($Installer) {
    & (Join-Path $PSScriptRoot 'setup\Get-Interception.ps1')
    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup not found. Install it: winget install JRSoftware.InnoSetup' }
    Invoke-Native $iscc @("/DAppVersion=$Version", 'installer\MultiSeat.iss')
    Write-Host ''
    Write-Host "Installer: out\installer\MultiSeat-Setup.exe" -ForegroundColor Green
} else {
    Write-Host ''
    Write-Host 'Built into .\out. For the all-in-one installer: .\build.ps1 -Installer' -ForegroundColor Green
}
