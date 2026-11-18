# Troubleshooting

## GPU

**Seat shows "Microsoft Basic Display Adapter" or the GPU has a yellow mark (Code 43)**
- Host and seat drivers do not match. VM off → `Update-SeatGuest.ps1 -SeatName <seat>`.
- Make sure the GPU partition exists: `Get-VMGpuPartitionAdapter -VMName MultiSeat-<seat>`.
- Some NVIDIA drivers need the guest to be Windows 11 22H2+ when the host is Windows 11.

**`Add-VMGpuPartitionAdapter` fails / no partitionable GPU**
- Update the GPU driver; reboot. Check `Get-VMHostPartitionableGpu`.
- Disable "Core isolation → Memory integrity" only if your GPU vendor's notes say so (it normally works).

**VM won't start: "insufficient resources" / MMIO error**
- Increase `highMmioGB` (e.g. 64) for large-VRAM cards, then `Set-SeatGpuPartition.ps1` again.

**Games stutter when another seat loads**
- That is GPU time-sharing. Lower settings/fps caps per seat; give the main player a larger `gpuShare`.

## Video / Moonlight

**Black screen in Moonlight**
- Sunshine needs a display: install/enable the virtual display driver inside the seat.
- In Sunshine → Configuration → Audio/Video, pick the virtual display and the GPU encoder (not software).

**Viewer opens on the wrong monitor**
- Monitors are numbered left to right by position. Rearrange in Windows display settings or change `monitor`.

**Seat IP changed, Moonlight cannot connect**
- With `Default Switch`, IPs can change at reboot. `Start-MultiSeat.ps1` always looks up the current IP;
  if Moonlight asks to pair again, re-pair once. A static IP on an Internal/External switch avoids this.

## Input (router mode)

**SeatRouter: "Could not open the Interception driver"**
- Run `Install-Interception.ps1` and **reboot**. Check `C:\Windows\System32\drivers\keyboard.sys` exists.

**SeatRouter: "interception.dll was not found"**
- Re-run `.\build.ps1` after `Install-Interception.ps1` (it copies the DLL to `out\SeatRouter`).

**Keyboard routed to the wrong seat**
- `Start-MultiSeat.ps1 -Pair`, or delete `runtime\pairing.json`.

**Nothing happens in the seat**
- Is the agent running in the seat? `Get-ScheduledTask 'MultiSeat Agent'`; log: `C:\ProgramData\MultiSeat\agent.log`.
- "Rejected packets" in the log: the seat's `C:\MultiSeat\seat.json` secret differs from the host's
  `<vmRoot>\<seat>\agent.secret` → VM off, `Update-SeatGuest.ps1 -SeatName <seat> -SkipDrivers`.
- Check the clock: the agent rejects packets more than 10 s off. Keep Hyper-V "Time synchronization" on.

**Mouse is too fast / slow / accelerated in the seat**
- Sign out and in after `Initialize-SeatGuest.ps1` (it disables "Enhance pointer precision").
- Games using raw input see the real mouse counts; set sensitivity in-game.

**I am stuck: all keyboards go to seats**
- Press **Ctrl + Alt + F12** on any seat keyboard → it controls the host. Close the SeatRouter window.
- Every device returns to the host when SeatRouter exits (even if it crashes).

**UAC prompt / Ctrl+Alt+Del inside a seat doesn't react**
- Windows blocks injected input on the secure desktop. Use Hyper-V's console for those, or
  `usb-dda`/`usbip` for real USB input.

## Sound

**All seats' sound comes out of the same headset**
- `viewer.perSeatCopies` must be `true`. Then set each copy's output (`runtime\viewers\SeatN\Moonlight.exe`)
  in *Settings → System → Sound → Volume mixer*, or set `tools.soundVolumeView`.
- Device names must match the names in Windows sound settings (rename devices there to "Headset 1", "Headset 2").

**No microphone in the seat**
- Moonlight/Sunshine does not carry the microphone. Use `usb-dda` or `usbip` to give the USB headset to the VM.

## Anti-cheat

Some games refuse to run in a VM. Nothing in this project hides the VM; that is intentional.
Pick games without kernel anti-cheat VM checks, or let that player use the host itself as their seat.
