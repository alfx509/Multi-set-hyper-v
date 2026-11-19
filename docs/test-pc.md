# Test PC: setup and first experiments

This is the plan for the day the test PC arrives. The test PC is for the **kernel display path**
(ASTER-style: each seat's session shows directly on one output of the same GPU). It must never be the
user's main PC: driver tests there crash often.

## What the test PC needs

| Part | Minimum | Notes |
|---|---|---|
| CPU / RAM / disk | Intel 6th gen or Ryzen, 8 GB (16 GB better), SSD 128 GB+ | A second disk is useful for fast restore |
| GPU | One card with **2+ outputs** (HDMI/DP/DVI), e.g. GTX 1050 / RX 560 or newer | iGPU with 2 outputs is enough to start |
| Monitors | 2–3 (one 120/144 Hz if possible) | Or one monitor + an HDMI dummy plug |
| Input / audio | 2–3 USB keyboards and mice, 1–2 USB headsets | For seat tests |
| Network | Ethernet to the main PC | For the kernel debugger (KDNET) |

## Step 1 – Install (about 1 hour)

1. **BIOS/UEFI:** turn **Secure Boot off** (test-signed drivers need it), turn virtualization on, set
   "boot from USB". Write down the GPU model and outputs.
2. **Windows Server 2025** (Desktop Experience) from the same ISO as the VM (`iso\WindowsServer2025_Eval.iso`).
   Use [Rufus](https://rufus.ie) or the Media Creation tool to make a USB stick.
3. Install the **GPU driver** from NVIDIA/AMD/Intel. Check that every monitor works and **extend** the desktop.
4. Network: plug the Ethernet cable. Note the test PC IP (`ipconfig`) and the main PC IP on the same network.
5. Copy the dev scripts: on the main PC copy `setup\dev\` and `tools` builds to a USB stick → `C:\MultiSeatDev` on the test PC.

## Step 2 – Make it a test machine

Inside the test PC, PowerShell as Administrator:

```powershell
cd C:\MultiSeatDev
# Does this network card support kernel debugging? (kdnet.exe is in the WDK: Debuggers\x64)
.\kdnet.exe
.\Enable-DriverTesting.ps1 -HostIp <main PC IP>     # test signing + network debugging + full dumps
Restart-Computer
```

- `kdnet.exe` must say the network card is supported. If not, use a cheap supported USB/PCIe network card
  or a USB 3 debug cable (see Microsoft "Setting up KDNET network kernel debugging automatically").
- Allow UDP port 50000 on the main PC firewall when WinDbg asks.

## Step 3 – Backup point (instead of a VM checkpoint)

The test PC has no Hyper-V checkpoints, so make a system image before any experiment:

```powershell
Install-WindowsFeature Windows-Server-Backup
wbadmin start backup -backupTarget:D: -allCritical -quiet     # D: = second disk or USB disk
```

Restore: boot the Server ISO → Repair → System Image Recovery.

## Step 4 – Check the debugger

On the main PC:

```powershell
& "${env:ProgramFiles(x86)}\Windows Kits\10\Debuggers\x64\kd.exe" -k net:port=50000,key=1.2.3.4
```

Restart the test PC; `Connected to target` must appear. **Phase 0 for the test PC is done.**

## Step 5 – Baseline (before ASTER)

Run on the test PC, as Administrator, in the console session:

```powershell
C:\MultiSeatDev\Collect-SeatInfo.ps1 -Label baseline
```

Then attach WinDbg and run the commands in [setup/dev/windbg/observe.txt](../setup/dev/windbg/observe.txt).
Save the log (`.logopen`). This shows a clean Windows: `!chkimg` should report **0 errors**.

## Step 6 – Observe ASTER (black box, rule 4)

1. Make a backup (Step 3). Install the **ASTER trial**. Set up 2 seats on 2 outputs of the same GPU.
2. Collect from Windows' point of view:
   - console: `Collect-SeatInfo.ps1 -Label aster-console`
   - inside seat 2 (sign in there, run as Administrator): `Collect-SeatInfo.ps1 -Label aster-seat2`
   - `SeatProbe.exe` in both: session type, GPU, outputs, DWM fps.
3. Attach WinDbg and run `observe.txt` again. The difference to the baseline answers:
   - Which Windows functions does ASTER change (`!chkimg` on dxgkrnl / win32k)?
   - Which drivers, filters and callbacks does it add?
   - How do seat 2's session and its monitor look inside the kernel?
4. Write the findings in `docs/research/aster-observations.md` as **"what Windows does"** – never copy ASTER code.
   Disassembling ASTER's driver only if this is not enough, and after checking ASTER's licence (rule 4).
5. Restore the backup before writing our own driver.

## Step 7 – Our own prototype

Goal of the first prototype: **one extra session shows on output 2 of the same GPU at full refresh rate.**
Plan it from the Step 6 notes. Input already works (`drivers/MultiSeatInput`).

## Safety rules on the test PC

- Make a backup before every new driver build.
- Never put personal accounts or files on the test PC.
- Never copy test-signed drivers to any other PC.
