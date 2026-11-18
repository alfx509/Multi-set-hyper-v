#Requires -Version 5.1
<#
    Shared helpers for the Multi-Seat Hyper-V scripts.
    Compatible with Windows PowerShell 5.1 (which ships the Hyper-V module) and PowerShell 7.
#>

Set-StrictMode -Version Latest

$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Get-MultiSeatRepoRoot { $script:RepoRoot }

function Write-Step([string]$Message) { Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok([string]$Message) { Write-Host "    [ OK ] $Message" -ForegroundColor Green }
function Write-Note([string]$Message) { Write-Host "    [WARN] $Message" -ForegroundColor Yellow }
function Write-Bad([string]$Message) { Write-Host "    [FAIL] $Message" -ForegroundColor Red }

function Assert-Administrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script from an elevated PowerShell (Run as Administrator).'
    }
}

function Assert-HyperV {
    if (-not (Get-Command Get-VM -ErrorAction SilentlyContinue)) {
        throw 'The Hyper-V PowerShell module is missing. Enable Hyper-V: Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All'
    }
}

function Get-ConfigValue {
    <# Returns $Object.$Name if present and not empty, else $Default. #>
    param($Object, [string]$Name, $Default = $null)
    if ($null -eq $Object) { return $Default }
    $prop = $Object.PSObject.Properties[$Name]
    if ($null -eq $prop -or $null -eq $prop.Value -or ($prop.Value -is [string] -and $prop.Value -eq '')) {
        return $Default
    }
    return $prop.Value
}

function Import-MultiSeatConfig {
    param([string]$Path)

    if (-not $Path) { $Path = Join-Path $script:RepoRoot 'config\seats.json' }
    if (-not (Test-Path $Path)) {
        throw "Config not found: $Path. Copy config\seats.example.json to config\seats.json and edit it."
    }

    $config = Get-Content -Raw -Path $Path | ConvertFrom-Json
    $config | Add-Member -NotePropertyName '__path' -NotePropertyValue (Resolve-Path $Path).Path -Force

    if (-not (Get-ConfigValue $config 'vmRoot')) { throw 'Config: vmRoot is required.' }
    if (-not (Get-ConfigValue $config 'seats')) { throw 'Config: at least one seat is required.' }

    $names = @{}
    $totalShare = 0.0
    foreach ($seat in $config.seats) {
        $name = Get-ConfigValue $seat 'name'
        if (-not $name -or $name -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]{0,30}$') {
            throw "Config: invalid seat name '$name' (letters, digits, - and _ only)."
        }
        if ($names.ContainsKey($name)) { throw "Config: duplicate seat name '$name'." }
        $names[$name] = $true

        $share = [double](Get-ConfigValue $seat 'gpuShare' 0)
        if ($share -le 0 -or $share -gt 1) { throw "Config: seat '$name' gpuShare must be > 0 and <= 1." }
        $totalShare += $share

        $mode = Get-ConfigValue $seat 'input' 'router'
        if (@('router', 'usb-dda', 'usbip', 'viewer') -notcontains $mode) {
            throw "Config: seat '$name' input must be router, usb-dda, usbip or viewer (got '$mode')."
        }
        if ($mode -eq 'usb-dda' -and -not (Get-ConfigValue $seat 'usbControllerLocationPath')) {
            throw "Config: seat '$name' uses usb-dda but has no usbControllerLocationPath (see Get-UsbControllers.ps1)."
        }
    }
    if ($totalShare -gt 1.0001) {
        Write-Note ("GPU shares add up to {0:P0}. The GPU will be oversubscribed." -f $totalShare)
    }

    return $config
}

function Get-Seat {
    param($Config, [string]$Name)
    $seat = $Config.seats | Where-Object { $_.name -eq $Name }
    if (-not $seat) { throw "Seat '$Name' is not in $($Config.__path)." }
    return $seat
}

function Get-SeatSetting {
    <# Seat value, falling back to config.defaults, then to $Default. #>
    param($Config, $Seat, [string]$Name, $Default = $null)
    $value = Get-ConfigValue $Seat $Name
    if ($null -ne $value) { return $value }
    return Get-ConfigValue (Get-ConfigValue $Config 'defaults') $Name $Default
}

function Get-SeatVmName {
    param($Seat)
    return "MultiSeat-$($Seat.name)"
}

function Get-SeatPaths {
    param($Config, $Seat)
    $dir = Join-Path $Config.vmRoot $Seat.name
    [pscustomobject]@{
        Directory  = $dir
        Vhd        = Join-Path $dir "$($Seat.name).vhdx"
        SecretFile = Join-Path $dir 'agent.secret'
    }
}

function Get-SeatSecret {
    <# Per-seat 32-byte random key shared by SeatRouter (host) and SeatAgent (guest). Created on first use. #>
    param($Config, $Seat)
    $paths = Get-SeatPaths $Config $Seat
    if (Test-Path $paths.SecretFile) {
        return (Get-Content -Raw $paths.SecretFile).Trim()
    }
    New-Item -ItemType Directory -Force -Path $paths.Directory | Out-Null
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $secret = [Convert]::ToBase64String($bytes)
    Set-Content -Path $paths.SecretFile -Value $secret -NoNewline -Encoding ASCII

    # Only Administrators and SYSTEM may read the key.
    $acl = Get-Acl $paths.SecretFile
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($id in @('BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM')) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($id, 'FullControl', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -Path $paths.SecretFile -AclObject $acl
    return $secret
}

function Get-HostPartitionableGpus {
    <# Windows 11 / Server 2022+: Get-VMHostPartitionableGpu. Older builds: Get-VMPartitionableGpu. #>
    if (Get-Command Get-VMHostPartitionableGpu -ErrorAction SilentlyContinue) {
        return @(Get-VMHostPartitionableGpu)
    }
    if (Get-Command Get-VMPartitionableGpu -ErrorAction SilentlyContinue) {
        return @(Get-VMPartitionableGpu)
    }
    return @()
}

function Get-MultiSeatGpu {
    param($Config)
    $gpus = Get-HostPartitionableGpus
    if ($gpus.Count -eq 0) {
        throw 'No partitionable GPU found. Update the GPU driver (WDDM 2.5+) and make sure Hyper-V is enabled.'
    }
    $wanted = Get-ConfigValue (Get-ConfigValue $Config 'gpu') 'instancePath'
    if ($wanted) {
        $gpu = $gpus | Where-Object { $_.Name -eq $wanted }
        if (-not $gpu) { throw "GPU '$wanted' is not partitionable on this host." }
        return $gpu
    }
    return $gpus[0]
}

function ConvertTo-PnpInstanceId {
    <# '\\?\PCI#VEN_10DE&DEV_2204&...#4&1234&0&0008#{guid}' -> 'PCI\VEN_10DE&DEV_2204&...\4&1234&0&0008' #>
    param([string]$GpuName)
    $id = $GpuName -replace '^\\\\\?\\', ''
    $id = ($id -split '#\{')[0]
    return ($id -replace '#', '\')
}

function Get-GpuFriendlyName {
    param([string]$GpuName)
    $instanceId = ConvertTo-PnpInstanceId $GpuName
    $device = Get-PnpDevice -InstanceId $instanceId -ErrorAction SilentlyContinue
    if ($device) { return $device.FriendlyName }
    return $instanceId
}

function Get-VmIPv4 {
    <# Reads the guest's IPv4 address through Hyper-V integration services (KVP). #>
    param([string]$VMName, [int]$TimeoutSeconds = 180)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $ips = @(Get-VMNetworkAdapter -VMName $VMName | ForEach-Object { $_.IPAddresses }) |
            Where-Object { $_ -match '^\d{1,3}(\.\d{1,3}){3}$' -and $_ -notlike '169.254.*' }
        if ($ips) { return $ips[0] }
        Start-Sleep -Seconds 2
    } while ((Get-Date) -lt $deadline)
    return $null
}

function Mount-SeatVhdWindows {
    <#
        Mounts a seat VHDX on the host and returns the drive root that holds \Windows
        (e.g. 'F:\'). Call Dismount-VHD -Path <vhd> when done.
    #>
    param([string]$VhdPath)
    $disk = Mount-VHD -Path $VhdPath -Passthru | Get-Disk
    Start-Sleep -Seconds 2
    foreach ($part in (Get-Partition -DiskNumber $disk.Number | Sort-Object Size -Descending)) {
        if ($part.Type -in @('Reserved', 'Recovery', 'System')) { continue }
        if (-not $part.DriveLetter -or $part.DriveLetter -eq [char]0) {
            try {
                $part | Add-PartitionAccessPath -AssignDriveLetter -ErrorAction Stop
            } catch {
                continue
            }
            $part = Get-Partition -DiskNumber $disk.Number -PartitionNumber $part.PartitionNumber
        }
        $root = "$($part.DriveLetter):\"
        if (Test-Path (Join-Path $root 'Windows\System32')) { return $root }
    }
    Dismount-VHD -Path $VhdPath
    throw "No Windows installation found inside $VhdPath. Install Windows in the VM first."
}

Export-ModuleMember -Function *
