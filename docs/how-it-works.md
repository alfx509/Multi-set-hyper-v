# How it works

Windows can already run several people's desktops at the same time: every signed-in user gets a
**session**. Windows Server allows several sessions at once. MultiSeat connects each session to
one player's hardware.

```
                       MultiSeat.exe  (main desktop, Administrator)
                       │
   keyboard 2 ─┐       ├─ Input router (Interception driver)
   mouse 2   ──┼──────►│    • devices of a seat → that seat only (main desktop never sees them)
               │       │    • all other devices → main desktop
               │       │                 │ UDP 127.0.0.1:478xx, HMAC per seat
               │       │                 ▼
               │       │        SeatAgent.exe in player2's session → SendInput → game
               │       │
               │       ├─ Seat window: a copy of Windows' Remote Desktop client, full screen on
               │       │    monitor 2, connected to 127.0.0.2 = this same PC, signed in as player2.
               │       │    • picture: rendered by the graphics card in player2's session,
               │       │      hardware H.264, up to 60 fps (never leaves the PC)
               │       │    • sound + microphone: through this client, which is set to Headset 2
               │       │    • USB devices (experimental): Remote Desktop USB redirection
               │       │
               │       └─ Main desktop mouse is kept on monitor 1 while seats run
```

## Pieces

| File | Job |
|---|---|
| `src/MultiSeat` | The app: seat list, pairing, start/stop, input router, audio routing, seat windows |
| `src/SeatAgent` | Tiny program in every seat session; types the seat's keys and moves its mouse |
| `src/MultiSeat.Core` | Settings, packet format (shared, unit-tested) |
| `setup/Install-MultiSeat.ps1` | One-time Windows setup (driver, policies, users, install) |

## Why each part is needed

- **Separate sessions** give every player their own focus, mouse pointer, Steam login and settings –
  so any game works, even without split-screen.
- **Interception** is the only common way on Windows to know *which* keyboard pressed a key and to
  stop that key from reaching the main desktop.
- **Remote Desktop to 127.0.0.x** is how Windows shows a non-console session on a screen. With the
  GPU policy set, games in the session render on the real graphics card.
- **One client copy per seat**: Windows remembers the sound device per program path, so seat 2's
  copy can play on headset 2 and seat 3's copy on headset 3.
- **Software pointer in seats**: the agent asks Windows to draw the mouse pointer into the picture,
  because the seat's pointer is moved from inside the session.

## Security

- Each seat has its own random key. Packets are signed (HMAC-SHA256), time-stamped and numbered,
  so other programs cannot type into a seat and old packets cannot be replayed.
- The agent listens only on 127.0.0.1. No firewall port is opened.
- Seat passwords are kept in Windows Credential Manager of the admin account, not in files.
- `C:\ProgramData\MultiSeat\config.json` (holds the keys) is readable by Administrators only;
  each seat's agent file is readable only by that seat's user.

## Limits

- Seat screens: 60 fps maximum (a Windows limit for sessions); Seat 1 is unlimited.
- Players share one GPU, CPU and disk.
- Windows' secure screens (UAC prompts, Ctrl+Alt+Del) inside a seat can't receive injected input;
  run games without admin rights or use Ctrl+Alt+F12 and the main desktop.
- Interception supports up to 10 keyboards and 10 mice.
