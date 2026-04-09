# SpaceCommsKit — OpenLST Explorer Kit + SCK-PBL-1 Payload Platform

> **Flight-heritage RF communications technology. Now in your lab — and in the stratosphere.**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-blue)](https://github.com/eor123/spacecommskit)
[![Hardware: CC1110](https://img.shields.io/badge/Hardware-CC1110%20915MHz-green)](https://github.com/eor123/spacecommskit)

The **OpenLST Explorer Kit** is a complete RF communications development kit built on the open-source [OpenLST](https://github.com/eor123/openlst) radio platform — the same design heritage as Planet Labs' Dove satellite Low-Speed Transceiver, which has accumulated over **200 cumulative years of on-orbit data** across more than 150 satellites.

This repository contains everything you need to get started:

- ✅ Windows ground station application (C# .NET 8) with live GPS map
- ✅ Raspberry Pi Pico firmware (MicroPython) — camera, SD, GPS, altimeter
- ✅ Windows installer (.exe)
- ✅ User Guide and Developer Guide (PDF)

---

## What is the OpenLST Explorer Kit?

Two CC1110 + CC1190-based radio boards operating at **915 MHz** ISM band (no license required), with a full Windows ground station for commanding, monitoring, live GPS tracking, and remote imaging over RF.

```
Windows Ground Station
        │  Live GPS map (Google Maps)
        │  Image file transfer
        │  OTA firmware flash
        │  Telemetry dashboard
        │
        │ COM port · 115200 baud · USB-Serial
        │
  SCK-915 Board 0001 (CC1110 + CC1190 · Ground Station Radio)
        │
        │ RF 915 MHz · CC1190 PA/LNA · +28 dBm
        │
  SCK-915 Board 0004 (CC1110 + CC1190 · Remote Radio)
        │
        │ UART0 · 115200 baud
        │
  Raspberry Pi Pico (SCK-PBL-1 Payload Board)
        │  OV2640 Camera + SD Card
        │  GPS6MV2 (NEO-6M) — live position every 10 seconds over RF
        │  BMP581 Barometric Altimeter [coming soon]
```

**Proven capabilities:**
- Real-time telemetry over RF
- Live GPS tracking — autonomous beacon every 10 seconds, plotted on Google Maps
- Remote temperature sensing
- Remote JPEG image capture (Arducam OV2640)
- Image file transfer over RF (chunked protocol)
- OTA firmware updates
- Custom command framework (extensible)
- KML flight track export for Google Earth

---

## Repository Structure

```
spacecommskit/
├── ground-station/               C# .NET 8 WinForms ground station (VS2022)
│   ├── OpenLstGroundStation.sln
│   └── OpenLstGroundStation/
│       ├── MainForm.cs           All UI — tabs, GPS map, file transfer
│       ├── OpenLstProtocol.cs    Packet framing, parsing, AES signing
│       ├── CustomCommand.cs      JSON-persistent custom commands
│       ├── AppLogger.cs          Daily log file writer
│       └── Program.cs
├── installer/
│   ├── OpenLST_Ground_Station.iss
│   └── OpenLST_Ground_Station_Setup_v1.0.0.exe
├── pico/                         Raspberry Pi Pico MicroPython firmware
│   ├── main.py                   Main pipeline — camera, SD, GPS, commands
│   ├── sdcard.py                 MicroPython SD card SPI driver
│   └── gps_test.py               Standalone GPS test and verification script
├── docs/
│   ├── OpenLST_Explorer_Kit_User_Guide.pdf
│   └── OpenLST_Explorer_Kit_Developer_Guide.pdf
└── tools/
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

Power board 0001 from an external 5V supply.

### 3. Connect and Launch

1. Open **OpenLST Ground Station** from the Start menu
2. Select your COM port, set HWID to `0001`, click **Connect**
3. Go to the **Commands** tab and click **Get Telem**
4. Green ✓ response within 300ms — you are live

---

## Pico Payload Setup (SCK-PBL-1)

### Wiring

| Device | Signal | Pico Pin |
|--------|--------|----------|
| OV2640 Camera | VCC | 3.3V |
| OV2640 Camera | MISO | GP16 |
| OV2640 Camera | MOSI | GP19 |
| OV2640 Camera | SCK | GP18 |
| OV2640 Camera | CS | GP15 |
| OV2640 Camera | SDA (I2C config) | GP4 |
| OV2640 Camera | SCL (I2C config) | GP5 |
| SD Card | MISO | GP16 |
| SD Card | MOSI | GP19 |
| SD Card | SCK | GP18 |
| SD Card | CS | GP17 |
| GPS6MV2 | TX → Pico RX | GP9 |
| GPS6MV2 | RX → Pico TX | GP8 |
| BMP581 Altimeter | SDA | GP2 |
| BMP581 Altimeter | SCL | GP3 |
| SCK-915 Board UART0 TX | → Pico RX | GP1 |
| SCK-915 Board UART0 RX | ← Pico TX | GP0 |

### Install Pico Firmware

1. Copy `pico/sdcard.py` to Pico via Thonny
2. Copy `pico/main.py` to Pico as `main.py`
3. Pico starts automatically on power-up

### Verify GPS Before Full Deploy

Run `pico/gps_test.py` directly in Thonny to verify GPS wiring and fix acquisition before running the full firmware stack. Take the module near a window — expect fix within 60–90 seconds.

```
✓ FIX ACQUIRED
  Lat:  36.058910
  Lon:  -87.384024
  Alt:  254.5 m
  Sats: 8

GPS packet that will go over RF:
  GPS:36.058910,-87.384024,254.5,8,1
```

---

## Pico Sub-Opcodes (opcode 0x20 = PICO_MSG)

| Sub-opcode | Command   | Response                                        |
|-----------|-----------|------------------------------------------------|
| `0x00`    | PING      | `PICO:ACK`                                     |
| `0x01`    | READ_TEMP | `TEMP:xx.xxC`                                  |
| `0x02`    | SNAP      | `SNAP:OK:<filename>:<bytes>`                   |
| `0x03`    | LIST      | `LIST:<file1>,<file2>,...`                     |
| `0x04`    | GET_INFO  | `INFO:<filename>:<bytes>:<chunks>`             |
| `0x05`    | GET_CHUNK | `CHUNK:<index>:<200 bytes data>`               |
| `0x06`    | DELETE    | `DEL:OK:<filename>`                            |
| `0x07`    | GET_GPS   | `GPS:<lat>,<lon>,<alt_m>,<sats>,<fix>`         |
| `0x08`    | GET_BARO  | `BARO:<hpa>,<alt_m>,<temp_c>` *(coming soon)*  |

### GPS Packet Format
```
GPS:36.058910,-87.384024,254.5,8,1
     │          │          │    │  └─ fix  (1=valid, 0=no fix)
     │          │          │    └──── satellites in use
     │          │          └───────── altitude metres MSL
     │          └──────────────────── longitude decimal degrees (negative=W)
     └─────────────────────────────── latitude decimal degrees
```

### Autonomous GPS Beacon
The Pico transmits a GPS packet **every 10 seconds** without any ground command. The ground station receives and plots these over RF automatically. No polling required during a balloon flight.

### Airborne Mode (Critical for HAB Flights)
At startup the firmware sends a **UBX CFG-NAV5 command** to configure the NEO-6M in Airborne (<1g) dynamic model, removing the CoCom 18km altitude limit. Without this the GPS goes silent above ~18km — right when the flight gets interesting.

---

## Ground Station — Tabs

| Tab             | Function                                                      |
|-----------------|---------------------------------------------------------------|
| Home            | Live telemetry — uptime, RSSI, LQI, packet counts            |
| Commands        | Standard OpenLST commands — get_telem, reboot, set_callsign  |
| Firmware        | Build (SDCC/mingw32-make), sign (CBC-MAC AES), OTA flash      |
| Terminal        | Raw command terminal                                          |
| Custom Commands | Save and send custom opcode/payload commands to Pico          |
| Files           | Browse, download, delete files from Pico SD card over RF      |
| GPS / Map       | Live GPS on Google Maps, flight track, KML export             |
| Provision       | Flash bootloader to fresh CC1110 via CC Debugger              |

### GPS / Map Tab
- Live position on **Google Maps** via GMap.NET
- Autonomous beacon plotting — map updates every 10 seconds over RF
- Flight track — cyan polyline builds as position history accumulates
- Stat panels — latitude, longitude, altitude, satellites, fix status, packet count
- **Log filter** — toggle GPS log lines on/off to keep log clean during long flights
- **Export KML** — full flight track with launch, apogee, and landing pins for Google Earth

---

## HAB Mission — High Altitude Balloon *(coming soon)*

The SCK-PBL-1 + SCK-915 stack is a complete amateur space mission platform for under $200.

```
Target altitude:  ~30km (stratosphere)
RF link:          SCK-915 @ 915MHz, +28dBm, CC1190 PA
Ground antenna:   10–13 dBi Yagi, handheld tracking
Telemetry:        GPS position every 10 seconds over RF
Altimeter:        BMP581 — ascent rate and burst detection
Camera:           OV2640 — images to SD, selectively downlinked
Recovery:         KML track exported from ground station → Google Earth
Balloon:          Kaymont 600g latex
Regulations:      FAA 14 CFR Part 101 Subpart B exemption (<6 lbs payload)
```

### Recommended Hardware

| Item | Source | Est. Cost |
|------|--------|-----------|
| SCK-915 Kit | spacecommskit.com | TBD |
| SCK-PBL-1 Board | spacecommskit.com | TBD |
| Raspberry Pi Pico W | Digi-Key / Adafruit | $6 |
| GPS6MV2 (NEO-6M breakout) | Amazon | $8 |
| BMP581 Altimeter | Digi-Key | $4 |
| OV2640 Camera Module | Amazon | $8 |
| MicroSD Card 32GB | Amazon | $8 |
| LiPo Battery 2000mAh | Amazon | $12 |
| Kaymont 600g Balloon | kaymont.com | $45 |
| Parachute 36" | highaltitudescience.com | $25 |
| 915MHz Yagi Antenna | Amazon / DIY | $15–40 |
| Foam payload enclosure | Hardware store | $5 |
| **Total** | | **~$140–170** |

---

## Building from Source

### Ground Station (Windows)

Requirements: Visual Studio 2022, .NET 8 SDK

```bash
cd ground-station
dotnet build OpenLstGroundStation\OpenLstGroundStation.csproj -c Release
```

Self-contained publish:
```bash
dotnet publish OpenLstGroundStation\OpenLstGroundStation.csproj ^
    -c Release -r win-x64 --self-contained true
```

### Building the Installer

1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. Open `installer\OpenLST_Ground_Station.iss`
3. Press **F9** to compile

### CC1110 Radio Firmware

Requirements:
- [SDCC](https://sdcc.sourceforge.net/) — Small Device C Compiler
- [mingw32-make](https://sourceforge.net/projects/mingw/) — GNU Make for Windows
- [SmartRF Flash Programmer](https://www.ti.com/tool/FLASH-PROGRAMMER) — TI bootloader tool

```bash
cd radios/openlst_437
mingw32-make openlst_437_radio
```

Output: `openlst_437_radio.hex` — flash via the ground station Firmware tab.

See the [Developer Guide](docs/OpenLST_Explorer_Kit_Developer_Guide.pdf) for full build instructions.

---

## Provisioning a New Board

First-time CC1110 setup requires a CC Debugger and SmartRF Flash Programmer:

1. Ground Station → Provision tab
2. Enter HWID (e.g. `0004`)
3. Enter 3× AES signing keys (default: `FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF`)
4. Select `openlst_437_bootloader.hex`
5. Click **◎ Detect CC Debugger** — verify connection
6. Click **▶ PROVISION BOARD**

After provisioning, all subsequent firmware updates are OTA via the Firmware tab. No CC Debugger needed.

---

## Hardware

The SCK-915 boards are based on the OpenLST hardware design with the following substitutions for parts availability:

| Original | Replacement | Notes |
|----------|-------------|-------|
| Qorvo RFFM6403SB | TI CC1190 | RF front-end — PA/LNA for 915MHz |
| pSemi PE4259 | pSemi PE4250MLI-2 | RF switch — pin-for-pin, same electrical spec |

**Boards are hand-assembled in Tennessee, USA using components sourced exclusively from Digi-Key, Mouser, or manufacturer direct.**

---

## Related Links

| Resource | Link |
|----------|------|
| OpenLST Hardware (forked) | https://github.com/eor123/openlst |
| Original OpenLST by Planet Labs | https://github.com/OpenLST/openlst |
| SpaceCommsKit Website | https://www.spacecommskit.com |
| Buy a Kit | https://www.spacecommskit.com/shop |
| Patreon (Section 7 HV Lab) | https://www.patreon.com/spacecommskit |
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

The ground station software, Pico firmware, and documentation in this repository are released under the **GNU General Public License v3.0** in keeping with the OpenLST project license.

The OpenLST hardware design is Copyright (C) 2018 Planet Labs Inc., also released under GPLv3.

See [LICENSE](LICENSE) for full terms.

**Hardware (SCK-915, SCK-PBL-1): Proprietary — all rights reserved.**

---

## Acknowledgements

This project builds on the extraordinary work of the Planet Labs team who designed and open-sourced the OpenLST radio system. The Dove LST radio has proven itself in one of the most demanding RF environments possible: low Earth orbit.

Special thanks to the OpenLST contributors:
Henry Hallam, Alex Ray, Rob Zimmerman, Matt Peddie, Bryan Klofas,
Ryan Kingsbury, and the full Planet Labs team.

---

*OpenLST inspired. SCK-915 evolved. Open-source software, hand-assembled hardware.*
*Made in Tennessee, USA.*
