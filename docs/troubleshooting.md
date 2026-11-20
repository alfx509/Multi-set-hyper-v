# Troubleshooting

## Can I use Windows 10 / 11 Pro?

Not for extra players, sorry. Windows Pro *does* include Remote Desktop and Hyper-V, but its license and
its design allow **only one signed-in person at a time**: when a second user signs in, the first is
signed out. There is no setting that changes this.

Guides that say "it works on Pro" use **RDP Wrapper** or patch `termsrv.dll`. That breaks Microsoft's
license terms and Windows updates regularly break it, so MultiSeat does not use or support it.
(ASTER works on Pro because it is a licensed product with its own technology.)

Use **Windows Server** (2019/2022/2025, the free 180-day evaluation is fine):
2–3 players work as is; 4–8 players need the *Remote Desktop Session Host* role
(run MultiSeat-Setup.exe and tick *Allow 4 to 8 players*), which works for 120 days before RDS licenses are needed.

## Problems

**"This is Windows 10/11 (client)… only ONE signed-in person"**
Extra seats need Windows Server. Windows 10/11 sign out the main desktop when a second user signs in.

**"The Interception input driver is not running"**
Run MultiSeat-Setup.exe again and restart the PC. Details of the last setup: `C:\ProgramData\MultiSeat\setup.log`.

**A seat asks for a password / "Your credentials did not work"**
Edit the seat and type the password again. Make sure you restarted after installing
(it allows saved passwords for 127.0.0.x).

**A seat window opens on the wrong monitor or is not full screen**
Press Stop, click *Identify monitors*, check each seat's monitor, and press Start again.
Make sure Windows display scaling is set per monitor and all monitors are on the same graphics card.

**The player's keys/mouse do nothing in their seat**
- The player's user must be signed in; the agent starts at sign-in. Check
  `%TEMP%\MultiSeatAgent.log` inside that user's session.
- If you just installed, sign the seat out once (*Sign out seats*) and Start again.
- The device moved after re-plugging: MultiSeat says "has changed" → edit the seat, Assign again.

**No mouse pointer visible in a seat**
Since v0.4.0-test.3 the seat agent draws the player's pointer itself. If it is still missing: sign the player
out (right-click the player → Sign out this player) and press Start again, so the new agent runs. Then tell us
your Windows version in an issue.

**Mouse feels too fast/slow in a seat**
Set the sensitivity inside the seat (Mouse settings) or in the game. Turn off *Enhance pointer precision* for 1:1 movement.

**Games are slow or use "Microsoft Basic Render Driver" in a seat**
The GPU policy is missing: run MultiSeat-Setup.exe again and restart. Check that the game
shows your real graphics card.

**No sound / wrong headset**
- Windows Audio must be running (the installer turns it on).
- If MultiSeat logs "could not set the headset automatically": open *Settings → System → Sound →
  Volume mixer*, find `mstsc.exe` from `C:\ProgramData\MultiSeat\clients\seatNNNNN\` and choose the headset.

**More than 3 players on Windows Server**
Run MultiSeat-Setup.exe again, tick *Allow 4 to 8 players*, and restart. Works 120 days without licenses;
then add RDS licenses (Microsoft licensing).

**With 6–8 players some screens are slow or the CPU is very busy**
NVIDIA GeForce cards limit how many video encodes run at once; extra players then use the CPU.
Update the GPU driver, lower the game resolution, or see [hardware.md](hardware.md).

**"Assign devices" never reacts for one player**
Some gaming mice/keyboards use 2 slots; with many players the 10 keyboard / 10 mouse slots can run out.
Try a simple keyboard/mouse for that player.

**My keyboard is stuck in a seat**
Ctrl+Alt+F12 on it, or press Stop, or close MultiSeat – every device returns to the main desktop.

**"Could not create user" when adding a player**
Windows Server requires complex passwords by default (8+ characters with upper case, lower case and a
digit or symbol). Use a stronger password.
