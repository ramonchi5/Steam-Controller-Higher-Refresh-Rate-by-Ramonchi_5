# Steam Controller Higher-Refresh-Rate (750–1000 Hz OpenPuck + Steam Input 1000 Hz Suite)

> **VIBE CODED with Google Gemini Antigravity.**  
> Uses a modified high-refresh-rate build of [OpenPuck](https://github.com/safijari/openpuck) on an external **nRF52840 USB Dongle** (such as the **GeeekPi / Makerdiary nRF52840 MDK USB Dongle** or **Pro Micro / SuperMini nRF52840**) plus a **1000 Hz Steam Input & 0.5 ms Windows Kernel Timer** configuration to triple the wireless refresh rate of the **2nd-Gen Steam Controller ("Ibex" / "Triton")** from the stock **250 Hz (`4.0 ms`)** to **~750 Hz (`~1.33 ms` average, `1.00 ms` median)** of **100% genuine, zero-duplicate input frames**.

---

## Table of Contents

1. [Why This Exists (And Why ~750 Hz Real vs. 1000 Hz Poll)](#1-why-this-exists-and-why-750-hz-real-vs-1000-hz-poll)
2. [What Goes on the Dongle vs. What Goes on the PC?](#2-what-goes-on-the-dongle-vs-what-goes-on-the-pc)
3. [Supported nRF52840 Dongles & Which `.uf2` File to Use](#3-supported-nrf52840-dongles--which-uf2-file-to-use)
4. [Part 1: Flashing the Dongle & Pairing Your Steam Controller](#4-part-1-flashing-the-dongle--pairing-your-steam-controller)
5. [Part 2: Making Steam Input Permanently Refresh at 1000 Hz (PC)](#5-part-2-making-steam-input-permanently-refresh-at-1000-hz-pc)
6. [Nintendo Switch / Switch 2, Xbox, PlayStation & Lizard Mode](#6-nintendo-switch--switch-2-xbox-playstation--lizard-mode)
7. [Live Benchmarking & Verification](#7-live-benchmarking--verification)
8. [Credits, Licenses & Legal Notices](#8-credits-licenses--legal-notices)

---

## 1. Why This Exists (And Why ~750 Hz Real vs. 1000 Hz Poll)

### The Stock Bottleneck (`250 Hz` / `4.0 ms`)
By default, a wireless 2nd-Gen Steam Controller is bottlenecked at three layers:
1. **Wireless Puck Poll Rate:** Stock pucks (and default OpenPuck builds) poll the controller every **`4000 us` (`250 Hz`)**.
2. **Steam Input Translation Rate:** Even if a controller sends faster USB reports, Steam Input's internal translation loop defaults to **`2 ms` (`500 Hz`)** unless explicitly configured to `1 ms` (`1000 Hz`).
3. **Windows Kernel Sleep Timer:** Windows 10/11 defaults to a `15.625 ms` scheduler tick, and Windows 11 throttles background helper processes (`EcoQoS`) when a game runs fullscreen unless high-resolution `0.500 ms` (`2000 Hz`) kernel timers and process priorities are locked.

In fast action games requiring **frame-perfect inputs at 60 FPS** (`16.67 ms` per frame — such as frame-perfect jumps/deflects in *Sekiro*), a `4.0 ms` wireless polling interval means an input pressed near the end of a frame has up to a **24% chance (`4.0 / 16.67`)** of "missing the bus" and landing on the next frame.

### Why Our 1000 Hz Poll Delivers `~750 Hz` of True Controller Frames (Zero Fake Duplicates!)
- Our firmware configures the nRF52840 dongle to poll the Steam Controller over 2.4 GHz Enhanced ShockBurst (ESB) at **1000 Hz (`1000 us`)** and stream over USB at **1000 Hz (`bInterval = 1 ms`)**.
- Inside the Steam Controller itself, Valve's internal sensor/ADC firmware generates new input state packets at a hardware ceiling of roughly **`~750–845 Hz`**.
- **No Fake Padding:** Rather than padding repeated packets up to 1000 Hz (which corrupts Steam Input's gyro/trackpad velocity integration and makes mouse-like gyro feel stuttery), our firmware preserves OpenPuck's on-board **byte-level content deduplicator** (`g_fwdContentDedup`) and fixes a high-rate USB `0x79` slot-theft bug in [`puck_hid.cpp`](firmware/openpuck-highrate/OpenPuck/puck_hid.cpp).
- **Verified Result:** Every single one of the **`~750 Hz` (`1.006 ms` median, `1.33 ms` average)** `0x42` input reports delivered to Windows contains a **genuinely new 16-bit ADC / sensor sample (`0%` duplicate frames)** — cutting worst-case wireless sampling delay by **3×** (`4.0 ms -> ~1.33 ms`) while keeping **Gyro, Trackpads, Haptics, Battery %, and OBS Gamepad Viewer working identically to the official Valve Puck**.

---

## 2. What Goes on the Dongle vs. What Goes on the PC?

| Component | Where Does It Live? | Required? | What Does It Do? |
| :--- | :--- | :--- | :--- |
| **`.uf2` Firmware** (`firmware/releases/*.uf2`) | **100% on the USB Dongle** | **Yes** | Runs the 1000 Hz 2.4 GHz radio poll, 1000 Hz USB HID interface, wireless pairings, Lizard mode, and console emulation (Switch / Switch 2 / Xbox / PS). **Zero files are needed on the host device once flashed.** |
| **Steam Input `1 ms` Poll Setting** (`localconfig.vdf`) | **On your PC** (inside Steam's config file) | **Yes (on PC)** | Tells Steam Input to translate controller inputs at **1000 Hz (`1 ms`)** instead of its default **500 Hz (`2 ms`)**. Can be set manually in 30 seconds with Notepad or via our 1-click `.cmd` script. |
| **Windows 0.5 ms Timer & Priority Booster** (`Enable-Persistent-1000Hz.cmd`) | **On your Windows PC** (Optional) | **Optional (Recommended on Windows 11)** | Locks the Windows kernel timer to `0.500 ms` (`2000 Hz`) and keeps `steam.exe` at `High` priority with `EcoQoS` disabled so Windows 11 never throttles Steam Input while a game is in the foreground. |

> **Using the dongle on another PC, Steam Deck, or Nintendo Switch / Nintendo Switch 2?**  
> Nothing from this repository needs to be installed on that device — all wireless pairings, 1000 Hz radio polling, and controller modes live inside the USB dongle's flash memory.

---

## 3. Supported nRF52840 Dongles & Which `.uf2` File to Use

All pre-built firmware files are located in [`firmware/releases/`](firmware/releases/):

### Option A: GeeekPi / Makerdiary nRF52840 MDK USB Dongle *(Recommended — Case Included, Dual Hardware Crystal)*
- **First-Time Install (Coming from Factory Firmware):**
  - Use **[`firmware/releases/OpenPuck-MDK-FirstInstall-1000Hz.uf2`](firmware/releases/OpenPuck-MDK-FirstInstall-1000Hz.uf2)** (`783 KB`)
  - *Why?* Factory Makerdiary/GeeekPi MDK dongles ship with an older Nordic `S132 v5.1.0` SoftDevice in `INFO_UF2.TXT`. This one-time UF2 upgrades the SoftDevice region (`0x00000..0x25FFF`) to `S140 v6.1.1` and installs the `1000 Hz` OpenPuck firmware (`0x26000..0x60100`) in a single drag-and-drop while keeping the factory UF2 bootloader intact.
- **Updating an Already-Flashed MDK Dongle (Preserves Your Controller Pairing!):**
  - Use **[`firmware/releases/OpenPuck-MDK-1000Hz.uf2`](firmware/releases/OpenPuck-MDK-1000Hz.uf2)** *(Primary 1000 Hz poll)* or **[`firmware/releases/OpenPuck-MDK-500Hz.uf2`](firmware/releases/OpenPuck-MDK-500Hz.uf2)** *(500 Hz fallback for heavy RF congestion)*.
  - These app-only UF2s only write `0x26000..0x60100` and leave `InternalFS` (`0xED000..0xF3FFF`, where your `bonds.bin` controller pairing is stored) untouched.

### Option B: Pro Micro nRF52840 / SuperMini nRF52840 / nice!nano v2
- **First-Time Install or Update:**
  - Use **[`firmware/releases/OpenPuck-1Ctrl-1000Hz.uf2`](firmware/releases/OpenPuck-1Ctrl-1000Hz.uf2)** *(Primary 1000 Hz poll)*
  - Or **[`firmware/releases/OpenPuck-1Ctrl-500Hz.uf2`](firmware/releases/OpenPuck-1Ctrl-500Hz.uf2)** *(500 Hz fallback)*
  - *(Pro Micro / SuperMini boards already ship with `S140 v6.1.1` in their Adafruit UF2 bootloader, so they use the standard UF2 directly.)*

---

## 4. Part 1: Flashing the Dongle & Pairing Your Steam Controller

### Step 1: Enter UF2 Bootloader Mode & Flash (15 Seconds)
1. **Enter UF2 Mode:**
   - **GeeekPi / Makerdiary nRF52840 MDK USB Dongle:** Hold the physical button on the dongle **while plugging it into your PC's USB port** (or if it already has OpenPuck installed, click **UF2 DFU** in the [OpenPuck WebUSB Configurator](https://safijari.github.io/openpuck/)). The LED will turn green and a USB drive named **`UF2BOOT`** will appear in Windows Explorer.
   - **Pro Micro / SuperMini nRF52840:** Plug the board in via USB-C. If a drive (`NICENANO` / `FTHR840BOOT` / `UF2BOOT`) does not appear automatically, quickly short `RST` to `GND` twice.
2. **Drag and Drop the `.uf2` File:**
   - Copy the matching `.uf2` file from [`firmware/releases/`](firmware/releases/) (see [Section 3](#3-supported-nrf52840-dongles--which-uf2-file-to-use)) onto the `UF2BOOT` USB drive.
   - As soon as the copy finishes, the drive will automatically unmount and the dongle will reboot as **`Steam Controller Puck`** (`VID_28DE&PID_1304`). *(Note: In normal Steam mode on the GeeekPi MDK, the LED stays off during normal operation and only lights up in bootloader/wake diagnostic states.)*

### Step 2: Pair Your Steam Controller (Keep Your Official Puck Paired too!)
The 2nd-Gen Steam Controller can store **two separate wireless puck pairings** simultaneously (`Slot 1 = RB + A + Steam`, `Slot 2 = LB + A + Steam`):

1. Open **Steam** on your PC and make sure your flashed **nRF52840 dongle** is plugged in.
2. Go to **Steam Settings -> Controller**. Under **Steam Controller Adapters**, you will see **`Steam Controller Puck`**.
3. Click **Add Controller** (at the top of `Steam Settings -> Controller`). Steam will prompt you to connect your Steam Controller via USB to pair.
4. *(Optional Dual-Puck Tip)* If you want to keep your original Valve Puck paired on Slot 1, turn the controller off (`Steam + Y`), hold **`LB + A + Steam`** to boot into Wireless Slot 2, and then plug the controller into your PC using a **USB-C data cable**.
5. Steam will immediately bind the controller to your new OpenPuck dongle.
6. **Unplug the USB-C cable from the controller.** Your controller will now connect wirelessly to the high-refresh-rate dongle!

---

## 5. Part 2: Making Steam Input Permanently Refresh at 1000 Hz (PC)

> **CRITICAL:** By default, Steam Input's translation layer runs at **`2 ms` (`500 Hz`)**. Even when your dongle delivers `~750–1000 Hz` over USB, Steam Input will downsample translated game inputs to `500 Hz` unless you set `"SteamController_Poll_Rate" "1"`.

You can configure this on your PC using **either** the automated 1-click tool **or** a manual text edit (no files needed on your PC).

### Method A: Manual Configuration (Zero PC Files Needed)
If you only want to use the dongle and do not want any files or background tools from this repo on your PC:

1. **Completely exit Steam** (`Steam -> Exit`, and wait 10 seconds for `steam.exe` to close — Steam overwrites its config file on exit if you edit it while Steam is running).
2. Open File Explorer and navigate to:
   ```text
   C:\Program Files (x86)\Steam\userdata\<YourSteamAccountID>\config\
   ```
3. Open **`localconfig.vdf`** in Notepad (or VS Code).
4. Search for `"SteamController_Poll_Rate"`:
   - If `"SteamController_Poll_Rate"` already exists, change its value from `"2"` to **`"1"`**:
     ```vdf
     "SteamController_Poll_Rate"		"1"
     ```
   - If it does not exist yet, find the `"System"` block (or `"controller_settings"` block) inside `localconfig.vdf` and add:
     ```vdf
     "SteamController_Poll_Rate"		"1"
     ```
5. Save `localconfig.vdf` and launch Steam. *(You can also add `+controller_rate 1000` to your Steam shortcut target as an extra guarantee.)*

### Method B: Automated 1-Click Windows Suite (`Enable-Persistent-1000Hz.cmd`)
If you are on Windows 10/11 and want both **1000 Hz Steam Input** and **0.500 ms Windows Kernel Timer + Anti-EcoQoS Priority Boosting** (requires [.NET 10 SDK/Runtime](https://dotnet.microsoft.com/download)):

1. Double-click **[`Enable-Persistent-1000Hz.cmd`](Enable-Persistent-1000Hz.cmd)** in the repository root.
2. This automatically:
   - Gracefully closes Steam, backs up `localconfig.vdf`, sets `"SteamController_Poll_Rate" "1"` (`1000 Hz / 1000 us`), and relaunches Steam with `+controller_rate 1000`.
   - Registers and starts the silent, windowless startup agent (`sc-boost-agent.exe` in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, consuming `<0.01%` CPU and `~4 MB` RAM), which locks the Windows kernel timer (`NtSetTimerResolution`) to **`0.500 ms` (`2000 Hz`)** and elevates `steam.exe` to `High` priority with Windows 11 `EcoQoS` timer throttling disabled.
3. **To Revert Everything at Any Time:** Double-click **[`Disable-All-Optimizations.cmd`](Disable-All-Optimizations.cmd)** to remove the startup entry, stop the background agent, and restore Steam Input to Valve's default `2 ms` (`500 Hz`).

---

## 6. Nintendo Switch / Switch 2, Xbox, PlayStation & Lizard Mode

Because this firmware is built on full **OpenPuck**, **all console personalities and Lizard Mode work right out of the box** and live entirely on the dongle:

### Switching Modes with Controller Button Chords
Hold **all 4 back paddles (`L4 + R4 + L5 + R5`)** plus a face button at any time:

| Button Combo | Mode | Details |
| :--- | :--- | :--- |
| **`L4 + R4 + L5 + R5 + A`** | **Steam Controller Mode** *(Default)* | Native `28DE:1304` Steam Controller Puck (`~750 Hz` genuine wireless frames, full Steam Input, Gyro, Trackpads, Paddles, Haptics, Battery). |
| **`L4 + R4 + L5 + R5 + B`** | **Lizard Mode** | Hardware mouse + keyboard desktop mode (works even in Windows UAC prompts, Task Manager, or when Steam is closed). |
| **`L4 + R4 + L5 + R5 + X`** | **Xbox 360 Mode** | Presents a wired XInput / Xbox 360 controller for PC Game Pass or non-Steam games. |
| **`L4 + R4 + L5 + R5 + Y`** | **Nintendo Switch / Switch 2 Pro Controller** | Emulates an official **Nintendo Switch Pro Controller (`057E:2009`)** with **full Gyro + HD Rumble/Haptics + configurable back paddles**. |

*(Additional modes — **PS5 DualSense**, **PS4 DualShock 4**, **PS3 Sixaxis**, **Original Xbox**, **DirectInput Dual-Joystick**, and **SInput SDL3** — can be selected anytime via the browser-based [OpenPuck WebUSB Configurator](https://safijari.github.io/openpuck/) while in Steam mode.)*

### Using on Nintendo Switch & Nintendo Switch 2
1. Pair your Steam Controller to the dongle on a PC in Steam mode first (one time only).
2. Switch the dongle into **Switch Pro Mode** by holding **`L4 + R4 + L5 + R5 + Y`** on the controller.
3. On your **Nintendo Switch** or **Nintendo Switch 2**, open **System Settings -> Controllers and Sensors** and turn **ON** **`Pro Controller Wired Communication`**.
4. Plug the USB dongle into the Switch / Switch 2 dock (or top/bottom USB-C port with an adapter).
5. How our 1000 Hz RF poll helps on Switch / Switch 2: the dongle still feeds the console standard `0x30` Pro Controller USB reports at the exact cadence and 3-sample IMU integration scale the Switch OS expects (so gyro never drifts), while sampling the controller over 2.4 GHz RF every `1 ms` so each USB report sent to the console contains the freshest possible input state!

---

## 7. Live Benchmarking & Verification

On Windows, double-click **[`Run-Benchmark.cmd`](Run-Benchmark.cmd)** (or run `sc-refresh benchmark --seconds 5`) while continuously moving both thumbsticks/trackpads. It opens a non-exclusive HID stream alongside Steam and measures:

```text
Interface : Wireless Controller Slot 2 (HID_PROXY_2) (PID 0x1304, Usage 0xFF00:0x0001)
Duration  : 4.991 s | State RID: 0x42 | Sample[0..16]: 422B0000000000000000CDFF74036801
Reports   : 3731 total (3725 Triton state 0x42/0x45/0x47, 6 aux status 0x43/0x79/0x7B)
  -> USB HID Delivery Rate      :  747.32 Hz
  -> State Report Delivery Rate :  746.12 Hz  (every 1.340 ms)
  -> True Unique Sequence Rate  :  746.12 Hz  (3725 unique seqs, 0 dupes, 495 sequence jumps)
  -> Unique Sensor Payload Rate :  746.12 Hz  (3725 distinct input/ADC payloads)
  -> State Frame Timing (ms)    : min=0.028 | p50=1.006 | avg=1.340 | p95=2.036 | p99=3.010 | max=4.370 | jitter(sd)=0.531
```

* **2.4 GHz Wireless Mouse Coexistence:** Tested live alongside a 2.4 GHz wireless gaming mouse dongle (`VXE R1`) with **`0` CRC errors/sec** and automatic clean channel selection.

---

## 8. Credits, Licenses & Legal Notices

We take open-source licensing and proper attribution seriously. Please see **[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md)** and **[`LICENSE`](LICENSE)** for full legal texts:

* **OpenPuck Firmware ([safijari/openpuck](https://github.com/safijari/openpuck)):** Created by **Safi Jari ([@safijari](https://github.com/safijari))** and the OpenPuck contributors. Licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)**. Our modified firmware source in [`firmware/openpuck-highrate/`](firmware/openpuck-highrate/) and the compiled `OpenPuck-*.uf2` application binaries in [`firmware/releases/`](firmware/releases/) are distributed under **AGPL-3.0**.
* **Makerdiary / GeeekPi MDK Board Support:** Based on OpenPuck [PR #268](https://github.com/safijari/openpuck/pull/268) by **Edgar ([@edgar-d-hdez](https://github.com/edgar-d-hdez))** and contributors.
* **Vendored Libraries in OpenPuck:**
  * [`libxsm3`](https://github.com/InvoxiPlayGames/libxsm3) by **InvoxiPlayGames** and **oct0xor** (**LGPL-2.1-or-later**)
  * [`ExCrypt`](https://github.com/emoose/ExCrypt) by **emoose** (**BSD 3-Clause License**)
* **Arduino Core & USB Stack:** [Adafruit nRF52 Arduino Core](https://github.com/adafruit/Adafruit_nRF52_Arduino) and [TinyUSB](https://github.com/hathach/tinyusb) by **Adafruit Industries / Ha Thach** (**MIT / LGPL-2.1 / BSD-3-Clause**).
* **Nordic Semiconductor S140 SoftDevice (`v6.1.1`):** Included in `s140_nrf52_6.1.1.hex` and `OpenPuck-MDK-FirstInstall-1000Hz.uf2` (`0x00000..0x25FFF`) under the **Nordic Semiconductor 5-Clause License** solely for use on genuine Nordic Semiconductor nRF52840 hardware.
* **Original Windows Host Tools (`src/`, `scripts/`, `*.cmd`):** Created by **Ramonchi_5 ([@ramonchi5](https://github.com/ramonchi5))** with **Google Gemini Antigravity** and licensed under the **MIT License**.
* **Trademark Disclaimer:** Not affiliated with, endorsed by, or sponsored by Valve Corporation, Nintendo Co., Ltd., Microsoft Corporation, or Sony Interactive Entertainment Inc.
