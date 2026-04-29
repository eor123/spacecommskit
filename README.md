# SpaceCommsKit — SCK-915 Explorer Kit + SCK-PBL-1 Payload Platform

> **Flight-heritage RF communications technology. Now in your lab — and in the stratosphere.**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-blue)](https://github.com/eor123/spacecommskit)
[![Hardware: CC1110](https://img.shields.io/badge/Hardware-CC1110%20915MHz-green)](https://github.com/eor123/spacecommskit)
[![Version: 1.2.0](https://img.shields.io/badge/Version-1.2.0-orange)](https://github.com/eor123/spacecommskit)

The **SCK-915 Explorer Kit** is a complete RF communications development kit built on the open-source [OpenLST](https://github.com/eor123/openlst) radio platform — the same design heritage as Planet Labs' Dove satellite Low-Speed Transceiver, which has accumulated over **200 cumulative years of on-orbit data** across more than 150 satellites.

This repository contains everything you need to get started:

- ✅ Windows ground station application (C# .NET 8) with live GPS map, flight recorder, and replay
- ✅ Raspberry Pi Pico firmware (MicroPython) — camera, SD, GPS, altimeter, fused telemetry
- ✅ Windows installer (.exe) — no prerequisites, .NET 8 runtime bundled
- ✅ User Guide and Developer Guide (PDF)

---

## What is the SCK-915 Explorer Kit?

Two CC1110 + CC1190-based radio boards operating at **915 MHz** ISM band (no license required), with a full Windows ground station for commanding, monitoring, live GPS tracking, remote imaging, and flight recording over RF.

```
Windows Ground Station
        │  Live GPS map (Google Maps)
        │  Fused GPS + barometric altitude telemetry
        │  Flight recorder → .sckflight + .kml
        │  Flight replay — animated map + altitude chart
        │  Image file transfer
        │  OTA firmware flash over RF
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
  Raspberry Pi Pico W (SCK-PBL-1 Payload Board)
        │  OV2640 Camera + MicroSD Card
        │  GPS6MV2 (NEO-6M) — airborne mode, rated to 50km
        │  MS5611 Barometric Altimeter — aviation grade, 10-1200 hPa
        │  Fused GPS+baro beacon every 10 seconds over RF
```

**Proven capabilities:**
- Real-time fused GPS + barometric telemetry over RF
- Live GPS tracking — autonomous beacon every 10 seconds, plotted on Google Maps
- Barometric altitude — pressure, altitude, temperature in every beacon
- Remote temperature sensing
- Remote JPEG image capture (Arducam OV2640)
- Image file transfer over RF (chunked protocol)
- OTA firmware updates over RF — no physical access required
- Flight recorder — saves .sckflight and .kml on every flight
- Flight replay — animated map playback with altitude chart and event timeline
- Custom command framework (extensible)
- KML flight track export for Google Earth

---

## Repository Structure

```
spacecommskit/
├── ground-station/               C# .NET 8 WinForms ground station (VS2022)
│   ├── OpenLstGroundStation.sln
│   └── OpenLstGroundStation/
│       ├── MainForm.cs           All UI — tabs, GPS map, flight recorder
│       ├── FlightReplayForm.cs   Flight replay — animated map + altitude chart
│       ├── OpenLstProtocol.cs    Packet framing, parsing, AES signing
│       ├── CustomCommand.cs      JSON-persistent custom commands
│       ├── AppLogger.cs          Daily log file writer
│       └── Program.cs
├── installer/
│   ├── OpenLST_Ground_Station.iss         ← Inno Setup script
│   └── SCK_Ground_Station_Setup_v1.2.0.exe
├── pico/                         Raspberry Pi Pico MicroPython firmware
│   ├── main.py                   Main pipeline — camera, SD, GPS, altimeter, commands
│   ├── sdcard.py                 MicroPython SD card SPI driver
│   └── gps_test.py               Standalone GPS test and verification script
├── docs/
│   ├── SCK-915_Explorer_Kit_User_Guide.pdf
│   └── SCK-915_Explorer_Kit_Developer_Guide.pdf
└── tools/
    └── README.md
```

---

## Quick Start

### 1. Install the Ground Station

Download and run:
```
installer\SCK_Ground_Station_Setup_v1.2.0.exe
```
No prerequisites — the .NET 8 runtime is bundled. Requires Windows 10 (1809) or later.

### 2. Connect Board 0001

```
USB-Serial Adapter TX  →  Board 0001 P0_5 (UART1 RX)
USB-Serial Adapter RX  ←  Board 0001 P0_4 (UART1 TX)
GND                    —  GND
```

Power Board 0001 from an external 5V supply.

### 3. Connect and Launch

1. Open **SCK Ground Station** from the Start menu
2. Select your COM port, set HWID to `0001`, click **Connect**
3. Go to the **Commands** tab and click **Get Telem**
4. Green ✓ response within 300ms — you are live

---

## Pico Payload Setup (SCK-PBL-1)

### Wiring

| Device | Signal | Pico Pin |
|--------|--------|----------|
| OV2640 Camera | MISO | GP16 |
| OV2640 Camera | MOSI | GP19 |
| OV2640 Camera | SCK | GP18 |
| OV2640 Camera | CS | GP15 |
| OV2640 Camera | SDA (I2C config) | GP4 |
| OV2640 Camera | SCL (I2C config) | GP5 |
| SD Card | MISO | GP16 (shared SPI0) |
| SD Card | MOSI | GP19 (shared SPI0) |
| SD Card | SCK | GP18 (shared SPI0) |
| SD Card | CS | GP17 |
| GPS6MV2 | TX → Pico RX | GP9 (UART1) |
| GPS6MV2 | RX → Pico TX | GP8 (UART1) |
| MS5611 Altimeter | SDA | GP2 (I2C1) |
| MS5611 Altimeter | SCL | GP3 (I2C1) |
| MS5611 Altimeter | PS | 3.3V (selects I2C mode) |
| MS5611 Altimeter | CSB | 3.3V |
| MS5611 Altimeter | SDO | GND (addr 0x76) |
| SCK-915 Board UART0 TX | → Pico RX | GP1 (UART0) |
| SCK-915 Board UART0 RX | ← Pico TX | GP0 (UART0) |

### Status LEDs (SCK-PBL-1 onboard)

| LED | Color | Pin | Meaning |
|-----|-------|-----|---------|
| Camera | GREEN | GP10 | Camera initialized OK |
| SD Card | GREEN | GP11 | SD card mounted OK |
| GPS | BLUE | GP12 | GPS fix acquired |
| Altimeter | YELLOW | GP13 | MS5611 responding |
| Fault | RED | GP14 | Error condition |
| 5V Rail | GREEN | — | 5V power indicator |
| 3.3V Rail | GREEN | — | 3.3V power indicator |

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

Fused beacon that will go over RF:
  GPS:36.058910,-87.384024,254.5,8,1,989.31,201.3,20.56
```

---

## Pico Sub-Opcodes (opcode 0x20 = PICO_MSG)

| Sub-opcode | Command | Response |
|-----------|---------|----------|
| `0x00` | PING | `PICO:ACK` |
| `0x01` | READ_TEMP | `TEMP:xx.xxC` |
| `0x02` | SNAP | `SNAP:OK:<filename>:<bytes>` |
| `0x03` | LIST | `LIST:<file1>,<file2>,...` |
| `0x04` | GET_INFO | `INFO:<filename>:<bytes>:<chunks>` |
| `0x05` | GET_CHUNK | `CHUNK:<index>:<200 bytes data>` |
| `0x06` | DELETE | `DEL:OK:<filename>` |
| `0x07` | GET_GPS | `GPS:<lat>,<lon>,<gps_alt>,<sats>,<fix>,<hpa>,<baro_alt>,<temp_c>` |
| `0x08` | GET_BARO | `BARO:<hpa>,<alt_m>,<temp_c>` |

### Fused GPS + Baro Packet Format

```
GPS:36.058910,-87.384024,254.5,8,1,989.31,201.3,20.56
     │          │          │    │  │  │       │     └── baro temp °C
     │          │          │    │  │  │       └──────── baro altitude metres
     │          │          │    │  │  └──────────────── pressure hPa
     │          │          │    │  └─────────────────── fix (1=valid, 0=no fix)
     │          │          │    └────────────────────── satellites in use
     │          │          └─────────────────────────── GPS altitude metres MSL
     │          └────────────────────────────────────── longitude decimal degrees
     └───────────────────────────────────────────────── latitude decimal degrees
```

### Autonomous Fused Beacon
The Pico transmits a fused GPS + barometric packet **every 10 seconds** without any ground command. The ground station receives, parses, and plots all 8 fields automatically. No polling required during a balloon flight.

### Airborne Mode (Critical for HAB Flights)
At startup the firmware sends a **UBX CFG-NAV5 command** to configure the NEO-6M in Airborne (<1g) dynamic model. This removes the CoCom 18km altitude limit — the NEO-6M is then rated to 50km, well above any amateur balloon ceiling.

---

## Ground Station — Tabs

| Tab | Function |
|-----|----------|
| Home | Live telemetry — uptime, RSSI, LQI, packet counts |
| Commands | Standard OpenLST commands — get_telem, reboot, set_callsign |
| Firmware | Build (SDCC/mingw32-make), sign (CBC-MAC AES), OTA flash over RF |
| Terminal | Raw command terminal |
| Custom Commands | Save and send custom opcode/payload commands to Pico |
| Files | Browse, download, delete files from Pico SD card over RF |
| GPS / Map | Live GPS on Google Maps, fused telemetry, flight recorder, replay |
| Provision | Flash bootloader to fresh CC1110 via CC Debugger |

### GPS / Map Tab
- Live position on **Google Maps** via GMap.NET
- Autonomous fused beacon plotting — map updates every 10 seconds over RF
- Flight track — cyan polyline builds as position history accumulates
- **8 stat panels** — latitude, longitude, GPS altitude, satellites (row 1) + pressure hPa, baro altitude, baro temp, fix/packets (row 2)
- **Altitude display** — animated bar graphs for baro alt, GPS alt, and temperature; ascent rate with arrows; max altitude session high; GPS/baro delta; burst detection
- **Log filter** — toggle GPS log lines on/off to keep log clean during long flights
- **Export KML** — full flight track with launch, apogee, and landing pins for Google Earth
- **⏺ Record** — starts a flight recording session, saves `.sckflight` and auto-generates `.kml` on stop
- **▶ Replay** — opens the Flight Replay form for post-flight analysis

### Flight Replay Form
- Opens any `.sckflight` file — including Mission V1 real flight data (download from spacecommskit.com)
- **Animated Google Maps track** — position marker moves in real time as playback runs
- **Altitude / temperature profile chart** — baro alt (cyan), GPS alt (blue dashed), temperature (orange dotted)
- **Variable speed** — 1x, 5x, 10x, 30x, 60x — watch a 2-hour flight in 2 minutes
- **Scrub bar** — jump to any point in the flight
- **8 live data panels** — all telemetry fields update during playback
- **Event timeline** — LAUNCH, APOGEE, BURST, LANDING with timestamps
- **Summary panels** — max altitude, flight duration, burst status

---

## HAB Mission — High Altitude Balloon

The SCK-PBL-1 + SCK-915 stack is a complete amateur space mission platform. **No radio license required.**

```
Target altitude:  ~30km (stratosphere)
RF link:          SCK-915 @ 915MHz, +28dBm, CC1190 PA
GPS ceiling:      50km (NEO-6M in airborne mode)
Altimeter:        MS5611 — aviation grade, 10-1200 hPa, rated above 45km
Camera:           OV2640 — images to SD, selectively downlinked over RF
Recovery:         SPOT Trace (satellite) + AirTag (last-mile precision)
Balloon:          Kaymont 600g latex
Regulations:      FAA 14 CFR Part 101 Subpart B exemption (<6 lbs payload)
License:          None required — 915MHz ISM + SPOT Trace satellite tracker
```

### Recommended Hardware

| Item | Source | Est. Cost |
|------|--------|-----------|
| SCK-915 Explorer Kit | spacecommskit.com | TBD |
| SCK-PBL-1 Payload Board | spacecommskit.com | TBD |
| Raspberry Pi Pico W | Digi-Key / Adafruit | $6 |
| GPS6MV2 (NEO-6M breakout) | Amazon | $8 |
| MS5611 Altimeter | Digi-Key | $10 |
| OV2640 Camera Module | Amazon | $8 |
| MicroSD Card 32GB | Amazon | $8 |
| LiPo Battery 2S 2000mAh | Amazon | $15 |
| Kaymont 600g Balloon | kaymont.com | $45 |
| Parachute 36" | highaltitudescience.com | $25 |
| Helium (party store tank) | Local | $80–120 |
| SPOT Trace + subscription | findmespot.com | $50 + $100/yr |
| AirTag | Apple | $29 |
| Foam payload enclosure | Hardware store | $10 |
| **Total first launch** | | **~$400–430** |

### Recovery Strategy (No License Required)

```
PRIMARY:   SPOT Trace → satellite GPS every 2.5 minutes
           findmespot.com on phone in chase vehicle
           Works anywhere — no cell coverage needed
           Gets you to within 10 metres of landing

LAST MILE: AirTag → Precision Finding
           iPhone guides you to within inches
           Plays sound when nearby

SCIENCE:   SCK-915 → full fused telemetry during flight
           Camera images to SD
           Flight recorder → .sckflight for replay
           KML track for Google Earth

BACKUP:    Phone number on payload box
           "WEATHER EXPERIMENT — PLEASE CALL XXX-XXX-XXXX"
```

### HAB Flight Planning

1. **HabHub predictor** — predict.habhub.org — enter launch coordinates, ascent rate, burst altitude, descent rate → shows predicted landing zone on Google Maps
2. **FAA notification** — B4UFLY app — file 24 hours before launch, takes 5 minutes
3. **Weather** — windy.com for upper atmosphere winds — launch on calm low-wind days
4. **Chase vehicle** — one person drives, one tracks SPOT Trace on phone in real time

---

## SCK-PBL-1 Power Architecture

The SCK-PBL-1 accepts a wide range of power inputs with full polarity protection:

```
INPUT (9–12V DC recommended):
  ├── JST-PH 2.0mm LiPo/Li-Ion connector (2S LiPo recommended)
  ├── Screw terminals (field serviceable, any wire gauge)
  └── DC barrel jack

PROTECTION:
  Full bridge rectifier — any polarity accepted, AC capable

REGULATION:
  L7805 TO-220 + bolt-on heatsink → 5V regulated
  TC1262-3.3VDBTR LDO → 3.3V regulated (Pico + all sensors)

SWITCH:     Onboard power switch
INDICATORS:
  5V rail  → 1.5kΩ → GREEN LED
  3.3V rail → 680Ω → GREEN LED
```

**Minimum input voltage: 9V** (L7805 requires ~2V headroom above 5V output plus ~1.4V bridge rectifier drop).
**Recommended: 9V wall adapter or 2S LiPo (7.4–8.4V fully charged).**

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
2. Open `installer\OpenLST_Ground_Station.iss` in Inno Setup
3. Press **F9** to compile

### CC1110 Radio Firmware

Requirements:
- [SDCC](https://sdcc.sourceforge.net/) — Small Device C Compiler
- [mingw32-make](https://sourceforge.net/projects/mingw/) — GNU Make for Windows
- [SmartRF Flash Programmer](https://www.ti.com/tool/FLASH-PROGRAMMER) — TI bootloader tool
- [CC Debugger](https://www.ti.com/tool/CC-DEBUGGER) — for initial provisioning only

```bash
# Build bootloader (required for new board provisioning)
cd radios/openlst_437
mingw32-make openlst_437_bootloader

# Build radio application (OTA flash via ground station)
mingw32-make openlst_437_radio
```

Note: The `tail` memory summary warning on Windows is cosmetic — the hex file builds successfully.

See the [Developer Guide](docs/SCK-915_Explorer_Kit_Developer_Guide.pdf) for full build instructions.

---

## Provisioning a New Board

First-time CC1110 setup requires a CC Debugger and SmartRF Flash Programmer.

**Step 0 — Erase locked chip (required for new or previously programmed chips):**
1. Open SmartRF Flash Programmer GUI
2. Select action: **Erase**
3. Click **Perform Actions** — chip is now unlocked

**Step 1 — Provision bootloader:**
1. Ground Station → Provision tab
2. Enter HWID (e.g. `0004`)
3. Enter 3× AES signing keys (default: `FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF`)
4. Select `openlst_437_bootloader.hex`
5. Click **◎ Detect CC Debugger** — verify green light
6. Click **▶ PROVISION BOARD**

**Step 2 — Flash radio application (OTA):**
After provisioning, all subsequent firmware updates are via the Firmware tab over RF. No CC Debugger needed.

---

## Hardware Notes

### SCK-915 Radio Board

The SCK-915 boards are based on the OpenLST hardware design with the following substitutions:

| Original | Replacement | Notes |
|----------|-------------|-------|
| Qorvo RFFM6406 | TI CC1190 | RF front-end — PA/LNA for 915MHz ISM band |
| pSemi PE4259 | pSemi PE4250MLI-2 | RF switch — pin-for-pin compatible |

**CC1190 Control Pins:**

| CC1190 Pin | Function | CC1110 Pin | Idle State |
|-----------|----------|-----------|------------|
| Pin 6 HGM | High Gain Mode | 10kΩ → 3.3V | Always HIGH |
| Pin 7 LNA_EN | LNA Enable | P1_7 (GDO2) | HIGH in RX |
| Pin 8 PA_EN | PA Enable | P1_6 (GDO1) | HIGH in TX |

**915MHz RF Parameters:**
```
Carrier frequency:  914.999512 MHz (nearest synthesizer step)
Crystal:            27.000 MHz
Modulation:         2-FSK
Data rate:          7.415 kBaud
Deviation:          3.708 kHz
RX bandwidth:       60.268 kHz
Output power:       +28 dBm (CC1190 PA)
```

**RF Characterization — Prototype Units:**

RF characterization was performed on two prototype SCK-915 boards using SDR verification.
Both units show a clean 915.000 MHz center frequency with well-formed 2-FSK modulation
and no observable harmonic artifacts.

As production scales we will characterize additional units and publish aggregate RF
performance data. Batch-to-batch variation is expected in small-run hand-assembled
hardware and will be documented openly as part of our ongoing bring-up process.
Characterization data across production units will be available to Patreon members
via Section 7.

**Boards are hand-assembled in Tennessee, USA using components sourced exclusively from Digi-Key, Mouser, or manufacturer direct.**

---

## Related Links

| Resource | Link |
|----------|------|
| OpenLST Hardware (forked) | https://github.com/eor123/openlst |
| Original OpenLST by Planet Labs | https://github.com/OpenLST/openlst |
| SpaceCommsKit Website | https://www.spacecommskit.com |
| Buy a Kit | https://www.spacecommskit.com/shop |
| HabHub Flight Predictor | https://predict.habhub.org |
| SPOT Trace Tracker | https://www.findmespot.com |
| SDCC Compiler | https://sdcc.sourceforge.net |
| SmartRF Flash Programmer | https://www.ti.com/tool/FLASH-PROGRAMMER |
| Inno Setup 6 | https://jrsoftware.org/isinfo.php |

---

## Changelog

### v1.2.0
- **Rebranded: SCK Ground Station** (formerly OpenLST Ground Station)
- RF QA tab — automated spectrum verification via rtl_power + RTL-SDR
- 2-FSK center frequency detection, PASS/FAIL per board, printable certificate
- Snapshot naming system (SCK915_SERIAL_snap001_fund.csv)
- RTL-SDR tools bundled in installer (rtlsdr\ folder, GPLv2)
- SD black box flight recorder — .sckflight written on every boot
- Beacon ON/OFF control (sub-opcode 0x09)
- GPS blind handling — last known position shown in yellow when fix lost
- RF Power Mode — bench/field toggle patches board.h before OTA build
- SmartRF path configurable and persisted in appsettings.json
- OTA flash progress bar reset fix (second flash crash resolved)
- Fused GPS + barometric altimeter beacon (8-field packet)
- MS5611 aviation-grade altimeter (replaces BMP581 — better HAB altitude range)
- Flight recorder — .sckflight + .kml auto-generated on every flight
- Flight Replay form — animated map, altitude chart, event timeline, variable speed
- GPS / Map tab — 8 stat panels, sci-fi altitude display, ascent rate, burst detection
- OTA RF flash proven on SCK-915 — full bootloader pipeline over 915MHz
- CC1190 PA/LNA control fixed in both application and bootloader
- COMMAND_WATCHDOG_DELAY extended for RF OTA flash reliability
- Pico beacon queue fix — commands always processed before beacon fires
- Cancel button on OTA flash

### v1.1.0
- GPS live tracking on Google Maps
- OV2640 camera snap and file transfer over RF
- MicroSD file management over RF
- Custom command framework

### v1.0.0
- Initial release
- Basic RF telemetry
- OTA firmware flash

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
