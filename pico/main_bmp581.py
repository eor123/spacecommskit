# ============================================================================
# main.py — SCK-915 Pico Payload Pipeline
# SpaceCommsKit — https://spacecommskit.com
# MicroPython v1.27 on Raspberry Pi Pico (RP2040)
#
# PURPOSE:
#   This is the firmware for the Raspberry Pi Pico on the SCK-915 payload
#   board. It runs the payload pipeline — receiving commands from the CC1110
#   radio board over UART0, processing them, and returning responses.
#
# SYSTEM ARCHITECTURE:
#   Ground Station (C#) ←RF→ CC1110 ←UART0→ Pico (this file)
#                                              ├── OV2640 camera (SPI + I2C)
#                                              ├── SD card (SPI)
#                                              ├── GPS NEO-6M (UART1)
#                                              └── MS5611 altimeter (I2C1, GPIO2/3)
#
# COMMAND PROTOCOL:
#   All UART0 communication uses ESP framing:
#     [0x22][0x69][length][payload bytes...]
#   The CC1110 sends a command payload, this firmware processes it and
#   sends a response payload back. The CC1110 then relays that response
#   to the ground station as an RF ACK packet.
#
# COMMAND TABLE:
#   Byte 0 of payload = sub-opcode:
#   0x00 = PING        → "PICO:ACK"
#   0x01 = READ_TEMP   → "TEMP:xx.xxC"
#   0x02 = SNAP        → "SNAP:OK:<filename>:<bytes>"
#   0x03 = LIST        → "LIST:<file1>,<file2>,..." (chunked if needed)
#   0x04 = GET_INFO    → "INFO:<filename>:<bytes>:<chunks>"
#   0x05 = GET_CHUNK   → "CHUNK:<index>:<200 bytes data>"
#   0x06 = DELETE      → "DEL:OK:<filename>"
#   0x07 = GET_GPS     → "GPS:<lat>,<lon>,<gps_alt>,<sats>,<fix>,<hpa>,<baro_alt>,<temp_c>"
#   0x08 = GET_BARO    → "BARO:<hpa>,<baro_alt>,<temp_c>"
#   0x09 = BEACON_CTRL → "BEACON:ON" or "BEACON:OFF"
#
# HARDWARE VARIANTS:
#   SCK-915 Prototype  — hand-wired jumper board (see TIMING WARNING below)
#   SCK-PBL-1          — production PCB, all connections on-board
#
# !! PROTOTYPE BOARD TIMING WARNING !!
# ============================================================
# If you are developing on the hand-wired SCK-915 prototype board
# (NOT the SCK-PBL-1 PCB), you may experience timeout and NACK issues
# due to SPI bus noise and UART timing problems caused by jumper wire
# capacitance and crosstalk.
#
# KNOWN ISSUES ON PROTOTYPE BOARD:
#
# 1. SPI SPEED — SD card and camera SPI bus
#    The SPI bus runs at 400kHz on the prototype instead of the normal
#    1.32MHz production speed. Higher speeds cause SD card errors and
#    camera capture failures due to signal integrity issues on the
#    jumper wire connections. DO NOT increase SPI baudrate on the
#    prototype board until you have verified clean signal integrity
#    with an oscilloscope or logic analyzer.
#
#    On the SCK-PBL-1 production board with direct PCB traces, the SPI
#    bus can run at 1.32MHz (the sdcard.py design speed) or higher.
#    When you move to SCK-PBL-1 hardware:
#      - Change baudrate=400000 to baudrate=1320000 in SPI init below
#      - Change baudrate=100000 to baudrate=400000 in mount_sd() slow init
#      - Test SD card reliability at 1.32MHz before deploying
#
# 2. UART TIMING — CC1110 to Pico communication
#    The CC1110 has tight response timeouts (PICO_TIMEOUT in board.c).
#    On the prototype, jumper wire noise can slow UART responses enough
#    to cause intermittent NACKs. If you see commands that work sometimes
#    but fail other times, increase PICO_TIMEOUT_OUTER in board.c.
#
# 3. SPI BUS SHARING — camera and SD card share the SPI bus
#    The OV2640 camera and SD card share SPI0. The CS pins (GPIO15 for
#    camera, GPIO16 for SD) must be managed carefully. Both CS pins must
#    be HIGH before switching between devices. The code includes explicit
#    CS management — do not remove the cs(1) calls before switching.
#
# These issues are specific to the prototype board. The SCK-PBL-1
# production board resolves all of them through proper PCB routing.
#
# [SCK-DEV: TIMING] — see SpaceCommsKit Developer Guide Section 3.3
# ============================================================================
#
# TO ADD A NEW COMMAND:
# ============================================================
# 1. Define a new sub-opcode constant (e.g. CMD_MY_COMMAND = 0x0A)
# 2. Add a new elif block in the main command dispatch loop at the bottom
# 3. Format a response string and call send_esp(response.encode())
# 4. If the response is larger than MAX_PAYLOAD (251 bytes), implement
#    chunking — see CMD_LIST and CMD_GET_CHUNK for the chunking pattern
# 5. Add the matching opcode in the C# ground station CommandOpcode enum
# 6. Add the command handler in the C# ground station command processor
# 7. Document the new command in the SpaceCommsKit Developer Guide
#
# [SCK-DEV: ADD_COMMAND] — see SpaceCommsKit Developer Guide Section 3.1
# ============================================================================
#
# CHUNKING LARGE RESPONSES:
# ============================================================
# The maximum RF packet payload is 251 bytes (MAX_PAYLOAD). For responses
# larger than this, use the chunking pattern:
#
# SENDER (Pico) side:
#   - Send first chunk with prefix "LIST:" (or equivalent)
#   - Send subsequent chunks with prefix "LIST+:" (continuation marker)
#   - Final chunk has no trailing comma/separator (signals end of data)
#   - Each chunk is a separate send_esp() call
#
# RECEIVER (C# ground station) side:
#   - Accumulate chunks until a packet with no trailing separator arrives
#   - Reassemble by stripping prefixes and concatenating
#
# See CMD_LIST implementation below for the complete chunking pattern.
# See CMD_GET_CHUNK for binary data chunking (images, files).
#
# [SCK-DEV: CHUNKING] — see SpaceCommsKit Developer Guide Section 3.2
# ============================================================================

from machine import UART, ADC, Pin, SPI, I2C
import time
import uos
import math
import sdcard

# ── ESP Framing Constants ──────────────────────────────────────────────────
# These must match the values in the CC1110 uart.h and board.c
# Do not change without updating both sides of the UART link
ESP_START_0   = 0x22   # First start byte of ESP frame
ESP_START_1   = 0x69   # Second start byte of ESP frame
MAX_PAYLOAD   = 251    # Maximum payload bytes in one RF packet
                       # Responses larger than this must be chunked
                       # [SCK-DEV: CHUNKING] — see Developer Guide Section 3.2

# ── Sub-opcodes ────────────────────────────────────────────────────────────
# These must match the values in the C# ground station CommandOpcode enum
# and the PICO_SUB_* defines in board.c
# [SCK-DEV: ADD_COMMAND] — add new opcodes here when extending the command set
CMD_PING        = 0x00  # Health check — returns "PICO:ACK"
CMD_READ_TEMP   = 0x01  # Onboard temperature sensor
CMD_SNAP        = 0x02  # Capture JPEG image to SD card
CMD_LIST        = 0x03  # List files on SD card (chunked response)
CMD_GET_INFO    = 0x04  # Get file size and chunk count
CMD_GET_CHUNK   = 0x05  # Get one 200-byte chunk of a file
CMD_DELETE      = 0x06  # Delete a file from SD card
CMD_GET_GPS     = 0x07  # Get fused GPS + barometric data
CMD_GET_BARO    = 0x08  # Get barometric pressure and altitude only
CMD_BEACON_CTRL = 0x09  # Enable or disable autonomous GPS beacon

# ── Chunk Size ────────────────────────────────────────────────────────────
# Each GET_CHUNK response carries this many bytes of file data.
# Must leave room for the "CHUNK:NNN:" prefix in the 251-byte packet.
# 200 bytes leaves 51 bytes for the prefix — sufficient for "CHUNK:65535:"
# [SCK-DEV: CHUNKING] — see SpaceCommsKit Developer Guide Section 3.2
CHUNK_SIZE    = 200

# ── Beacon Configuration ──────────────────────────────────────────────────
# GPS beacon transmits autonomously every GPS_BEACON_INTERVAL_MS milliseconds
# when no command is being processed and beacon is enabled.
# Disable beacon during sensitive operations (e.g. high-rate file transfer)
# using CMD_BEACON_CTRL (0x09) from the ground station.
GPS_BEACON_INTERVAL_MS = 10000  # 10 seconds between beacon transmissions
_beacon_enabled = True           # Controlled by CMD_BEACON_CTRL at runtime

# ── UART0 — CC1110 Communication ──────────────────────────────────────────
# UART0 connects to the CC1110 radio board over the SCK-PBL-1 header.
# Baud rate must match the CC1110 UART0 configuration in the OpenLST firmware.
# TX = GPIO0, RX = GPIO1 (fixed by Pico hardware UART0 mapping)
uart = UART(0, baudrate=115200, tx=Pin(0), rx=Pin(1))

# ── UART1 — GPS NEO-6M ─────────────────────────────────────────────────────
# NEO-6M GPS module default baud rate is 9600 bps.
# TX = GPIO8 (Pico → GPS, used only for UBX config commands)
# RX = GPIO9 (GPS → Pico, receives NMEA sentences continuously)
gps_uart = UART(1, baudrate=9600, tx=Pin(8), rx=Pin(9))

# ── Onboard Status LED ─────────────────────────────────────────────────────
# Pico built-in LED on GPIO25 — used for boot confirmation blinks
led = Pin(25, Pin.OUT)

def blink(times=1):
    """Blink the onboard LED for visual feedback."""
    for _ in range(times):
        led.on(); time.sleep_ms(80)
        led.off(); time.sleep_ms(80)

# ── Status LEDs — Payload Board ───────────────────────────────────────────
# These LEDs provide hardware status indication without needing a serial
# connection. They are driven by GPIO pins on the Pico.
# [SCK-DEV: BOARD_INIT] — LED assignments are fixed by PCB layout on SCK-PBL-1
led_camera = Pin(10, Pin.OUT, value=0)  # GREEN  — Camera initialized OK
led_sd     = Pin(11, Pin.OUT, value=0)  # GREEN  — SD card mounted OK
led_gps    = Pin(12, Pin.OUT, value=0)  # BLUE   — GPS: slow flash=searching, solid=fix
led_alti   = Pin(13, Pin.OUT, value=0)  # YELLOW — MS5611 altimeter initialized OK
led_fault  = Pin(14, Pin.OUT, value=0)  # RED    — Any subsystem failed

# Fault flags — any True triggers the red fault LED
# These are set/cleared by each subsystem init function
_fault_camera = False
_fault_sd     = False
_fault_alti   = False

def update_fault_led():
    """Update the red fault LED based on current fault flags."""
    led_fault.value(1 if (_fault_camera or _fault_sd or _fault_alti) else 0)

# ── GPS LED State Machine ─────────────────────────────────────────────────
# GPS LED flashes slowly while searching for fix, goes solid when fix acquired
_gps_led_last_toggle = time.ticks_ms()
_gps_led_state       = False
GPS_SEARCH_FLASH_MS  = 500  # Toggle every 500ms while searching

def update_gps_led():
    """Non-blocking GPS LED update — call frequently from main loop."""
    global _gps_led_last_toggle, _gps_led_state
    if gps_fix['fix']:
        led_gps.on()   # Solid when fix acquired
    else:
        now = time.ticks_ms()
        if time.ticks_diff(now, _gps_led_last_toggle) >= GPS_SEARCH_FLASH_MS:
            _gps_led_state = not _gps_led_state
            led_gps.value(_gps_led_state)
            _gps_led_last_toggle = now

# ── Onboard Temperature Sensor ────────────────────────────────────────────
# RP2040 internal temperature sensor on ADC channel 4
# Used by CMD_READ_TEMP (0x01) for basic health telemetry
temp_sensor       = ADC(4)
CONVERSION_FACTOR = 3.3 / 65535

def read_temp_celsius():
    """Read RP2040 internal die temperature in Celsius."""
    reading = temp_sensor.read_u16() * CONVERSION_FACTOR
    return round(27 - (reading - 0.706) / 0.001721, 2)

# ── SPI Bus — Shared Camera and SD Card ──────────────────────────────────
#
# !! PROTOTYPE vs PRODUCTION BOARD SPI SPEED !!
# ============================================================
# PROTOTYPE BOARD (hand-wired / long lead connections):
#   Run at 100kHz maximum. Long leads add capacitance and inductance
#   which cause signal integrity issues at higher speeds. This was
#   verified on the hand-wired SD card adapter with direct soldered leads.
#   Once verified working at 100kHz, try stepping up to 400kHz then
#   1.32MHz if signal integrity allows.
#
# SCK-PBL-1 PRODUCTION BOARD (direct PCB traces):
#   Increase baudrate to 1320000 (1.32MHz) — the sdcard.py design speed.
#   This is safe on the PCB and improves file transfer performance.
#
# [SCK-DEV: TIMING] — see SpaceCommsKit Developer Guide Section 3.3
#
# GPIO ASSIGNMENTS (direct wired SD adapter — confirmed wiring):
#   GPIO18 = SCLK  (SCK)
#   GPIO19 = MOSI  (TX) — valid SPI0 MOSI pin on RP2040
#   GPIO16 = MISO  (RX) — valid SPI0 MISO pin on RP2040
#   GPIO17 = SD CS      — software controlled chip select
#   GPIO15 = Camera CS  — software controlled chip select
#
# SD CARD ADAPTER WIRING (direct soldered leads):
#   SD pin 1 (DAT2) → 10kΩ pullup to 3.3V (passive, no GPIO)
#   SD pin 2 (CS)   → GPIO17
#   SD pin 3 (MOSI) → GPIO19
#   SD pin 4 (VDD)  → 3.3V
#   SD pin 5 (SCLK) → GPIO18
#   SD pin 6 (VSS)  → GND
#   SD pin 7 (MISO) → GPIO16
#   SD pin 8 (DAT1) → 10kΩ pullup to 3.3V (passive, no GPIO)
spi = SPI(0, baudrate=400000, polarity=0, phase=0, bits=8,
          firstbit=SPI.MSB, sck=Pin(18), mosi=Pin(19), miso=Pin(16))

cam_cs = Pin(15, Pin.OUT, value=1)  # Camera CS — start deselected (HIGH)
sd_cs  = Pin(17, Pin.OUT, value=1)  # SD CS    — start deselected (HIGH)

# ── SPI1 — SCK-2400 High Speed Payload Interface ──────────────────────────
# NOT YET IMPLEMENTED — wiring confirmed 2026-06-05, implement when SCK-2400
# SPI command handler is added to firmware.
#
# The SCK-2400 exposes SSI1 on DIO23/24/25/26 as the high-speed payload SPI
# bus. This Pico connects to it via SPI1 on GPIO10-13, which are currently
# unused and don't conflict with SPI0 (camera/SD on GPIO16/18/19).
#
# Pin mapping (SCK-2400 → Pico):
#   SCK-2400 DIO23 (SSI1 SCLK) → Pico GPIO10 (Pin 14)
#   SCK-2400 DIO24 (SSI1 PICO) → Pico GPIO11 (Pin 15)  [MOSI]
#   SCK-2400 DIO25 (SSI1 POCI) → Pico GPIO12 (Pin 16)  [MISO]
#   SCK-2400 DIO26 (SSI1 CS)   → Pico GPIO13 (Pin 17)
#
# When implementing, init with:
#   spi_sck2400 = SPI(1, baudrate=1000000, polarity=0, phase=0,
#                     sck=Pin(10), mosi=Pin(11), miso=Pin(12))
#   sck2400_cs  = Pin(13, Pin.OUT, value=1)
#
# CS follows the same active-low software pattern as cam_cs and sd_cs above.
# SCK-2400 SSI1 is configured Four Pin Active Low in SysConfig.
# Start at 1MHz and verify signal integrity before increasing speed.
# [SCK-DEV: SCK2400_SPI] — implement SPI command handler when ready

# ── I2C Buses ─────────────────────────────────────────────────────────────
# I2C0: OV2640 camera configuration register interface
# I2C1: MS5611-01BA03 barometric pressure / altitude sensor
#        GPIO2 = SDA, GPIO3 = SCL (same physical pins as previous BMP581)
#        PS pin on MS5611 must be tied HIGH on PCB to select I2C mode
#        MS5611 max I2C clock = 400kHz
i2c0 = I2C(0, sda=Pin(4), scl=Pin(5), freq=100000)  # Camera: 100kHz
i2c1 = I2C(1, sda=Pin(2), scl=Pin(3), freq=400000)  # BMP581: 400kHz (same pins as MS5611)
CAM_ADDR = 0x30  # OV2640 I2C address (fixed by hardware)

# ── SD Card Mount ─────────────────────────────────────────────────────────
# SD card uses FAT filesystem via MicroPython uos.VfsFat
# Files are stored under /sd/ path after mounting
sd_mounted = False

def mount_sd():
    """
    Mount the SD card. Returns True on success, False on failure.

    Uses slow SPI speed (100kHz) for SD card initialization as required
    by the SD card SPI specification. After successful init, SPI speed
    is raised to the operating speed.

    PROTOTYPE BOARD NOTE:
    If mount fails intermittently on the prototype board, try reducing
    spi_slow baudrate further (e.g. 50000) to compensate for jumper
    wire capacitance. On SCK-PBL-1, the slow init speed can be raised
    to 400kHz and the operating speed to 1.32MHz.

    [SCK-DEV: TIMING] — see SpaceCommsKit Developer Guide Section 3.3
    """
    global sd_mounted, spi, _fault_sd
    if sd_mounted:
        return True
    try:
        # Ensure both CS pins are deselected before init
        # Camera CS must be HIGH during SD init to prevent bus conflict
        cam_cs(1); sd_cs(1)
        time.sleep_ms(100)

        # Power-up settle sequence — same as sd_test.py verified working
        # Send 80 clock pulses with CS HIGH then settle before init
        # This is required for reliable init on direct-wired SD adapters
        spi_settle = SPI(0, baudrate=10000, polarity=0, phase=0, bits=8,
                         firstbit=SPI.MSB, sck=Pin(18), mosi=Pin(19), miso=Pin(16))
        sd_cs(1)
        spi_settle.write(b'\xFF' * 10)
        time.sleep_ms(100)

        # SD card SPI initialization requires slow clock per SD spec
        # 100kHz safe for direct-wired connections
        spi_slow = SPI(0, baudrate=100000, polarity=0, phase=0, bits=8,
                       firstbit=SPI.MSB, sck=Pin(18), mosi=Pin(19), miso=Pin(16))
        sd  = sdcard.SDCard(spi_slow, sd_cs)
        vfs = uos.VfsFat(sd)
        uos.mount(vfs, '/sd')
        sd_mounted = True
        _fault_sd  = False
        led_sd.on()
        update_fault_led()
        print("SD mounted OK")

        # Operating speed — 400kHz confirmed working on direct-wired adapter
        # SCK-PBL-1 production PCB: raise to 1320000
        spi = SPI(0, baudrate=400000, polarity=0, phase=0,
                  bits=8, firstbit=SPI.MSB,
                  sck=Pin(18), mosi=Pin(19), miso=Pin(16))
        cam_cs(1); sd_cs(1)
        time.sleep_ms(50)
        return True
    except Exception as e:
        _fault_sd = True
        led_sd.off()
        update_fault_led()
        print(f"SD mount failed: {e}")
        return False

# ============================================================================
# BAROMETRIC PRESSURE / ALTITUDE SENSOR DRIVER
# ============================================================================
#
# ACTIVE DRIVER: MS5611-01BA03 (TE Connectivity / MEAS Switzerland)
#   Part number: MS561101BA03-50
#   Interface:   I2C on GPIO2 (SDA) and GPIO3 (SCL)
#   PCB note:    PS pin must be tied HIGH on the PCB to select I2C mode
#
# PRESERVED FOR REFERENCE (commented out below):
#   BMP581 driver — used on SCK-PBL-1 v1.0 payload board
#   The BMP581 code is kept here for reference and for developers who
#   want to revert to BMP581 hardware. Both drivers expose the same
#   public interface: init_baro(), read_baro(), baro_packet(), baro_data{}
#   so the rest of main.py requires no changes when switching sensors.
#
# DATA FUSION:
#   GPS altitude and barometric altitude are both reported in GET_GPS
#   responses. For HAB missions, barometric altitude is more reliable
#   at low altitudes (<3000m) while GPS altitude is better at high altitude.
#
# SEA LEVEL PRESSURE:
#   SEA_LEVEL_HPA defaults to standard atmosphere (1013.25 hPa).
#   For accurate altitude update this to local QNH before flight.
#
# [SCK-DEV: ADD_COMMAND] — to expose QNH setting as a ground station command,
# add CMD_SET_QNH = 0x0A in the command table and update SEA_LEVEL_HPA here.
# ============================================================================

# ── Shared baro state — used by both MS5611 and BMP581 drivers ────────────
# Do not change these names — they are referenced throughout main.py
SEA_LEVEL_HPA = 1013.25   # Standard atmosphere — update for local QNH
baro_ready    = False
baro_data     = {'hpa': 0.0, 'alt_m': 0.0, 'temp_c': 0.0, 'valid': False}

# ============================================================================
# MS5611-01BA03 DRIVER (ACTIVE)
# ============================================================================
#
# The MS5611 is a 24-bit ADC barometric sensor with factory calibration
# coefficients stored in on-chip PROM. Unlike sensors that return pre-
# compensated values, the MS5611 returns raw ADC counts that must be
# compensated using the PROM coefficients via the datasheet formula.
#
# DRIVER OVERVIEW:
#   1. init_baro()  — reset chip, read 6 PROM calibration coefficients
#   2. read_baro()  — trigger D2 (temp) and D1 (pressure) conversions,
#                     read 24-bit ADC results, apply second-order
#                     temperature compensation formula
#   3. baro_packet() — format result string for RF response
#
# I2C ADDRESS:
#   0x77 — default (CSB pin floating or tied to GND — 2.2kΩ pulldown built in)
#   0x76 — alternate (CSB tied to VDD)
#   Address formula: 0b111011Cx where C = complement of CSB pin state
#
# CONVERSION TIME (OSR=4096 — maximum resolution):
#   Each conversion (D1 and D2) takes approximately 9.04ms
#   Total read cycle = ~20ms (D2 convert + D1 convert + two ADC reads)
#   This is called every BARO_READ_INTERVAL_MS = 2000ms — no timing issue
#
# PROM CALIBRATION COEFFICIENTS:
#   C1 = Pressure sensitivity (SENS_T1)
#   C2 = Pressure offset (OFF_T1)
#   C3 = Temperature coefficient of pressure sensitivity (TCS)
#   C4 = Temperature coefficient of pressure offset (TCO)
#   C5 = Reference temperature (T_REF)
#   C6 = Temperature coefficient of temperature (TEMPSENS)
#
# COMPENSATION FORMULA (from MS5611 datasheet AN520):
#   dT   = D2 - C5 * 2^8
#   TEMP = 2000 + dT * C6 / 2^23          (in 0.01°C units)
#   OFF  = C2 * 2^17 + (C4 * dT) / 2^6
#   SENS = C1 * 2^16 + (C3 * dT) / 2^7
#   P    = (D1 * SENS / 2^21 - OFF) / 2^15  (in 0.01 mbar units)
#
#   Second-order temperature compensation applied when TEMP < 2000 (20.00°C)
#   and when TEMP < -1500 (-15.00°C) for very low temperatures.
# ============================================================================

# MS5611 I2C address — determined by CSB pin connection on PCB
# CSB tied to VDD (3.3V) → address = 0x76
# CSB tied to GND        → address = 0x77
# Formula: 111011Cx where C = complement of CSB
# Your PCB: CSB tied to 3.3V → C=0 → 0x76
# MS5611_ADDR = 0x76
# 
# MS5611 command bytes (from datasheet Table 1)
# _MS_RESET      = 0x1E   # Reset — must send before first use
# _MS_CONV_D1    = 0x48   # Convert D1 (pressure) at OSR=4096 (~9ms)
# _MS_CONV_D2    = 0x58   # Convert D2 (temperature) at OSR=4096 (~9ms)
# _MS_ADC_READ   = 0x00   # Read ADC result (3 bytes)
# _MS_PROM_BASE  = 0xA0   # PROM read base address (0xA0..0xAE, step 2)
# 
# PROM calibration coefficients — loaded once at init_baro()
# These are factory-programmed values unique to each sensor
# _ms_C = [0] * 7   # C[1]..C[6] used, C[0] is factory data / CRC word
# 
# def _ms_read_prom(coeff_index):
#     """Read one 16-bit calibration coefficient from MS5611 PROM.
#     coeff_index: 0-7 (addresses 0xA0, 0xA2, 0xA4, 0xA6, 0xA8, 0xAA, 0xAC, 0xAE)
#     C[1]..C[6] are the calibration coefficients used in compensation.
#     """
#     cmd = _MS_PROM_BASE + (coeff_index * 2)
#     i2c1.writeto(MS5611_ADDR, bytes([cmd]))
#     d = i2c1.readfrom(MS5611_ADDR, 2)
#     return (d[0] << 8) | d[1]
# 
# def _ms_convert_and_read(cmd):
#     """Trigger a conversion and read the 24-bit ADC result.
#     cmd: _MS_CONV_D1 (pressure) or _MS_CONV_D2 (temperature)
#     OSR=4096 requires at least 9.04ms conversion time.
#     """
#     i2c1.writeto(MS5611_ADDR, bytes([cmd]))
#     time.sleep_ms(10)   # 10ms > 9.04ms minimum for OSR=4096
#     i2c1.writeto(MS5611_ADDR, bytes([_MS_ADC_READ]))
#     d = i2c1.readfrom(MS5611_ADDR, 3)
#     return (d[0] << 16) | (d[1] << 8) | d[2]
# 
# def init_baro():
#     """
#     Initialize MS5611. Sends reset and reads PROM calibration coefficients.
#     Sets baro_ready flag and updates fault LED.
#     Must be called once at boot before read_baro() will work.
#     """
#     global baro_ready, _fault_alti, _ms_C
#     try:
        # Reset the MS5611 — required after power-on per datasheet
        # Loads PROM data into internal registers
#         i2c1.writeto(MS5611_ADDR, bytes([_MS_RESET]))
#         time.sleep_ms(3)   # 2.8ms reload time per datasheet
# 
        # Read all 8 PROM words — C[1] through C[6] are calibration coefficients
        # C[0] is factory data, C[7] contains CRC (not checked here)
#         for i in range(8):
#             _ms_C[i] = _ms_read_prom(i) if i < 7 else 0
# 
        # Verify calibration loaded — C[1] should never be 0 on a real sensor
        # A value of 0 means the I2C read failed or sensor is not responding
#         if _ms_C[1] == 0:
#             raise OSError("MS5611 PROM read failed — C1 is zero")
# 
#         baro_ready  = True
#         _fault_alti = False
#         led_alti.on()
#         update_fault_led()
#         print(f"MS5611 altimeter ready — C1={_ms_C[1]} C2={_ms_C[2]}")
#         return True
# 
#     except Exception as e:
#         baro_ready  = False
#         _fault_alti = True
#         led_alti.off()
#         update_fault_led()
#         print(f"MS5611 init failed: {e}")
#         return False
# 
# def read_baro():
#     """
#     Read pressure, altitude, and temperature from MS5611.
#     Applies full second-order temperature compensation per datasheet AN520.
#     Takes approximately 20ms to complete (two conversions at OSR=4096).
#     Non-blocking in the sense that it does not spin-wait — but it does
#     sleep_ms(10) twice for conversion time. Call from the 2-second
#     background read interval, not from a latency-sensitive path.
#     """
#     global baro_data
#     if not baro_ready:
#         baro_data['valid'] = False
#         return False
#     try:
        # ── Step 1: Read raw temperature (D2) ─────────────────────────────
#         D2 = _ms_convert_and_read(_MS_CONV_D2)
# 
        # ── Step 2: Read raw pressure (D1) ────────────────────────────────
#         D1 = _ms_convert_and_read(_MS_CONV_D1)
# 
        # ── Step 3: First-order temperature compensation ───────────────────
        # Reference: MS5611-01BA03 datasheet Section 4.1
        # All integer arithmetic — no floating point until final conversion
#         C1, C2, C3, C4, C5, C6 = (_ms_C[1], _ms_C[2], _ms_C[3],
#                                     _ms_C[4], _ms_C[5], _ms_C[6])
# 
#         dT   = D2 - C5 * (1 << 8)
#         TEMP = 2000 + dT * C6 // (1 << 23)   # in 0.01°C units
# 
#         OFF  = C2 * (1 << 17) + (C4 * dT) // (1 << 6)
#         SENS = C1 * (1 << 16) + (C3 * dT) // (1 << 7)
# 
        # ── Step 4: Second-order temperature compensation ──────────────────
        # Required for accuracy when TEMP < 2000 (20.00°C)
        # For HAB / CubeSat use cases temperatures below 0°C are common
#         T2    = 0
#         OFF2  = 0
#         SENS2 = 0
# 
#         if TEMP < 2000:
            # Low temperature correction
#             T2    = dT * dT // (1 << 31)
#             OFF2  = 5 * (TEMP - 2000) ** 2 // 2
#             SENS2 = 5 * (TEMP - 2000) ** 2 // 4
# 
#             if TEMP < -1500:
                # Very low temperature correction (below -15°C)
#                 OFF2  += 7 * (TEMP + 1500) ** 2
#                 SENS2 += 11 * (TEMP + 1500) ** 2 // 2
# 
#         TEMP -= T2
#         OFF  -= OFF2
#         SENS -= SENS2
# 
        # ── Step 5: Final pressure calculation ────────────────────────────
#         P = (D1 * SENS // (1 << 21) - OFF) // (1 << 15)  # in 0.01 mbar units
# 
        # ── Step 6: Convert to engineering units ──────────────────────────
#         press_hpa = P / 100.0        # 0.01 mbar → hPa (same as mbar)
#         temp_c    = TEMP / 100.0     # 0.01°C → °C
# 
        # Barometric altitude formula
#         alt_m = 44330.0 * (1.0 - math.pow(
#             press_hpa / SEA_LEVEL_HPA, 0.1903))
# 
#         baro_data['hpa']    = round(press_hpa, 2)
#         baro_data['alt_m']  = round(alt_m, 1)
#         baro_data['temp_c'] = round(temp_c, 2)
#         baro_data['valid']  = True
#         return True
# 
#     except Exception as e:
#         baro_data['valid'] = False
#         print(f"MS5611 read error: {e}")
#         return False
# 
# def baro_packet():
#     """Format barometric data as response string for CMD_GET_BARO."""
#     if baro_data['valid']:
#         return (f"BARO:{baro_data['hpa']:.2f},"
#                 f"{baro_data['alt_m']:.1f},"
#                 f"{baro_data['temp_c']:.2f}").encode()
#     return b"BARO:ERR:NOT_READY"

# ============================================================================
# BMP581 DRIVER — PRESERVED FOR REFERENCE (NOT ACTIVE)
# ============================================================================
# This driver was used on SCK-PBL-1 v1.0 payload board with the Bosch BMP581
# sensor. The new payload board uses the MS5611-01BA03 (driver above).
#
# To revert to BMP581:
#   1. Comment out the MS5611 driver above
#   2. Uncomment the BMP581 driver below
#   3. No other changes needed — same public interface
#
# BMP581 I2C address:
#   0x47 — SparkFun breakout default (SDO tied to 3.3V)
#   0x46 — alternate (SDO tied to GND)
#
# [SCK-DEV: BOARD_CONFIG] — hardware variant documentation
# ============================================================================

# ── BMP581 DRIVER (ACTIVE for prototype testing) ──

BMP581_ADDR    = 0x47
BMP581_CHIP_ID = 0x50

_BMP_CHIP_ID    = 0x01
_BMP_TEMP_XL    = 0x1D
_BMP_PRESS_XL   = 0x20
_BMP_OSR_CONFIG = 0x36
_BMP_ODR_CONFIG = 0x37

def _bmp_read(reg, nbytes=1):
    i2c1.writeto(BMP581_ADDR, bytes([reg]))
    return i2c1.readfrom(BMP581_ADDR, nbytes)

def _bmp_write(reg, val):
    i2c1.writeto(BMP581_ADDR, bytes([reg, val]))
    time.sleep_ms(5)

def init_baro():
    """Initialize BMP581. Sets baro_ready and updates fault LED."""
    global baro_ready, _fault_alti
    try:
        chip_id = _bmp_read(_BMP_CHIP_ID)[0]
        if chip_id != BMP581_CHIP_ID:
            raise OSError(f"BMP581 ID mismatch: 0x{chip_id:02X}")
        _bmp_write(_BMP_OSR_CONFIG, 0b01011000)  # pressure x4, temp x1
        _bmp_write(_BMP_ODR_CONFIG, 0b01100101)  # normal continuous ~50Hz
        time.sleep_ms(100)
        baro_ready  = True
        _fault_alti = False
        led_alti.on()
        update_fault_led()
        print("BMP581 altimeter ready")
        return True
    except Exception as e:
        baro_ready  = False
        _fault_alti = True
        led_alti.off()
        update_fault_led()
        print(f"BMP581 init failed: {e}")
        return False

def read_baro():
    """Read pressure, altitude, and temperature from BMP581."""
    global baro_data
    if not baro_ready:
        baro_data['valid'] = False
        return False
    try:
        td    = _bmp_read(_BMP_TEMP_XL, 3)
        raw_t = (td[2] << 16) | (td[1] << 8) | td[0]
        if raw_t & 0x800000: raw_t -= 0x1000000
        temp_c = raw_t / 65536.0

        pd    = _bmp_read(_BMP_PRESS_XL, 3)
        raw_p = (pd[2] << 16) | (pd[1] << 8) | pd[0]
        if raw_p & 0x800000: raw_p -= 0x1000000
        press_pa  = raw_p / 64.0
        press_hpa = press_pa / 100.0
        alt_m     = 44330.0 * (1.0 - math.pow(
            press_pa / (SEA_LEVEL_HPA * 100.0), 0.1903))

        baro_data['hpa']    = round(press_hpa, 2)
        baro_data['alt_m']  = round(alt_m, 1)
        baro_data['temp_c'] = round(temp_c, 2)
        baro_data['valid']  = True
        return True
    except Exception as e:
        baro_data['valid'] = False
        print(f"BMP581 read error: {e}")
        return False

def baro_packet():
    """Format barometric data as response string for CMD_GET_BARO."""
    if baro_data['valid']:
        return (f"BARO:{baro_data['hpa']:.2f},"
                f"{baro_data['alt_m']:.1f},"
                f"{baro_data['temp_c']:.2f}").encode()
    return b"BARO:ERR:NOT_READY"

# ── END BMP581 DRIVER ──

# ============================================================================
# GPS DRIVER — NEO-6M NMEA PARSER
# ============================================================================
#
# Parses NMEA sentences from the NEO-6M GPS module on UART1.
# Supports GPRMC/GNRMC (position, speed, heading) and GPGGA/GNGGA (altitude).
#
# UBX AIRBORNE MODE:
#   The GPS is configured for airborne dynamic mode via a UBX binary command
#   sent at startup. This improves accuracy and reliability at HAB altitudes
#   (>12km) where the default pedestrian/automotive modes give poor results.
#   The UBX command is sent once in init_gps() — no acknowledgment is checked
#   as the NEO-6M processes it silently.
#
# POLL MODEL:
#   poll_gps() must be called frequently from the main loop to drain the
#   UART1 buffer and update gps_fix. The recv_esp() function calls poll_gps()
#   during its wait loop so GPS is updated even while waiting for commands.
#   If you add long-running operations, call poll_gps() periodically within them.
#
# [SCK-DEV: BEACON] — see SpaceCommsKit Developer Guide Section 3.6
# ============================================================================
gps_fix = {
    'lat': 0.0, 'lon': 0.0, 'alt': 0.0,
    'sats': 0,  'fix': False,
    'time': '', 'date': '',
}

# UBX-CFG-NAV5 command to set airborne (<1g) dynamic model
# This improves GPS performance at HAB altitudes above 12km
# Reference: u-blox NEO-6M Protocol Specification, Section 32.10.14
_UBX_AIRBORNE = bytes([
    0xB5, 0x62, 0x06, 0x24, 0x24, 0x00,
    0xFF, 0xFF, 0x06, 0x03,
    0x00, 0x00, 0x00, 0x00, 0x10, 0x27, 0x00, 0x00,
    0x05, 0x00, 0xFA, 0x00, 0xFA, 0x00, 0x64, 0x00,
    0x2C, 0x01, 0x00, 0x3C,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x16, 0xDC,
])

def init_gps():
    """Send UBX airborne mode config and flush UART buffer."""
    print("GPS: configuring airborne dynamic mode...")
    gps_uart.write(_UBX_AIRBORNE)
    time.sleep_ms(500)
    while gps_uart.any():
        gps_uart.read(gps_uart.any())
    print("GPS: ready")

def _nmea_checksum_ok(sentence):
    """Verify NMEA sentence checksum. Returns True if valid."""
    try:
        star = sentence.rindex('*')
        body = sentence[1:star]
        expected = int(sentence[star+1:star+3], 16)
        calc = 0
        for c in body: calc ^= ord(c)
        return calc == expected
    except: return False

def _parse_lat(val, hemi):
    if not val: return 0.0
    r = float(val[:2]) + float(val[2:]) / 60.0
    return round(-r if hemi == 'S' else r, 6)

def _parse_lon(val, hemi):
    if not val: return 0.0
    r = float(val[:3]) + float(val[3:]) / 60.0
    return round(-r if hemi == 'W' else r, 6)

def _parse_gprmc(fields):
    """Parse GPRMC/GNRMC sentence for position and fix status."""
    global gps_fix
    try:
        if len(fields) < 10 or fields[2] != 'A':
            gps_fix['fix'] = False; return
        gps_fix['lat']  = _parse_lat(fields[3], fields[4])
        gps_fix['lon']  = _parse_lon(fields[5], fields[6])
        gps_fix['time'] = fields[1]
        gps_fix['date'] = fields[9]
        gps_fix['fix']  = True
    except Exception as e: print(f"GPRMC error: {e}")

def _parse_gpgga(fields):
    """Parse GPGGA/GNGGA sentence for altitude and satellite count."""
    global gps_fix
    try:
        if len(fields) < 10 or not fields[6] or fields[6] == '0': return
        gps_fix['sats'] = int(fields[7])   if fields[7] else 0
        gps_fix['alt']  = float(fields[9]) if fields[9] else 0.0
    except Exception as e: print(f"GPGGA error: {e}")

_gps_buf = bytearray()

def poll_gps():
    """
    Non-blocking GPS UART drain and NMEA parse.
    Call this frequently from the main loop and from within recv_esp().
    If you add long-running operations to this firmware, call poll_gps()
    periodically within them to keep GPS data current.
    """
    global _gps_buf
    while gps_uart.any():
        b = gps_uart.read(1)
        if b is None: break
        ch = b[0]
        if ch == ord('\n'):
            line = _gps_buf.decode('ascii', 'ignore').strip()
            _gps_buf = bytearray()
            if line.startswith('$') and '*' in line:
                if _nmea_checksum_ok(line):
                    fields = line.split(',')
                    tag    = fields[0][1:]
                    if tag in ('GPRMC', 'GNRMC'):    _parse_gprmc(fields)
                    elif tag in ('GPGGA', 'GNGGA'):  _parse_gpgga(fields)
        elif ch != ord('\r'):
            if len(_gps_buf) < 120: _gps_buf.append(ch)

def gps_packet():
    """
    Format fused GPS + barometric data as response string for CMD_GET_GPS.

    Response format: GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c
    All fields present even if no fix or baro not ready (zeros used).
    Ground station parses by field position, not by presence/absence.

    [SCK-DEV: BEACON] — this same packet is used for autonomous beacons
    and for CMD_GET_GPS responses. See Developer Guide Section 3.6.
    """
    if gps_fix['fix']:
        lat = gps_fix['lat']; lon = gps_fix['lon']
        galt = gps_fix['alt']; sats = gps_fix['sats']; fix = 1
    else:
        lat = lon = galt = 0.0; sats = 0; fix = 0

    if baro_data['valid']:
        hpa = baro_data['hpa']; balt = baro_data['alt_m']
        btemp = baro_data['temp_c']
    else:
        hpa = balt = btemp = 0.0

    return (f"GPS:{lat:.6f},{lon:.6f},{galt:.1f},{sats},{fix},"
            f"{hpa:.2f},{balt:.1f},{btemp:.2f}").encode()

# ============================================================================
# SD FLIGHT LOG — BLACK BOX RECORDER
# ============================================================================
#
# Every beacon transmission is logged to a .sckflight file on the SD card.
# One file is created per power cycle, named FLT-001.sckflight, FLT-002, etc.
# The file format is JSON compatible with the ground station Flight Replay tool.
#
# PURPOSE:
#   The flight log is the mission black box. Even if RF communication is lost
#   (which happens during HAB flights at certain altitudes and orientations),
#   all telemetry is preserved on the SD card and can be recovered post-flight
#   by reading the card directly.
#
# FILE FORMAT:
#   {"flight_id":"FLT-001","hardware":"SCK-915+SCK-PBL-1","packets":[
#   {"t":0.0,"lat":0.0,"lon":0.0,...,"event":""},
#   {"t":10.1,"lat":35.123,...,"event":"SNAP:snap_001.jpg"},
#   ...
#   ],"summary":{"total_packets":42,"flight_duration_s":420.0}}
#
# EXTENDING THE LOG:
#   To add new fields to each log entry, modify the write_flight_packet()
#   function below. Add new key:value pairs to the JSON line.
#   Also update the ground station FlightReplayForm to parse new fields.
#
# [SCK-DEV: SD_FLIGHT_LOG] — see SpaceCommsKit Developer Guide Section 3.7
# ============================================================================
_flight_log_file   = None
_flight_log_count  = 0
_flight_log_t0     = time.ticks_ms()
_flight_log_name   = ""

def _next_flight_number():
    """Find the next sequential FLT-NNN number on the SD card."""
    try:
        nums = []
        for f in uos.listdir('/sd'):
            try:
                if f.startswith('FLT-') and f.endswith('.sckflight'):
                    nums.append(int(f[4:7]))
            except Exception:
                pass
        return (max(nums) + 1) if nums else 1
    except:
        return 1

def open_flight_log():
    """Open a new .sckflight file for this power cycle."""
    global _flight_log_file, _flight_log_name
    if not sd_mounted:
        print("Flight log: SD not mounted")
        return
    try:
        num  = _next_flight_number()
        name = f"/sd/FLT-{num:03d}.sckflight"
        _flight_log_file = open(name, 'w')
        _flight_log_name = name
        _flight_log_file.write('{"flight_id":"FLT-' + f'{num:03d}' + '",'
                               '"hardware":"SCK-915+SCK-PBL-1",'
                               '"packets":[\n')
        _flight_log_file.flush()
        print(f"Flight log: {name}")
    except Exception as e:
        print(f"Flight log open failed: {e}")
        _flight_log_file = None

def write_flight_packet(event=""):
    """
    Write one beacon packet to the SD flight log.

    Call this every time a beacon is transmitted and for significant events
    (e.g. SNAP command, CMD_GET_GPS). The event string is optional context.

    TO ADD NEW FIELDS to the log entry:
      Add key:value pairs to the line string below, matching the JSON format.
      Also update the ground station FlightReplayForm parser.

    [SCK-DEV: SD_FLIGHT_LOG] — see SpaceCommsKit Developer Guide Section 3.7
    """
    global _flight_log_count
    if _flight_log_file is None:
        return
    try:
        t = round(time.ticks_diff(time.ticks_ms(), _flight_log_t0) / 1000.0, 1)
        fix = gps_fix['fix']
        sep = "" if _flight_log_count == 0 else ","
        line = (f'{sep}{{"t":{t},'
                f'"lat":{gps_fix["lat"]:.6f},'
                f'"lon":{gps_fix["lon"]:.6f},'
                f'"gps_alt":{gps_fix["alt"]:.1f},'
                f'"sats":{gps_fix["sats"]},'
                f'"fix":{1 if fix else 0},'
                f'"baro_hpa":{baro_data["hpa"]:.2f},'
                f'"baro_alt":{baro_data["alt_m"]:.1f},'
                f'"baro_temp":{baro_data["temp_c"]:.2f},'
                f'"event":"{event}"}}\n')
        _flight_log_file.write(line)
        _flight_log_count += 1
        if _flight_log_count % 10 == 0:
            _flight_log_file.flush()
            print(f"Flight log: {_flight_log_count} packets")
    except Exception as e:
        print(f"Flight log write error: {e}")

def close_flight_log():
    """Close the flight log with summary footer. Call on clean shutdown."""
    global _flight_log_file
    if _flight_log_file is None:
        return
    try:
        duration = round(time.ticks_diff(time.ticks_ms(), _flight_log_t0) / 1000.0, 1)
        _flight_log_file.write(f'],\n"summary":{{"total_packets":{_flight_log_count},'
                               f'"flight_duration_s":{duration}}}}}\n')
        _flight_log_file.flush()
        _flight_log_file.close()
        _flight_log_file = None
        print(f"Flight log closed: {_flight_log_count} packets, {duration}s")
    except Exception as e:
        print(f"Flight log close error: {e}")

# ============================================================================
# ARDUCAM OV2640 CAMERA DRIVER
# ============================================================================
#
# The OV2640 is a 2MP camera that communicates over two buses:
#   SPI  — capture control and FIFO data readout (shared with SD card)
#   I2C0 — register configuration (camera address 0x30)
#
# SPI BUS SHARING — IMPORTANT:
#   The camera and SD card share SPI0. Only one can be active at a time.
#   Always ensure the OTHER device's CS is HIGH before accessing either.
#   The code uses explicit cs(1) calls before every bus switch.
#   Removing these calls will cause data corruption on both devices.
#
# CAPTURE SEQUENCE:
#   1. Reset and reinitialize camera (init_camera())
#   2. Clear FIFO, start capture
#   3. Wait for capture complete flag
#   4. Read FIFO size registers
#   5. Burst-read FIFO data over SPI
#   Returns raw JPEG bytes
#
# IMAGE SIZE:
#   Configured for 320x240 JPEG. Typical file size 15-40KB.
#   To change resolution, replace OV2640_JPEG_320x240 register list
#   with appropriate SmartRF register values for the desired resolution.
# ============================================================================
def cam_write(addr, data):
    """Write one byte to ArduCAM SPI control register."""
    cam_cs(0); spi.write(bytes([addr | 0x80, data])); cam_cs(1)

def cam_read(addr):
    """Read one byte from ArduCAM SPI control register."""
    cam_cs(0); spi.write(bytes([addr & 0x7F]))
    r = spi.read(1, 0xFF); cam_cs(1); return r[0]

def ov_write(reg, val):
    """Write one byte to OV2640 I2C register."""
    i2c0.writeto_mem(CAM_ADDR, reg, bytes([val])); time.sleep_ms(1)

def ov_write_list(regs):
    """Write a list of (register, value) pairs to OV2640."""
    for reg, val in regs:
        if reg == 0xFF and val == 0xFF: time.sleep_ms(10)
        else: ov_write(reg, val)

# OV2640 initialization register sequences
# These values are from the OV2640 application note for JPEG mode
# Do not modify unless you have the OV2640 register reference manual
OV2640_JPEG_INIT = [
    (0xFF,0x00),(0x2C,0xFF),(0x2E,0xDF),(0xFF,0x01),(0x3C,0x32),
    (0x11,0x00),(0x09,0x02),(0x04,0x28),(0x13,0xE5),(0x14,0x48),
    (0x2C,0x0C),(0x33,0x78),(0x3A,0x33),(0x3B,0xFB),(0x3E,0x00),
    (0x43,0x11),(0x16,0x10),(0x39,0x02),(0x35,0x88),(0x22,0x0A),
    (0x37,0x40),(0x23,0x00),(0x34,0xA0),(0x36,0x1A),(0x06,0x02),
    (0x07,0xC0),(0x0D,0xB7),(0x0E,0x01),(0x4C,0x00),(0x4A,0x81),
    (0x21,0x99),(0x24,0x40),(0x25,0x38),(0x26,0x82),(0x5C,0x00),
    (0x63,0x00),(0x61,0x70),(0x62,0x80),(0x7C,0x05),(0x20,0x80),
    (0x28,0x30),(0x6C,0x00),(0x6D,0x80),(0x6E,0x00),(0x70,0x02),
    (0x71,0x94),(0x73,0xC1),(0x3D,0x34),(0x5A,0x57),(0x12,0x00),
    (0x11,0x00),(0xFF,0xFF),
]
OV2640_JPEG_320x240 = [
    (0xFF,0x01),(0x12,0x40),(0x17,0x11),(0x18,0x43),(0x19,0x00),
    (0x1A,0x4B),(0x32,0x09),(0x37,0xC0),(0x4F,0xCA),(0x50,0xA8),
    (0x6D,0x00),(0x3D,0x38),(0xFF,0x00),(0xE0,0x04),(0xC0,0x64),
    (0xC1,0x4B),(0x86,0x35),(0x50,0x89),(0x51,0xC8),(0x52,0x96),
    (0x53,0x00),(0x54,0x00),(0x55,0x00),(0x57,0x00),(0x5A,0x50),
    (0x5B,0x3C),(0x5C,0x00),(0xD3,0x04),(0xE0,0x00),(0xFF,0xFF),
]
OV2640_JPEG_FORMAT = [
    (0xFF,0x01),(0x15,0x00),(0xFF,0x00),(0xDA,0x10),
    (0xD7,0x03),(0xE0,0x00),(0xFF,0xFF),
]

cam_ready = False

def init_camera():
    """Initialize OV2640 for 320x240 JPEG capture. Returns True on success."""
    global cam_ready, _fault_camera
    try:
        cam_write(0x07, 0x80); time.sleep_ms(100)  # Reset ArduCAM
        cam_write(0x07, 0x00); time.sleep_ms(100)  # Release reset
        ov_write(0xFF, 0x01); ov_write(0x12, 0x80); time.sleep_ms(100)  # OV2640 soft reset
        ov_write_list(OV2640_JPEG_INIT)
        ov_write_list(OV2640_JPEG_320x240)
        ov_write_list(OV2640_JPEG_FORMAT)
        cam_write(0x00, 0x00)  # Clear ArduCAM fifo
        cam_ready     = True
        _fault_camera = False
        led_camera.on()
        update_fault_led()
        print("Camera ready")
        return True
    except Exception as e:
        cam_ready     = False
        _fault_camera = True
        led_camera.off()
        update_fault_led()
        print(f"Camera init failed: {e}")
        return False

def capture_jpeg():
    """
    Capture one JPEG image from OV2640 and return raw bytes.

    Raises OSError if capture times out or FIFO size is invalid.
    SPI bus must be free (both CS HIGH) before calling.

    FIFO SIZE RANGE:
      Typical 320x240 JPEG: 15,000 - 40,000 bytes
      Valid range check: 0 < size <= 100,000
      If FIFO reads as 0 or >100KB, something went wrong with capture.
    """
    # Clear FIFO and flush stale data before starting new capture
    cam_write(0x04, 0x01)  # Clear FIFO write done flag
    cam_write(0x04, 0x00)
    time.sleep_ms(20)

    cam_write(0x04, 0x02)  # Start capture
    for _ in range(1000):
        if cam_read(0x41) & 0x08: break  # Wait for capture done flag
        time.sleep_ms(1)
    else: raise OSError("Capture timeout")

    time.sleep_ms(10)  # Allow FIFO to settle
    len1 = cam_read(0x42); time.sleep_ms(5)
    len2 = cam_read(0x43); time.sleep_ms(5)
    len3 = cam_read(0x44) & 0x7F
    fifo_len = (len3 << 16) | (len2 << 8) | len1
    print(f"FIFO: {fifo_len} bytes")
    if fifo_len == 0 or fifo_len > 100000:
        raise OSError(f"Invalid FIFO: {fifo_len}")

    # Burst read FIFO over SPI
    cam_cs(0); spi.write(bytes([0x3C]))
    data = spi.read(fifo_len + 1, 0xFF); cam_cs(1)
    return b'\xff' + data[1:]  # Prepend 0xFF (first byte is dummy)

def next_filename():
    """Generate next sequential snap_NNN.jpg filename on SD card."""
    try:
        nums = []
        for f in uos.listdir('/sd'):
            try:
                if f.startswith('snap_') and f.endswith('.jpg'):
                    nums.append(int(f[5:8]))
            except Exception:
                pass
        return f"snap_{(max(nums)+1 if nums else 1):03d}.jpg"
    except:
        return "snap_001.jpg"

# ============================================================================
# ESP FRAMING — UART0 COMMUNICATION PROTOCOL
# ============================================================================
#
# All UART0 messages between Pico and CC1110 use ESP framing:
#   Frame format: [0x22][0x69][length][payload bytes...]
#
# send_esp() wraps a payload in an ESP frame and sends it to the CC1110.
# recv_esp() waits for an ESP frame from the CC1110 and returns the payload.
#
# TIMEOUT:
#   recv_esp() waits up to timeout_ms for a complete frame.
#   During waiting, it calls poll_gps() and update_gps_led() to keep
#   GPS data current and the GPS LED responsive.
#   The default 5000ms timeout is generous — commands typically arrive
#   within milliseconds of each other in normal operation.
#
# [SCK-DEV: ESP_FRAMING] — see SpaceCommsKit Developer Guide Section 3.4
# ============================================================================
def send_esp(payload_bytes):
    """Send payload_bytes to CC1110 wrapped in ESP frame."""
    uart.write(bytes([ESP_START_0, ESP_START_1, len(payload_bytes)]) + payload_bytes)

def recv_esp(timeout_ms=5000):
    """
    Wait for and return the next ESP frame payload from CC1110.
    Returns bytes on success, None on timeout.
    Calls poll_gps() and update_gps_led() while waiting.
    """
    STATE_START0, STATE_START1, STATE_LENGTH, STATE_PAYLOAD = 0, 1, 2, 3
    state = STATE_START0; payload = bytearray(); remaining = 0
    deadline = time.ticks_add(time.ticks_ms(), timeout_ms)
    while time.ticks_diff(deadline, time.ticks_ms()) > 0:
        poll_gps()         # Keep GPS data current while waiting
        update_gps_led()   # Keep GPS LED responsive while waiting
        if uart.any():
            b = uart.read(1)[0]
            if state == STATE_START0:
                if b == ESP_START_0: state = STATE_START1
            elif state == STATE_START1:
                if b == ESP_START_1:   state = STATE_LENGTH
                elif b == ESP_START_0: state = STATE_START1
                else:                  state = STATE_START0
            elif state == STATE_LENGTH:
                if 1 <= b <= MAX_PAYLOAD:
                    remaining = b; payload = bytearray(); state = STATE_PAYLOAD
                else: state = STATE_START0
            elif state == STATE_PAYLOAD:
                payload.append(b); remaining -= 1
                if remaining == 0: return bytes(payload)
        else: time.sleep_ms(1)
    return None

# ============================================================================
# STARTUP SEQUENCE
# ============================================================================
blink(3)
print("SCK-915 Pico ready — UART0 ESP framing @ 115200")

# All LEDs off for clean start — each subsystem lights its LED when ready
led_camera.off(); led_sd.off()
led_gps.off();    led_alti.off(); led_fault.off()

# Initialize subsystems in order — each updates its LED on success/failure
mount_sd()        # GREEN SD LED on if OK — must be first (flight log needs SD)
open_flight_log() # Opens FLT-NNN.sckflight for this power cycle
init_gps()        # BLUE GPS LED starts slow-flashing while searching
init_baro()       # YELLOW altimeter LED on if MS5611 found
# Camera initialized on first idle iteration — avoids blocking UART at boot

# ============================================================================
# MAIN COMMAND LOOP
# ============================================================================
#
# The main loop waits for commands from the CC1110 via recv_esp().
# When a command arrives, it is dispatched by sub-opcode.
# If no command arrives within the recv_esp timeout, autonomous operations
# (GPS beacon, baro read, camera init) are performed.
#
# COMMAND RESPONSE FORMAT:
#   All responses are ASCII strings sent via send_esp().
#   Format: "PREFIX:data,data,data"
#   Prefix identifies the response type to the ground station.
#   See the command table at the top of this file for all formats.
#
# ADDING NEW COMMANDS:
#   Add a new elif block below following the existing pattern:
#     elif sub_opcode == CMD_MY_COMMAND:
#         # process command
#         send_esp(b"MYRESP:data")
#         blink(1)
#
# [SCK-DEV: ADD_COMMAND]   — see SpaceCommsKit Developer Guide Section 3.1
# [SCK-DEV: CHUNKING]      — see SpaceCommsKit Developer Guide Section 3.2
# [SCK-DEV: RESPONSE_FORMAT] — see SpaceCommsKit Developer Guide Section 3.5
# ============================================================================
_cam_init_done    = False
_last_gps_beacon  = time.ticks_ms()
_last_baro_read   = time.ticks_ms()
BARO_READ_INTERVAL_MS = 2000  # Refresh baro data every 2 seconds in background

while True:
    payload = recv_esp(timeout_ms=5000)
    now     = time.ticks_ms()

    update_gps_led()  # Keep GPS LED state current every loop

    # ── Background Baro Read — every 2 seconds ────────────────────────────
    # Keeps baro_data fresh for both beacons and CMD_GET_GPS responses
    if time.ticks_diff(now, _last_baro_read) >= BARO_READ_INTERVAL_MS:
        read_baro()
        _last_baro_read = now

    # ── No Command Received — Idle Operations ─────────────────────────────
    if payload is None:
        # Initialize camera on first idle — deferred from boot to keep
        # UART responsive during the first few seconds after power-on
        if not _cam_init_done:
            init_camera()
            _cam_init_done = True

        # Autonomous GPS+baro beacon — fires every GPS_BEACON_INTERVAL_MS
        # Only fires when no command is being processed (payload is None)
        # Disable via CMD_BEACON_CTRL (0x09) if beacon interferes with
        # high-rate file transfers
        # [SCK-DEV: BEACON] — see Developer Guide Section 3.6
        if _beacon_enabled and time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
            pkt = gps_packet()
            send_esp(pkt)
            write_flight_packet()  # Log every beacon to SD black box
            print(f"Beacon: {pkt.decode()}")
            blink(1)
            _last_gps_beacon = now
        continue

    if len(payload) == 0:
        # Empty payload — fire beacon if due, then continue
        if _beacon_enabled and time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
            pkt = gps_packet()
            send_esp(pkt)
            write_flight_packet()
            print(f"Beacon: {pkt.decode()}")
            blink(1)
            _last_gps_beacon = now
        continue

    # ── Command Received — Dispatch by Sub-Opcode ─────────────────────────
    # Commands are processed BEFORE any pending beacon to ensure the
    # response always follows the command without a beacon packet in between.
    sub_opcode = payload[0]

    # ── 0x00 PING ──────────────────────────────────────────────────────────
    # Health check — verifies Pico is alive and UART is working
    # Response: "PICO:ACK"
    if sub_opcode == CMD_PING:
        send_esp(b"PICO:ACK"); blink(1)
        print("PING → PICO:ACK")

    # ── 0x01 READ_TEMP ─────────────────────────────────────────────────────
    # Read RP2040 internal die temperature
    # Response: "TEMP:xx.xxC"
    elif sub_opcode == CMD_READ_TEMP:
        temp = read_temp_celsius()
        msg  = f"TEMP:{temp}C"
        send_esp(msg.encode()); blink(2)
        print(f"READ_TEMP → {msg}")

    # ── 0x02 SNAP ──────────────────────────────────────────────────────────
    # Capture JPEG image and save to SD card
    # Response: "SNAP:OK:<filename>:<bytes>" or "SNAP:ERR:<reason>"
    #
    # NOTE: This command takes 2-4 seconds to complete (camera + SD write).
    # The CC1110 uses PICO_TIMEOUT_OUTER_SNAP (200 outer loops) to allow
    # sufficient time. Do not add long operations inside this handler
    # without verifying the CC1110 timeout is still sufficient.
    elif sub_opcode == CMD_SNAP:
        blink(1); print("SNAP...")
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"SNAP:ERR:NO_SD")
            else:
                # Ensure clean CS state before switching to camera SPI
                cam_cs(1); sd_cs(1)
                time.sleep_ms(50)
                if not init_camera():
                    send_esp(b"SNAP:ERR:NO_CAM")
                else:
                    jpeg = capture_jpeg()
                    # Switch SPI back to SD card for file write
                    cam_cs(1); sd_cs(1)
                    time.sleep_ms(10)
                    filename = next_filename()
                    with open(f"/sd/{filename}", 'wb') as f: f.write(jpeg)
                    msg = f"SNAP:OK:{filename}:{len(jpeg)}"
                    send_esp(msg.encode()); blink(3)
                    write_flight_packet(f"SNAP:{filename}")
                    print(f"SNAP → {msg}")
        except Exception as e:
            cam_cs(1); sd_cs(1)  # Always clean up CS on error
            send_esp(f"SNAP:ERR:{str(e)[:20]}".encode())
            print(f"SNAP error: {e}")

    # ── 0x03 LIST ──────────────────────────────────────────────────────────
    # List files on SD card
    # Response: "LIST:<file1>,<file2>,..." (single packet if fits)
    # Chunked:  "LIST:<files...>,"  then "LIST+:<files...>,"  then "LIST+:<last files>"
    #           Ground station accumulates until packet with no trailing comma
    #
    # CHUNKING PATTERN — use this as a template for any large list response:
    # [SCK-DEV: CHUNKING] — see SpaceCommsKit Developer Guide Section 3.2
    elif sub_opcode == CMD_LIST:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"LIST:ERR:NO_SD")
            else:
                try:
                    all_files = uos.listdir('/sd')
                except OSError as ose:
                    send_esp(f"LIST:ERR:OS:{ose.args}".encode())
                    all_files = []

                # Filter to known file types, skip hidden/system files
                files = []
                for f in all_files:
                    try:
                        if (f.endswith('.jpg') or f.endswith('.txt')
                                or f.endswith('.sckflight')):
                            files.append(f)
                    except Exception:
                        pass

                if not files:
                    send_esp(b"LIST:EMPTY"); blink(2)
                else:
                    # Chunk files into packets that fit within MAX_PAYLOAD
                    # "LIST:"  prefix = first chunk
                    # "LIST+:" prefix = continuation chunk
                    # No trailing comma on final chunk = end-of-list signal
                    CHUNK = MAX_PAYLOAD - 8  # Leave room for prefix
                    chunk_files = []
                    chunk_len   = 0
                    first_chunk = True
                    for f in files:
                        entry = f + ","
                        if chunk_len + len(entry) > CHUNK and chunk_files:
                            prefix = "LIST:" if first_chunk else "LIST+:"
                            msg = prefix + ",".join(chunk_files) + ","
                            send_esp(msg.encode()); blink(1)
                            print(f"LIST chunk: {msg}")
                            first_chunk = False
                            chunk_files = [f]
                            chunk_len   = len(f) + 1
                        else:
                            chunk_files.append(f)
                            chunk_len += len(entry)
                    # Final chunk — no trailing comma signals end of list
                    if chunk_files:
                        prefix = "LIST:" if first_chunk else "LIST+:"
                        msg = prefix + ",".join(chunk_files)
                        send_esp(msg.encode()); blink(2)
                        print(f"LIST final: {msg}")
        except Exception as e:
            send_esp(b"LIST:ERR:FAIL")
            print(f"LIST error: {e}")

    # ── 0x04 GET_INFO ──────────────────────────────────────────────────────
    # Get file size and chunk count for a named file
    # Payload: sub-opcode + filename string
    # Response: "INFO:<filename>:<bytes>:<chunks>"
    # Ground station calls this before GET_CHUNK to know how many chunks to request
    elif sub_opcode == CMD_GET_INFO:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"INFO:ERR:NO_SD")
            else:
                filename = payload[1:].decode('utf-8').strip()
                size     = uos.stat(f"/sd/{filename}")[6]
                chunks   = (size + CHUNK_SIZE - 1) // CHUNK_SIZE
                msg      = f"INFO:{filename}:{size}:{chunks}"
                send_esp(msg.encode())
                print(f"GET_INFO → {msg}")
        except Exception as e:
            send_esp(b"INFO:ERR:NOTFOUND")
            print(f"GET_INFO error: {e}")

    # ── 0x05 GET_CHUNK ─────────────────────────────────────────────────────
    # Get one 200-byte chunk of a file by index
    # Payload: sub-opcode + chunk_index (2 bytes, little-endian) + filename
    # Response: "CHUNK:<index>:<200 bytes binary data>"
    #
    # BINARY CHUNKING PATTERN:
    # This is how large binary files (images) are transferred over RF.
    # The ground station calls GET_INFO first, then GET_CHUNK for each
    # chunk index from 0 to (chunks-1), reassembling on the C# side.
    # [SCK-DEV: CHUNKING] — see SpaceCommsKit Developer Guide Section 3.2
    elif sub_opcode == CMD_GET_CHUNK:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"CHUNK:ERR:NO_SD")
            else:
                chunk_index = payload[1] | (payload[2] << 8)  # Little-endian uint16
                filename    = payload[3:].decode('utf-8').strip()
                offset      = chunk_index * CHUNK_SIZE
                with open(f"/sd/{filename}", 'rb') as f:
                    f.seek(offset); data = f.read(CHUNK_SIZE)
                send_esp(f"CHUNK:{chunk_index}:".encode() + data)
                print(f"GET_CHUNK {chunk_index}: {len(data)} bytes")
        except Exception as e:
            send_esp(b"CHUNK:ERR:FAIL")
            print(f"GET_CHUNK error: {e}")

    # ── 0x06 DELETE ────────────────────────────────────────────────────────
    # Delete a file from SD card by name
    # Payload: sub-opcode + filename string
    # Response: "DEL:OK:<filename>" or "DEL:ERR:FAIL"
    elif sub_opcode == CMD_DELETE:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"DEL:ERR:NO_SD")
            else:
                filename = payload[1:].decode('utf-8').strip()
                uos.remove(f"/sd/{filename}")
                msg = f"DEL:OK:{filename}"
                send_esp(msg.encode()); blink(2)
                print(f"DELETE → {msg}")
        except Exception as e:
            send_esp(b"DEL:ERR:FAIL")
            print(f"DELETE error: {e}")

    # ── 0x07 GET_GPS ───────────────────────────────────────────────────────
    # Get current fused GPS + barometric data on demand
    # Response: "GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c"
    # Takes a fresh baro reading before responding for accuracy
    # [SCK-DEV: BEACON] — same format as autonomous beacon packets
    elif sub_opcode == CMD_GET_GPS:
        read_baro()  # Fresh baro reading for this response
        pkt = gps_packet()
        send_esp(pkt)
        write_flight_packet("CMD_GET_GPS")
        blink(1)
        print(f"GET_GPS → {pkt.decode()}")

    # ── 0x08 GET_BARO ──────────────────────────────────────────────────────
    # Get barometric data only (no GPS)
    # Response: "BARO:<hpa>,<baro_alt>,<temp_c>"
    elif sub_opcode == CMD_GET_BARO:
        read_baro()  # Fresh reading
        pkt = baro_packet()
        send_esp(pkt); blink(1)
        print(f"GET_BARO → {pkt.decode()}")

    # ── 0x09 BEACON_CTRL ──────────────────────────────────────────────────
    # Enable or disable the autonomous GPS beacon
    # Payload: sub-opcode + 0x01 (enable) or 0x00 (disable)
    # Response: "BEACON:ON" or "BEACON:OFF"
    #
    # USE CASE: Disable beacon during high-rate file transfers to prevent
    # beacon packets from interleaving with file chunk responses.
    # Re-enable beacon when file transfer is complete.
    elif sub_opcode == CMD_BEACON_CTRL:
        if len(payload) >= 2:
            _beacon_enabled = (payload[1] == 0x01)
        state = "ON" if _beacon_enabled else "OFF"
        resp = f"BEACON:{state}"
        send_esp(resp.encode()); blink(1)
        print(f"BEACON_CTRL → {resp}")

    # ── ADD NEW COMMANDS HERE ─────────────────────────────────────────────
    # Follow this pattern:
    #
    # elif sub_opcode == CMD_MY_COMMAND:  # 0x0A (next available opcode)
    #     try:
    #         # Process the command
    #         result = do_something(payload[1:])
    #         msg = f"MYRESP:{result}"
    #         send_esp(msg.encode()); blink(1)
    #         print(f"MY_COMMAND → {msg}")
    #     except Exception as e:
    #         send_esp(b"MYRESP:ERR:FAIL")
    #         print(f"MY_COMMAND error: {e}")
    #
    # If the response may exceed MAX_PAYLOAD (251 bytes), implement chunking.
    # See CMD_LIST above for the chunking pattern.
    #
    # [SCK-DEV: ADD_COMMAND] — see SpaceCommsKit Developer Guide Section 3.1
    # [SCK-DEV: CHUNKING]    — see SpaceCommsKit Developer Guide Section 3.2

    else:
        # Unknown sub-opcode — return error to ground station
        err = f"ERR:UNKNOWN:{sub_opcode:#04x}"
        send_esp(err.encode())
        print(f"Unknown sub-opcode: {sub_opcode:#04x}")
