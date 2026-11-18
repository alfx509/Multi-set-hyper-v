# MultiSeat

**One PC. Many players at the same time – from 2 up to 8.**
Every player gets their own monitor, keyboard, mouse and headset, and their own Windows desktop.
No virtual machines.

MultiSeat is a free, open-source alternative to multiseat programs like ASTER.

```
                   ┌──────────── ONE PC, ONE GRAPHICS CARD ────────────┐
   Player 1  ──►   │  Seat 1  = your normal Windows desktop            │  ◄── Monitor 1
   (you)           │            (all keyboards/mice not given to a seat)│
                   │                                                    │
   Player 2  ──►   │  Seat 2  = Windows user "player2"                 │  ◄── Monitor 2
   keyboard 2      │            own desktop, own Steam, own games       │      Headset 2
   mouse 2         │                                                    │
                   │                                                    │
   Player 3  ──►   │  Seat 3  = Windows user "player3"                 │  ◄── Monitor 3
   keyboard 3      │                                                    │      Headset 3
   mouse 3         └────────────────────────────────────────────────────┘
```

## How it looks

You open **MultiSeat**, and for every player you choose:

| | |
|---|---|
| **Monitor** | which screen is theirs ("Identify monitors" shows big numbers on the screens) |
| **Keyboard** | click *Assign*, then the player presses a key on **their** keyboard |
| **Mouse** | click *Assign*, then the player clicks **their** mouse |
| **Headset / Microphone** | pick from a list |
| **Windows user** | each player has their own Windows account (own Steam login, own saves) |

The fastest way: **🎮 Assign devices…** asks every player in turn, **on their own monitor**, to press
a key and click their mouse. Then press **▶ Start**. Every player plays on their own screen.
Press **■ Stop** when done.

Extras: a live **● playing** light shows whose keyboard/mouse is being used (great for testing),
closed player screens reopen by themselves, MultiSeat can open at sign-in and start the players
automatically, and it keeps running from the tray icon.

**Ctrl + Alt + F12** on a player's keyboard lends their keyboard and mouse to the main desktop
(press again to give them back).

## What you need

- **Windows Server 2019, 2022 or 2025** (free 180-day evaluation: <https://www.microsoft.com/evalcenter>).
  - **2–3 players:** works as is.
  - **4–8 players:** add the *Remote Desktop Session Host* role (`-InstallSessionHost`, see below).
    It works 120 days without licenses; after that Microsoft requires RDS licenses.
  - **Windows 10/11 Pro?** See [Can I use Windows Pro?](docs/troubleshooting.md#can-i-use-windows-10--11-pro)
- One monitor per player. Most graphics cards have 4 outputs; for more see [docs/hardware.md](docs/hardware.md).
- One keyboard + mouse per player (USB or wireless dongle). Headsets: USB headsets are easiest.
- A good CPU (6+ cores for 3 players) and 16–32 GB RAM.

## Install

On the gaming PC, in PowerShell **as Administrator**, in this folder:

```powershell
winget install Microsoft.DotNet.SDK.8      # once, to build
.\build.ps1
.\setup\Install-MultiSeat.ps1 -Players 2                       # 2 players (creates user player2)
# or, for up to 8 players:
.\setup\Install-MultiSeat.ps1 -Players 8 -InstallSessionHost    # creates player2 … player8
```

Restart the PC, then open **MultiSeat** from the desktop.

Full guide: [docs/guide.md](docs/guide.md) · Problems: [docs/troubleshooting.md](docs/troubleshooting.md) ·
How it works: [docs/how-it-works.md](docs/how-it-works.md) · Hardware for 2–8 players: [docs/hardware.md](docs/hardware.md)

## Good to know

- Seat screens run at up to **60 fps**. Seat 1 (the main desktop) has no limit, so give it to the most demanding player.
- Every player needs their **own game license / account** (like separate PCs).
- Some games allow only one copy per PC, and some anti-cheat systems block this. Test your games.
- **Gamepads and other USB devices** in seats are **experimental** (Remote Desktop USB redirection).
- The keyboard/mouse driver (Interception) is free for **non-commercial** use.

## Status

New project. The C# code builds and the core logic has tests, but it has **not yet been tested on a real
multi-monitor Windows Server PC**. Please report what works and what doesn't.

## License

[MIT](LICENSE)
