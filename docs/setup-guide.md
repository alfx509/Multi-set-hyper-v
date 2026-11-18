# Setup guide

This walks through a 2-player setup. More players = more entries in `seats.json`.
All host commands run in **PowerShell as Administrator** from the repository folder.

## 1. Prepare the host

1. BIOS/UEFI: enable **Intel VT-x + VT-d** or **AMD SVM + IOMMU**. Enable **Above 4G decoding** and
   **Resizable BAR** if available.
2. Install the latest GPU driver from NVIDIA/AMD/Intel.
3. Enable Hyper-V and reboot:
   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All
   ```
4. Install the .NET 8 SDK and build:
   ```powershell
   winget install Microsoft.DotNet.SDK.8
   .\build.ps1
   ```
5. For `router` input mode, install the Interception driver and **reboot**:
   ```powershell
   .\scripts\host\Install-Interception.ps1
   ```
6. Install [Moonlight](https://moonlight-stream.org) on the host. Optional: download NirSoft
   [SoundVolumeView](https://www.nirsoft.net/utils/sound_volume_view.html) for automatic headset routing.

## 2. Make a base Windows image (recommended)

Installing Windows and Steam once and cloning it saves hours and disk space:

1. In Hyper-V Manager create a temporary Gen 2 VM with a 256 GB VHDX, install Windows 11.
2. Inside it: Windows Update, install Steam / Epic / Battle.net (do not sign in yet) and the
   [virtual display driver](https://github.com/VirtualDrivers/Virtual-Display-Driver).
3. Optional but recommended: install common games here, so every seat starts with them.
4. Generalize so every seat gets a unique identity:
   `C:\Windows\System32\Sysprep\sysprep.exe /generalize /oobe /shutdown`
5. Delete the temporary VM (keep the VHDX!) and set `baseVhd` in `seats.json` to that VHDX path.
   Treat it as **read-only** from now on – seats are differencing disks on top of it.

Without `baseVhd`, set `installIso` instead; each seat then gets an empty disk and boots the installer.

## 3. Describe your seats

```powershell
Copy-Item config\seats.example.json config\seats.json
notepad config\seats.json
```

Key fields per seat:

| Field | Example | Meaning |
|---|---|---|
| `name` | `Seat1` | VM is called `MultiSeat-Seat1` |
| `gpuShare` | `0.5` | Fraction of the GPU. Shares should add up to ≤ 1. |
| `cpuCount`, `memoryGB` | `6`, `12` | Override `defaults` |
| `monitor` | `1` | Host monitor for this seat, counted **left to right** |
| `resolution` | `1920x1080` or `native` | Stream resolution |
| `input` | `router` | `router`, `usb-dda`, `usbip`, `viewer` |
| `usbControllerLocationPath` | `PCIROOT(0)#PCI(0300)#PCI(0000)` | `usb-dda` only, from `Get-UsbControllers.ps1` |
| `audioOutput` | `Headset 2` | Windows playback device name for this seat |
| `viewerExtraArguments` | `--audio-on-host` | Extra viewer arguments for this seat only |

Run the check:

```powershell
.\scripts\host\Test-HostReadiness.ps1
```

## 4. Create the seats

```powershell
.\scripts\host\Deploy-MultiSeat.ps1
```

This creates the VMs, gives each a GPU slice, generates each seat's secret key, and (with `baseVhd`)
copies the GPU driver and SeatAgent into each VM's disk.

## 5. First boot of each seat

1. Start the VM in Hyper-V Manager and connect (the basic console works for setup).
2. Finish Windows OOBE: create the player's local account.
3. Run, as Administrator:
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\MultiSeat\Initialize-SeatGuest.ps1 -InstallSunshine
   ```
4. Open `https://localhost:47990` in the VM, create the Sunshine login.
5. Set the virtual display as the main display at your monitor's resolution and refresh rate.
6. Enable automatic sign-in ([Sysinternals Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon)).
7. On the **host**, open Moonlight, add the seat by IP (`Get-VMNetworkAdapter -VMName MultiSeat-Seat1`),
   and enter the PIN Moonlight shows in the seat's Sunshine web UI → *PIN*.
8. Sign the player into Steam with their own account.

## 6. Play

```powershell
.\scripts\host\Start-MultiSeat.ps1
```

- The first time, the SeatRouter window asks each player to **press a key on their keyboard** and
  **click their mouse**. This is remembered (`runtime\pairing.json`). Re-pair with `-Pair`.
- Each Moonlight window opens borderless on its seat's monitor.
- **Ctrl + Alt + F12** on a seat keyboard: that keyboard/mouse control the host (to click a window,
  fix something). Press again to return to the seat.

Stop with:

```powershell
.\scripts\host\Stop-MultiSeat.ps1
```

## After a host GPU driver update

The seats use a copy of the host driver; they must match. With all seats off:

```powershell
.\scripts\host\Stop-MultiSeat.ps1
.\scripts\host\Update-SeatGuest.ps1 -All
```

## Using usb-dda instead (Windows Server)

1. Install one PCIe USB card per seat. Plug that seat's keyboard, mouse, headset into its card.
2. `.\scripts\host\Get-UsbControllers.ps1` → copy each card's `LocationPath` into `seats.json`,
   set `"input": "usb-dda"`.
3. `.\scripts\host\Deploy-MultiSeat.ps1` (or `Add-SeatUsbController.ps1 -SeatName Seat1` for existing seats).
4. In the seat, the headset is a normal USB audio device: set it as default playback and recording.
   So that the sound stays in the VM (and reaches that headset) instead of being streamed to the host,
   add to the seat in `seats.json`: `"viewerExtraArguments": "--audio-on-host"`.
