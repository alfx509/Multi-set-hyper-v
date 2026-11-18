#Requires -Version 5.1
<#
.SYNOPSIS
    Builds MultiSeat into .\out. Needs the .NET 8 SDK:  winget install Microsoft.DotNet.SDK.8
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

function Invoke-Dotnet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($args -join ' ') failed" }
}

if (-not $SkipTests) { Invoke-Dotnet test tests\MultiSeat.Core.Tests -c Release }

foreach ($p in 'MultiSeat', 'SeatAgent') {
    Invoke-Dotnet publish "src\$p\$p.csproj" -c Release -r win-x64 --self-contained true -o "out\$p"
}

Write-Host ''
Write-Host 'Built into .\out. Next (as Administrator):  .\setup\Install-MultiSeat.ps1' -ForegroundColor Green
