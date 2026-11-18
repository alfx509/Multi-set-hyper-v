# Step-by-step guide

Examples use 2 players; for more, everything is the same with more players.

## 1. Hardware

- Plug the monitors into the graphics card (for more than 4 monitors see [hardware.md](hardware.md)).
- Plug in one keyboard, one mouse and one headset per player.
- In Windows *Settings → Sound*, rename the headsets so you can tell them apart
  (for example "Headset 2", "Headset 3"): click the device → *Rename*.

## 2. Windows Server

Install Windows Server 2022 or 2025 **"Desktop Experience"** (the version with the normal desktop).
Then install the latest graphics driver from NVIDIA / AMD / Intel, and Steam or your launchers.

## 3. Install MultiSeat

PowerShell as Administrator, in the MultiSeat folder:

```powershell
winget install Microsoft.DotNet.SDK.8
.\build.ps1
.\setup\Install-MultiSeat.ps1 -Players 2
```

For 4–8 players use `-Players 8 -InstallSessionHost` (Windows Server only).
It asks for a password for each new user. **Restart the PC.**

## 4. First sign-in of each player (once)

Sign out, sign in as `player2`, let Windows finish setting up, sign in to Steam with player 2's
account, sign out. Repeat for every other player. Sign in again as yourself.

## 5. Set up the players

Open **MultiSeat** (desktop icon).

1. Click **🖥 Identify monitors** – a big number appears on each screen.
2. Double-click **Seat 1 (this desktop)** and choose your own monitor.
3. Click **➕ Add player** for each extra player: Windows user (`player2`), its password, and its monitor.
   Headset and microphone: choose the player's headset.
4. Click **🎮 Assign devices…**. On each player's monitor a big message appears: that player presses
   a key on **their** keyboard and clicks **their** mouse. (Esc skips.)
5. Click **✔ Check this PC**. Fix anything marked ✖.

## 6. Play

Press **▶ Start**. After a few seconds each player sees their own desktop on their monitor.

- Your keyboard and mouse stay on your screen.
- **Ctrl + Alt + F12** on a player's keyboard: that keyboard/mouse works on the main desktop for a
  moment (to fix something); press again to return.
- The **Status** column shows **● playing** while a player uses their keyboard or mouse – an easy way to
  check that every device goes to the right person.
- **■ Stop** closes the player screens. Players stay signed in, so a game keeps running and the next Start is fast.
- **Players → Sign out all players** signs them out completely. Right-click a player for more
  (reopen their screen, sign out only them).
- Minimizing MultiSeat while playing hides it to the tray icon.

## Options menu

- *Start the seats when MultiSeat opens* and *Open MultiSeat when I sign in to Windows*: turn both on
  and the PC is ready to play right after you sign in.
- *Reopen a seat screen if it closes* (on by default).
- *Keep my mouse on my own monitor while playing* (on by default).

## Gamepads (experimental)

In Device Manager, open the gamepad → *Details* → *Device instance path*, copy it, and paste it into
the seat's *USB devices* box. The gamepad then belongs to that seat. If it doesn't work, please open
an issue with your gamepad model.
