# SpaceCommsKit SCK-2400 — Ground Station User Guide

**Version 1.00 — June 2026**
**SpaceCommsKit — https://spacecommskit.com**

---

## Overview

The SCK-2400 is a 2.4GHz RF communications board built on OpenLST roots,
using the Texas Instruments CC1352P SimpleLink wireless microcontroller.
It replaces the SCK-915's OpenLST packet protocol with CCSDS framing —
the same packet standard used by real spacecraft — while keeping the
proven payload architecture from SCK-915: a Raspberry Pi Pico payload
board running GPS, barometric altimeter, camera, and SD card flight
logging.

With the SCK-2400 Ground Station you can:

- Send and receive CCSDS commands between a ground station and a remote
  board over 2.4GHz RF
- View real-time telemetry — uptime, RX/TX counters, link status
- Update remote firmware over RF using OAD (Over-Air Download) — a full
  firmware image transfers in under 30 seconds
- Query the payload board for GPS position, barometric altitude, and
  temperature
- Trigger camera snapshots and manage files on the payload SD card
- Build and flash custom firmware to either board from the Ground
  Station app

> **Note:** The SCK-2400 operates in the 2.4GHz ISM band. Check local
> regulations for power limits in your jurisdiction before field or
> high-power operation.

---

## What You Need

| Item | Description |
|------|-------------|
| SCK-2400 Board ×2 | CC1352P-based 2.4GHz transceiver (one Ground Station board, one Remote board) |
| USB cables ×2 | Connect each board to your PC for programming and (GS board) operation |
| Antennas ×2 | 2.4GHz antennas |
| Raspberry Pi Pico payload board | Runs GPS, altimeter, camera, and SD logging (optional but recommended) |
| SCK Ground Station software | Windows application for commanding and monitoring |

The development hardware is two TI **LAUNCHXL-CC1352P-2** LaunchPads.
Production hardware is the SCK-2400 custom PCB using the CC2652P.

---

## Installation

1. **Run the installer** or build from source — see the
   [SCK-2400 Developer Guide](./SCK-2400_Developer_Guide.md) for build
   instructions.
2. **Connect both boards** to your PC via USB. Each LaunchPad exposes
   two COM ports — the lower numbered port (e.g. COM8) is the XDS110
   debug/programming interface used by the Ground Station app.
3. **Launch SCK Ground Station.**
4. **Select "SCK-2400 (CCSDS)"** from the Board dropdown in the top
   right of the application.

---

## Quick Start

**Quick Start Checklist:**

1. Connect the Ground Station board (APID `0x010`) to your PC via USB
2. Select its COM port and click **Connect**
3. Go to the **Commands** tab and click **Get Telem**
4. You should see a `tlm_beacon` response with a green checkmark

If you see the green checkmark, your Ground Station board is alive and
the link is working. To talk to a Remote board over RF, set the **APID**
field to the remote board's address (e.g. `011`) before sending commands
— the Ground Station board relays the command over RF automatically.

---

## Hardware Connections

### Ground Station Board (APID 0x010)

Connect via USB directly to your PC. This board must remain connected
for the duration of your session — it relays all RF traffic between the
Ground Station app and the Remote board.

### Remote Board (APID 0x011)

This is the board that flies on your mission, or sits across the room
during bench testing. It connects via RF to the Ground Station board and,
optionally, via UART to a Raspberry Pi Pico payload board.

**Remote board → Pico wiring:**

| Remote Board Pin | Pico Pin | Function |
|-------------------|----------|----------|
| DIO5 (header pin 10) | GPIO1 (pin 2) | UART TX → Pico RX |
| DIO16 (header pin 32) | GPIO0 (pin 1) | UART RX ← Pico TX |
| GND | GND | Common ground |

Both boards run on 3.3V logic — no level shifter is required.

### Power Requirements

| Component | Supply | Typical Current |
|-----------|--------|-----------------|
| SCK-2400 / LAUNCHXL-CC1352P-2 | 5V USB | ~100mA idle, higher during RF TX |
| Raspberry Pi Pico | 5V USB or 3.3V | ~25mA typical |
| BMP581/MS5611 altimeter | 3.3V | <1mA |
| GPS NEO-6M | 3.3V/5V | ~50mA |

---

## Home Tab

The Home tab shows live telemetry from the connected and addressed board.

| Field | Description |
|-------|--------------|
| Uptime | Seconds since last boot |
| RX Count | Packets received over RF |
| TX Count | Packets transmitted over RF |
| VCC | Supply voltage |

Telemetry updates each time you click **Get Telem** on the Commands tab,
or automatically if the board's autonomous beacon is enabled.

---

## Commands Tab

Provides quick access to standard CCSDS commands.

| Button | Opcode | Description |
|--------|--------|-------------|
| Get Telem | 0x01 | Request telemetry from the addressed board |
| Reboot | 0x02 | Reboot the addressed board |
| Beacon ON / OFF | 0x04 | Enable or disable the autonomous RF beacon |

### OAD (Over-Air Download) Controls

| Control | Description |
|---------|-------------|
| Start OAD | Streams the built firmware image to the addressed remote board over RF |
| Inter-chunk delay | 10ms (production default) to 100ms — controls streaming speed |
| OAD Status | Query bytes received / total size during a transfer |
| Abort OAD | Cancel an in-progress transfer and clear the flash slot |

**OAD performance:** A full 335KB firmware image transfers in approximately
**24 seconds at the 10ms setting** — about 5% of a typical 8-minute LEO
ground station pass, leaving ample time for acquisition and telemetry.

> The remote board automatically reboots into the new firmware once the
> transfer completes and the image CRC is verified. If the connection is
> lost before verification, the board's bootloader (BIM) keeps the
> previous working firmware — OAD never bricks the board.

---

## Custom Commands Tab

Lets you send any CCSDS command opcode with any payload. Commands persist
between sessions in `customcommands.json`.

**Pre-loaded payload board commands:**

| Name | Opcode | Payload | Expected Response |
|------|--------|---------|-------------------|
| PICO Ping | 0x20 | — | `PICO:ACK` |
| PICO Read Temp | 0x21 | — | `TEMP:13.47C` |
| PICO Snap | 0x22 | — | `SNAP:OK:snap_001.jpg:4617` |
| PICO List Files | 0x23 | — | `LIST:snap_001.jpg,...` |
| PICO Get GPS | 0x27 | — | `GPS:lat,lon,alt,sats,fix,hpa,baro_alt,temp_c` |
| PICO Get Baro | 0x28 | — | `BARO:hpa,baro_alt,temp_c` |
| PICO Beacon ON | 0x29 | `01` | `BEACON:ON` |
| PICO Beacon OFF | 0x29 | `00` | `BEACON:OFF` |

> **Tip:** The Pico transmits its own GPS+baro beacon autonomously every
> 10 seconds. If your responses occasionally look mixed up or garbled,
> send **PICO Beacon OFF** first, run your commands, then send
> **PICO Beacon ON** when finished.

---

## GPS / Map Tab

Displays live position from the payload board's GPS and barometric
altimeter on an interactive map.

| Control | Description |
|---------|--------------|
| Get GPS | Request a fresh GPS+baro fix on demand |
| Get Baro | Request barometric data only |
| Beacon ON / OFF | Toggle the payload board's autonomous 10-second beacon |
| Clear Track | Clear the plotted flight track from the map |
| Export KML | Save the current track as a KML file for Google Earth |
| Record | Begin recording a flight session to disk |
| Replay | Play back a previously recorded flight session |

The map shows current position, altitude, ascent rate, GPS/baro altitude
delta, and satellite fix status — updated live as beacon packets or
on-demand GPS responses arrive.

---

## Files Tab

A file manager for the payload board's SD card.

> **Before using the Files tab:** send **PICO Beacon OFF** to prevent
> the autonomous beacon from interrupting file transfers.

### Downloading a File

1. Click **Refresh List** to retrieve the current SD card file listing
2. Click a filename to select it
3. Click **Get File** — the file transfers in 200-byte chunks over RF
4. When complete, the file is saved locally and opens automatically

A typical flight log (`.sckflight`) or small JPEG transfers in a few
seconds depending on RF conditions and file size.

---

## Firmware Tab

Used to build, sign, and flash custom firmware for either board.

### Building

1. Set **Project Dir** to your `sck2400_firmware` CCS project folder
2. Set **Board Role** — Ground Station (APID 0x010) or Remote (APID
   0x011, 0x012, ...)
3. Choose **RF Power Mode** — Bench (0 dBm, indoor/safe) or Max Power
   (field/HAB/LEO mission)
4. Click **Clean + Build**

> **Always use Clean + Build** after making source code changes. Stale
> object files are the most common cause of mysterious firmware behavior.

### Flashing

1. Verify the **HEX File** path points to the freshly built output
2. Click **Flash** (or **Build + Flash** to do both in one step)
3. The Ground Station merges the application image with the bootloader
   (BIM) automatically and programs the board over the XDS110 debug
   interface

> After flashing, send 2–3 **Get Telem** commands before moving the board
> to remote RF operation to confirm the radio stack is healthy.

---

## Provision Tab

One-time setup for new boards — flashes the bootloader (BIM) and sets
the board's APID (address). BIM is permanent after initial flashing and
is never overwritten by subsequent application or OAD updates.

---

## Troubleshooting

### No reply to Get Telem on Ground Station board (0x010)

- Check the COM port selection matches the XDS110 interface
- Confirm the board is connected via USB (RF boards need external power
  for sustained TX in field deployments)
- Rapid LED double-blink pattern indicates the bootloader is running
  without a valid application — reflash the application firmware

### No reply to Get Telem on Remote board (0x011)

- Confirm the Ground Station board (0x010) responds first — it relays
  RF commands
- Boards should be at least a few feet apart during bench testing to
  avoid RF saturation
- Try Get Telem 2–3 times — the first packet after boot is sometimes
  missed while the RF synthesizer stabilizes

### PICO commands return no reply

- Confirm the Pico is running `main.py` and showing beacon output on its
  serial console
- Verify UART wiring: Remote board DIO5 → Pico GPIO1, DIO16 → Pico GPIO0
- Send **PICO Beacon OFF** before other payload commands to avoid
  response collisions with the autonomous beacon

### PICO responses look garbled or mixed up

- This is the autonomous beacon racing with your command response.
  Send **PICO Beacon OFF**, wait a moment, then retry

### OAD transfer fails or times out

- Confirm the remote board responded to **OAD Status** before starting
- If a transfer fails partway through, the Ground Station automatically
  retries — re-erasing the flash slot and re-streaming
- A failed or interrupted OAD never corrupts the running firmware — BIM
  always falls back to the last known-good image

### Board unresponsive after a firmware flash

- Confirm you flashed the **merged** hex (application + BIM), not the
  application-only hex — the Ground Station does this automatically via
  **Build + Flash**
- A rapid 1-flash-pause-3-flash LED pattern after reset usually indicates
  BIM is running without a valid application image

---

## Specifications

| Parameter | Value |
|-----------|-------|
| RF Frequency | 2.4GHz ISM band |
| Microcontroller (dev) | Texas Instruments CC1352P1F3RGZ (LAUNCHXL-CC1352P-2) |
| Microcontroller (production) | Texas Instruments CC2652P1FRGZ |
| RTOS | TI-RTOS7 |
| Protocol | CCSDS Space Packet Protocol |
| OAD transfer (335KB image) | ~24 seconds @ 10ms inter-chunk delay |
| Cold boot time | <40ms |
| Interface | USB (XDS110), 921600 baud to Ground Station |
| Payload UART | 115200 baud, ESP framing, to Raspberry Pi Pico |

---

*SpaceCommsKit SCK-2400 User Guide v1.00*
*For updates and source code see https://spacecommskit.com*
