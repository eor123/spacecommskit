# SpaceCommsKit SCK-915 — Ground Station User Guide

**Version 1.00 — June 2026**
**SpaceCommsKit — https://spacecommskit.com**

---

## Overview

The SCK-915 is a complete RF communications development kit based on the
open-source OpenLST radio platform originally developed by Planet Labs.
The kit includes two CC1110-based radio boards operating at 915MHz and a
Windows ground station application for commanding, monitoring, and
imaging.

With the SCK-915 Ground Station you can:

- Send and receive commands between a ground station and a remote radio
  board over RF
- View real-time telemetry including uptime, signal strength, and ADC
  readings
- Trigger a remote Raspberry Pi Pico to capture images with an Arducam
  OV2640 camera
- Transfer JPEG images from the remote SD card back to your Windows PC
  over RF
- Build and flash custom firmware to the remote board
- Provision new boards with the bootloader and hardware IDs

> **Note:** The SCK-915 operates at 915MHz in the ISM band. Check local
> regulations for power limits in your jurisdiction before field or
> high-power operation.

---

## What's in the Box

| Item | Qty | Description |
|------|-----|-------------|
| SCK-915 Radio Board | 2 | CC1110-based 915MHz transceiver (HWID 0001 and 0004 pre-provisioned) |
| USB-Serial Adapter | 1 | 3.3V TTL FTDI adapter for connecting board 0001 to your PC |
| Antennas | 2 | SMA 915MHz antennas |
| Power Supply | 1 | 5V DC power supply for board 0001 |
| Ground Station Software | 1 | SCK Ground Station Windows application |

> **Note:** Sold separately for full imaging: Raspberry Pi Pico, Arducam
> OV2640, SPI SD card adapter, microSD card (FAT32 formatted).

---

## Installation

1. **Run the installer.** Double-click `SCK_GroundStation_Setup.exe` and
   follow the prompts.
2. **Install USB-Serial driver.** Connect the USB-serial adapter. Windows
   installs the driver automatically. If not, download from
   ftdichip.com.
3. **Note your COM port.** Open Device Manager → Ports and note the COM
   port for your adapter (e.g. COM5).
4. **Launch the application.** Find SCK Ground Station in the Start menu.

---

## Quick Start

**Quick Start Checklist:**

1. Connect the USB-serial adapter to board 0001's UART1 header
2. Power board 0001 from the external 5V supply
3. Power board 0004 at least 2 feet away
4. Select the COM port, set HWID to `0001`, click **Connect**
5. Go to the **Commands** tab and click **Get Telem** — you should see a
   green checkmark response

---

## Hardware Connections

### Board 0001 — Ground Station Radio

Board 0001 connects to your Windows PC via the USB-serial adapter.

| USB-Serial Pin | Board 0001 Pin |
|-----------------|----------------|
| TX | P0_5 (UART1 RX) |
| RX | P0_4 (UART1 TX) |
| GND | GND |

### Board 0004 — Remote Radio

Board 0004 is the remote unit. It communicates via RF and connects to the
Pico for imaging.

| Board 0004 Pin | Connection |
|------------------|------------|
| P1_5 (UART0 TX) | Pico GP1 (UART0 RX) |
| P1_4 (UART0 RX) | Pico GP0 (UART0 TX) |
| GND | Pico GND |

### Power Requirements

> **Warning:** Board 0001 must be powered from the included external 5V
> supply during RF operation. The RF PA draws a current spike during TX
> that will cause a brownout if powered only from USB.

| Component | Supply | Current |
|-----------|--------|---------|
| Board 0001 / 0004 | 5V DC | ~120mA idle, ~1.6A during RF TX |
| Raspberry Pi Pico | 5V (USB) or 3.3V | ~25mA typical |
| Arducam OV2640 | 3.3V | ~50mA active |
| SD Card Adapter | 5V (VBUS) | ~100mA active |

---

## Raspberry Pi Pico Wiring

Full imaging requires a Pico with Arducam OV2640 and SPI SD card adapter.
All three share SPI0 with separate CS pins.

| Device | Signal | Pico Pin |
|--------|--------|----------|
| Arducam OV2640 | VCC | 3.3V |
| Arducam OV2640 | GND | GND |
| Arducam OV2640 | MISO | GP16 |
| Arducam OV2640 | MOSI | GP19 |
| Arducam OV2640 | SCK | GP18 |
| Arducam OV2640 | CS | GP15 |
| Arducam OV2640 | SDA | GP4 |
| Arducam OV2640 | SCL | GP5 |
| SD Card Adapter | VCC | VBUS (5V) |
| SD Card Adapter | GND | GND |
| SD Card Adapter | MISO | GP16 (shared) |
| SD Card Adapter | MOSI | GP19 (shared) |
| SD Card Adapter | SCK | GP18 (shared) |
| SD Card Adapter | CS | GP17 |

> **Warning:** The Arducam OV2640 must be connected with direct jumper
> wires — not through a breadboard. Breadboard connections are unreliable
> for SPI at the required speed.

---

## Home Tab

The Home tab shows live telemetry updated every 5 seconds when connected.

| Panel | Description |
|-------|--------------|
| Uptime | Seconds since last boot |
| Last RSSI | Received signal strength (dBm) |
| Last LQI | Link quality indicator (0–255) |
| Packets Sent/Good | RF packet counters since boot |
| UART0/1 RX Count | Packets received on each serial port |
| ADC 0–9 | Raw ADC readings from CC1110 analog inputs |

---

## Commands Tab

Provides quick access to all standard OpenLST commands.

| Button | Opcode | Description |
|--------|--------|-------------|
| Get Telem | 0x17 | Request telemetry from the addressed board |
| Reboot | 0x12 | Immediately reboot the addressed board |
| Get Time | 0x13 | Request current RTC time |
| Set Time | 0x14 | Set the RTC time |
| Get Callsign | 0x19 | Read stored callsign |
| Set Callsign | 0x1A | Store callsign (up to 8 characters) |

---

## Custom Commands Tab

Lets you send any opcode with any payload. Commands persist between
sessions. Pre-loaded Pico commands:

| Name | Opcode | Payload | Response |
|------|--------|---------|----------|
| PICO Ping | 0x20 | `00` | `PICO:ACK` |
| PICO Read Temp | 0x20 | `01` | `TEMP:13.47C` |
| PICO Snap | 0x20 | `02` | `SNAP:OK:snap_001.jpg:4617` |
| PICO List Files | 0x20 | `03` | `LIST:snap_001.jpg,...` |

---

## Files Tab

A complete SD card file manager for the remote Pico. List, download, and
delete files over RF.

> **Warning:** Set HWID to `0004` before using the Files tab. The Pico
> must be running `main.py` with an SD card inserted.

### Downloading a File

1. Click **Refresh List** to get the current SD card file list
2. Click a filename to select it
3. Click **Get File** — the file transfers in 200-byte chunks over RF
4. When complete the file is saved to the `Images\` folder and Explorer
   opens automatically

> A typical 320×240 JPEG (4–8 KB) transfers in approximately 2–5 seconds
> depending on RF conditions.

---

## Firmware Tab

Used to build, sign, and flash custom firmware. Set Project Dir to your
OpenLST source folder.

> **Warning:** Always use Clean + Build after making source code changes
> to avoid stale object files being linked.

### Flash OTA Steps

1. Set HWID to the target board
2. Verify the HEX file path and AES key
3. Click **Flash OTA**

> After flashing via serial, send 2–3 **Get Telem** commands before
> moving the board to remote RF operation.

---

## Provision Tab

Programs the bootloader onto a fresh CC1110 board using the TI CC
Debugger. One-time operation for new boards.

1. Connect CC Debugger to the board programming header
2. Enter HWID (e.g. `0001`) and AES key
3. Browse to the bootloader HEX file
4. Click **Flash Bootloader**

---

## Troubleshooting

### No reply to Get Telem on board 0001

- Check the USB-serial adapter COM port selection
- Verify TX→P0_5 and RX→P0_4 (not swapped)
- Confirm external 5V power supply is connected
- Rapid LED flash = bootloader mode; flash the radio app

### No reply to Get Telem on board 0004

- Confirm board 0001 responds first — it relays RF commands
- Boards must be at least 2 feet apart
- Try Get Telem 2–3 times — first packet after boot sometimes missed

### PICO commands return no reply

- Wait 5–10 seconds after Pico boot for camera initialization
- Verify UART wires: board 0004 P1_5→Pico GP1, P1_4→Pico GP0
- Confirm Pico is running `main.py`

### SNAP returns error

- Camera must be direct wired, not on breadboard
- SD card must be inserted and formatted as FAT32
- SD card adapter VCC must connect to VBUS (5V), not 3.3V

### Board resets during RF transmission

- External 5V supply must be connected
- Add 100µF capacitor across board power pins

---

## Specifications

| Parameter | Value |
|-----------|-------|
| RF Frequency | 915MHz (ISM band) |
| Modulation | 2-FSK with Forward Error Correction (FEC) |
| Data Rate | 7.4 kbaud |
| TX Power | +30 dBm (1W) via on-board power amplifier |
| Receiver Sensitivity | −112 dBm |
| Microcontroller | Texas Instruments CC1110 |
| Flash Memory | 32 KB |
| Interface | 3.3V TTL UART ×2, 115200 baud |
| Supply Voltage | 5V DC |
| Board Size | 50 × 60 × 5 mm |

---

*SpaceCommsKit SCK-915 User Guide v1.00*
*For updates and source code see https://spacecommskit.com*
