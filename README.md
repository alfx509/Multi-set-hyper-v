# Multi-Seat Hyper-V

**One gaming PC, several players, each on their own screen, keyboard, mouse and headset, all at the same time.**

Multi-Seat Hyper-V splits one Windows PC into several gaming "seats". Each seat is a Hyper-V virtual
machine with a **slice of the same physical GPU** (Hyper-V GPU partitioning, "GPU-P"). Every player
gets their own Windows, their own game install and account, their own monitor, and their own
keyboard, mouse and headset. Two to four friends can play the same game (LAN or online) on one computer.

```
                          ┌──────────────────────── ONE PHYSICAL PC (Hyper-V host) ───────────────────────┐
                          │                                                                                │
  Player 1                │   ┌──────────── VM "Seat1" ────────────┐   ┌──────────── VM "Seat2" ────────────┐ │
  ┌──────────┐            │   │ Windows 11 + Steam + game          │   │ Windows 11 + Steam + game          │ │
  │ Monitor 1│◄─viewer────┼───┤ Sunshine (stream) │ SeatAgent      │   │ Sunshine (stream) │ SeatAgent      │ │
  │ KB1 Mouse1├─router────┼──►│ GPU-P slice 50 %  │ (input inject) │   │ GPU-P slice 50 %  │ (input inject) │ │
  │ Headset 1│◄─sound─────┤   └───────────────▲────────────────────┘   └───────────────▲────────────────────┘ │
  └──────────┘            │                   │ partition                              │ partition            │
  Player 2                │             ┌─────┴────────────────────────────────────────┴─────┐                │
  ┌──────────┐            │             │                 ONE GPU (NVIDIA / AMD / Intel)      │                │
  │ Monitor 2│◄─viewer────┤             └─────────────────────────────────────────────────────┘                │
  │ KB2 Mouse2├─router────┤   SeatRouter: each physical keyboard/mouse → its own seat (Interception driver)   │
  │ Headset 2│◄─sound─────┤   Start-MultiSeat.ps1: boots VMs, places one viewer per monitor, routes sound       │
  └──────────┘            └────────────────────────────────────────────────────────────────────────────────┘
```

## What is in this repository

| Part | What it does |
|---|---|
| `scripts/host/*.ps1` | PowerShell automation for the **host**: check hardware, create seat VMs, share the GPU, copy GPU drivers into VMs, pass through USB controllers, start/stop a session. |
| `src/SeatRouter` | Host program. Uses the [Interception](https://github.com/oblitum/Interception) driver to tell keyboards and mice apart, so **each player's keyboard and mouse only control their own seat**. |
| `src/SeatAgent` | Small program inside each VM that receives the routed keyboard/mouse input and injects it. Authenticated (HMAC-SHA256 per seat), replay-protected, releases all keys if the link drops. |
| `scripts/guest/Initialize-SeatGuest.ps1` | One-time setup inside each VM (agent autostart, firewall, mouse 1:1, power, Sunshine). |
| `config/seats.example.json` | Describe your seats: GPU share, CPU/RAM, monitor, input mode, headset. |
| `docs/` | [Architecture](docs/architecture.md) · [Setup guide](docs/setup-guide.md) · [Hardware guide](docs/hardware.md) · [Troubleshooting](docs/troubleshooting.md) |

## How each piece of hardware reaches its player

| | How it works |
|---|---|
| **GPU** | Hyper-V GPU-P: one GPU is split between all seats (`gpuShare`, e.g. 0.5 each for 2 players). The VM uses the host's own GPU driver (copied in by `Update-SeatGuest.ps1`). |
| **Screen** | GPU-P VMs have no physical video output. Each seat runs **Sunshine** (stream server); the host runs one **Moonlight** window per seat, borderless on that seat's monitor. Encode + virtual network + decode typically adds about 5–15 ms. |
| **Keyboard + mouse** | Pick per seat:<br>• `router` (Windows 10/11 Pro): SeatRouter captures each physical device and sends it only to its seat.<br>• `usb-dda` (Windows Server): a whole PCIe USB card is passed to the VM. Real USB, zero software in between.<br>• `usbip`: individual USB devices shared over the virtual network (usbip-win2 / VirtualHere).<br>• `viewer`: Moonlight's own input (good for 1 seat + host). |
| **Headset** | Sound: each seat's viewer is a separate program copy, so Windows can send Seat 1 to Headset 1 and Seat 2 to Headset 2 (set automatically with NirSoft SoundVolumeView, or once by hand in the volume mixer).<br>Microphone: use `usb-dda` or `usbip` so the USB headset is attached directly to the VM, or use voice chat on the host / phone. |

## Quick start

> Full walkthrough with screenshots-in-words: **[docs/setup-guide.md](docs/setup-guide.md)**

```powershell
# On the host, in an elevated PowerShell, from the repo folder
winget install Microsoft.DotNet.SDK.8
.\build.ps1                                            # builds SeatRouter + SeatAgent
.\scripts\host\Install-Interception.ps1                # router input mode only; then REBOOT

Copy-Item config\seats.example.json config\seats.json  # then edit it
.\scripts\host\Test-HostReadiness.ps1                  # what can this PC do?
.\scripts\host\Deploy-MultiSeat.ps1                    # creates the seat VMs

# In each VM (once), as Administrator:
#   powershell -ExecutionPolicy Bypass -File C:\MultiSeat\Initialize-SeatGuest.ps1 -InstallSunshine
#   then pair Moonlight on the host with that seat (one-time PIN).

.\scripts\host\Start-MultiSeat.ps1                     # play!
.\scripts\host\Stop-MultiSeat.ps1                      # done
```

While playing: **Ctrl + Alt + F12** on a seat's keyboard gives that keyboard and mouse back to the
host (and again to return). Closing the SeatRouter window gives every device back to the host.

## Requirements (short version)

- **Windows 11 Pro / Enterprise / Education** (Windows 10 21H2+ works; Home has no Hyper-V) or **Windows Server 2022/2025** (needed for `usb-dda`).
- CPU with VT-x/AMD-V and VT-d/AMD-Vi enabled in BIOS. 8+ cores recommended for 2 seats, 12–16 for 3–4.
- 32 GB RAM for 2 seats, 64 GB for 4.
- A GPU that supports partitioning with a current driver: NVIDIA GeForce/RTX (GTX 10-series and newer), AMD Radeon (RX 5000+), Intel Arc / Iris Xe. 12 GB+ VRAM recommended for 2 seats, 16–24 GB for 3–4.
- One monitor per player (plus optional one for the host), one keyboard/mouse/headset per player.
- A fast NVMe SSD (seats share it).

See **[docs/hardware.md](docs/hardware.md)** for sizing per seat count.

## Honest limitations

- **Anti-cheat:** many competitive games (Valorant/Vanguard, FACEIT, some EasyAntiCheat/BattlEye titles) refuse to run in a VM. Check each game first.
- **Licenses:** every seat needs its own game license and account (Steam Family Sharing does not allow the same library to be played twice at once).
- **GPU-P on Windows 11 is not officially supported by Microsoft for gaming.** It works well on many consumer GPUs, but after a host GPU driver update you must run `Update-SeatGuest.ps1 -All` (VMs off). Hyper-V checkpoints and live migration cannot be used with GPU-P.
- **Performance:** all seats share one GPU. Two players on an RTX 4070-class card at 1080p/high works well in most games; four players needs a high-end card and lower settings.
- **Router mode** uses Interception, which is free for non-commercial use only and supports up to 10 keyboards and 10 mice. Input in the VM is injected with `SendInput`: it works with almost every game, but not on the Windows secure desktop (UAC prompt, Ctrl+Alt+Del) – use `usb-dda`/`usbip` if you need that.

## Credits and related projects

- [Easy-GPU-PV](https://github.com/jamesstringerparsec/Easy-GPU-PV) – the driver-copy approach for GPU-P guests is based on its findings.
- [Interception](https://github.com/oblitum/Interception) – per-device keyboard/mouse capture.
- [Sunshine](https://github.com/LizardByte/Sunshine) and [Moonlight](https://moonlight-stream.org) – low-latency game streaming.
- [Virtual-Display-Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver) – virtual monitors inside the seats.

## License

[MIT](LICENSE). Third-party tools keep their own licenses and are downloaded from their official sources, not bundled.
