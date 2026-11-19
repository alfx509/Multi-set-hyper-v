# MultiSeatInput (prototype)

KMDF keyboard/mouse **class upper filter** – MultiSeat's own replacement for the Interception driver.
It sits just below `kbdclass` / `mouclass` on every keyboard and mouse and, per device, either passes
input to Windows or **captures** it for the MultiSeat manager (which sends it to the player's seat).

- Control device: `\\.\MultiSeatInput` (Administrators/SYSTEM only, one client at a time).
- IOCTLs and structures: [public.h](public.h). Test tool: [tools/InputProbe](../../tools/InputProbe).
- Fail-safe: closing the handle (exit or crash of the manager) puts every device back to PASS;
  held keys/buttons are released to Windows before a device is captured.

> **Status: compiles (CI, VS 2022 + WDK, /W4 /WX, 0 warnings) but has never run yet.** The build output is
> unsigned: `setup/dev/Sign-TestDriver.ps1` test-signs it. Test **only in the test VM** (see [docs/driver-plan.md](../../docs/driver-plan.md), Phase 1).
> A broken keyboard/mouse class filter can disable all input – recovery steps are in
> `setup/dev/Install-InputDriver.ps1`.

Build: `msbuild drivers\MultiSeatInput\MultiSeatInput.vcxproj /p:Configuration=Debug /p:Platform=x64`
→ `out\drivers\Debug\MultiSeatInput.sys` (test-signed).
