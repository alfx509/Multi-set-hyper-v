#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run on the HOST: copies the freshly built driver + test tools into the test VM (C:\MultiSeatDev)
    and, with -Install, installs the driver and restarts the VM (PowerShell Direct).

.EXAMPLE
    msbuild drivers\MultiSeatInput\MultiSeatInput.vcxproj /p:Configuration=Debug /p:Platform=x64
    dotnet publish tools\InputProbe -c Release -r win-x64 --self-contained -o out\InputProbe
    dotnet publish tools\SeatProbe  -c Release -r win-x64 --self-contained -o out\SeatProbe
    .\setup\dev\Deploy-ToTestVM.ps1 -Install
#>
[CmdletBinding()]
param(
    [string]$Name = 'MultiSeatDriverTest',
    [string]$Configuration = 'Debug',
    [switch]$Install,
    [switch]$ToolsOnly,
    [pscredential]$Credential
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$files = @(
    (Join-Path $PSScriptRoot 'Enable-DriverTesting.ps1'),
    (Join-Path $PSScriptRoot 'Install-InputDriver.ps1')
)
if (-not $ToolsOnly) {
    $files += Join-Path $repo "out\drivers\$Configuration\MultiSeatInput.sys"
    foreach ($tool in 'InputProbe', 'SeatProbe') {
        $dir = Join-Path $repo "out\$tool"
        if (Test-Path $dir) { $files += Get-ChildItem $dir -File | ForEach-Object FullName }
    }
}

foreach ($f in $files) {
    if (-not (Test-Path $f)) { Write-Warning "missing: $f"; continue }
    Copy-VMFile -Name $Name -SourcePath $f -DestinationPath "C:\MultiSeatDev\$(Split-Path $f -Leaf)" -CreateFullPath -FileSource Host -Force
}
Write-Host "Copied $($files.Count) file(s) to ${Name}: C:\MultiSeatDev" -ForegroundColor Green

if ($Install) {
    if (-not $Credential) { $Credential = Get-Credential -Message "Administrator account inside $Name" }
    Invoke-Command -VMName $Name -Credential $Credential -ScriptBlock {
        & C:\MultiSeatDev\Install-InputDriver.ps1 -SysPath C:\MultiSeatDev\MultiSeatInput.sys
        Restart-Computer -Force
    }
    Write-Host 'Driver installed; the VM is restarting.' -ForegroundColor Green
}
