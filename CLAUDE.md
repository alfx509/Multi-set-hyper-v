# MultiSeat – notes for Claude

## The user

- Haitham (GitHub `alfx509`). English is not their first language: **reply in short, simple English**, with
  clear steps. They want a free, open-source **alternative to ASTER** (IBIK's multiseat software):
  several players on one PC, each with their own monitor, keyboard, mouse, headset and gamepad, in a
  **separate Windows environment**, with **full frame rate** and **no Remote Desktop window**.
- They tested v0.3.0-test.1 on Windows Server and rejected it: player screens are RDP windows capped at
  60 fps, and the main mouse could move onto player monitors (now fixed with a low-level mouse hook).
- **Main focus now: an ASTER-style native implementation (drivers).** Plan: [docs/driver-plan.md](docs/driver-plan.md).
  Research: [docs/research/aster-research.md](docs/research/aster-research.md).
- This session runs on the user's own PC, so you can build drivers, create/drive the Hyper-V test VM and
  run tests directly. Ask before anything that changes their main Windows (drivers, bcdedit, policies).

## Hard rules

1. **Drivers: test VM / test PC only.** Test signing and kernel debugging only inside the VM created by
   `setup/dev/New-DriverTestVM.ps1`. Take a checkpoint before installing a new driver build.
2. **Never ship test-signed drivers** in `MultiSeat-Setup.exe` or releases.
3. **No licensing workarounds**: no termsrv.dll patching / RDP Wrapper / bypassing the Windows 10/11
   one-session limit. Multi-session target is Windows Server. (The user was told this and why.)
4. **No disassembly of ASTER.** Black-box observation is fine (see driver-plan Phase 2).
5. Everything must fail safe: if MultiSeat exits or crashes, every device returns to the main desktop.

## Repository layout

| Path | What |
|---|---|
| `src/MultiSeat` | Manager app (WinForms, admin): seats, pairing, start/stop, tray, settings, input router, session launcher |
| `src/SeatAgent` | Runs in each player session (scheduled task at logon); injects routed input with SendInput |
| `src/MultiSeat.Core` | Config model, authenticated UDP packet format, RDP file builder (unit-tested) |
| `drivers/MultiSeatInput` | **KMDF keyboard/mouse class filter (prototype, never compiled yet)** – replaces Interception |
| `tools/SeatProbe` | Reports what a session gets: console/remote, GPU, outputs, DWM fps, desktop duplication fps |
| `tools/InputProbe` | Test tool for MultiSeatInput: `list`, `watch`, `capture <id>` |
| `setup/Configure-MultiSeat.ps1` | Windows settings run by the installer |
| `setup/dev/*` | Dev machine tools, test VM creation, driver deploy/install |
| `installer/MultiSeat.iss` | Inno Setup all-in-one installer |
| `docs/` | User guide, troubleshooting, hardware, how-it-works, driver plan, research |

## Current architecture (v0.3, works but not ASTER-like)

Player sessions = loopback RDP (mstsc to 127.0.0.x) on Windows Server, full screen on the player's monitor.
Input: Interception driver → MultiSeat → UDP 127.0.0.1 (HMAC per seat) → SeatAgent → SendInput.
Audio: per-app default device for each seat's mstsc copy (AudioPolicyConfig). Limits: RDP 60 fps cap.

## Build and test

```powershell
dotnet test tests\MultiSeat.Core.Tests
.\build.ps1 -Installer                     # out\installer\MultiSeat-Setup.exe
dotnet publish tools\SeatProbe -c Release -r win-x64 --self-contained -o out\SeatProbe
dotnet publish tools\InputProbe -c Release -r win-x64 --self-contained -o out\InputProbe
msbuild drivers\MultiSeatInput\MultiSeatInput.vcxproj /p:Configuration=Debug /p:Platform=x64
.\setup\dev\Deploy-ToTestVM.ps1 -Install   # driver → test VM, install, restart VM
```

CI (`.github/workflows/build.yml`) builds and tests on every push; **Actions → build → Run workflow** with
`release` = `v0.x.y-test.n` publishes a pre-release with MultiSeat-Setup.exe.

## Conventions

- C#: .NET 8, nullable on, warnings as errors. Driver C: KMDF, /W4 warnings as errors.
- Develop on branch `claude/multi-player-gpu-game-repo-2kmbl8` (or a new feature branch), push, use the release workflow for test builds.
