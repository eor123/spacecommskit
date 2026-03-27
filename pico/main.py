# main.py — OpenLST Pico Pipeline
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

CHUNK_SIZE    = 200  # bytes of image data per chunk — fits in ESP_MAX_PAYLOAD

# ── UART0 — connects to OpenLST board 0004 UART0 ──────────────────────────
uart = UART(0, baudrate=115200, tx=Pin(0), rx=Pin(1))

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
# Camera initialised on first idle timeout so UART listener
# is not blocked during the slow I2C register sequence at boot

# ── Main loop ──────────────────────────────────────────────────────────────
_cam_init_done = False

while True:
    payload = recv_esp(timeout_ms=5000)

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
    # Payload: sub-opcode(1) + filename bytes
    # Response: INFO:<filename>:<total_bytes>:<total_chunks>
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
    # Payload: sub-opcode(1) + chunk_index(2 bytes LE) + filename bytes
    # Response: CHUNK:<index>: followed by raw binary data bytes
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
                # Response: header string + raw binary data
                header = f"CHUNK:{chunk_index}:".encode()
                send_esp(header + data)
                print(f"GET_CHUNK {chunk_index} → {len(data)} bytes")
        except Exception as e:
            send_esp(b"CHUNK:ERR:FAIL")
            print(f"GET_CHUNK error: {e}")

    # ── 0x06 DELETE ────────────────────────────────────────────────────────
    # Payload: sub-opcode(1) + filename bytes
    # Response: DEL:OK:<filename> or DEL:ERR:<reason>
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

    else:
        err = f"ERR:UNKNOWN:{sub_opcode:#04x}"
        send_esp(err.encode())
        print(f"Unknown sub-opcode: {sub_opcode:#04x}")

