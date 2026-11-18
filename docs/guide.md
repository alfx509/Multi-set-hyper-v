# Step-by-step guide (3 players)

## 1. Hardware

- Plug **all monitors into the same graphics card**.
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
.\setup\Install-MultiSeat.ps1 -CreateUsers player2,player3
```

It asks for a password for each new user. **Restart the PC.**

## 4. First sign-in of each player (once)

Sign out, sign in as `player2`, let Windows finish setting up, sign in to Steam with player 2's
account, sign out. Repeat for `player3`. Sign in again as yourself.

## 5. Set up the seats

Open **MultiSeat** (desktop icon).

1. Click **Identify monitors** – a big number appears on each screen.
2. Double-click **Seat 1 (this desktop)** and choose your own monitor.
3. Click **Add seat**:
   - Windows user: `player2`, password: the one you chose
   - Monitor: player 2's screen
   - Keyboard: *Assign* → player 2 presses a key on their keyboard
   - Mouse: *Assign* → player 2 clicks their mouse
   - Headset and microphone: choose player 2's headset
4. Same for player 3.
5. Click **Check this PC**. Fix anything marked ✖.

## 6. Play

Press **▶ Start**. After a few seconds each player sees their own desktop on their monitor.

- Your keyboard and mouse stay on your screen.
- **Ctrl + Alt + F12** on a player's keyboard: that keyboard/mouse works on the main desktop for a
  moment (to fix something); press again to return.
- **■ Stop** closes the seat screens. Players stay signed in, so a game keeps running and the next Start is fast.
- **Sign out seats** signs the players out completely.

## Gamepads (experimental)

In Device Manager, open the gamepad → *Details* → *Device instance path*, copy it, and paste it into
the seat's *USB devices* box. The gamepad then belongs to that seat. If it doesn't work, please open
an issue with your gamepad model.
