# main.py — OpenLST Pico Pipeline + GPS
# MicroPython v1.27 on Raspberry Pi Pico (RP2040)
#
# Payload byte 0 = sub-opcode:
#   0x00 = PING        → PICO:ACK
#   0x01 = READ_TEMP   → TEMP:xx.xxC
#   0x02 = SNAP        → SNAP:OK:<filename>:<bytes>
#   0x03 = LIST        → LIST:<file1>,<file2>,...
#   0x04 = GET_INFO    → INFO:<filename>:<bytes>:<chunks>  or INFO:ERR:<reason>
#   0x05 = GET_CHUNK   → CHUNK:<index>:<200 bytes data>    or CHUNK:ERR:<reason>
#   0x06 = DELETE      → DEL:OK:<filename>                 or DEL:ERR:<reason>
#   0x07 = GET_GPS     → GPS:<lat>,<lon>,<alt>,<sats>,<fix>  or GPS:ERR:<reason>
#
# GPS also transmits autonomously every GPS_BEACON_INTERVAL_MS (10 seconds)
# so the ground station gets a live track without issuing commands.
# GPS log is also written to /sd/gps_log.txt for post-flight analysis.

from machine import UART, ADC, Pin, SPI, I2C
import time
import uos
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

CHUNK_SIZE    = 200  # bytes of image data per chunk — fits in ESP_MAX_PAYLOAD

# ── GPS beacon interval ────────────────────────────────────────────────────
GPS_BEACON_INTERVAL_MS = 10000  # send autonomous GPS packet every 10 seconds

# ── UART0 — connects to OpenLST board 0004 UART0 ──────────────────────────
uart = UART(0, baudrate=115200, tx=Pin(0), rx=Pin(1))

# ── UART1 — GPS6MV2 (NEO-6M) module ───────────────────────────────────────
gps_uart = UART(1, baudrate=9600, tx=Pin(8), rx=Pin(9))

# ── Status LED ─────────────────────────────────────────────────────────────
led = Pin(25, Pin.OUT)

def blink(times=1):
    for _ in range(times):
        led.on()
        time.sleep_ms(80)
        led.off()
        time.sleep_ms(80)

# ── Onboard temperature sensor ─────────────────────────────────────────────
temp_sensor       = ADC(4)
CONVERSION_FACTOR = 3.3 / 65535

def read_temp_celsius():
    reading = temp_sensor.read_u16() * CONVERSION_FACTOR
    temp_c  = 27 - (reading - 0.706) / 0.001721
    return round(temp_c, 2)

# ── SPI bus (shared: Arducam + SD card) ───────────────────────────────────
spi = SPI(0,
    baudrate=400000,
    polarity=0,
    phase=0,
    bits=8,
    firstbit=SPI.MSB,
    sck=Pin(18),
    mosi=Pin(19),
    miso=Pin(16))

cam_cs = Pin(15, Pin.OUT, value=1)
sd_cs  = Pin(17, Pin.OUT, value=1)

# ── I2C for OV2640 config ──────────────────────────────────────────────────
i2c = I2C(0, sda=Pin(4), scl=Pin(5), freq=100000)
CAM_ADDR = 0x30

# ── SD card mount ──────────────────────────────────────────────────────────
sd_mounted = False

def mount_sd():
    global sd_mounted, spi
    if sd_mounted:
        return True
    try:
        sd  = sdcard.SDCard(spi, sd_cs)
        vfs = uos.VfsFat(sd)
        uos.mount(vfs, '/sd')
        sd_mounted = True
        print("SD mounted")
        # Reinit SPI after SD operations — SD card leaves bus in unexpected state
        # which corrupts Arducam FIFO register reads if not reset
        spi = SPI(0, baudrate=400000, polarity=0, phase=0,
            bits=8, firstbit=SPI.MSB,
            sck=Pin(18), mosi=Pin(19), miso=Pin(16))
        cam_cs(1)
        sd_cs(1)
        time.sleep_ms(50)
        return True
    except Exception as e:
        print(f"SD mount failed: {e}")
        return False

# ── GPS — NEO-6M / GPS6MV2 ────────────────────────────────────────────────
# Holds the most recent valid GPS fix. Updated continuously by poll_gps().
gps_fix = {
    'lat':  0.0,
    'lon':  0.0,
    'alt':  0.0,   # metres above MSL (from GPGGA)
    'sats': 0,     # satellites in use
    'fix':  False, # True = valid fix
    'time': '',    # UTC time string from NMEA e.g. "123519"
    'date': '',    # UTC date string from NMEA e.g. "230394"
}

# UBX command to set NEO-6M into Airborne (<1g) dynamic model.
# Without this the module stops reporting above ~18km (CoCom limit).
# CFG-NAV5: dynModel=6 (Airborne <1g), fixMode=3 (Auto 2D/3D)
_UBX_AIRBORNE = bytes([
    0xB5, 0x62,             # UBX sync chars
    0x06, 0x24,             # Class CFG, ID NAV5
    0x24, 0x00,             # payload length 36
    0xFF, 0xFF,             # mask — apply all settings
    0x06,                   # dynModel = 6 (Airborne <1g)
    0x03,                   # fixMode = 3 (Auto 2D/3D)
    0x00, 0x00, 0x00, 0x00, # fixedAlt
    0x10, 0x27, 0x00, 0x00, # fixedAltVar
    0x05,                   # minElev
    0x00,                   # drLimit
    0xFA, 0x00,             # pDop
    0xFA, 0x00,             # tDop
    0x64, 0x00,             # pAcc
    0x2C, 0x01,             # tAcc
    0x00,                   # staticHoldThresh
    0x3C,                   # dgpsTimeOut
    0x00, 0x00, 0x00, 0x00, # reserved
    0x00, 0x00, 0x00, 0x00, # reserved
    0x00, 0x00,             # reserved
    0x16, 0xDC,             # checksum A, B
])

def init_gps():
    """Send airborne mode config to NEO-6M and flush startup NMEA."""
    print("GPS: sending airborne mode UBX config...")
    gps_uart.write(_UBX_AIRBORNE)
    time.sleep_ms(500)
    # Flush any buffered NMEA sentences from startup
    while gps_uart.any():
        gps_uart.read(gps_uart.any())
    print("GPS: ready")

def _nmea_checksum_ok(sentence):
    """Validate NMEA checksum. sentence includes leading $ and *XX."""
    try:
        star = sentence.rindex('*')
        body = sentence[1:star]
        expected = int(sentence[star+1:star+3], 16)
        calc = 0
        for c in body:
            calc ^= ord(c)
        return calc == expected
    except Exception:
        return False

def _parse_lat(val, hemi):
    """Convert NMEA ddmm.mmmm + N/S to signed decimal degrees."""
    if not val:
        return 0.0
    deg = float(val[:2])
    mins = float(val[2:])
    result = deg + mins / 60.0
    if hemi == 'S':
        result = -result
    return round(result, 6)

def _parse_lon(val, hemi):
    """Convert NMEA dddmm.mmmm + E/W to signed decimal degrees."""
    if not val:
        return 0.0
    deg = float(val[:3])
    mins = float(val[3:])
    result = deg + mins / 60.0
    if hemi == 'W':
        result = -result
    return round(result, 6)

def _parse_gprmc(fields):
    """
    Parse $GPRMC sentence — provides lat, lon, date, time, fix status.
    $GPRMC,time,status,lat,N/S,lon,E/W,speed,course,date,magvar,E/W*cs
    """
    global gps_fix
    try:
        if len(fields) < 10:
            return
        status = fields[2]   # A = active (valid), V = void
        if status != 'A':
            gps_fix['fix'] = False
            return
        gps_fix['lat']  = _parse_lat(fields[3], fields[4])
        gps_fix['lon']  = _parse_lon(fields[5], fields[6])
        gps_fix['time'] = fields[1]
        gps_fix['date'] = fields[9]
        gps_fix['fix']  = True
    except Exception as e:
        print(f"GPRMC parse error: {e}")

def _parse_gpgga(fields):
    """
    Parse $GPGGA sentence — provides altitude and satellite count.
    $GPGGA,time,lat,N/S,lon,E/W,fix,sats,hdop,alt,M,...*cs
    """
    global gps_fix
    try:
        if len(fields) < 10:
            return
        fix_quality = int(fields[6]) if fields[6] else 0
        if fix_quality == 0:
            return
        gps_fix['sats'] = int(fields[7]) if fields[7] else 0
        gps_fix['alt']  = float(fields[9]) if fields[9] else 0.0
    except Exception as e:
        print(f"GPGGA parse error: {e}")

# Line buffer for GPS UART — accumulates bytes into complete NMEA sentences
_gps_buf = bytearray()

def poll_gps():
    """
    Call this frequently from the main loop.
    Reads all available bytes from GPS UART, assembles complete NMEA sentences,
    parses GPRMC and GPGGA. Non-blocking — returns immediately if no data.
    """
    global _gps_buf
    while gps_uart.any():
        b = gps_uart.read(1)
        if b is None:
            break
        ch = b[0]
        if ch == ord('\n'):
            # Complete sentence received
            line = _gps_buf.decode('ascii', 'ignore').strip()
            _gps_buf = bytearray()
            if line.startswith('$') and '*' in line:
                if _nmea_checksum_ok(line):
                    fields = line.split(',')
                    tag = fields[0][1:]  # strip leading $
                    if tag in ('GPRMC', 'GNRMC'):
                        _parse_gprmc(fields)
                    elif tag in ('GPGGA', 'GNGGA'):
                        _parse_gpgga(fields)
        elif ch != ord('\r'):
            if len(_gps_buf) < 120:  # guard against runaway buffer
                _gps_buf.append(ch)

def gps_packet():
    """Build the GPS response string from the current fix."""
    if gps_fix['fix']:
        return (f"GPS:{gps_fix['lat']:.6f},"
                f"{gps_fix['lon']:.6f},"
                f"{gps_fix['alt']:.1f},"
                f"{gps_fix['sats']},"
                f"1").encode()
    else:
        return b"GPS:0.000000,0.000000,0.0,0,0"

def log_gps_to_sd():
    """Append current GPS fix to /sd/gps_log.txt if SD is mounted and fix is valid."""
    if not sd_mounted or not gps_fix['fix']:
        return
    try:
        line = (f"{gps_fix['date']},{gps_fix['time']},"
                f"{gps_fix['lat']:.6f},{gps_fix['lon']:.6f},"
                f"{gps_fix['alt']:.1f},{gps_fix['sats']}\n")
        with open('/sd/gps_log.txt', 'a') as f:
            f.write(line)
    except Exception as e:
        print(f"GPS log write error: {e}")

# ── Arducam SPI helpers ────────────────────────────────────────────────────
def cam_write(addr, data):
    cam_cs(0)
    spi.write(bytes([addr | 0x80, data]))
    cam_cs(1)

def cam_read(addr):
    cam_cs(0)
    spi.write(bytes([addr & 0x7F]))
    r = spi.read(1, 0xFF)
    cam_cs(1)
    return r[0]

def ov_write(reg, val):
    i2c.writeto_mem(CAM_ADDR, reg, bytes([val]))
    time.sleep_ms(1)

def ov_write_list(regs):
    for reg, val in regs:
        if reg == 0xFF and val == 0xFF:
            time.sleep_ms(10)
        else:
            ov_write(reg, val)

# ── OV2640 register init sequences ────────────────────────────────────────
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
    global cam_ready
    try:
        cam_write(0x07, 0x80)
        time.sleep_ms(100)
        cam_write(0x07, 0x00)
        time.sleep_ms(100)
        ov_write(0xFF, 0x01)
        ov_write(0x12, 0x80)
        time.sleep_ms(100)
        ov_write_list(OV2640_JPEG_INIT)
        ov_write_list(OV2640_JPEG_320x240)
        ov_write_list(OV2640_JPEG_FORMAT)
        cam_write(0x00, 0x00)
        cam_ready = True
        print("Camera ready")
        return True
    except Exception as e:
        print(f"Camera init failed: {e}")
        cam_ready = False
        return False

def capture_jpeg():
    cam_write(0x04, 0x01)
    cam_write(0x04, 0x00)
    cam_write(0x04, 0x02)
    for _ in range(1000):
        if cam_read(0x41) & 0x08:
            break
        time.sleep_ms(1)
    else:
        raise OSError("Capture timeout")

    # Read FIFO length registers with extra delay for breadboard reliability
    time.sleep_ms(10)
    len1 = cam_read(0x42)
    time.sleep_ms(5)
    len2 = cam_read(0x43)
    time.sleep_ms(5)
    len3 = cam_read(0x44) & 0x7F
    fifo_len = (len3 << 16) | (len2 << 8) | len1
    print(f"FIFO: {len3:#04x} {len2:#04x} {len1:#04x} = {fifo_len}")

    if fifo_len == 0 or fifo_len > 100000:
        raise OSError(f"Invalid FIFO: {fifo_len}")
    cam_cs(0)
    spi.write(bytes([0x3C]))
    data = spi.read(fifo_len + 1, 0xFF)
    cam_cs(1)
    return b'\xff' + data[1:]

def next_filename():
    try:
        files = uos.listdir('/sd')
        nums  = []
        for f in files:
            if f.startswith('snap_') and f.endswith('.jpg'):
                try:
                    nums.append(int(f[5:8]))
                except:
                    pass
        n = max(nums) + 1 if nums else 1
        return f"snap_{n:03d}.jpg"
    except:
        return "snap_001.jpg"

# ── ESP framing helpers ────────────────────────────────────────────────────
def send_esp(payload_bytes):
    frame = bytes([ESP_START_0, ESP_START_1, len(payload_bytes)]) + payload_bytes
    uart.write(frame)

def recv_esp(timeout_ms=5000):
    STATE_START0, STATE_START1, STATE_LENGTH, STATE_PAYLOAD = 0, 1, 2, 3
    state     = STATE_START0
    payload   = bytearray()
    remaining = 0
    deadline  = time.ticks_add(time.ticks_ms(), timeout_ms)
    while time.ticks_diff(deadline, time.ticks_ms()) > 0:
        # ── Service GPS while waiting for a command ────────────────────────
        poll_gps()
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
                    remaining = b
                    payload   = bytearray()
                    state     = STATE_PAYLOAD
                else:
                    state = STATE_START0
            elif state == STATE_PAYLOAD:
                payload.append(b)
                remaining -= 1
                if remaining == 0:
                    return bytes(payload)
        else:
            time.sleep_ms(1)
    return None

# ── Startup ────────────────────────────────────────────────────────────────
blink(3)
print("OpenLST Pico ready — ESP framing on UART0 @ 115200")
mount_sd()
init_gps()
# Camera initialised on first idle timeout so UART listener
# is not blocked during the slow I2C register sequence at boot

# ── Main loop ──────────────────────────────────────────────────────────────
_cam_init_done    = False
_last_gps_beacon  = time.ticks_ms()

while True:
    payload = recv_esp(timeout_ms=5000)

    # ── Autonomous GPS beacon — send every GPS_BEACON_INTERVAL_MS ─────────
    now = time.ticks_ms()
    if time.ticks_diff(now, _last_gps_beacon) >= GPS_BEACON_INTERVAL_MS:
        pkt = gps_packet()
        send_esp(pkt)
        log_gps_to_sd()
        print(f"GPS beacon → {pkt.decode()}")
        blink(1)
        _last_gps_beacon = now

    # Use first idle timeout to init camera in background
    if payload is None:
        if not _cam_init_done:
            init_camera()
            _cam_init_done = True
        continue

    if len(payload) == 0:
        continue

    sub_opcode = payload[0]

    if sub_opcode == CMD_PING:
        send_esp(b"PICO:ACK")
        blink(1)
        print("PING → PICO:ACK")

    elif sub_opcode == CMD_READ_TEMP:
        temp = read_temp_celsius()
        msg  = f"TEMP:{temp}C"
        send_esp(msg.encode())
        blink(2)
        print(f"READ_TEMP → {msg}")

    elif sub_opcode == CMD_SNAP:
        blink(1)
        print("SNAP received...")
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"SNAP:ERR:NO_SD")
            elif not cam_ready and not init_camera():
                send_esp(b"SNAP:ERR:NO_CAM")
            else:
                jpeg     = capture_jpeg()
                filename = next_filename()
                with open(f"/sd/{filename}", 'wb') as f:
                    f.write(jpeg)
                msg = f"SNAP:OK:{filename}:{len(jpeg)}"
                send_esp(msg.encode())
                blink(3)
                print(f"SNAP → {msg}")
        except Exception as e:
            err = f"SNAP:ERR:{str(e)[:20]}"
            send_esp(err.encode())
            print(f"SNAP error: {e}")

    elif sub_opcode == CMD_LIST:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"LIST:ERR:NO_SD")
            else:
                files = [f for f in uos.listdir('/sd')
                         if f.endswith('.jpg') or f.endswith('.txt')]
                msg = ("LIST:" + ",".join(files)) if files else "LIST:EMPTY"
                if len(msg) > MAX_PAYLOAD - 1:
                    msg = msg[:MAX_PAYLOAD - 4] + "..."
                send_esp(msg.encode())
                blink(2)
                print(f"LIST → {msg}")
        except Exception as e:
            send_esp(b"LIST:ERR:FAIL")
            print(f"LIST error: {e}")

    # ── 0x04 GET_INFO ──────────────────────────────────────────────────────
    elif sub_opcode == CMD_GET_INFO:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"INFO:ERR:NO_SD")
            else:
                filename = payload[1:].decode('utf-8').strip()
                path     = f"/sd/{filename}"
                size     = uos.stat(path)[6]
                chunks   = (size + CHUNK_SIZE - 1) // CHUNK_SIZE
                msg      = f"INFO:{filename}:{size}:{chunks}"
                send_esp(msg.encode())
                print(f"GET_INFO → {msg}")
        except Exception as e:
            send_esp(b"INFO:ERR:NOTFOUND")
            print(f"GET_INFO error: {e}")

    # ── 0x05 GET_CHUNK ─────────────────────────────────────────────────────
    elif sub_opcode == CMD_GET_CHUNK:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"CHUNK:ERR:NO_SD")
            else:
                chunk_index = payload[1] | (payload[2] << 8)
                filename    = payload[3:].decode('utf-8').strip()
                path        = f"/sd/{filename}"
                offset      = chunk_index * CHUNK_SIZE
                with open(path, 'rb') as f:
                    f.seek(offset)
                    data = f.read(CHUNK_SIZE)
                header = f"CHUNK:{chunk_index}:".encode()
                send_esp(header + data)
                print(f"GET_CHUNK {chunk_index} → {len(data)} bytes")
        except Exception as e:
            send_esp(b"CHUNK:ERR:FAIL")
            print(f"GET_CHUNK error: {e}")

    # ── 0x06 DELETE ────────────────────────────────────────────────────────
    elif sub_opcode == CMD_DELETE:
        try:
            if not sd_mounted and not mount_sd():
                send_esp(b"DEL:ERR:NO_SD")
            else:
                filename = payload[1:].decode('utf-8').strip()
                path     = f"/sd/{filename}"
                uos.remove(path)
                msg = f"DEL:OK:{filename}"
                send_esp(msg.encode())
                blink(2)
                print(f"DELETE → {msg}")
        except Exception as e:
            send_esp(b"DEL:ERR:FAIL")
            print(f"DELETE error: {e}")

    # ── 0x07 GET_GPS ───────────────────────────────────────────────────────
    # Payload: sub-opcode(1) only
    # Response: GPS:<lat>,<lon>,<alt>,<sats>,<fix>
    #   lat/lon = decimal degrees (negative = S/W)
    #   alt     = metres above MSL
    #   sats    = satellites in use
    #   fix     = 1 (valid) or 0 (no fix)
    elif sub_opcode == CMD_GET_GPS:
        pkt = gps_packet()
        send_esp(pkt)
        log_gps_to_sd()
        blink(1)
        print(f"GET_GPS → {pkt.decode()}")

    else:
        err = f"ERR:UNKNOWN:{sub_opcode:#04x}"
        send_esp(err.encode())
        print(f"Unknown sub-opcode: {sub_opcode:#04x}")
