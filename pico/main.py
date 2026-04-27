# main.py — OpenLST Pico Pipeline + GPS + BMP581 Altimeter + Status LEDs
# MicroPython v1.27 on Raspberry Pi Pico (RP2040)
#
# Payload byte 0 = sub-opcode:
#   0x00 = PING        → PICO:ACK
#   0x01 = READ_TEMP   → TEMP:xx.xxC
#   0x02 = SNAP        → SNAP:OK:<filename>:<bytes>
#   0x03 = LIST        → LIST:<file1>,<file2>,...
#   0x04 = GET_INFO    → INFO:<filename>:<bytes>:<chunks>
#   0x05 = GET_CHUNK   → CHUNK:<index>:<200 bytes data>
#   0x06 = DELETE      → DEL:OK:<filename>
#   0x07 = GET_GPS     → GPS:<lat>,<lon>,<gps_alt>,<sats>,<fix>,<hpa>,<baro_alt>,<temp_c>
#   0x08 = GET_BARO    → BARO:<hpa>,<baro_alt>,<temp_c>
#   0x09 = BEACON_CTRL → BEACON:ON or BEACON:OFF  (payload[1] = 0x01 ON, 0x00 OFF)
#
# GPS beacon transmits autonomously every GPS_BEACON_INTERVAL_MS (10 seconds)
# Fused beacon includes both GPS and barometric data in one packet.
# GPS log written to /sd/gps_log.txt for post-flight analysis.
#
# Status LEDs:
#   GP10 GREEN  Camera Ready     — solid on after OV2640 init OK
#   GP11 GREEN  SD Card Present  — solid on after SD mount OK
#   GP12 BLUE   GPS Ready        — slow flash searching, solid on fix acquired
#   GP13 YELLOW Altimeter Ready  — solid on after BMP581 init OK
#   GP14 RED    Fault            — solid on if any subsystem failed

from machine import UART, ADC, Pin, SPI, I2C
import time
import uos
import math
import sdcard

# ── Constants matching OpenLST uart.h ─────────────────────────────────────
ESP_START_0   = 0x22
ESP_START_1   = 0x69
MAX_PAYLOAD   = 251

# ── Sub-opcodes ────────────────────────────────────────────────────────────
CMD_PING      = 0x00
CMD_READ_TEMP = 0x01
CMD_SNAP      = 0x02
CMD_LIST      = 0x03
CMD_GET_INFO  = 0x04
CMD_GET_CHUNK = 0x05
CMD_DELETE    = 0x06
CMD_GET_GPS   = 0x07
CMD_GET_BARO  = 0x08
CMD_BEACON_CTRL = 0x09  # payload[1]: 0x01=ON, 0x00=OFF

CHUNK_SIZE    = 200

# ── Beacon interval ────────────────────────────────────────────────────────
GPS_BEACON_INTERVAL_MS = 10000  # 10 seconds
_beacon_enabled = True          # controlled by CMD_BEACON_CTRL (0x09)

# ── UART0 — OpenLST board UART0 ───────────────────────────────────────────
uart = UART(0, baudrate=115200, tx=Pin(0), rx=Pin(1))

# ── UART1 — GPS6MV2 (NEO-6M) ──────────────────────────────────────────────
gps_uart = UART(1, baudrate=9600, tx=Pin(8), rx=Pin(9))

# ── Onboard status LED ─────────────────────────────────────────────────────
led = Pin(25, Pin.OUT)

def blink(times=1):
    for _ in range(times):
        led.on(); time.sleep_ms(80)
        led.off(); time.sleep_ms(80)

# ── Status LEDs ────────────────────────────────────────────────────────────
led_camera = Pin(10, Pin.OUT, value=0)  # GREEN  Camera Ready
led_sd     = Pin(11, Pin.OUT, value=0)  # GREEN  SD Card Present
led_gps    = Pin(12, Pin.OUT, value=0)  # BLUE   GPS Ready
led_alti   = Pin(13, Pin.OUT, value=0)  # YELLOW Altimeter Ready
led_fault  = Pin(14, Pin.OUT, value=0)  # RED    Fault

# Fault flags — any True lights the red fault LED
_fault_camera = False
_fault_sd     = False
_fault_alti   = False

def update_fault_led():
    led_fault.value(1 if (_fault_camera or _fault_sd or _fault_alti) else 0)

# ── GPS LED flash state (slow flash = searching, solid = fix) ──────────────
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

# ── Onboard temperature sensor ─────────────────────────────────────────────
temp_sensor       = ADC(4)
CONVERSION_FACTOR = 3.3 / 65535

def read_temp_celsius():
    reading = temp_sensor.read_u16() * CONVERSION_FACTOR
    return round(27 - (reading - 0.706) / 0.001721, 2)

# ── SPI bus (shared: Arducam + SD card) ───────────────────────────────────
spi = SPI(0, baudrate=400000, polarity=0, phase=0, bits=8,
          firstbit=SPI.MSB, sck=Pin(18), mosi=Pin(19), miso=Pin(16))

cam_cs = Pin(15, Pin.OUT, value=1)
sd_cs  = Pin(17, Pin.OUT, value=1)

# ── I2C0 — OV2640 config ──────────────────────────────────────────────────
i2c0 = I2C(0, sda=Pin(4), scl=Pin(5), freq=100000)
CAM_ADDR = 0x30

# ── I2C1 — BMP581 ─────────────────────────────────────────────────────────
i2c1 = I2C(1, sda=Pin(2), scl=Pin(3), freq=400000)

# ── SD card mount ──────────────────────────────────────────────────────────
sd_mounted = False

def mount_sd():
    global sd_mounted, spi, _fault_sd
    if sd_mounted:
        return True
    try:
        sd  = sdcard.SDCard(spi, sd_cs)
        vfs = uos.VfsFat(sd)
        uos.mount(vfs, '/sd')
        sd_mounted = True
        _fault_sd  = False
        led_sd.on()
        update_fault_led()
        print("SD mounted ✓")
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

# ══════════════════════════════════════════════════════════════════════════
#  BMP581 DRIVER
# ══════════════════════════════════════════════════════════════════════════
BMP581_ADDR    = 0x47   # SparkFun default — SDO tied to 3.3V
BMP581_CHIP_ID = 0x50

_BMP_CHIP_ID    = 0x01
_BMP_TEMP_XL    = 0x1D
_BMP_PRESS_XL   = 0x20
_BMP_OSR_CONFIG = 0x36
_BMP_ODR_CONFIG = 0x37

SEA_LEVEL_HPA   = 1013.25  # standard atmosphere — adjust for local QNH

baro_ready = False
baro_data  = {'hpa': 0.0, 'alt_m': 0.0, 'temp_c': 0.0, 'valid': False}

def _bmp_read(reg, nbytes=1):
    i2c1.writeto(BMP581_ADDR, bytes([reg]))
    return i2c1.readfrom(BMP581_ADDR, nbytes)

def _bmp_write(reg, val):
    i2c1.writeto(BMP581_ADDR, bytes([reg, val]))
    time.sleep_ms(5)

def init_baro():
    global baro_ready, _fault_alti
    try:
        chip_id = _bmp_read(_BMP_CHIP_ID)[0]
        if chip_id != BMP581_CHIP_ID:
            raise OSError(f"BMP581 ID mismatch: 0x{chip_id:02X}")
        # OSR: pressure x4, temp x1, pressure enabled
        _bmp_write(_BMP_OSR_CONFIG, 0b01011000)
        # ODR: normal mode continuous ~50Hz
        _bmp_write(_BMP_ODR_CONFIG, 0b01100101)
        time.sleep_ms(100)
        baro_ready  = True
        _fault_alti = False
        led_alti.on()
        update_fault_led()
        print("BMP581 altimeter ready ✓")
        return True
    except Exception as e:
        baro_ready  = False
        _fault_alti = True
        led_alti.off()
        update_fault_led()
        print(f"BMP581 init failed: {e}")
        return False

def read_baro():
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
    if baro_data['valid']:
        return (f"BARO:{baro_data['hpa']:.2f},"
                f"{baro_data['alt_m']:.1f},"
                f"{baro_data['temp_c']:.2f}").encode()
    return b"BARO:ERR:NOT_READY"

# ══════════════════════════════════════════════════════════════════════════
#  GPS DRIVER
# ══════════════════════════════════════════════════════════════════════════
gps_fix = {
    'lat': 0.0, 'lon': 0.0, 'alt': 0.0,
    'sats': 0,  'fix': False,
    'time': '', 'date': '',
}

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
    print("GPS: sending airborne mode UBX config...")
    gps_uart.write(_UBX_AIRBORNE)
    time.sleep_ms(500)
    while gps_uart.any():
        gps_uart.read(gps_uart.any())
    print("GPS: ready ✓")

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
    """
    Fused GPS + baro packet.
    Format: GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c
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

# ── SD Black Box Flight Recorder ──────────────────────────────────────────
# Opens a .sckflight file at boot — one file per power cycle
# Logs every beacon packet in Flight Replay compatible JSON format
# This is the payload black box — complete mission data even if RF fails
# Compatible with FlightReplayForm — drop into Flights folder and replay

_flight_log_file   = None   # open file handle
_flight_log_count  = 0      # packets written
_flight_log_t0     = time.ticks_ms()  # boot time reference
_flight_log_name   = ""     # filename for status

def _next_flight_number():
    """Find next FLT-NNN sequence number on SD card."""
    try:
        files = uos.listdir('/sd')
        nums = [int(f[4:7]) for f in files
                if f.startswith('FLT-') and f.endswith('.sckflight')]
        return (max(nums) + 1) if nums else 1
    except:
        return 1

def open_flight_log():
    """Open a new .sckflight file for this power cycle."""
    global _flight_log_file, _flight_log_name
    if not sd_mounted:
        print("Flight log: SD not mounted — skipping")
        return
    try:
        num  = _next_flight_number()
        name = f"/sd/FLT-{num:03d}.sckflight"
        _flight_log_file = open(name, 'w')
        _flight_log_name = name
        # Write opening JSON structure — matches ground station format
        _flight_log_file.write('{"flight_id":"FLT-' + f'{num:03d}' + '",'
                               '"hardware":"SCK-915+SCK-PBL-1",'
                               '"packets":[\n')
        _flight_log_file.flush()
        print(f"Flight log opened: {name}")
    except Exception as e:
        print(f"Flight log open failed: {e}")
        _flight_log_file = None

def write_flight_packet(event=""):
    """Write one beacon packet to the SD flight log."""
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
        # Flush every 10 packets (~100 seconds)
        # After flush rewrite a valid closing so file is always parseable
        # even if power is cut before close_flight_log() is called
        if _flight_log_count % 10 == 0:
            _flight_log_file.flush()
            print(f"Flight log: {_flight_log_count} packets written")
    except Exception as e:
        print(f"Flight log write error: {e}")

def close_flight_log():
    """Close the flight log with summary footer — call on clean shutdown."""
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



# ══════════════════════════════════════════════════════════════════════════
#  ARDUCAM OV2640
# ══════════════════════════════════════════════════════════════════════════
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
        print("Camera ready ✓")
        return True
    except Exception as e:
        cam_ready     = False
        _fault_camera = True
        led_camera.off()
        update_fault_led()
        print(f"Camera init failed: {e}")
        return False

def capture_jpeg():
    cam_write(0x04, 0x01); cam_write(0x04, 0x00); cam_write(0x04, 0x02)
    for _ in range(1000):
        if cam_read(0x41) & 0x08: break
        time.sleep_ms(1)
    else: raise OSError("Capture timeout")
    time.sleep_ms(10)
    len1 = cam_read(0x42); time.sleep_ms(5)
    len2 = cam_read(0x43); time.sleep_ms(5)
    len3 = cam_read(0x44) & 0x7F
    fifo_len = (len3 << 16) | (len2 << 8) | len1
    print(f"FIFO: {len3:#04x} {len2:#04x} {len1:#04x} = {fifo_len}")
    if fifo_len == 0 or fifo_len > 100000:
        raise OSError(f"Invalid FIFO: {fifo_len}")
    cam_cs(0); spi.write(bytes([0x3C]))
    data = spi.read(fifo_len + 1, 0xFF); cam_cs(1)
    return b'\xff' + data[1:]

def next_filename():
    try:
        files = uos.listdir('/sd')
        nums  = [int(f[5:8]) for f in files
                 if f.startswith('snap_') and f.endswith('.jpg')]
        return f"snap_{(max(nums)+1 if nums else 1):03d}.jpg"
    except: return "snap_001.jpg"

# ══════════════════════════════════════════════════════════════════════════
#  ESP FRAMING
# ══════════════════════════════════════════════════════════════════════════
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

# ══════════════════════════════════════════════════════════════════════════
#  STARTUP
# ══════════════════════════════════════════════════════════════════════════
blink(3)
print("OpenLST Pico ready — ESP framing on UART0 @ 115200")

# All LEDs off — clean start
led_camera.off(); led_sd.off()
led_gps.off();    led_alti.off(); led_fault.off()

# Init subsystems — LEDs update as each one comes up
mount_sd()       # GP11 GREEN on if OK
open_flight_log()# Open .sckflight black box on SD
init_gps()       # GP12 BLUE starts slow-flashing
init_baro()      # GP13 YELLOW on if OK
# Camera deferred to first idle — keeps UART responsive at boot

# ══════════════════════════════════════════════════════════════════════════
#  MAIN LOOP
# ══════════════════════════════════════════════════════════════════════════
_cam_init_done    = False
_last_gps_beacon  = time.ticks_ms()
_last_baro_read   = time.ticks_ms()
BARO_READ_INTERVAL_MS = 2000  # fresh baro reading every 2 seconds

while True:
    payload = recv_esp(timeout_ms=5000)
    now     = time.ticks_ms()

    # ── GPS LED update — runs every loop iteration ─────────────────────────
    update_gps_led()

    # ── Baro background read every 2 seconds ──────────────────────────────
    if time.ticks_diff(now, _last_baro_read) >= BARO_READ_INTERVAL_MS:
        read_baro()
        _last_baro_read = now

    # ── Camera init on first idle ──────────────────────────────────────────
    if payload is None:
        if not _cam_init_done:
            init_camera()
            _cam_init_done = True
        # ── Fused GPS+baro beacon — only fire when no command pending ──────
        if _beacon_enabled and time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
            pkt = gps_packet()
            send_esp(pkt)
            write_flight_packet()   # SD black box — every beacon logged
            print(f"Beacon → {pkt.decode()}")
            blink(1)
            _last_gps_beacon = now
        continue

    if len(payload) == 0:
        # No command — safe to fire beacon
        if _beacon_enabled and time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
            pkt = gps_packet()
            send_esp(pkt)
            write_flight_packet()   # SD black box — every beacon logged
            print(f"Beacon → {pkt.decode()}")
            blink(1)
            _last_gps_beacon = now
        continue

    # ── Command received — process it BEFORE any beacon ───────────────────
    # This prevents a queued beacon from jumping the response queue
    sub_opcode = payload[0]

    # ── 0x00 PING ──────────────────────────────────────────────────────────
    if sub_opcode == CMD_PING:
        send_esp(b"PICO:ACK"); blink(1)
        print("PING → PICO:ACK")

    # ── 0x01 READ_TEMP ─────────────────────────────────────────────────────
    elif sub_opcode == CMD_READ_TEMP:
        temp = read_temp_celsius()
        msg  = f"TEMP:{temp}C"
        send_esp(msg.encode()); blink(2)
        print(f"READ_TEMP → {msg}")

    # ── 0x02 SNAP ──────────────────────────────────────────────────────────
    elif sub_opcode == CMD_SNAP:
        blink(1); print("SNAP received...")
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"SNAP:ERR:NO_SD")
            elif not cam_ready and not init_camera():
                send_esp(b"SNAP:ERR:NO_CAM")
            else:
                jpeg = capture_jpeg(); filename = next_filename()
                with open(f"/sd/{filename}", 'wb') as f: f.write(jpeg)
                msg = f"SNAP:OK:{filename}:{len(jpeg)}"
                send_esp(msg.encode()); blink(3)
                write_flight_packet(f"SNAP:{filename}")  # log image event + position
                print(f"SNAP → {msg}")
        except Exception as e:
            send_esp(f"SNAP:ERR:{str(e)[:20]}".encode())
            print(f"SNAP error: {e}")

    # ── 0x03 LIST ──────────────────────────────────────────────────────────
    elif sub_opcode == CMD_LIST:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"LIST:ERR:NO_SD")
            else:
                files = [f for f in uos.listdir('/sd')
                         if f.endswith('.jpg') or f.endswith('.txt')
                         or f.endswith('.sckflight')]
                msg = ("LIST:" + ",".join(files)) if files else "LIST:EMPTY"
                if len(msg) > MAX_PAYLOAD - 1: msg = msg[:MAX_PAYLOAD-4] + "..."
                send_esp(msg.encode()); blink(2)
                print(f"LIST → {msg}")
        except Exception as e:
            send_esp(b"LIST:ERR:FAIL"); print(f"LIST error: {e}")

    # ── 0x04 GET_INFO ──────────────────────────────────────────────────────
    elif sub_opcode == CMD_GET_INFO:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"INFO:ERR:NO_SD")
            else:
                filename = payload[1:].decode('utf-8').strip()
                size     = uos.stat(f"/sd/{filename}")[6]
                chunks   = (size + CHUNK_SIZE - 1) // CHUNK_SIZE
                msg      = f"INFO:{filename}:{size}:{chunks}"
                send_esp(msg.encode()); print(f"GET_INFO → {msg}")
        except Exception as e:
            send_esp(b"INFO:ERR:NOTFOUND"); print(f"GET_INFO error: {e}")

    # ── 0x05 GET_CHUNK ─────────────────────────────────────────────────────
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
                print(f"GET_CHUNK {chunk_index} → {len(data)} bytes")
        except Exception as e:
            send_esp(b"CHUNK:ERR:FAIL"); print(f"GET_CHUNK error: {e}")

    # ── 0x06 DELETE ────────────────────────────────────────────────────────
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
            send_esp(b"DEL:ERR:FAIL"); print(f"DELETE error: {e}")

    # ── 0x07 GET_GPS ───────────────────────────────────────────────────────
    # Response: GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c
    elif sub_opcode == CMD_GET_GPS:
        read_baro()  # fresh baro for this response
        pkt = gps_packet()
        send_esp(pkt); write_flight_packet("CMD_GET_GPS"); blink(1)
        print(f"GET_GPS → {pkt.decode()}")

    # ── 0x08 GET_BARO ──────────────────────────────────────────────────────
    # Response: BARO:hpa,baro_alt,temp_c
    elif sub_opcode == CMD_GET_BARO:
        read_baro()  # fresh reading
        pkt = baro_packet()
        send_esp(pkt); blink(1)
        print(f"GET_BARO → {pkt.decode()}")

    # ── 0x09 CMD_BEACON_CTRL ───────────────────────────────────────────────
    # payload[1] = 0x01 → beacon ON
    # payload[1] = 0x00 → beacon OFF
    # Response: BEACON:ON or BEACON:OFF
    elif sub_opcode == CMD_BEACON_CTRL:
        if len(payload) >= 2:
            _beacon_enabled = (payload[1] == 0x01)
        state = "ON" if _beacon_enabled else "OFF"
        resp = f"BEACON:{state}"
        send_esp(resp.encode()); blink(1)
        print(f"BEACON_CTRL → {resp}")

    else:
        err = f"ERR:UNKNOWN:{sub_opcode:#04x}"
        send_esp(err.encode())
        print(f"Unknown sub-opcode: {sub_opcode:#04x}")
