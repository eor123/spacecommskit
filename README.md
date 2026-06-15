# SpaceCommsKit — SCK-915 Explorer Kit + SCK-2400 CCSDS Platform + SCK-PBL-1 Payload

> **Flight-heritage RF communications technology. Now in your lab — and in the stratosphere.**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![Platform: Windows](https://img.shields.io/badge/Platform-Windows-blue)](https://github.com/eor123/spacecommskit)
[![Hardware: CC1110](https://img.shields.io/badge/SCK--915-CC1110%20915MHz-green)](https://github.com/eor123/spacecommskit)
[![Hardware: CC1352P](https://img.shields.io/badge/SCK--2400-CC1352P%202.4GHz-blue)](https://github.com/eor123/spacecommskit)
[![Version: 1.2.0](https://img.shields.io/badge/Version-1.2.0-orange)](https://github.com/eor123/spacecommskit)

This repository is home to **two RF communications platforms** and a shared payload board:

- **SCK-915 Explorer Kit** — a complete RF communications development kit built on the
  open-source [OpenLST](https://github.com/eor123/openlst) radio platform, the same design
  heritage as Planet Labs' Dove satellite Low-Speed Transceiver (200+ cumulative years of
  on-orbit data across 150+ satellites). Operates at 915MHz, no license required.
- **SCK-2400** — a 2.4GHz platform built on the Texas Instruments CC1352P, speaking
  **CCSDS Space Packet Protocol** — the same packet standard used by real spacecraft —
  with Over-Air Download (OAD) firmware updates and full RF range performance suitable for
  LEO ground station passes.
- **SCK-PBL-1 Payload Board** — a Raspberry Pi Pico-based payload (camera, GPS, barometric
  altimeter, SD flight logging) shared unchanged between both radio platforms.

This repository contains everything you need to get started:

- ✅ Windows ground station application (C# .NET 8) — supports both SCK-915 (OpenLST) and
  SCK-2400 (CCSDS) boards, with live GPS map, flight recorder, replay, and OAD firmware updates
- ✅ SCK-915 CC1110 radio firmware (C, SDCC) — OpenLST-based, 915MHz
- ✅ SCK-2400 CC1352P firmware (C, TI-RTOS7) — CCSDS framing, OAD streaming, payload bridge
- ✅ Raspberry Pi Pico firmware (MicroPython) — camera, SD, GPS, altimeter, fused telemetry —
  shared unchanged between SCK-915 and SCK-2400
- ✅ Windows installer (.exe) — no prerequisites, .NET 8 runtime bundled
- ✅ User Guides and Developer Guides (Markdown) for both platforms, plus dev-log bringup notes

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

## What is SCK-2400?

SCK-2400 is a 2.4GHz platform built on the **Texas Instruments CC1352P**
SimpleLink wireless MCU (LAUNCHXL-CC1352P-2 for development; CC2652P1FRGZ
for the production SCK-2400 board). Unlike SCK-915's OpenLST packet
protocol, SCK-2400 speaks **CCSDS Space Packet Protocol** — the same
framing standard used on real spacecraft — and adds:

```
Windows Ground Station (SCK-2400 / CCSDS mode)
        │  CCSDS command/telemetry framing
        │  Over-Air Download (OAD) — full firmware update over RF
        │  Same GPS map, flight recorder, custom commands as SCK-915
        │
        │ USB · 921600 baud · XDS110
        │
  SCK-2400 Board (APID 0x010 · CC1352P · Ground Station)
        │
        │ RF 2.4GHz ISM band
        │
  SCK-2400 Board (APID 0x011 · CC1352P · Remote)
        │
        │ UART · 115200 baud · ESP framing (CC1352P↔Pico bridge)
        │
  Raspberry Pi Pico (SCK-PBL-1 Payload Board — unchanged from SCK-915)
        │  OV2640 Camera + MicroSD Card
        │  GPS6MV2 (NEO-6M) + MS5611/BMP581 altimeter
        │  Fused GPS+baro beacon every 10 seconds
```

**Key capabilities:**
- CCSDS Space Packet Protocol — APID-based addressing, command ACKs,
  telemetry beacons
- **OAD (Over-Air Download)** — stream a full firmware image (~335KB) to
  a remote board in **~24 seconds** at the production 10ms inter-chunk
  setting, about 5% of a typical 8-minute LEO ground station pass. BIM
  (Boot Image Manager) guarantees a failed OAD never bricks the board —
  the previous working firmware always remains bootable.
- **CCSDS↔ESP payload bridge** — the SCK-2400 remote board transparently
  translates CCSDS commands to ESP-framed commands for the Pico payload
  board. `main.py` requires **zero changes** between SCK-915 and SCK-2400.
- Same Ground Station app, same GPS/Map tab, same flight recorder/replay,
  same custom command framework — select "SCK-2400 (CCSDS)" from the
  Board dropdown.

See the [SCK-2400 User Guide](docs/SCK-2400_User_Guide.md) and
[SCK-2400 Developer Guide](docs/SCK-2400_Developer_Guide.md) for full
details, including the CCSDS opcode reference, OAD protocol, and SysConfig
hardware configuration.

> **Status:** SCK-2400 is in active bringup on LAUNCHXL-CC1352P-2
> development hardware. OAD streaming and the CCSDS↔ESP payload bridge
> are RF-verified end to end (~2 second round trip). Production CC2652P
> hardware is planned. See the
> [dev log](docs/dev-log/) for bringup notes and hard-won lessons.

---

## What is SCK-PBL-1?

The **SCK-PBL-1** is a Raspberry Pi Pico-based payload board — a complete,
self-contained sensor and imaging package that bolts onto either SCK-915
or SCK-2400 over a simple two-wire UART. It's the part of the system that
actually goes up: the camera, the GPS, the altimeter, the SD card flight
recorder.

```
SCK-PBL-1 Payload Board (Raspberry Pi Pico)
        │
        ├── OV2640 Camera ──────── JPEG snapshots to MicroSD
        ├── MicroSD Card ────────── flight log (.sckflight) + images
        ├── GPS6MV2 (NEO-6M) ────── airborne mode, rated to 50km
        ├── MS5611 / BMP581 ─────── aviation-grade barometric altimeter
        │
        └── Fused GPS + baro beacon every 10 seconds
                   │
                   ▼
        UART, 115200 baud, ESP framing
                   │
        ┌──────────┴──────────┐
        │                      │
   SCK-915 (CC1110)      SCK-2400 (CC1352P)
   915MHz · OpenLST       2.4GHz · CCSDS
```

**One payload board, two radio platforms — zero firmware changes.**
`main.py` is identical whether it's talking to an SCK-915 board over
OpenLST `PICO_MSG` sub-opcodes or an SCK-2400 board over the CCSDS↔ESP
bridge. The Pico has no idea which radio it's attached to.

**What it does:**
- Captures JPEG images on command (`SNAP`) and stores them to MicroSD
- Lists, downloads (chunked), and deletes files on the SD card over RF
- Reports temperature, barometric pressure/altitude, and GPS position
  on demand
- Transmits a fused GPS + barometric telemetry beacon autonomously every
  10 seconds — no polling required during a flight
- Logs every beacon to a `.sckflight` file for post-flight replay and KML
  export to Google Earth
- Runs the NEO-6M GPS in **Airborne (<1g) dynamic mode** at startup,
  removing the standard 18km CoCom altitude limit — rated to 50km, well
  above any amateur balloon ceiling

**Status indicators onboard** (camera, SD, GPS fix, altimeter, fault, and
power rail LEDs) give an at-a-glance health check without a laptop —
useful when you're standing in a field at 6am before a balloon launch.

Full wiring tables, power architecture, and the sub-opcode/response
reference are in [Pico Payload Setup](#pico-payload-setup-sck-pbl-1) and
[SCK-PBL-1 Power Architecture](#sck-pbl-1-power-architecture) below. For
the SCK-2400 CCSDS opcode mapping, see the
[SCK-2400 Developer Guide §3.5](docs/SCK-2400_Developer_Guide.md).

> **Status:** SCK-PBL-1 has flown on SCK-915 HAB missions and is
> RF-verified end-to-end on SCK-2400 (bench + RF range). A standalone
> product page and BOM are planned — see
> [spacecommskit.com](https://www.spacecommskit.com) for updates.

---

## Repository Structure

```
spacecommskit/
├── ground-station/               C# .NET 8 WinForms ground station (VS2022)
│   ├── OpenLstGroundStation.sln
│   └── OpenLstGroundStation/
│       ├── MainForm.cs           All UI — tabs, GPS map, flight recorder,
│       │                         OAD controls, SCK-915/SCK-2400 board switch
│       ├── FlightReplayForm.cs   Flight replay — animated map + altitude chart
│       ├── OpenLstProtocol.cs    SCK-915 packet framing, parsing, AES signing
│       ├── CcsdsProtocol.cs      SCK-2400 CCSDS packet framing + opcodes
│       ├── CustomCommand.cs      JSON-persistent custom commands (both platforms)
│       ├── AppLogger.cs          Daily log file writer
│       └── Program.cs
├── installer/
│   ├── OpenLST_Ground_Station.iss         ← Inno Setup script
│   └── SCK_Ground_Station_Setup_v1.2.0.exe
├── sck2400_firmware/              CC1352P firmware (CCS Theia / TI-RTOS7)
│   ├── sck2400.syscfg              SysConfig — UART/SPI/RF pin assignments
│   ├── main.c / main.h             RTOS tasks, rfTask
│   ├── uart.c / uart.h             CCSDS dispatch + CCSDS↔ESP payload bridge
│   ├── ccsds.h                     CCSDS packet structs + command opcodes
│   ├── radio.c / radio.h           RF driver wrapper
│   ├── telemetry.c / telemetry.h   Telemetry collection
│   └── oad_*.c                     OAD ext-flash transport + image header
├── pico/                          Raspberry Pi Pico MicroPython firmware
│   │                              (shared, unchanged, between SCK-915 & SCK-2400)
│   ├── main.py                   Main pipeline — camera, SD, GPS, altimeter, commands
│   ├── sdcard.py                 MicroPython SD card SPI driver
│   └── gps_test.py               Standalone GPS test and verification script
├── docs/
│   ├── SCK-915_User_Guide.md
│   ├── SCK-915_Developer_Guide.md
│   ├── SCK-2400_User_Guide.md
│   ├── SCK-2400_Developer_Guide.md
│   └── dev-log/                  Bringup session notes — debugging narratives
│       └── SCK-2400_Session10-11_OAD_PayloadBridge.md
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
2. Select **SCK-915 (OpenLST)** from the Board dropdown (top right) —
   this is the default
3. Select your COM port, set HWID to `0001`, click **Connect**
4. Go to the **Commands** tab and click **Get Telem**
5. Green ✓ response within 300ms — you are live

> **Using SCK-2400 instead?** Select **SCK-2400 (CCSDS)** from the Board
> dropdown, connect to the Ground Station board's XDS110 COM port at
> 921600 baud, and click **Get Telem** on the Commands tab — you should
> see a `tlm_beacon` response. See the
> [SCK-2400 User Guide](docs/SCK-2400_User_Guide.md) for full setup.

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

> **Note — SCK-2400 opcode mapping:** the table above shows the Pico's
> native ESP sub-opcodes (used directly by SCK-915 under CCSDS opcode
> `0x20` PICO_MSG). On SCK-2400, each of these is exposed as its **own
> CCSDS opcode** (`0x20`–`0x29`) — e.g. `GET_GPS` is CCSDS opcode `0x27`,
> not `0x20` with payload `0x07`. The Pico-side sub-opcodes and response
> formats are identical; only the CCSDS-facing opcode differs. See the
> [SCK-2400 Developer Guide §3.5](docs/SCK-2400_Developer_Guide.md) for
> the full mapping table.

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
| Home | Live telemetry — uptime, RSSI, LQI, packet counts (SCK-915) or CCSDS telemetry beacon (SCK-2400) |
| Commands | SCK-915: standard OpenLST commands (get_telem, reboot, set_callsign). SCK-2400: CCSDS commands + **OAD controls** (Start/Status/Abort, inter-chunk delay) |
| Firmware | SCK-915: build (SDCC/mingw32-make), sign (CBC-MAC AES), OTA flash over RF. SCK-2400: CCS Theia build, RF power mode + board role patching, merged BIM+app flash |
| Terminal | Raw command terminal |
| Custom Commands | Save and send custom opcode/payload commands to Pico — works on both platforms |
| Files | Browse, download, delete files from Pico SD card over RF |
| GPS / Map | Live GPS on Google Maps, fused telemetry, flight recorder, replay — works on both platforms |
| Provision | SCK-915: flash bootloader to fresh CC1110 via CC Debugger. SCK-2400: flash BIM + set board APID |

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

See the [SCK-915 Developer Guide](docs/SCK-915_Developer_Guide.md) for full build instructions.

### CC1352P Radio Firmware (SCK-2400)

Requirements:
- [TI Code Composer Studio (Theia)](https://www.ti.com/tool/CCSTUDIO)
- [SimpleLink CC13xx/CC26xx SDK](https://www.ti.com/tool/SIMPLELINK-CC13XX-CC26XX-SDK) (8.32.00.07)
- TI-RTOS7, TI Clang toolchain (bundled with CCS)
- XDS110 debug probe (onboard the LAUNCHXL-CC1352P-2)

The Ground Station's **Firmware** tab drives a headless CCS build directly
from the GUI — set **Project Dir** to `sck2400_firmware/`, choose
**Board Role** (Ground Station / Remote) and **RF Power Mode**
(Bench / Field), then **Clean + Build** and **Flash**. This patches
`SCK_APID_THIS_BOARD`, `TX_POWER`, and the OAD image header automatically.

To build manually in CCS Theia, open `sck2400_firmware/` as a project and
use **Project → Build**. See the
[SCK-2400 Developer Guide](docs/SCK-2400_Developer_Guide.md) for the full
SysConfig pin configuration, OAD image header patching details, and the
CCSDS↔ESP payload bridge architecture.

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
Output power:       +28 dBm (CC1190 PA datasheet spec — calibrated
                    measurement pending TinySA arrival)
```

**RF Characterization — Prototype Units:**

RF characterization was performed on two prototype SCK-915 boards using SDR verification.
Both units show a clean 915.000 MHz center frequency with well-formed 2-FSK modulation
and no observable harmonic artifacts at the SDR receiver.

Output power has not yet been independently verified with calibrated test equipment.
The +28 dBm figure is derived from CC1190 datasheet specifications at the selected
PA_TABLE0 register setting. Calibrated power measurement will be performed when
TinySA Ultra+ test equipment arrives and results will be published openly.

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
| SCK-915 User Guide | [docs/SCK-915_User_Guide.md](docs/SCK-915_User_Guide.md) |
| SCK-915 Developer Guide | [docs/SCK-915_Developer_Guide.md](docs/SCK-915_Developer_Guide.md) |
| SCK-2400 User Guide | [docs/SCK-2400_User_Guide.md](docs/SCK-2400_User_Guide.md) |
| SCK-2400 Developer Guide | [docs/SCK-2400_Developer_Guide.md](docs/SCK-2400_Developer_Guide.md) |
| SCK-2400 Dev Log / Bringup Notes | [docs/dev-log/](docs/dev-log/) |
| OpenLST Hardware (forked) | https://github.com/eor123/openlst |
| Original OpenLST by Planet Labs | https://github.com/OpenLST/openlst |
| SpaceCommsKit Website | https://www.spacecommskit.com |
| Buy a Kit | https://www.spacecommskit.com/shop |
| HabHub Flight Predictor | https://predict.habhub.org |
| SPOT Trace Tracker | https://www.findmespot.com |
| SDCC Compiler | https://sdcc.sourceforge.net |
| SmartRF Flash Programmer | https://www.ti.com/tool/FLASH-PROGRAMMER |
| TI Code Composer Studio (Theia) | https://www.ti.com/tool/CCSTUDIO |
| Inno Setup 6 | https://jrsoftware.org/isinfo.php |

---

## Changelog

### v1.3.0 (in progress)
- **SCK-2400 platform added** — CC1352P (LAUNCHXL-CC1352P-2), CCSDS Space Packet Protocol
- OAD (Over-Air Download) streaming — full 335KB firmware image in ~24 seconds (10ms
  inter-chunk delay), ~5% of an 8-minute LEO pass
- BIM (Boot Image Manager) — failed/interrupted OAD never bricks the board; previous
  working firmware always remains bootable
- CCSDS↔ESP payload bridge — SCK-PBL-1 Pico payload board (`main.py`) works unchanged
  on SCK-2400; CC1352P transparently translates CCSDS commands to ESP framing
- Ground Station: Board dropdown to switch between SCK-915 (OpenLST) and SCK-2400 (CCSDS)
- Ground Station: OAD controls on Commands tab (Start/Status/Abort, inter-chunk delay)
- Ground Station: SCK-2400 custom commands now route through `CcsdsProtocol.BuildCommand`
- New SCK-2400 User Guide and Developer Guide; SCK-915 User Guide converted to Markdown
- Dev log added — bringup session notes and hard-won lessons for SCK-2400

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
