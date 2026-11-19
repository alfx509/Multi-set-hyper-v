#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM (test signing on): installs or removes the MultiSeatInput keyboard/mouse filter.

.DESCRIPTION
    Copies MultiSeatInput.sys to System32\drivers, creates the kernel service, and adds "MultiSeatInput"
    to the Keyboard and Mouse class UpperFilters BEFORE kbdclass/mouclass (so it sits below them).
    Restart afterwards. Then: InputProbe.exe list / watch / capture <id>.

    RECOVERY: if keyboard and mouse stop working in the VM, from the HOST run
      Invoke-Command -VMName MultiSeatDriverTest -Credential (Get-Credential) -ScriptBlock { C:\MultiSeatDev\Install-InputDriver.ps1 -Uninstall; Restart-Computer -Force }
    or restore the "clean" checkpoint.
#>
[CmdletBinding()]
param(
    [string]$SysPath = (Join-Path $PSScriptRoot 'MultiSeatInput.sys'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$service = 'MultiSeatInput'
$classes = @{
    '{4D36E96B-E325-11CE-BFC1-08002BE10318}' = 'kbdclass'   # Keyboard
    '{4D36E96F-E325-11CE-BFC1-08002BE10318}' = 'mouclass'   # Mouse
}

foreach ($guid in $classes.Keys) {
    $key = "HKLM:\SYSTEM\CurrentControlSet\Control\Class\$guid"
    $filters = @((Get-ItemProperty -Path $key -Name UpperFilters -ErrorAction SilentlyContinue).UpperFilters) | Where-Object { $_ -and $_ -ne $service }
    if (-not $Uninstall) {
        $classDriver = $classes[$guid]
        if (@($filters) -notcontains $classDriver) { throw "$classDriver is not in UpperFilters of $guid - refusing to continue." }
        # Insert just before the class driver, so the filter sits BELOW kbdclass/mouclass.
        $ordered = New-Object System.Collections.Generic.List[string]
        foreach ($f in $filters) {
            if ($f -eq $classDriver) { $ordered.Add($service) }
            $ordered.Add($f)
        }
        $filters = $ordered.ToArray()
    }
    Set-ItemProperty -Path $key -Name UpperFilters -Value ([string[]]$filters) -Type MultiString
    Write-Host "  $guid UpperFilters = $($filters -join ', ')"
}

$target = Join-Path $env:SystemRoot "System32\drivers\$service.sys"
if ($Uninstall) {
    sc.exe delete $service | Out-Null
    Write-Host 'Removed from the filter lists. Restart, then delete' $target -ForegroundColor Green
    return
}

if (-not (Test-Path $SysPath)) { throw "$SysPath not found." }

# Trust the self-made test certificate (made by Sign-TestDriver.ps1) - TEST VM ONLY.
$cer = Join-Path (Split-Path $SysPath) 'MultiSeatTest.cer'
if (Test-Path $cer) {
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
    Write-Host '  Trusted the MultiSeat Test certificate (test VM only).'
}
Copy-Item -Force $SysPath $target
if (-not (Get-Service $service -ErrorAction SilentlyContinue)) {
    sc.exe create $service type= kernel start= demand error= normal binPath= "System32\drivers\$service.sys" DisplayName= "MultiSeat input filter" | Out-Null
}
Write-Host 'Installed. Restart the VM, then run: InputProbe.exe list' -ForegroundColor Green
