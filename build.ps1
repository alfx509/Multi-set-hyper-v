#Requires -Version 5.1
<#
.SYNOPSIS
    Builds SeatRouter (host) and SeatAgent (guest) into .\out and runs the unit tests.

.DESCRIPTION
    Needs the .NET 8 SDK: winget install Microsoft.DotNet.SDK.8
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.8'
}

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed ($LASTEXITCODE)" }
}

if (-not $SkipTests) {
    Invoke-Dotnet test tests\MultiSeat.Protocol.Tests -c $Configuration
}

foreach ($project in 'SeatRouter', 'SeatAgent') {
    Invoke-Dotnet publish "src\$project\$project.csproj" -c $Configuration -r win-x64 --self-contained true -o "out\$project"
}

$dll = 'third_party\Interception\library\x64\interception.dll'
if (Test-Path $dll) {
    Copy-Item -Force $dll out\SeatRouter
} else {
    Write-Host 'Note: interception.dll not found. Run scripts\host\Install-Interception.ps1 for router input mode.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Built:' -ForegroundColor Green
Write-Host "  out\SeatRouter\SeatRouter.exe   (host)"
Write-Host "  out\SeatAgent\SeatAgent.exe     (copied into each VM by Update-SeatGuest.ps1)"
