#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Run INSIDE the test VM: asks the MultiSeat Local provider to connect one seat (Phase 2, B1),
    then shows the sessions and the provider log.
#>
[CmdletBinding()]
param([int]$WaitSeconds = 15)

$log = 'C:\ProgramData\MultiSeat\protocol.log'
try {
    $event = [System.Threading.EventWaitHandle]::OpenExisting('Global\MultiSeatLocalConnect')
} catch {
    throw "The provider is not listening (event Global\MultiSeatLocalConnect missing). Check $log"
}

$before = (Get-Content $log -ErrorAction SilentlyContinue | Measure-Object -Line).Lines
[void]$event.Set()
$event.Dispose()
Write-Host "Seat connect requested. Waiting $WaitSeconds s..." -ForegroundColor Cyan
Start-Sleep -Seconds $WaitSeconds

Write-Host ''
Write-Host 'Sessions:' -ForegroundColor Cyan
qwinsta
Write-Host ''
Write-Host 'Provider log (new lines):' -ForegroundColor Cyan
Get-Content $log -ErrorAction SilentlyContinue | Select-Object -Skip $before
