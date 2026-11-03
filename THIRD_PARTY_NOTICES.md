# Third-Party Notices, Credits & License Attributions

This repository combines **original Windows host utilities** with a **high-refresh-rate derivative of the open-source OpenPuck firmware**. Below are the required credits, copyright notices, license terms, and trademark disclaimers for all upstream projects and components used.

---

## 1. OpenPuck Firmware (`firmware/openpuck-highrate/` & `firmware/releases/OpenPuck-*.uf2`)

* **Upstream Project:** [safijari/openpuck](https://github.com/safijari/openpuck)
* **Primary Author:** **Safi Jari ([@safijari](https://github.com/safijari))** and the OpenPuck community contributors
* **Makerdiary / GeeekPi nRF52840 MDK USB Dongle Support:** Adapted from upstream OpenPuck [PR #268](https://github.com/safijari/openpuck/pull/268) (`OPK_BOARD_MDK_USB_DONGLE`, `tools/make_mdk_first_install.py`) by **Edgar ([@edgar-d-hdez](https://github.com/edgar-d-hdez))** and contributors
* **License:** **GNU Affero General Public License v3.0 (AGPL-3.0)**
  * Full license text: [`firmware/openpuck-highrate/LICENSE`](firmware/openpuck-highrate/LICENSE) and [`LICENSE`](LICENSE)
  * Complete corresponding source code for all distributed `.uf2` binaries is included directly in [`firmware/openpuck-highrate/`](firmware/openpuck-highrate/), along with exact build scripts in [`scripts/Build-MDK-FirstInstall.ps1`](scripts/Build-MDK-FirstInstall.ps1) and [`scripts/Build-OpenPuckFirmware.ps1`](scripts/Build-OpenPuckFirmware.ps1).

### Summary of Modifications Made to OpenPuck in This Repository
1. **1000 Hz RF & USB Polling (`POLL_US_DEFAULT = 1000u`, `bInterval = 1 ms`):** Re-tuned the 2.4 GHz Enhanced ShockBurst (ESB) poll loop and adaptive RX window (`680 us` at 1000 Hz, `950 us` at 500 Hz) in [`rf_link.cpp`](firmware/openpuck-highrate/OpenPuck/rf_link.cpp).
2. **1-Controller Exclusive RF Air-Time Mode (`g_singleCtrlMode`):** Suppresses periodic `ch2` discovery scans (`800 us` per slot) and cold-slot probing while a bonded controller is actively streaming, dedicating 100% of radio air-time to the active controller.
3. **1000 Hz Haptic/Settings Relay + Poll Coalescing:** Harvests the controller's `0xF1` input state reply directly from `0xE3` haptic/settings relay frames so trackpad haptic bursts do not steal poll windows.
4. **High-Rate `0x79` USB Connection-State Fix & 16-Deep TX Ring (`TX_RING_N = 16`):** Fixed [`SteamPuckController::task()`](firmware/openpuck-highrate/OpenPuck/puck_hid.cpp) and [`SteamPuckController::onAuxReport()`](firmware/openpuck-highrate/OpenPuck/puck_hid.cpp) in [`puck_hid.cpp`](firmware/openpuck-highrate/OpenPuck/puck_hid.cpp) to gate on `USBDevice.mounted()` rather than `hid[s].ready()`, eliminating a ~304 Hz `0x79` connection-report storm when the USB IN endpoint is busy at 500–1000 Hz, and expanded `TX_RING_N` to `16` in [`usb_tx.cpp`](firmware/openpuck-highrate/OpenPuck/usb_tx.cpp).
5. **Makerdiary MDK Safe Reset Fallback:** Added a 10 ms `NVIC_SystemReset()` fallback after driving `P0.16` low in [`modeSwitchReboot()`](firmware/openpuck-highrate/OpenPuck/OpenPuck.ino) for factory Makerdiary UF2 bootloaders.

---

## 2. Vendored Libraries Inside OpenPuck (`libxsm3` & `ExCrypt`)

OpenPuck's Xbox 360 authentication module uses vendored code in [`firmware/openpuck-highrate/OpenPuck/src/libxsm3/`](firmware/openpuck-highrate/OpenPuck/src/libxsm3/):

* **`libxsm3`** ([InvoxiPlayGames/libxsm3](https://github.com/InvoxiPlayGames/libxsm3))
  * Copyright (C) 2022–2023 **InvoxiPlayGames**
  * Copyright (C) 2013 **oct0xor** ([xbox_security_method_3](https://github.com/oct0xor/xbox_security_method_3))
  * Additional credits: **sanjay900**, **fffaraz/cppDES**, **mohaps/TinySHA1**
  * **License:** **GNU Lesser General Public License v2.1 or later (LGPL-2.1-or-later)**
  * Full notice and license text: [`firmware/openpuck-highrate/OpenPuck/src/libxsm3/NOTICE.md`](firmware/openpuck-highrate/OpenPuck/src/libxsm3/NOTICE.md) and [`firmware/openpuck-highrate/OpenPuck/src/libxsm3/LICENSE.txt`](firmware/openpuck-highrate/OpenPuck/src/libxsm3/LICENSE.txt)
* **`ExCrypt`** ([emoose/ExCrypt](https://github.com/emoose/ExCrypt))
  * Copyright (c) 2021, **emoose**
  * **License:** **BSD 3-Clause License** (reproduced in [`firmware/openpuck-highrate/OpenPuck/src/libxsm3/NOTICE.md`](firmware/openpuck-highrate/OpenPuck/src/libxsm3/NOTICE.md))

---

## 3. Adafruit nRF52 Arduino Core, TinyUSB & LittleFS

The firmware is compiled against the [Adafruit nRF52 Arduino Core](https://github.com/adafruit/Adafruit_nRF52_Arduino) (`v1.7.0`):

* **Adafruit nRF52 Arduino Core & InternalFileSystem:** Copyright (c) Adafruit Industries / Ha Thach (`hathach`), licensed under the **MIT License** (with Arduino core components under **LGPL-2.1** and Nordic SDK headers under **BSD-3-Clause / Apache-2.0**).
* **TinyUSB (`Adafruit_TinyUSB_Arduino`):** Copyright (c) Ha Thach (`hathach`), licensed under the **MIT License**.
* **LittleFS:** Copyright (c) ARM Limited, licensed under the **BSD 3-Clause License**.

---

## 4. Nordic Semiconductor S140 SoftDevice (`s140_nrf52_6.1.1`)

The files [`firmware/releases/s140_nrf52_6.1.1.hex`](firmware/releases/s140_nrf52_6.1.1.hex) and the address range `0x000000..0x025FFF` inside [`firmware/releases/OpenPuck-MDK-FirstInstall-1000Hz.uf2`](firmware/releases/OpenPuck-MDK-FirstInstall-1000Hz.uf2) contain the unmodified **Nordic Semiconductor S140 BLE SoftDevice v6.1.1** and Master Boot Record (MBR), required by the Adafruit nRF52 Arduino Core when provisioning factory Makerdiary/GeeekPi nRF52840 MDK USB Dongles (which ship from the factory with S132 v5.1.0).

* Copyright (c) 2010–2018, **Nordic Semiconductor ASA**. All rights reserved.
* Redistributed in binary form under the **Nordic Semiconductor SoftDevice 5-Clause License**:
  1. Redistribution and use in binary form, without modification, are permitted provided that the SoftDevice is used **solely in conjunction with a genuine Nordic Semiconductor ASA integrated circuit** (such as the nRF52840).
  2. Any software provided in binary form under this license must not be reverse engineered, decompiled, modified, or disassembled.
  3. THIS SOFTWARE IS PROVIDED BY NORDIC SEMICONDUCTOR ASA "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY, NONINFRINGEMENT, AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.

*(Note: The app-only `.uf2` images — `OpenPuck-MDK-1000Hz.uf2`, `OpenPuck-MDK-500Hz.uf2`, `OpenPuck-1Ctrl-1000Hz.uf2`, and `OpenPuck-1Ctrl-500Hz.uf2` — contain only `0x26000..0x60100` application code and do not contain the Nordic SoftDevice.)*

---

## 5. Original Windows Host Utilities (`src/` & `scripts/`)

The Windows C# host utilities ([`src/SteamControllerRefreshRate/`](src/SteamControllerRefreshRate/), [`src/SteamControllerBoostAgent/`](src/SteamControllerBoostAgent/)), PowerShell build scripts ([`scripts/`](scripts/)), and batch launchers (`*.cmd`) are original works created for this repository by **Ramonchi_5 ([@ramonchi5](https://github.com/ramonchi5))** with **Google Gemini Antigravity** and are licensed under the **MIT License** (see [`LICENSE`](LICENSE)).

---

## 6. Trademark & Affiliation Disclaimer

This project is an independent, community-developed open-source hardware/software project and is **not affiliated with, endorsed by, or sponsored by Valve Corporation, Nintendo Co., Ltd., Microsoft Corporation, or Sony Interactive Entertainment Inc.**

* **Steam**, **Steam Controller**, **Steam Deck**, and **Steam Input** are trademarks and/or registered trademarks of **Valve Corporation**.
* **Nintendo Switch**, **Nintendo Switch 2**, and **Nintendo Switch Pro Controller** are trademarks of **Nintendo Co., Ltd.**
* **Xbox** and **Xbox 360** are trademarks of **Microsoft Corporation**.
* **PlayStation**, **DualShock**, and **DualSense** are trademarks of **Sony Interactive Entertainment Inc.**

No proprietary firmware or source code belonging to Valve Corporation is included in this repository.
