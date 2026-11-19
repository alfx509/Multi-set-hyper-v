# ASTER-style MultiSeat: driver plan

Goal: each player gets a **real local Windows session** with **their own physical monitor, keyboard, mouse,
headset and USB devices**, at the **monitor's full refresh rate**, with **no Remote Desktop window** –
the way ASTER works.

Background: [research/aster-research.md](research/aster-research.md) (how ASTER works, what Windows allows,
driver signing rules). Short version:

- ASTER = one kernel driver (`mutenx.sys`) + a service. Each extra workplace is a real Windows session
  called "Multiseat" (not RDP). ASTER gives physical GPU outputs to those sessions through undocumented
  Windows graphics internals, which is why it breaks on Windows updates.
- Microsoft documents that **GPU monitors always belong to the console session**. The documented way to
  give another session a screen is a **Remote Desktop Protocol Provider + a remote Indirect Display Driver
  (IddCx)**.
- New kernel drivers must be **signed by Microsoft** (EV certificate + Partner Center) to load on normal PCs.
  Until then we develop with **test signing in a VM / test PC only**.

## Rules for this work

1. **Develop and test drivers only in the Hyper-V test VM or on a dedicated test PC** (test signing on,
   Secure Boot off). Never on the user's daily Windows.
2. **Never ship a test-signed driver to users.** Releases need Microsoft signing (Phase 5).
3. **No licensing workarounds.** No `termsrv.dll` patching, no RDP Wrapper, no bypassing the
   one-session limit of Windows 10/11 Pro. Multi-session target = **Windows Server** (RDSH licensing).
4. **ASTER: observe first, never copy.** Start with black-box observation and the kernel debugger
   (sessions, drivers loaded, displays seen from inside a workplace, fps, `!chkimg` on dxgkrnl/win32k,
   registered callbacks). Disassembling ASTER's driver only if that is not enough, only to learn how it
   uses Windows, and after checking ASTER's licence. Never copy ASTER code.
5. Every driver must be HVCI (Memory Integrity) compatible and must fail **safe**: if MultiSeat stops,
   every keyboard, mouse and monitor goes back to the main desktop.

## Phase 0 – Developer PC and test VM

On the user's PC (Windows Server or Windows 11), as Administrator:

```powershell
.\setup\dev\Install-DevTools.ps1          # VS 2022 + WDK + .NET 8 + Git + Inno Setup + WinDbg + Hyper-V
.\setup\dev\New-DriverTestVM.ps1 -IsoPath <Windows Server 2025 eval ISO>
# inside the VM after installing Windows:
C:\MultiSeatDev\Enable-DriverTesting.ps1 -HostIp <host IP on the Default Switch>   # then restart the VM
# on the host:
Checkpoint-VM -Name MultiSeatDriverTest -SnapshotName clean
```

After any blue screen in the VM: `Restore-VMCheckpoint -VMName MultiSeatDriverTest -Name clean -Confirm:$false`.

**Done when:** the VM boots in Test Mode and WinDbg on the host can attach to it.

## Phase 1 – Our own keyboard/mouse driver (`drivers/MultiSeatInput`)

A KMDF class upper filter below `kbdclass`/`mouclass` (pattern of the WDK `kbfiltr`/`moufiltr` samples).
Per device: PASS (to Windows) or CAPTURE (to MultiSeat). Monitor mode copies all input for pairing.
Replaces Interception (which is non-commercial-only and unmaintained).

```powershell
msbuild drivers\MultiSeatInput\MultiSeatInput.vcxproj /p:Configuration=Debug /p:Platform=x64
dotnet publish tools\InputProbe -c Release -r win-x64 --self-contained -o out\InputProbe
.\setup\dev\Deploy-ToTestVM.ps1 -Install          # copies + installs + restarts the VM
# in the VM (Administrator):
C:\MultiSeatDev\InputProbe.exe list
C:\MultiSeatDev\InputProbe.exe watch 20
C:\MultiSeatDev\InputProbe.exe capture <id> 10
```

**Tests (all must pass):**
- [ ] Builds with warnings-as-errors (done, 2026-11-19); Driver Verifier (`verifier /standard /driver MultiSeatInput.sys`) runs a full day with no bug check.
- [x] `list` shows every keyboard and mouse with its hardware id. (VM: VMBus keyboard, HID mouse, and RDP `TS_INPT` keyboard/mouse in enhanced session)
- [x] `watch` shows events with the right device id; nothing is lost on the desktop.
- [x] `capture` takes ONE device away (desktop ignores it, InputProbe shows it); other devices still work. (keyboard id 1/3 and mouse id 2 tested)
- [ ] Holding Ctrl while capture starts does not leave Ctrl stuck on the desktop. (held Enter was released correctly; Ctrl still to confirm)
- [ ] Killing InputProbe (Task Manager) gives the device back immediately.
- [ ] Plug/unplug while running: no crash, ids update.
- [ ] Works with HVCI (Memory Integrity) on.

**Then:** add a `MultiSeatInput` backend to `src/MultiSeat/Input` (same interface as the Interception
backend) and make it the default when the driver is present.

## Phase 2 – Display feasibility (the hard part)

Two paths are studied side by side:
- **Kernel path (ASTER-style, main goal):** a seat's session shows directly on one output of the same GPU.
  Needs a real test PC with a GPU – setup and observation steps: [test-pc.md](test-pc.md).
- **Documented path:** protocol provider + remote IddCx (questions 3–4 below). Can start in the VM.

Questions to answer with measurements before writing the display driver:

1. **What does a player session get today?** Run `tools/SeatProbe` in the console session and in a
   MultiSeat (RDP) player session on the real PC. Record protocol, adapters, outputs, DWM compositions/s.
2. **What does an ASTER workplace get?** (black-box, on a test PC with the ASTER trial): run SeatProbe
   inside workplace 2. Does it report protocol 0, the real GPU, its own output attached, full refresh rate?
   Also record `qwinsta`, `driverquery /v`, `fltmc`, and whether each workplace has its own `dwm.exe`.
3. **Documented path prototype:** a minimal **Remote Desktop Protocol Provider** ("MultiSeat Local",
   `IWRdsProtocolManager` / `IWRdsProtocolListener` / `IWRdsProtocolConnection`) that creates a session
   for a local seat, plus a **remote IddCx driver** (IddCx 1.4+, `IDDCX_ADAPTER_FLAGS_REMOTE_SESSION_DRIVER`).
   Measure: does Windows accept a third-party remote IDD? What frame rate does DWM give that session?
   Microsoft samples: `Windows-driver-samples/video/IndirectDisplay`, Remote Desktop Protocol Provider docs.
4. **Presenter:** show the IDD's frames full-screen on the seat's physical monitor from the console
   session (shared D3D11 texture, flip-model swap chain, no video encoding). Measure added latency.

**Done when:** a written result says which path reaches ≥ the monitor's refresh rate (target 120/144 Hz)
with ≤ 1 frame of added latency – or that none does, with the numbers.

## Phase 3 – Audio and USB per seat

- Audio: per-seat endpoints through the protocol provider's audio channel, or a virtual audio driver
  per seat (research first; no public API for non-RDP providers).
- USB storage/gamepads: device-to-session assignment (filter drivers like MultiPoint used). Research first.

## Phase 4 – Service and integration

- Turn the manager into a Windows service + tray UI; seats survive UI crashes.
- Per-seat sign-in screen on its own monitor; recovery when a seat hangs.

## Phase 5 – Signing and release

- EV code-signing certificate + Microsoft Partner Center hardware account (owner's name/company).
- INF files, HLK/attestation submission, HVCI compliance, then ship in `MultiSeat-Setup.exe`.
- Until then: drivers are "developer preview", test-signed, test machines only.
