# Required Tools

The following third-party tools are required to build the CC1110 firmware
and program the bootloader. All are free to download.

## SDCC — Small Device C Compiler

Used to compile the CC1110 radio firmware.

- **Download:** https://sdcc.sourceforge.net/
- **Version:** 4.x recommended
- **Install to:** `C:\Program Files\SDCC\`
- After installing, verify with: `sdcc --version`

## mingw32-make — GNU Make for Windows

Used to run the OpenLST build system on Windows.

- **Download:** https://sourceforge.net/projects/mingw/
- Install MinGW, select `mingw32-make` during setup
- Add `C:\MinGW\bin` to your system PATH

## SmartRF Flash Programmer — TI Bootloader Tool

Used with the CC Debugger to flash the bootloader onto fresh CC1110 boards.
Only needed for initial board provisioning — not for routine firmware updates.

- **Download:** https://www.ti.com/tool/FLASH-PROGRAMMER
- Requires a Texas Instruments account (free)
- Install path: `C:\Program Files (x86)\Texas Instruments\SmartRF Tools\`

## CC Debugger — TI Hardware Programmer

The CC Debugger is the hardware programmer used with SmartRF Flash Programmer.

- **Purchase:** https://www.ti.com/tool/CC-DEBUGGER
- **Driver:** Installed automatically with SmartRF Flash Programmer

## Thonny — MicroPython IDE for Pico

Used to load and edit MicroPython firmware on the Raspberry Pi Pico.

- **Download:** https://thonny.org/
- Free, runs on Windows/Mac/Linux
- Select `MicroPython (Raspberry Pi Pico)` as the interpreter

## Inno Setup 6 — Windows Installer Builder

Only needed if you want to rebuild the ground station installer from source.

- **Download:** https://jrsoftware.org/isinfo.php
- Free, open source
