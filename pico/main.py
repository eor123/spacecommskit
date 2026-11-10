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
#   Ground Station (C#) <-RF-> CC1110 <-UART0-> Pico (this file)
#                                              |-- OV2640 camera (SPI + I2C)
#                                              |-- SD card (SPI)
#                                              |-- GPS NEO-6M (UART1)
#                                              `-- MS5611 altimeter (I2C1, GPIO2/3)
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
#   0x00 = PING        -> "PICO:ACK"
#   0x01 = READ_TEMP   -> "TEMP:xx.xxC"
#   0x02 = SNAP        -> "SNAP:OK:<filename>:<bytes>"
#   0x03 = LIST        -> "LIST:<file1>,<file2>,..." (chunked if needed)
#   0x04 = GET_INFO    -> "INFO:<filename>:<bytes>:<chunks>"
#   0x05 = GET_CHUNK   -> "CHUNK:<index>:<200 bytes data>"
#   0x06 = DELETE      -> "DEL:OK:<filename>"
#   0x07 = GET_GPS     -> "GPS:<lat>,<lon>,<gps_alt>,<sats>,<fix>,<hpa>,<baro_alt>,<temp_c>,<dyn_state>"
#   0x08 = GET_BARO    -> "BARO:<hpa>,<baro_alt>,<temp_c>"
#   0x09 = BEACON_CTRL -> "BEACON:ON" or "BEACON:OFF"
#
# HARDWARE VARIANTS:
#   SCK-915 Prototype  -- hand-wired jumper board (see TIMING WARNING below)
#   SCK-PBL-1          -- production PCB, all connections on-board
#
# [SCK-DEV: TIMING] -- see SpaceCommsKit Developer Guide Section 3.3
# [SCK-DEV: ADD_COMMAND] -- see SpaceCommsKit Developer Guide Section 3.1
# [SCK-DEV: CHUNKING] -- see SpaceCommsKit Developer Guide Section 3.2
# ============================================================================

from machine import UART, ADC, Pin, SPI, I2C
import time
import uos
import math
import sdcard

# -- ESP Framing Constants --------------------------------------------------
ESP_START_0   = 0x22
ESP_START_1   = 0x69
MAX_PAYLOAD   = 251

# -- Sub-opcodes --------------------------------------------------------------
CMD_PING        = 0x00
CMD_READ_TEMP   = 0x01
CMD_SNAP        = 0x02
CMD_LIST        = 0x03
CMD_GET_INFO    = 0x04
CMD_GET_CHUNK   = 0x05
CMD_DELETE      = 0x06
CMD_GET_GPS     = 0x07
CMD_GET_BARO    = 0x08
CMD_BEACON_CTRL = 0x09

CHUNK_SIZE    = 200

GPS_BEACON_INTERVAL_MS = 10000
_beacon_enabled = True

# -- UART0 -- CC1110 Communication -------------------------------------------
uart = UART(0, baudrate=115200, tx=Pin(0), rx=Pin(1))

# -- UART1 -- GPS NEO-6M ------------------------------------------------------
gps_uart = UART(1, baudrate=9600, tx=Pin(8), rx=Pin(9))

# -- Onboard Status LED --------------------------------------------------------
led = Pin(25, Pin.OUT)

def blink(times=1):
    """Blink the onboard LED for visual feedback."""
    for _ in range(times):
        led.on(); time.sleep_ms(80)
        led.off(); time.sleep_ms(80)

# -- Status LEDs -- Payload Board ---------------------------------------------
led_camera = Pin(10, Pin.OUT, value=0)
led_sd     = Pin(11, Pin.OUT, value=0)
led_gps    = Pin(12, Pin.OUT, value=0)
led_alti   = Pin(13, Pin.OUT, value=0)
led_fault  = Pin(14, Pin.OUT, value=0)

_fault_camera = False
_fault_sd     = False
_fault_alti   = False

def update_fault_led():
    led_fault.value(1 if (_fault_camera or _fault_sd or _fault_alti) else 0)

_gps_led_last_toggle = time.ticks_ms()
_gps_led_state       = False
GPS_SEARCH_FLASH_MS  = 500

def update_gps_led():
    global _gps_led_last_toggle, _gps_led_state
    if gps_fix['fix']:
        led_gps.on()
    else:
        now = time.ticks_ms()
        if time.ticks_diff(now, _gps_led_last_toggle) >= GPS_SEARCH_FLASH_MS:
            _gps_led_state = not _gps_led_state
            led_gps.value(_gps_led_state)
            _gps_led_last_toggle = now

temp_sensor       = ADC(4)
CONVERSION_FACTOR = 3.3 / 65535

def read_temp_celsius():
    reading = temp_sensor.read_u16() * CONVERSION_FACTOR
    return round(27 - (reading - 0.706) / 0.001721, 2)

spi = SPI(0, baudrate=400000, polarity=0, phase=0, bits=8,
          firstbit=SPI.MSB, sck=Pin(18), mosi=Pin(19), miso=Pin(16))

cam_cs = Pin(15, Pin.OUT, value=1)
sd_cs  = Pin(17, Pin.OUT, value=1)

i2c0 = I2C(0, sda=Pin(4), scl=Pin(5), freq=100000)
i2c1 = I2C(1, sda=Pin(2), scl=Pin(3), freq=400000)  # Back to 400kHz -- the
                                                     # 100kHz test ruled out
                                                     # bus speed as a factor
                                                     # in the pressure bug
                                                     # (real root cause was
                                                     # the SENS/OFF formula
                                                     # constants, since fixed)
CAM_ADDR = 0x30

sd_mounted = False

def mount_sd():
    global sd_mounted, spi, _fault_sd
    if sd_mounted:
        return True
    try:
        cam_cs(1); sd_cs(1)
        time.sleep_ms(100)
        spi_settle = SPI(0, baudrate=10000, polarity=0, phase=0, bits=8,
                         firstbit=SPI.MSB, sck=Pin(18), mosi=Pin(19), miso=Pin(16))
        sd_cs(1)
        spi_settle.write(b'\xFF' * 10)
        time.sleep_ms(100)
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
# MS5611-01BA03 DRIVER (ACTIVE)
# ============================================================================
SEA_LEVEL_HPA = 1013.25
baro_ready    = False
baro_data     = {'hpa': 0.0, 'alt_m': 0.0, 'temp_c': 0.0, 'valid': False}

# MS5611 I2C address depends on the CSB pin: CSB high = 0x76, CSB low = 0x77.
# On the prototype payload board pins 4 (CSB) and 5 share one trace, so a board
# reworked to ground pin 5 answers at 0x77; a stock board answers at 0x76.
# init_baro() scans the bus and uses whichever one is present (11/2026), so the
# same main.py runs on both. MS5611_ADDR below is only the fallback.
MS5611_ADDR_CANDIDATES = (0x76, 0x77)
MS5611_ADDR = 0x76

# Set True to print raw D1/D2/OFF/SENS on every baro read (bench debugging).
BARO_DEBUG = False

_MS_RESET      = 0x1E
_MS_CONV_D1    = 0x48
_MS_CONV_D2    = 0x58
_MS_ADC_READ   = 0x00
_MS_PROM_BASE  = 0xA0

# PROM calibration coefficients -- loaded once at init_baro()
# FIX: was `[0] * 7` (only 7 elements, valid indices 0..6) but init_baro()
# loops `for i in range(8)` and assigns _ms_C[7] on the last iteration --
# that's the "list index out of range" you're seeing. C[0] is factory data,
# C[1]..C[6] are the calibration coefficients actually used, C[7] is the
# CRC word (read but not checked here) -- needs all 8 slots.
_ms_C = [0] * 8   # C[0]..C[7]

def _ms_read_prom(coeff_index):
    """Read one 16-bit calibration coefficient from MS5611 PROM.
    coeff_index: 0-7 (addresses 0xA0, 0xA2, 0xA4, 0xA6, 0xA8, 0xAA, 0xAC, 0xAE)
    """
    cmd = _MS_PROM_BASE + (coeff_index * 2)
    i2c1.writeto(MS5611_ADDR, bytes([cmd]))
    d = i2c1.readfrom(MS5611_ADDR, 2)
    return (d[0] << 8) | d[1]

def _ms_convert_and_read(cmd):
    i2c1.writeto(MS5611_ADDR, bytes([cmd]))
    time.sleep_ms(20)   # widened from 10ms (only ~1ms above the 9.04ms
                         # datasheet max for OSR=4096) — testing whether a
                         # marginal early read explains the anomalous
                         # pressure reading on this board
    i2c1.writeto(MS5611_ADDR, bytes([_MS_ADC_READ]))
    d = i2c1.readfrom(MS5611_ADDR, 3)
    return (d[0] << 16) | (d[1] << 8) | d[2]

def init_baro():
    """Initialize MS5611. Sends reset and reads PROM calibration coefficients."""
    global baro_ready, _fault_alti, _ms_C, MS5611_ADDR
    try:
        found = [a for a in MS5611_ADDR_CANDIDATES if a in i2c1.scan()]
        if not found:
            raise OSError("MS5611 not found at 0x76 or 0x77")
        MS5611_ADDR = found[0]
        print(f"MS5611 found at 0x{MS5611_ADDR:02X}")
        i2c1.writeto(MS5611_ADDR, bytes([_MS_RESET]))
        time.sleep_ms(3)

        for i in range(8):
            _ms_C[i] = _ms_read_prom(i) if i < 7 else 0

        if _ms_C[1] == 0:
            raise OSError("MS5611 PROM read failed -- C1 is zero")

        baro_ready  = True
        _fault_alti = False
        led_alti.on()
        update_fault_led()
        print(f"MS5611 altimeter ready -- C1={_ms_C[1]} C2={_ms_C[2]}")
        return True

    except Exception as e:
        baro_ready  = False
        _fault_alti = True
        led_alti.off()
        update_fault_led()
        print(f"MS5611 init failed: {e}")
        return False

def read_baro():
    global baro_data
    if not baro_ready:
        baro_data['valid'] = False
        return False
    try:
        D2 = _ms_convert_and_read(_MS_CONV_D2)
        D1 = _ms_convert_and_read(_MS_CONV_D1)

        C1, C2, C3, C4, C5, C6 = (_ms_C[1], _ms_C[2], _ms_C[3],
                                    _ms_C[4], _ms_C[5], _ms_C[6])

        dT   = D2 - C5 * (1 << 8)
        TEMP = 2000 + dT * C6 // (1 << 23)

        # FIXED (found via cross-checking against RobTillaart/MS5611, a
        # trusted independent Arduino library): this driver's SENS/OFF
        # constants were the "AppNote/MS5607" shift values (C1*2^16,
        # C2*2^17, TCS/2^7, TCO/2^6), NOT the genuine MS5611 datasheet
        # values (C1*2^15, C2*2^16, TCS/2^8, TCO/2^7) -- one bit off on
        # every term, all in the same direction. That inflates both SENS
        # and OFF by exactly 2x, which propagates through the final
        # pressure formula as an exact 2x error on P regardless of D1 or
        # which physical chip is attached -- this was the real root cause
        # of the whole "~1970 hPa instead of ~985 hPa" investigation, not
        # a defective sensor batch.
        OFF  = C2 * (1 << 16) + (C4 * dT) // (1 << 7)
        SENS = C1 * (1 << 15) + (C3 * dT) // (1 << 8)

        T2    = 0
        OFF2  = 0
        SENS2 = 0

        if TEMP < 2000:
            T2    = dT * dT // (1 << 31)
            OFF2  = 5 * (TEMP - 2000) ** 2 // 2
            SENS2 = 5 * (TEMP - 2000) ** 2 // 4

            if TEMP < -1500:
                OFF2  += 7 * (TEMP + 1500) ** 2
                SENS2 += 11 * (TEMP + 1500) ** 2 // 2

        TEMP -= T2
        OFF  -= OFF2
        SENS -= SENS2

        P = (D1 * SENS // (1 << 21) - OFF) // (1 << 15)

        if BARO_DEBUG:
            print(f"BARO DEBUG: D1={D1} D2={D2} dT={dT} TEMP={TEMP} "
                  f"OFF={OFF} SENS={SENS} P={P} "
                  f"PICO_TEMP_C={read_temp_celsius()}")  # cross-check against
                                                         # the RP2040's own sensor

        press_hpa = P / 100.0
        temp_c    = TEMP / 100.0

        alt_m = 44330.0 * (1.0 - math.pow(
            press_hpa / SEA_LEVEL_HPA, 0.1903))

        baro_data['hpa']    = round(press_hpa, 2)
        baro_data['alt_m']  = round(alt_m, 1)
        baro_data['temp_c'] = round(temp_c, 2)
        baro_data['valid']  = True
        return True

    except Exception as e:
        baro_data['valid'] = False
        print(f"MS5611 read error: {e}")
        return False

def baro_packet():
    if baro_data['valid']:
        return (f"BARO:{baro_data['hpa']:.2f},"
                f"{baro_data['alt_m']:.1f},"
                f"{baro_data['temp_c']:.2f}").encode()
    return b"BARO:ERR:NOT_READY"

# ============================================================================
# GPS DRIVER -- NEO-6M NMEA PARSER
# ============================================================================
gps_fix = {
    'lat': 0.0, 'lon': 0.0, 'alt': 0.0,
    'sats': 0,  'fix': False,
    'time': '', 'date': '',
}

# GPS airborne dynamic-model confirmation state, reported as the 9th field
# of gps_packet().
#   0 = unconfirmed (boot default, before init_gps() resolves)
#   1 = confirmed airborne <1g mode via UBX ACK at boot
#   2 = failed / NAK'd (module still capped in default Portable mode)
#   3 = confirmed airborne <1g mode after a ground-triggered manual retry
GPS_DYN_UNCONFIRMED     = 0
GPS_DYN_CONFIRMED_BOOT  = 1
GPS_DYN_FAILED          = 2
GPS_DYN_CONFIRMED_RETRY = 3
gps_dyn_state = GPS_DYN_UNCONFIRMED

GPS_INIT_RETRIES = 3

# UBX-CFG-NAV5 command to set airborne (<1g) dynamic model.
# FIX: the original array here was 2 bytes short of a valid 44-byte frame
# (6 header + 36 payload + 2 checksum) -- its trailing "0x16, 0xDC" was
# actually landing inside the 36-byte payload's reserved4 field, not
# functioning as a checksum at all, so the module never saw a valid
# checksum and silently ignored the write.
# Also narrowed mask from 0xFFFF (apply every NAV5 field) to 0x0001
# (dynModel only) -- on a real NEO-6M clone, a full-mask write was
# silently rejected while a dyn-only mask succeeded immediately, since
# the full mask also asks the module to accept/validate every other
# guessed field (DOP masks, static-hold thresholds, etc.).
_UBX_AIRBORNE = bytes([
    0xB5, 0x62, 0x06, 0x24, 0x24, 0x00,
    0x01, 0x00,               # mask = 0x0001 -> dynModel only
    0x06, 0x03,                # dynModel=6 (Airborne <1g), fixMode=3 (ignored)
    0x00, 0x00, 0x00, 0x00,    # fixedAlt
    0x10, 0x27, 0x00, 0x00,    # fixedAltVar
    0x05, 0x00,                # minElev, drLimit
    0xFA, 0x00,                # pDop
    0xFA, 0x00,                # tDop
    0x64, 0x00,                # pAcc
    0x2C, 0x01,                 # tAcc
    0x00, 0x3C,                # staticHoldThresh, dgpsTimeOut
    0x00, 0x00,                # cnoThreshNumSVs, cnoThresh
    0x00, 0x00,                # reserved2
    0x00, 0x00,                # staticHoldMaxDist
    0x00, 0x00,                # utcStandard, reserved3
    0x00, 0x00, 0x00, 0x00,    # reserved4
    0x55, 0x53,   # CK_A, CK_B -- verified against this exact 36-byte
                  # payload (44-byte frame total).
])

# UBX-CFG-NAV5 poll request (zero-length payload) -- fallback confirmation
# for modules that don't emit ACK/NAK on CFG writes (common on cheap
# NEO-6M clones that speak NMEA fine but only partially implement UBX).
_UBX_POLL_NAV5 = bytes([0xB5, 0x62, 0x06, 0x24, 0x00, 0x00, 0x2A, 0x84])

def _wait_ubx_frame(want_cls, want_id, timeout_ms=1000):
    """Wait for any complete UBX frame matching (want_cls, want_id) and
    return its payload bytes, or None on timeout."""
    deadline = time.ticks_add(time.ticks_ms(), timeout_ms)
    state = 0
    cls = mid = length = 0
    payload = bytearray()
    ck_remaining = 0
    while time.ticks_diff(deadline, time.ticks_ms()) > 0:
        if not gps_uart.any():
            time.sleep_ms(2)
            continue
        b = gps_uart.read(1)
        if not b:
            continue
        byte = b[0]
        if state == 0:
            state = 1 if byte == 0xB5 else 0
        elif state == 1:
            state = 2 if byte == 0x62 else 0
        elif state == 2:
            cls = byte; state = 3
        elif state == 3:
            mid = byte; state = 4
        elif state == 4:
            length = byte; state = 5
        elif state == 5:
            length |= (byte << 8); state = 6; payload = bytearray()
        elif state == 6:
            payload.append(byte)
            if len(payload) >= length:
                state = 7; ck_remaining = 2
        elif state == 7:
            ck_remaining -= 1
            if ck_remaining <= 0:
                if cls == want_cls and mid == want_id:
                    return bytes(payload)
                state = 0
    return None

def _poll_gps_dynmodel(timeout_ms=1000):
    """Poll UBX-CFG-NAV5 and return the module's live dynModel byte, or
    None if the module didn't respond to the poll either."""
    while gps_uart.any():
        gps_uart.read(gps_uart.any())
    gps_uart.write(_UBX_POLL_NAV5)
    payload = _wait_ubx_frame(0x06, 0x24, timeout_ms)
    while gps_uart.any():
        gps_uart.read(gps_uart.any())
    if payload is not None and len(payload) >= 3:
        return payload[2]
    return None

def _wait_ubx_ack(timeout_ms=1000):
    """Wait for a UBX-ACK-ACK/NAK for our CFG-NAV5 write.
    Returns (result, bytes_seen): result is 'ACK', 'NAK', or 'TIMEOUT'."""
    deadline = time.ticks_add(time.ticks_ms(), timeout_ms)
    window = bytearray()
    bytes_seen = 0
    while time.ticks_diff(deadline, time.ticks_ms()) > 0:
        if gps_uart.any():
            b = gps_uart.read(1)
            if not b:
                continue
            bytes_seen += 1
            window.append(b[0])
            if len(window) > 10:
                window = window[1:]   # MicroPython doesn't support in-place
                                       # `del window[0]` on bytearray on all ports
            if (len(window) == 10 and window[0] == 0xB5 and window[1] == 0x62
                    and window[2] == 0x05 and window[4] == 0x02 and window[5] == 0x00
                    and window[6] == 0x06 and window[7] == 0x24):
                return (('ACK' if window[3] == 0x01 else 'NAK'), bytes_seen)
        else:
            time.sleep_ms(2)
    return ('TIMEOUT', bytes_seen)

def _send_ubx_airborne_and_verify():
    """Send UBX-CFG-NAV5 airborne config. Tries ACK/NAK first; falls back
    to polling CFG-NAV5 and checking dynModel directly if no ACK/NAK
    arrives at all. Returns (result, bytes_seen)."""
    while gps_uart.any():
        gps_uart.read(gps_uart.any())
    gps_uart.write(_UBX_AIRBORNE)
    result, bytes_seen = _wait_ubx_ack(timeout_ms=1000)
    while gps_uart.any():
        gps_uart.read(gps_uart.any())

    if result == 'TIMEOUT':
        dyn = _poll_gps_dynmodel(timeout_ms=1000)
        if dyn == 6:
            result = 'READBACK_OK'
        elif dyn is not None:
            result = 'READBACK_MISMATCH'

    return (result, bytes_seen)

def _log_ubx_result(prefix, result, bytes_seen):
    if result == 'NAK':
        print(f"{prefix}: module explicitly rejected the config (NAK) -- "
              f"check the UBX-CFG-NAV5 payload/checksum")
    elif result == 'READBACK_MISMATCH':
        print(f"{prefix}: no ACK, and poll read-back shows dynModel did NOT "
              f"change to airborne -- write was likely rejected or ignored")
    elif result == 'TIMEOUT' and bytes_seen == 0:
        print(f"{prefix}: no bytes received at all -- check UART wiring "
              f"(RX/TX swap?), baud rate, or power to the module")
    elif result == 'TIMEOUT' and bytes_seen > 0:
        print(f"{prefix}: {bytes_seen} bytes received but no ACK/NAK matched, "
              f"and the module didn't respond to a CFG-NAV5 poll either")

def init_gps():
    """Send UBX airborne mode config, verifying via ACK/NAK (falling back
    to poll+read-back), retrying a few times at boot before giving up."""
    global gps_dyn_state
    print("GPS: configuring airborne dynamic mode...")
    for attempt in range(1, GPS_INIT_RETRIES + 1):
        result, bytes_seen = _send_ubx_airborne_and_verify()
        if result in ('ACK', 'READBACK_OK'):
            gps_dyn_state = GPS_DYN_CONFIRMED_BOOT
            print(f"GPS: airborne mode confirmed via {result} (attempt {attempt}/{GPS_INIT_RETRIES})")
            break
        print(f"GPS: airborne mode {result} (attempt {attempt}/{GPS_INIT_RETRIES})")
        _log_ubx_result("GPS", result, bytes_seen)
        time.sleep_ms(300)
    else:
        gps_dyn_state = GPS_DYN_FAILED
        print("GPS: airborne mode NOT confirmed after retries")
    print("GPS: ready")

def _nmea_checksum_ok(sentence):
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
    global gps_fix
    try:
        if len(fields) < 10 or not fields[6] or fields[6] == '0': return
        gps_fix['sats'] = int(fields[7])   if fields[7] else 0
        gps_fix['alt']  = float(fields[9]) if fields[9] else 0.0
    except Exception as e: print(f"GPGGA error: {e}")

_gps_buf = bytearray()

def poll_gps():
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
    """Format fused GPS + barometric data as response string for CMD_GET_GPS.
    Response format: GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c,dyn_state
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
            f"{hpa:.2f},{balt:.1f},{btemp:.2f},{gps_dyn_state}").encode()

# ============================================================================
# SD FLIGHT LOG -- BLACK BOX RECORDER
# ============================================================================
_flight_log_file   = None
_flight_log_count  = 0
_flight_log_t0     = time.ticks_ms()
_flight_log_name   = ""

def _next_flight_number():
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
def cam_write(addr, data):
    cam_cs(0); spi.write(bytes([addr | 0x80, data])); cam_cs(1)

def cam_read(addr):
    cam_cs(0); spi.write(bytes([addr & 0x7F]))
    r = spi.read(1, 0xFF); cam_cs(1); return r[0]

def ov_write(reg, val):
    i2c0.writeto_mem(CAM_ADDR, reg, bytes([val])); time.sleep_ms(1)

def ov_write_list(regs):
    for reg, val in regs:
        if reg == 0xFF and val == 0xFF: time.sleep_ms(10)
        else: ov_write(reg, val)

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
    global cam_ready, _fault_camera
    try:
        cam_write(0x07, 0x80); time.sleep_ms(100)
        cam_write(0x07, 0x00); time.sleep_ms(100)
        ov_write(0xFF, 0x01); ov_write(0x12, 0x80); time.sleep_ms(100)
        ov_write_list(OV2640_JPEG_INIT)
        ov_write_list(OV2640_JPEG_320x240)
        ov_write_list(OV2640_JPEG_FORMAT)
        cam_write(0x00, 0x00)
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
    cam_write(0x04, 0x01)
    cam_write(0x04, 0x00)
    time.sleep_ms(20)

    cam_write(0x04, 0x02)
    for _ in range(1000):
        if cam_read(0x41) & 0x08: break
        time.sleep_ms(1)
    else: raise OSError("Capture timeout")

    time.sleep_ms(10)
    len1 = cam_read(0x42); time.sleep_ms(5)
    len2 = cam_read(0x43); time.sleep_ms(5)
    len3 = cam_read(0x44) & 0x7F
    fifo_len = (len3 << 16) | (len2 << 8) | len1
    print(f"FIFO: {fifo_len} bytes")
    if fifo_len == 0 or fifo_len > 100000:
        raise OSError(f"Invalid FIFO: {fifo_len}")

    cam_cs(0); spi.write(bytes([0x3C]))
    data = spi.read(fifo_len + 1, 0xFF); cam_cs(1)
    return b'\xff' + data[1:]

def next_filename():
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
# ESP FRAMING -- UART0 COMMUNICATION PROTOCOL
# ============================================================================
def send_esp(payload_bytes):
    uart.write(bytes([ESP_START_0, ESP_START_1, len(payload_bytes)]) + payload_bytes)

def recv_esp(timeout_ms=5000):
    STATE_START0, STATE_START1, STATE_LENGTH, STATE_PAYLOAD = 0, 1, 2, 3
    state = STATE_START0; payload = bytearray(); remaining = 0
    deadline = time.ticks_add(time.ticks_ms(), timeout_ms)
    while time.ticks_diff(deadline, time.ticks_ms()) > 0:
        poll_gps()
        update_gps_led()
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
print("SCK-915 Pico ready -- UART0 ESP framing @ 115200")

led_camera.off(); led_sd.off()
led_gps.off();    led_alti.off(); led_fault.off()

# SD mount retry -- boot-time mounting has been observed to fail
# intermittently across resets with no physical handling involved,
# consistent with a marginal power-up timing margin rather than a
# seating/connection issue. Retry a few times with a short settle
# delay between attempts before giving up, same pattern as init_gps().
SD_MOUNT_RETRIES = 3
for _sd_attempt in range(1, SD_MOUNT_RETRIES + 1):
    if mount_sd():
        if _sd_attempt > 1:
            print(f"SD mount succeeded on retry (attempt {_sd_attempt}/{SD_MOUNT_RETRIES})")
        break
    print(f"SD mount attempt {_sd_attempt}/{SD_MOUNT_RETRIES} failed, retrying...")
    time.sleep_ms(300)
else:
    print("SD mount failed after all retries -- flight log will not record")

open_flight_log()
init_gps()
init_baro()

# ============================================================================
# MAIN COMMAND LOOP
# ============================================================================
_cam_init_done    = False
_last_gps_beacon  = time.ticks_ms()
_last_baro_read   = time.ticks_ms()
BARO_READ_INTERVAL_MS = 2000

while True:
    payload = recv_esp(timeout_ms=5000)
    now     = time.ticks_ms()

    update_gps_led()

    if time.ticks_diff(now, _last_baro_read) >= BARO_READ_INTERVAL_MS:
        read_baro()
        _last_baro_read = now

    if payload is None:
        if not _cam_init_done:
            init_camera()
            _cam_init_done = True

        if _beacon_enabled and time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
            pkt = gps_packet()
            send_esp(pkt)
            write_flight_packet()
            print(f"Beacon: {pkt.decode()}")
            blink(1)
            _last_gps_beacon = now
        continue

    if len(payload) == 0:
        if _beacon_enabled and time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
            pkt = gps_packet()
            send_esp(pkt)
            write_flight_packet()
            print(f"Beacon: {pkt.decode()}")
            blink(1)
            _last_gps_beacon = now
        continue

    sub_opcode = payload[0]

    if sub_opcode == CMD_PING:
        send_esp(b"PICO:ACK"); blink(1)
        print("PING -> PICO:ACK")

    elif sub_opcode == CMD_READ_TEMP:
        temp = read_temp_celsius()
        msg  = f"TEMP:{temp}C"
        send_esp(msg.encode()); blink(2)
        print(f"READ_TEMP -> {msg}")

    elif sub_opcode == CMD_SNAP:
        blink(1); print("SNAP...")
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"SNAP:ERR:NO_SD")
            else:
                cam_cs(1); sd_cs(1)
                time.sleep_ms(50)
                if not init_camera():
                    send_esp(b"SNAP:ERR:NO_CAM")
                else:
                    jpeg = capture_jpeg()
                    cam_cs(1); sd_cs(1)
                    time.sleep_ms(10)
                    filename = next_filename()
                    with open(f"/sd/{filename}", 'wb') as f: f.write(jpeg)
                    msg = f"SNAP:OK:{filename}:{len(jpeg)}"
                    send_esp(msg.encode()); blink(3)
                    write_flight_packet(f"SNAP:{filename}")
                    print(f"SNAP -> {msg}")
        except Exception as e:
            cam_cs(1); sd_cs(1)
            send_esp(f"SNAP:ERR:{str(e)[:20]}".encode())
            print(f"SNAP error: {e}")

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
                    CHUNK = MAX_PAYLOAD - 8
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
                    if chunk_files:
                        prefix = "LIST:" if first_chunk else "LIST+:"
                        msg = prefix + ",".join(chunk_files)
                        send_esp(msg.encode()); blink(2)
                        print(f"LIST final: {msg}")
        except Exception as e:
            send_esp(b"LIST:ERR:FAIL")
            print(f"LIST error: {e}")

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
                print(f"GET_INFO -> {msg}")
        except Exception as e:
            send_esp(b"INFO:ERR:NOTFOUND")
            print(f"GET_INFO error: {e}")

    elif sub_opcode == CMD_GET_CHUNK:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"CHUNK:ERR:NO_SD")
            else:
                chunk_index = payload[1] | (payload[2] << 8)
                filename    = payload[3:].decode('utf-8').strip()
                offset      = chunk_index * CHUNK_SIZE
                with open(f"/sd/{filename}", 'rb') as f:
                    f.seek(offset); data = f.read(CHUNK_SIZE)
                send_esp(f"CHUNK:{chunk_index}:".encode() + data)
                print(f"GET_CHUNK {chunk_index}: {len(data)} bytes")
        except Exception as e:
            send_esp(b"CHUNK:ERR:FAIL")
            print(f"GET_CHUNK error: {e}")

    elif sub_opcode == CMD_DELETE:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"DEL:ERR:NO_SD")
            else:
                filename = payload[1:].decode('utf-8').strip()
                uos.remove(f"/sd/{filename}")
                msg = f"DEL:OK:{filename}"
                send_esp(msg.encode()); blink(2)
                print(f"DELETE -> {msg}")
        except Exception as e:
            send_esp(b"DEL:ERR:FAIL")
            print(f"DELETE error: {e}")

    elif sub_opcode == CMD_GET_GPS:
        read_baro()
        pkt = gps_packet()
        send_esp(pkt)
        write_flight_packet("CMD_GET_GPS")
        blink(1)
        print(f"GET_GPS -> {pkt.decode()}")

    elif sub_opcode == CMD_GET_BARO:
        read_baro()
        pkt = baro_packet()
        send_esp(pkt); blink(1)
        print(f"GET_BARO -> {pkt.decode()}")

    elif sub_opcode == CMD_BEACON_CTRL:
        if len(payload) >= 2:
            _beacon_enabled = (payload[1] == 0x01)
        state = "ON" if _beacon_enabled else "OFF"
        resp = f"BEACON:{state}"
        send_esp(resp.encode()); blink(1)
        print(f"BEACON_CTRL -> {resp}")

    else:
        err = f"ERR:UNKNOWN:{sub_opcode:#04x}"
        send_esp(err.encode())
        print(f"Unknown sub-opcode: {sub_opcode:#04x}")
