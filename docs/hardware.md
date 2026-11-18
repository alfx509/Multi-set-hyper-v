# Hardware for 2 to 8 players

## How much PC do I need?

| Players | CPU | RAM | Graphics card (1080p) | Monitors |
|---|---|---|---|---|
| 2 | 6 cores | 16 GB | RTX 3060 / RX 6600 | 2 |
| 3–4 | 8 cores | 32 GB | RTX 4070 / RX 7800 XT | 3–4 |
| 5–6 | 12–16 cores | 48 GB | RTX 4080 / RX 7900 XT | 5–6 |
| 7–8 | 16+ cores | 64 GB | RTX 4090 / RX 7900 XTX | 7–8 |

These numbers are for light/medium games (Minecraft, Rocket League, Fortnite on low/medium, CS2, Valorant-class games where anti-cheat allows).
For heavy AAA games, plan fewer players. Use an NVMe SSD; every player loads games from it.

## Monitors: how to connect 5–8 screens

Most graphics cards have **4 outputs**. For more monitors:

- **A second graphics card** (even a small one) just for extra screens, or
- **DisplayPort MST hub** (one DisplayPort output → 2–3 monitors), or
- Monitors with **DisplayPort daisy-chain** (MST out).

Games of all players normally run on one graphics card (Windows picks the GPU for player sessions).
Each player can choose the GPU per game in their own *Settings → Display → Graphics* (not yet tested with MultiSeat).

## Video encoder limit (NVIDIA GeForce)

Each extra player's picture is compressed by the graphics card's video encoder. NVIDIA GeForce drivers
limit how many encodes run at once (8 on recent drivers, fewer on old ones). AMD and Intel have no fixed
limit. If the limit is reached, Windows uses the CPU instead: it still works, with more CPU load.
Keep the GeForce driver up to date.

## Keyboards and mice

- Any USB or wireless (USB dongle) keyboard and mouse. Bluetooth works but re-pairs after restarts.
- MultiSeat can see **10 keyboards and 10 mice**. Some gaming mice also report a "keyboard" (for macro
  buttons) and some keyboards report a "mouse". With 8 players that can use up the 10 slots.
  Simple office keyboards/mice avoid this.
- Plug everything in **before** you assign devices. If you move a device to another USB port,
  MultiSeat asks you to assign it again.

## Headsets

- **USB headsets** (or a cheap USB sound card per player for 3.5 mm headsets) – recommended.
  Each player then has their own sound device and microphone.
- In *Settings → Sound* rename each one ("Headset 2", "Headset 3", …) so they are easy to pick.

## Gamepads

Experimental: paste the gamepad's *Device instance path* (Device Manager → Details) into the player's
*USB devices* box. Please report which gamepads work.

## USB ports

8 players × (keyboard + mouse + headset) = 24 USB devices. Use **powered USB hubs**, ideally one hub
per player, and spread the hubs over different USB ports on the PC.
