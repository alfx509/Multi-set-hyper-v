# Hardware guide

## Sizing per number of players

| | 2 players | 3 players | 4 players |
|---|---|---|---|
| CPU | 8 cores / 16 threads | 12 cores / 24 threads | 16 cores / 32 threads |
| RAM | 32 GB | 48 GB | 64 GB |
| GPU VRAM | 12 GB+ | 16 GB+ | 20–24 GB |
| GPU class (1080p, high) | RTX 4070 / RX 7800 XT | RTX 4080 / RX 7900 XT | RTX 4090 / RX 7900 XTX |
| Storage | 1 TB NVMe | 2 TB NVMe | 2–4 TB NVMe |
| Monitors | 2 (+1 host optional) | 3 | 4 |

Rules of thumb:

- Give each seat **4–6 vCPUs** and **8–12 GB RAM**; keep 2+ threads and ~6 GB for the host
  (streaming, routing, Hyper-V).
- VRAM is what runs out first. Divide the GPU's VRAM by players and compare with the game's
  recommended VRAM.
- Esports titles (CS2, Rocket League, Fortnite on performance mode, Minecraft) run fine on 3–4 seats
  even on mid-range cards. AAA titles: plan for 2 seats.
- Streaming uses the GPU's video engine. NVIDIA consumer cards allow several simultaneous NVENC
  sessions with current drivers; AMD/Intel encoders have no session limit but less throughput at 4×4K.

## GPU compatibility (GPU-P)

Run `Test-HostReadiness.ps1` – it lists partitionable GPUs. In practice:

- **NVIDIA**: GeForce GTX 10-series and newer, RTX all, with a current Game Ready / Studio driver.
- **AMD**: Radeon RX 5000 series and newer with Adrenalin drivers.
- **Intel**: Arc and Iris Xe.
- **Laptops** with hybrid graphics (iGPU drives the screen) are often problematic.

The VM's GPU driver is the host's driver, so host and seat always run the same driver version.

## Monitors

Any monitors; one per player. Each seat streams at its monitor's resolution and refresh rate
(set the seat's virtual display to match). Connect all monitors to the same GPU when possible.

## Keyboards and mice

- `router` mode: **any** USB or wireless (dongle) keyboard/mouse. Up to 10 keyboards and 10 mice.
  Bluetooth devices also work but pair-on-reboot behaviour varies; dongles are easier.
- Avoid "combo" receivers that serve keyboard + mouse for two different players.

## Headsets

| Headset type | Sound | Microphone |
|---|---|---|
| USB headset / USB dongle (wireless) | `router`: via viewer + per-app output · `usb-dda`/`usbip`: direct | `usb-dda` / `usbip` only |
| 3.5 mm analog | Needs a separate output per player: a cheap **USB sound card per player** (then same as USB) | same as USB |
| Bluetooth | Works for sound via per-app output; mic is low quality (hands-free profile) | Not recommended |

**Recommended:** one USB headset (or 3.5 mm headset + USB sound card) per player.

## USB cards for `usb-dda` (Windows Server)

- One **PCIe USB 3 card per seat**, each with its own controller chip (Renesas/NEC uPD720201/202,
  Fresco Logic FL1100, ASMedia ASM3142 are commonly reported to work with DDA).
- The motherboard's own USB controllers usually cannot be passed through cleanly.
- You need enough PCIe slots (x1 is enough) and the card must sit alone in its IOMMU group.

## Network

The seats talk to the host over the Hyper-V virtual switch (no physical network needed for
streaming/input). For internet the default `Default Switch` (NAT) is fine. Use an External switch
if seats should appear on your LAN (e.g. to host LAN games for other PCs).
