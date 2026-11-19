#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run on the HOST after building: test-signs MultiSeatInput.sys with a self-made "MultiSeat Test" certificate.

.DESCRIPTION
    Test-signed drivers load ONLY on machines with test signing on (the test VM) and only when the
    certificate is trusted there. Deploy-ToTestVM.ps1 copies MultiSeatTest.cer into the VM and
    Install-InputDriver.ps1 trusts it. NEVER trust this certificate on a normal PC and never ship
    drivers signed with it.
#>
[CmdletBinding()]
param([string]$Configuration = 'Debug')

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sys = Join-Path $repo "out\drivers\$Configuration\MultiSeatInput.sys"
$cerFile = Join-Path $repo "out\drivers\$Configuration\MultiSeatTest.cer"
if (-not (Test-Path $sys)) { throw "$sys not found. Build the driver first." }

$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=MultiSeat Test' -and $_.HasPrivateKey } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=MultiSeat Test' -CertStoreLocation Cert:\CurrentUser\My `
        -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(3)
    Write-Host 'Created test certificate CN=MultiSeat Test (CurrentUser\My)'
}
Export-Certificate -Cert $cert -FilePath $cerFile | Out-Null

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $signtool) { throw 'signtool.exe not found (install the Windows SDK / WDK).' }

& $signtool.FullName sign /fd sha256 /sha1 $cert.Thumbprint /t http://timestamp.digicert.com $sys
if ($LASTEXITCODE -ne 0) {
    & $signtool.FullName sign /fd sha256 /sha1 $cert.Thumbprint $sys   # offline: sign without timestamp
    if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }
}
Write-Host "Signed $sys (test only). Certificate: $cerFile" -ForegroundColor Green
