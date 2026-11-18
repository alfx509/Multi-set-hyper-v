# Architecture

## Goals

1. Several people play at the same time on **one PC with one GPU**.
2. Each player has a **separate Windows session**: own game install, own account, own save games,
   own mouse cursor and keyboard focus – so even games without split-screen support work.
3. Each player's **monitor, keyboard, mouse and headset** belong to that player only.

A single Windows desktop cannot do this: it has one keyboard focus and one cursor for all devices.
So every player gets a VM, and the host's job is to connect the right physical devices to the right VM.

## Components

```
HOST (Windows 11 Pro / Server, Hyper-V)
├── Hyper-V
│   ├── VM MultiSeat-Seat1  ── GpuPartitionAdapter (gpuShare 0.5)  [+ optional DDA USB card]
│   └── VM MultiSeat-Seat2  ── GpuPartitionAdapter (gpuShare 0.5)
├── SeatRouter.exe           Interception driver → per-device input → UDP → SeatAgent
├── Moonlight copy per seat  runtime\viewers\SeatN\Moonlight.exe, borderless on monitor N,
│                            Windows audio output = Headset N
└── scripts\host\*.ps1       lifecycle automation

GUEST (each seat VM, Windows 10/11)
├── Host GPU driver          C:\Windows\System32\HostDriverStore\... (copied from the host)
├── Virtual display driver   gives the seat a real desktop at the monitor's resolution/refresh
├── Sunshine                 captures that desktop with the GPU's encoder, streams to Moonlight
├── SeatAgent.exe            receives routed input, injects with SendInput (scheduled task at logon)
└── Steam / games
```

## Data paths

### Video
`Game → GPU slice (render) → virtual display → Sunshine (NVENC/AMF/QSV encode on the same GPU)
→ Hyper-V virtual switch → Moonlight on host (hardware decode) → host monitor N`

Encoding and decoding both run on the dedicated video engines, so the 3D cost is small.
The virtual switch itself adds well under a millisecond; encode + decode typically add about 5–15 ms in total.

### Keyboard / mouse – `router` mode
```
USB keyboard 2 ─► Windows kbd class driver ─► Interception filter ─► SeatRouter
                                                   │ (stroke is NOT passed on to the host)
                                                   ▼
                                  UDP packet, HMAC-SHA256 with Seat2's key
                                                   ▼
                                        SeatAgent in Seat2 ─► SendInput ─► game
```
Unpaired devices (for example the host admin's own keyboard) keep working on the host.

### Keyboard / mouse / headset – `usb-dda` mode
A whole PCIe USB controller is unbound from the host and assigned to the VM with Discrete Device
Assignment. The VM loads its own USB driver; the devices never exist on the host. This is the most
"real hardware" option and includes the headset microphone, but DDA is only supported on Windows Server.

### Keyboard / mouse / headset – `usbip` mode
The host shares individual USB devices over IP (usbip-win2 server, or VirtualHere) and each VM
attaches its own devices as virtual USB. Works on Windows 11 Pro and includes the microphone;
small extra latency. Configured with those tools' own UI (not automated here).

### Sound
Sunshine streams the seat's sound to its Moonlight window. Windows remembers the output device per
**program path** ("App volume and device preferences"), so Start-MultiSeat.ps1 runs a separate copy
of Moonlight for each seat. Seat N's copy is pointed at Headset N (automatically with SoundVolumeView).

## Input protocol

56-byte UDP datagrams, one event each (see `src/MultiSeat.Protocol/PacketCodec.cs`):

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | magic `MSR1` |
| 4 | 4 | session id (random per router start) |
| 8 | 8 | sequence number |
| 16 | 1 | type: heartbeat, key, mouse move (rel/abs), button, wheel, h-wheel, release-all |
| 18 | 2 | scan code / button |
| 20 | 4+4 | x, y |
| 28 | 4 | flags (key up, E0, E1, button released) |
| 32 | 8 | timestamp (Unix ms) |
| 40 | 16 | HMAC-SHA256 truncated tag over bytes 0–39 |

Security properties:

- Every seat has its own random 32-byte key (`<vmRoot>\<seat>\agent.secret`, Administrators only).
  A packet for one seat is rejected by every other seat.
- The agent rejects packets older than 10 s, repeated sequence numbers, and "new sessions"
  older than the newest packet seen – captured traffic cannot be replayed.
- The agent's firewall rule only allows the local subnet.
- If no packet (including the 500 ms heartbeat) arrives for 2 s, the agent releases every held key
  and button, so a crash or network hiccup never leaves "W" held down.

Keyboard events carry **scan codes**, not virtual keys, so each seat's own keyboard layout applies
and games that read scan codes (WASD on AZERTY etc.) behave as with a physical keyboard.

## GPU partitioning details

`Set-SeatGpuPartition.ps1`:

- `Add-VMGpuPartitionAdapter` on the configured GPU instance path.
- Min = Max = Optimal = `MaxPartition{VRAM,Encode,Decode,Compute} × gpuShare`.
- `GuestControlledCacheTypes = true`, low MMIO 1 GB, high MMIO 32 GB (configurable).
- Checkpoints disabled, static memory, `AutomaticStopAction = TurnOff` – GPU-P (and DDA) VMs
  cannot be saved or checkpointed.

The partition values reserve VRAM; 3D/compute time is still shared by the GPU scheduler, so an idle
seat leaves performance for a busy one.

`Update-SeatGuest.ps1` finds every file of the host GPU driver via WMI
(`Win32_PnPSignedDriver` + `Win32_PnPSignedDriverCIMDataFile`), copies DriverStore package folders to
the guest's `System32\HostDriverStore\FileRepository`, and other files (for example `nvapi64.dll`)
to the same path in the guest.
