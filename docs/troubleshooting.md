# Troubleshooting

**"This is Windows 10/11 (client)… only ONE signed-in person"**
Extra seats need Windows Server. Windows 10/11 sign out the main desktop when a second user signs in.

**"The Interception input driver is not running"**
Run `setup\Install-MultiSeat.ps1` and restart the PC.

**A seat asks for a password / "Your credentials did not work"**
Edit the seat and type the password again. Make sure you restarted after `Install-MultiSeat.ps1`
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
Inside the seat: *Settings → Bluetooth & devices → Mouse → Additional mouse settings → Pointer
Options → Display pointer trails* (shortest). Then tell us your Windows version in an issue.

**Mouse feels too fast/slow in a seat**
Set the sensitivity inside the seat (Mouse settings) or in the game. Turn off *Enhance pointer precision* for 1:1 movement.

**Games are slow or use "Microsoft Basic Render Driver" in a seat**
The GPU policy is missing: run `setup\Install-MultiSeat.ps1` again and restart. Check that the game
shows your real graphics card.

**No sound / wrong headset**
- Windows Audio must be running (the installer turns it on).
- If MultiSeat logs "could not set the headset automatically": open *Settings → System → Sound →
  Volume mixer*, find `mstsc.exe` from `C:\ProgramData\MultiSeat\clients\seatNNNNN\` and choose the headset.

**More than 3 players on Windows Server**
Install the *Remote Desktop Session Host* role and RDS licenses (Microsoft licensing).

**My keyboard is stuck in a seat**
Ctrl+Alt+F12 on it, or press Stop, or close MultiSeat – every device returns to the main desktop.
