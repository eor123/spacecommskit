# SpaceCommsKit — OpenLST Explorer Kit

> **Flight-heritage RF communications technology. Now in your lab.**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-blue)](https://github.com/eor123/spacecommskit)
[![Hardware: CC1110](https://img.shields.io/badge/Hardware-CC1110%20437MHz-green)](https://github.com/eor123/spacecommskit)

The **OpenLST Explorer Kit** is a complete RF communications development kit built on the open-source [OpenLST](https://github.com/eor123/openlst) radio platform — the same design heritage as Planet Labs' Dove satellite Low-Speed Transceiver, which has accumulated over **200 cumulative years of on-orbit data** across more than 150 satellites.

This repository contains everything you need to get started:

- ✅ Windows ground station application (C# .NET 8)
- ✅ Raspberry Pi Pico firmware (MicroPython)
- ✅ Windows installer (.exe)
- ✅ User Guide and Developer Guide (PDF)

---

## What is the OpenLST Explorer Kit?

Two CC1110-based radio boards operating at **437 MHz** in the amateur 70cm band, with a full Windows ground station application for commanding, monitoring, and remote imaging over RF.

```
Windows Ground Station
        │
        │ COM5 · 115200 baud · USB-Serial
        │
  Board 0001 (CC1110 · Ground Station Radio)
        │
        │ RF 437 MHz · 7.4 kbaud · 2-FSK + FEC
        │
  Board 0004 (CC1110 · Remote Radio)
        │
        │ UART0 · 115200 baud
        │
  Raspberry Pi Pico (Camera + SD Card)
```

**Proven capabilities:**
- Real-time telemetry over RF
- Remote temperature sensing
- Remote JPEG image capture (Arducam OV2640)
- Image file transfer over RF (chunked protocol)
- OTA firmware updates
- Custom command framework (extensible)

---

## Repository Structure

```
spacecommskit/
├── ground-station/          C# .NET 8 WinForms ground station (VS2022)
│   ├── OpenLstGroundStation.sln
│   └── OpenLstGroundStation/
│       ├── MainForm.cs      All UI — tabs, controls, event handlers
│       ├── OpenLstProtocol.cs  Packet framing, parsing, AES signing
│       ├── CustomCommand.cs    JSON-persistent custom commands
│       ├── AppLogger.cs        Daily log file writer
│       └── Program.cs
├── installer/               Windows installer
│   ├── OpenLST_Ground_Station.iss         Inno Setup 6 script
│   └── OpenLST_Ground_Station_Setup_v1.0.0.exe  Ready-to-run installer
├── pico/                    Raspberry Pi Pico MicroPython firmware
│   ├── main.py              Main pipeline — ESP framing, camera, SD, commands
│   └── sdcard.py            MicroPython SD card SPI driver
├── docs/                    Documentation
│   ├── OpenLST_Explorer_Kit_User_Guide.pdf
│   └── OpenLST_Explorer_Kit_Developer_Guide.pdf
└── tools/                   Third-party tool links and notes
    └── README.md
```

---

## Quick Start

### 1. Install the Ground Station

Download and run:
```
installer\OpenLST_Ground_Station_Setup_v1.0.0.exe
```
No prerequisites — the .NET 8 runtime is bundled. Requires Windows 10 (1809) or later.

### 2. Connect Board 0001

```
USB-Serial Adapter TX  →  Board 0001 P0_5 (UART1 RX)
USB-Serial Adapter RX  ←  Board 0001 P0_4 (UART1 TX)
GND                    —  GND
```

Power board 0001 from an external 5V supply — not USB alone.

### 3. Connect and Launch

1. Open **OpenLST Ground Station** from the Start menu
2. Select your COM port, set HWID to `0001`, click **Connect**
3. Go to the **Commands** tab and click **Get Telem**
4. You should see a green ✓ response within 300ms

---

## Pico Imaging Setup

For the full remote imaging pipeline, you need:
- Raspberry Pi Pico
- Arducam OV2640 Mini 2MP camera module
- SPI SD card adapter
- microSD card (FAT32 formatted)

**Wiring:**

| Device | Signal | Pico Pin |
|--------|--------|----------|
| Arducam OV2640 | VCC | 3.3V |
| Arducam OV2640 | MISO | GP16 |
| Arducam OV2640 | MOSI | GP19 |
| Arducam OV2640 | SCK | GP18 |
| Arducam OV2640 | CS | GP15 |
| Arducam OV2640 | SDA | GP4 |
| Arducam OV2640 | SCL | GP5 |
| SD Card | VCC | VBUS (5V) |
| SD Card | MISO | GP16 |
| SD Card | MOSI | GP19 |
| SD Card | SCK | GP18 |
| SD Card | CS | GP17 |
| Board 0004 UART0 TX (P1_5) | — | GP1 |
| Board 0004 UART0 RX (P1_4) | — | GP0 |

**Install Pico firmware:**
1. Copy `pico/sdcard.py` to the Pico filesystem via Thonny
2. Copy `pico/main.py` to the Pico filesystem as `main.py`
3. The Pico will start automatically on power-up

---

## Building from Source

### Ground Station (Windows)

Requirements: Visual Studio 2022, .NET 8 SDK

```
cd ground-station
dotnet build OpenLstGroundStation\OpenLstGroundStation.csproj -c Release
```

To publish a self-contained build:
```
dotnet publish OpenLstGroundStation\OpenLstGroundStation.csproj ^
    -c Release -r win-x64 --self-contained true
```

### Building the Installer

1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. Open `installer\OpenLST_Ground_Station.iss`
3. Press **F9** to compile
4. Installer appears in `installer\Output\`

### CC1110 Firmware

The custom firmware for board 0004 lives in the OpenLST source tree.
See the [Developer Guide](docs/OpenLST_Explorer_Kit_Developer_Guide.pdf)
for full build instructions.

Required tools:
- [SDCC](https://sdcc.sourceforge.net/) — Small Device C Compiler
- [mingw32-make](https://sourceforge.net/projects/mingw/) — GNU Make for Windows
- [SmartRF Flash Programmer](https://www.ti.com/tool/FLASH-PROGRAMMER) — TI bootloader tool

---

## Hardware

The OpenLST Explorer Kit boards are based directly on the
[OpenLST hardware design](https://github.com/eor123/openlst)
with two component substitutions for parts availability:

| Original | Replacement | Notes |
|----------|-------------|-------|
| Qorvo RFFM6403SB | Qorvo RFFM6406 | RF front-end module — pin-for-pin, same footprint |
| pSemi PE4259 | pSemi PE4250MLI-2 | RF switch — pin-for-pin, same electrical spec |

Both substitutions are direct drop-in replacements — no schematic changes required.

**Boards are hand-assembled in Tennessee, USA using components sourced
exclusively from Digi-Key, Mouser, or manufacturer direct.**

---

## Related Links

| Resource | Link |
|----------|------|
| OpenLST Hardware (forked) | https://github.com/eor123/openlst |
| Original OpenLST by Planet Labs | https://github.com/OpenLST/openlst |
| SpaceCommsKit Website | https://www.spacecommskit.com |
| Buy a Kit | https://www.spacecommskit.com/shop |
| SDCC Compiler | https://sdcc.sourceforge.net |
| SmartRF Flash Programmer | https://www.ti.com/tool/FLASH-PROGRAMMER |
| Inno Setup 6 | https://jrsoftware.org/isinfo.php |

---

## Documentation

| Document | Description |
|----------|-------------|
| [User Guide](docs/OpenLST_Explorer_Kit_User_Guide.pdf) | Step-by-step guide for operating the ground station |
| [Developer Guide](docs/OpenLST_Explorer_Kit_Developer_Guide.pdf) | Firmware extension, protocol reference, C# architecture |

---

## License

The ground station software, Pico firmware, and documentation in this
repository are released under the **GNU General Public License v3.0**
in keeping with the OpenLST project license.

The OpenLST hardware design is Copyright (C) 2018 Planet Labs Inc.,
also released under GPLv3.

See [LICENSE](LICENSE) for full terms.

---

## Acknowledgements

This project builds on the extraordinary work of the Planet Labs team
who designed and open-sourced the OpenLST radio system. The Dove LST
radio — inspiration for this design — has proven itself in one of the
most demanding RF environments possible: low Earth orbit.

Special thanks to the OpenLST contributors:
Henry Hallam, Alex Ray, Rob Zimmerman, Matt Peddie, Bryan Klofas,
Ryan Kingsbury, and the full Planet Labs team.

---

*SpaceCommsKit — Putting space-grade RF in your hands.*
