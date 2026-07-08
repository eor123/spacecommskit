# SpaceCommsKit SCK-2400 Developer Guide

**Version 1.10 — July 2026**
**SpaceCommsKit — https://spacecommskit.com/docs**

> This document covers the SCK-2400 development stack — from the CCSDS
> protocol foundation through the CC1352P firmware, OAD (Over-Air
> Download) system, and the CCSDS↔ESP payload bridge to the Raspberry Pi
> Pico payload board. If you are new to the system, read Section 1
> before anything else.
>
> The SCK-2400 shares its payload board architecture with the SCK-915.
> If you have read the SCK-915 Developer Guide, Section 4 (Payload Board
> Hardware Reference) will be familiar — the Pico firmware (`main.py`) is
> unchanged between the two products.

---

## Quick Reference — Search Tags

Every cross-reference in the firmware source uses a `[SCK-DEV: TAG]`
comment. Search for any tag in the codebase to jump directly to the
relevant section:

| Tag | Topic | Section |
|-----|-------|---------|
| `SCK-DEV: BOARD_ADDRESSING` | CCSDS APID board addressing | 2.1 |
| `SCK-DEV: TX_POWER` | Bench vs field power modes | 2.2 |
| `SCK-DEV: PAYLOAD_UART` | CC1352P↔Pico UART bridge | 2.3 |
| `SCK-DEV: BIM_OAD` | Bootloader / OAD boot chain | 2.4 |
| `SCK-DEV: ADD_COMMAND` | Adding new commands | 3.1 |
| `SCK-DEV: OAD_STREAMING` | OAD Phase 3 streaming transport | 3.2 |
| `SCK-DEV: CHUNKING` | Chunking large payload responses | 3.3 |
| `SCK-DEV: ESP_FRAMING` | ESP framing on the payload UART | 3.4 |
| `SCK-DEV: RESPONSE_FORMAT` | Pico response string conventions | 3.5 |
| `SCK-DEV: BEACON` | Autonomous GPS beacon | 3.6 |
| `SCK-DEV: SD_FLIGHT_LOG` | SD card flight log format | 3.7 |
| `SCK-DEV: WATCHDOG` | Hardware watchdog timer — LEO fault recovery | 3.8 |
| `SCK-DEV: CRYPTO` | AES-128-CCM RF encryption | 3.9 |
| `SCK-DEV: NV_STORAGE` | Non-volatile reset counter / safe mode | 3.10 |

---

## 1.0 System Overview

### 1.1 Architecture

```
Ground Station (Windows C#)
    ↕ USB — XDS110 — 921600 baud — CCSDS framing
SCK-2400 GS Board (CC1352P, APID 0x010)
    ↕ 2.4GHz RF — CCSDS framing
SCK-2400 Remote Board (CC1352P, APID 0x011)
    ↕ UART — 115200 baud — ESP framing
SCK-PBL-1 Payload Board (Raspberry Pi Pico)
    ├── OV2640 Camera    (SPI0 + I2C0)
    ├── MicroSD Card     (SPI0)
    ├── GPS NEO-6M       (UART1)
    └── BMP581/MS5611    (I2C1)
```

The GS board relays CCSDS commands transparently over RF to the remote
board's APID. The remote board is a **CCSDS↔ESP bridge** — it speaks
CCSDS on the RF side and ESP framing on the payload UART side, exactly
mirroring the Pico's existing protocol from SCK-915.

**Key architectural decision:** the Pico's `main.py` requires **zero
changes** from SCK-915. The CC1352P translates CCSDS commands into ESP
frames the Pico already understands, and translates ESP responses back
into CCSDS ACKs for the Ground Station. The Pico never knows whether it's
talking to a CC1110 (SCK-915) or a CC1352P (SCK-2400).

### 1.2 Hardware Variants

| Board | Description | Status |
|-------|-------------|--------|
| LAUNCHXL-CC1352P-2 ×2 | TI LaunchPad — bringup validation platform | Firmware validated here before custom PCB spin |
| SCK-2400 Mini | CC2652P1FRGZ, SCK-915 footprint — drop-in for existing 3D-printed stands and SCK-PBL-1 | In production |
| SCK-2400 | CC2652P1FRGZ, RF sections only, PC-104 compliant | In design |
| SCK-PBL-1 Payload | Pico + camera + GPS + altimeter + SD PCB | Shared with SCK-915 |

> Both production variants share identical firmware to what's being bringup-tested
> on SCK-2400 Mini in this guide — the CC1352P and CC2652P are
> pin/peripheral-compatible within the SimpleLink family, so the CCSDS dispatch,
> OAD transport, and payload bridge code carries forward unchanged. The
> **SCK-2400 Mini variant** is a footprint-compatible drop-in replacement for
> SCK-915 — same mounting, same payload board connector — intended for
> development work and HAB missions. The **SCK-2400 variant** strips the
> board to RF sections only on a PC-104 compliant form factor, for integration
> into real CubeSat flight stacks.

### 1.3 Firmware Components

| File | Language | Runs On | Purpose |
|------|----------|---------|---------|
| `main.c` | C | CC1352P | RTOS task creation, rfTask (RF + beacon + routing) |
| `uart.c` / `uart.h` | C | CC1352P | CCSDS dispatch, payload UART bridge, OAD handlers |
| `ccsds.h` | C | CC1352P | CCSDS packet structures and command opcodes |
| `radio.c` / `radio.h` | C | CC1352P | RF driver wrapper (TX/RX, RX queue) |
| `telemetry.c` / `.h` | C | CC1352P | Telemetry collection (uptime, RX/TX counters, VCC) |
| `oad_*.c` | C | CC1352P | OAD ext-flash transport and image header |
| `main.py` | MicroPython | Pico | Payload pipeline — **unchanged from SCK-915** |

### 1.4 Repository Layout

```
sck2400_firmware/                ← CCS Theia project root
├── sck2400.syscfg               ← SysConfig — UART/SPI/RF pin assignments
├── main.c / main.h               ← RTOS tasks, rfTask
├── uart.c / uart.h                ← CCSDS dispatch + payload bridge
├── ccsds.h                       ← packet structs + command opcodes
├── radio.c / radio.h              ← RF driver wrapper
├── telemetry.c / telemetry.h      ← telemetry collection
├── oad_task.c / oad_task.h        ← OAD ext-flash session
├── oad_flash_stub.c               ← ext flash SPI (MX25R8035F)
├── oad_image_header_app.c         ← OAD image metadata (auto-patched by GS)
└── Debug/                         ← build output (sck2400_firmware.hex etc.)
```

> **Never hand-edit `oad_image_header_app.c` placeholder values.** The
> Ground Station's build pipeline patches `.prgEntry` and `.len`
> automatically after each build. See Section 6.3.

---

## 2.0 Hardware Configuration Reference

### 2.1 CCSDS Board Addressing [SCK-DEV: BOARD_ADDRESSING]

The SCK-2400 reuses OpenLST-style board addressing, but encodes the
board address in the CCSDS **APID** field instead of a separate HWID.

| APID | Role | Notes |
|------|------|-------|
| `0x010` | Ground Station board | Connects via USB to the GS app, relays RF traffic |
| `0x011` | Remote 1 | First remote/payload board |
| `0x012` | Remote 2 | Second remote board (multi-board missions) |
| `0x001` | Telemetry beacon | `CCSDS_APID_TLM_BEACON` — used for `tlm_beacon` responses |
| `0x002` | Legacy command path | `CCSDS_APID_COMMAND` — generic command APID |
| `0x003` | Command ACK | `CCSDS_APID_CMD_ACK` — all command responses |

Each board's address is set at compile time via `SCK_APID_THIS_BOARD` in
`ccsds.h`. The Ground Station's Firmware tab patches this value
automatically based on the selected **Board Role**.

**Routing logic (`uart_dispatch_ccsds_packet` in `uart.c`):**

- GS board + packet addressed to a remote APID → forward over RF via
  `rf_forward_enqueue()`, do not dispatch locally
- GS board + packet addressed to `0x010` or `CCSDS_APID_COMMAND` →
  dispatch locally
- Remote board + packet addressed to its own APID (pre-filtered by
  `rfTask`) → dispatch locally
- Any board + unrecognized APID → discard silently

### 2.2 RF Power Modes [SCK-DEV: TX_POWER]

Power mode is set at flash time via `TX_POWER` in `radio.h`, patched
automatically by the Ground Station's Firmware tab.

| Mode | `TX_POWER` | Output | Use Case |
|------|-----------|--------|----------|
| Bench | `BENCH` (0 dBm) | ~0 dBm | Indoor/bench testing — default |
| Field | `MAX` | Full CC1352P output | Field / HAB / LEO mission |

> **Critical:** There must be exactly **one** `#define TX_POWER` line in
> `radio.h`. The Ground Station's patching tool searches for this exact
> string — duplicating or relocating it breaks patching silently.

### 2.3 Payload UART — CC1352P↔Pico Bridge [SCK-DEV: PAYLOAD_UART]

The remote board bridges CCSDS (RF side) to ESP framing (Pico side) over
a dedicated UART.

**SysConfig UART2 instances (`sck2400.syscfg`):**

| Instance | List Order / Index | Peripheral | TX Pin | RX Pin | Purpose |
|----------|---------------------|-----------|--------|--------|---------|
| `PAYLOAD_UART` | **0** (must be listed first) | UART1 | DIO13 | DIO12 | GS↔CC1352P — XDS110 backchannel |
| `DEBUG_UART` | 1 | UART0 | DIO5 | DIO16 | CC1352P↔Pico payload bridge |

> **Naming is historical and confusing — read carefully.** Despite its
> name, `PAYLOAD_UART` is the **Ground Station link** (it was the
> original UART before the Pico bridge existed). `DEBUG_UART` is the one
> repurposed for the **Pico payload link**. Do not rename these in
> SysConfig — the Ground Station patching tool and `uart.h` defines
> assume these exact names and this exact list order.

**`uart.h` configuration:**

```c
#define SCK_UART_IDX          0   /* PAYLOAD_UART — GS↔CC1352P (DIO12/13) */
#define SCK_PAYLOAD_UART_IDX  1   /* DEBUG_UART   — CC1352P↔Pico (DIO5/16) */
#define SCK_UART_BAUD         921600
#define SCK_PAYLOAD_UART_BAUD 115200  /* Matches Pico main.py */
```

**LaunchPad → Pico wiring:**

```
DIO5  (header pin 10) TX  →  Pico GPIO1 (UART0 RX, physical pin 2)
DIO16 (header pin 32) RX  ←  Pico GPIO0 (UART0 TX, physical pin 1)
GND                        →  Pico GND
```

Both boards are 3.3V — no level shifter required.

**Lazy initialization is mandatory.** The payload UART is opened on
first use via `payload_uart_open()`, never at boot:

```c
static bool payload_uart_open(void)
{
    if (sPayloadUartHandle != NULL) return true;

    UART2_Params p;
    UART2_Params_init(&p);
    p.baudRate   = SCK_PAYLOAD_UART_BAUD;
    p.readMode   = UART2_Mode_NONBLOCKING;
    p.writeMode  = UART2_Mode_BLOCKING;
    sPayloadUartHandle = UART2_open(SCK_PAYLOAD_UART_IDX, &p);
    return (sPayloadUartHandle != NULL);
}
```

If the Pico is not connected, `UART2_open` on a floating RX line can
block indefinitely if attempted at boot — opening lazily on the first
payload command avoids this entirely. Also enable **"Enable Nonblocking
Mode"** on `DEBUG_UART` in SysConfig — this allocates the RX ring buffer
required for `UART2_Mode_NONBLOCKING` reads to return immediately when no
data is available.

### 2.4 BIM / OAD Boot Chain [SCK-DEV: BIM_OAD]

```
Power-on reset
    ↓
BIM (Boot Image Manager) — runs first, permanent, never updated by OAD
    ↓ scans ext flash page 0 for OAD_EFL_MAGIC + imgCpStat == NEED_COPY
    ├─ found valid image → copy ext flash → internal flash → boot it
    └─ not found / invalid → boot existing internal flash application
```

BIM is flashed once during board provisioning and is **never** touched by
application builds or OAD transfers. This is the safety net: a failed or
corrupted OAD image simply isn't copied, and the board boots whatever was
already in internal flash.

OAD writes the new application image to **external flash** (MX25R8035F,
1MB SPI flash) starting at `OAD_IMG_OFFSET = 0x001000`, then writes an
`ExtImageInfo_t` metadata header to `OAD_SLOT_OFFSET = 0x000000` with
`imgCpStat = NEED_COPY`. On next boot, BIM finds this header, copies the
image to internal flash, and the new firmware runs.

See Section 3.2 for the full OAD streaming protocol.

### 2.5 Programming / Debug Connector (J2)

The SCK-2400 Mini carries a **2×5 (10-pin), 0.05" pitch programming/debug
header (J2)**, matching the standard **TI LaunchPad XDS110 debug-out**
pinout used on LAUNCHXL-CC1352P-2 boards. It allows an external XDS110
(including a LaunchPad's onboard XDS110 in pass-through mode) to
program/debug boards that don't carry their own onboard debug probe.

> **This is not a JTAG connector**, despite two of its signal labels
> (`TCKC`, `TMSC`) being inherited from the CC1352P pin-naming in TI's
> pinout tool. Those names describe SWD-capable pins on the die, not a
> JTAG-standard header pinout. Refer to J2 as the **Programming/Debug
> connector** or **XDS110 debug header** in documentation and silkscreen
> — not "JTAG."

| Pin | Signal |
|-----|--------|
| 1 | U1-Reset_N |
| 2 | GND |
| 3 | U1-DIO_17 |
| 4 | GND |
| 5 | U1-DIO_16 |
| 6 | +3V3 |
| 7 | U1-JTAG_TCKC |
| 8 | GND |
| 9 | U1-JTAG_TMSC |
| 10 | +3V3 |

A standard 10-pin ribbon cable from any LaunchPad's debug-out header
plugs directly into J2 for external programming/debug.

---

## 3.0 Firmware Extension Reference

### 3.1 Adding New Commands [SCK-DEV: ADD_COMMAND]

Adding a new command requires changes in up to four places.

#### Step 1 — Assign a CCSDS opcode in `ccsds.h`

```c
/* Payload board commands occupy 0x20-0x29.
 * General commands occupy 0x01-0x04, OAD occupies 0x10-0x14.
 * Pick the next free opcode in the appropriate range. */
#define CMD_MY_COMMAND  0x2A
```

#### Step 2 — Add a case in `uart_dispatch_ccsds_packet()` (`uart.c`)

For a **payload board command** (forwards to the Pico), use the generic
bridge helper:

```c
case CMD_MY_COMMAND:
    handle_pico_cmd(seqCount, /* picoSub */ 0x0A,
                    dataField + 1, (uint8_t)(dataFieldLen - 1),
                    CMD_MY_COMMAND, /* respMax */ 64);
    break;
```

For a **board-local command** (no Pico involved), write a dedicated
handler following the pattern of `handle_get_telem()` or
`handle_cmd_ack()`.

#### Step 3 — Add the matching sub-opcode to Pico `main.py`

```python
CMD_MY_COMMAND = 0x0A   # Next available ESP sub-opcode

# In the main command dispatch loop:
elif sub_opcode == CMD_MY_COMMAND:
    try:
        result = do_something(payload[1:])
        msg = f"MYRESP:{result}"
        send_esp(msg.encode())
        blink(1)
    except Exception as e:
        send_esp(b"MYRESP:ERR:FAIL")
```

If the response may exceed the static buffer size used by
`handle_pico_cmd()` (220 bytes), implement chunking — see Section 3.3.

#### Step 4 — Add to the C# Ground Station

```csharp
// In CcsdsProtocol.cs — add the opcode constant
public const byte CMD_MY_COMMAND = 0x2A;

// In CustomCommand.cs Defaults() — add a pre-loaded entry
new CustomCommand { Name = "My Command", Opcode = 0x2A, Payload = "",
                     Notes = "Description — expects MYRESP:..." },
```

`SendCustomCommandAsync()` already handles SCK-2400 CCSDS framing — no
further GS changes needed for a simple request/response command.

#### Critical rules

| Rule | Consequence if violated |
|------|--------------------------|
| `handle_pico_cmd()` buffers must stay `static` | Stack overflow — `uart_task` crashes silently (see L6) |
| `SCK_TASK_STACK_UART` must be ≥2048 | Payload command call chain overflows a 512-byte stack |
| Never call `payload_uart_open()` at boot | Floating RX line can block `UART2_open` indefinitely |
| One opcode = one CCSDS sub-opcode | Do not reuse 0x20 with different payload bytes — each command needs its own CCSDS opcode (see L4) |
| `ccsds.h` opcode defines must be in sync across firmware AND GS | Mismatched/undefined opcodes compile silently with wrong values |

---

### 3.2 OAD Streaming Transport [SCK-DEV: OAD_STREAMING]

OAD (Over-Air Download) transfers a complete firmware image to a remote
board over RF in three phases.

**Performance (335KB image):**

| Mode | Time | Throughput | % of 8-min LEO pass |
|------|------|-----------|----------------------|
| Per-chunk ACK (legacy) | 168s | 2.0 KB/s | 35% |
| Streaming, 20ms delay | 43.5s | 7.7 KB/s | 9% |
| **Streaming, 10ms delay (production)** | **24.1s** | **13.9 KB/s** | **5%** |

#### Phase 1 — `CMD_OAD_START` (0x10)

```
Payload: [4B imgSize BE][2B crc16 BE]
```

Remote board:
1. Opens ext flash (`extFlashOpen()`)
2. Erases the OAD slot — `OAD_SLOT_OFFSET` through `imgSize + 4KB`,
   rounded to sector boundary
3. Initializes session state, sets `gOadActive = true`
4. **Leaves flash open** for the duration of the session — closing and
   reopening on every chunk triggers Deep Power-Down cycling at 50Hz,
   which is unreliable (see L7)

ACK payload: `[subOp][status][flashErrCode][manfId][devId]`

#### Phase 2 — `CMD_OAD_CHUNK` (0x11), streamed

```
Payload: [4B offset BE][1B chunkLen][chunkData...]
```

**Streaming mode — no per-chunk ACK.** The Ground Station sends all
chunks back-to-back at the configured inter-chunk delay (10ms production
default). The remote board writes each chunk to
`OAD_IMG_OFFSET + offset` and returns immediately — no response.

`OAD_CHUNK_SIZE = 240` bytes. **This must match the Ground Station's
chunk size exactly** — a mismatch causes every chunk to fail the
`chunkLen > OAD_CHUNK_SIZE` check silently (see L1).

**`gOadActive` and the rfTask sleep:** during OAD, `rfTask` must drain
the RX queue fast enough to keep up with incoming chunks (~100Hz at 10ms).
The RX queue holds only 4 entries.

```c
/* main.c — rfTask main loop, remote board only */
#if !SCK_IS_GS_BOARD
usleep(gOadActive ? 1000 : SCK_BEACON_REST_US);
#else
usleep(SCK_BEACON_REST_US);
#endif
```

Without this, the 950ms beacon sleep causes the RX queue to overflow and
chunks are silently dropped (see L2).

Diagnostic NACK (only sent on error):
```
[CMD_OAD_CHUNK][0x01][flashErrCode][4B offset BE]
```

#### Phase 3 — `CMD_OAD_END` (0x12)

```
Payload: [4B crc32 BE]  (legacy field — actual verification uses CRC16)
```

Remote board:
1. Verifies `bytesReceived == imgSize`
2. Reads back the full image from ext flash and computes CRC16
3. If CRC matches, writes `ExtImageInfo_t` metadata to `EFL_ADDR_META`
   with `imgCpStat = NEED_COPY`
4. Closes ext flash, sends ACK `[CMD_OAD_END][status]`
5. `Task_sleep(200)` then `SysCtrlSystemReset()`

The 200ms delay lets the ACK transmit before reset. The Ground Station's
`oad_end` wait timeout is `8000 + (imgSize/1024) * 50` ms — for 335KB
this is ~24.4s. If no ACK arrives within this window (the board may have
already reset), the Ground Station treats this as success and waits for
the board to reboot and resume beaconing.

#### `CMD_OAD_ABORT` (0x13) and `CMD_OAD_STATUS` (0x14)

Abort clears session state and closes ext flash with no further action.
Status returns `[1B active][4B bytesReceived][4B imgSize]` for progress
monitoring.

---

### 3.3 Chunking Large Payload Responses [SCK-DEV: CHUNKING]

The CCSDS↔ESP bridge (`handle_pico_cmd`) uses static 220/222-byte
buffers. Responses from the Pico larger than this — e.g. `LIST:` with
many files — are truncated at the buffer boundary in the current
implementation.

For responses that must exceed this limit, follow the SCK-915 chunking
pattern in `main.py` (unchanged):

```python
# "LIST:" prefix = first chunk, "LIST+:" = continuation
# Final chunk has NO trailing comma — signals end of list
CHUNK = MAX_PAYLOAD - 8
# ... build and send_esp() each chunk ...
```

**Ground Station reassembly for SCK-2400 chunked responses is not yet
implemented** — this is a known gap for the Files tab when listing SD
cards with many files. See Section 7, item pending.

---

### 3.4 ESP Framing on the Payload UART [SCK-DEV: ESP_FRAMING]

Identical to SCK-915 — the Pico's `send_esp()` / `recv_esp()` are
unchanged.

```
[0x22][0x69][length][payload bytes...]
  ↑      ↑      ↑         ↑
Start  Start  1 byte   1-251 bytes
byte0  byte1  payload  command data
              length
```

**CC1352P-side implementation (`pico_send_recv` in `uart.c`):**

```c
#define PICO_ESP_BYTE0   0x22
#define PICO_ESP_BYTE1   0x69
#define PICO_TIMEOUT_MS  5000

/* Send: [0x22][0x69][1+argsLen][subOpcode][args...] */
/* Receive: poll UART2_read() non-blocking, 1ms sleep between polls,
 * state machine: sync0 → sync1 → length → payload */
```

> **Critical:** unlike the SCK-915 CC1110 implementation (interrupt-driven
> UART), the CC1352P implementation here uses a **polling loop** with
> `usleep(1000)` between non-blocking read attempts. This works correctly
> but ties up `uart_task` for up to `PICO_TIMEOUT_MS` (5 seconds) per
> payload command. Other CCSDS commands queued during this window will
> wait. This is acceptable for the current command set but should be
> revisited if low-latency commands need to interleave with payload
> commands.

---

### 3.5 Pico Response String Conventions [SCK-DEV: RESPONSE_FORMAT]

Unchanged from SCK-915 — `PREFIX:field1,field2,...`. The CCSDS↔ESP bridge
maps each CCSDS opcode to a Pico ESP sub-opcode by the pattern
**CCSDS `0x2N` → ESP `0x0N`**:

| CCSDS Opcode | Pico ESP Sub-opcode | Success Response |
|--------------|----------------------|-------------------|
| `CMD_PICO_PING` (0x20) | 0x00 | `PICO:ACK` |
| `CMD_PICO_TEMP` (0x21) | 0x01 | `TEMP:23.45C` |
| `CMD_PICO_SNAP` (0x22) | 0x02 | `SNAP:OK:snap_001.jpg:24576` |
| `CMD_PICO_LIST` (0x23) | 0x03 | `LIST:file1.jpg,file2.jpg` |
| `CMD_PICO_INFO` (0x24) | 0x04 | `INFO:snap_001.jpg:24576:123` |
| `CMD_PICO_CHUNK` (0x25) | 0x05 | `CHUNK:0:<200 bytes>` |
| `CMD_PICO_DELETE` (0x26) | 0x06 | `DEL:OK:snap_001.jpg` |
| `CMD_GET_GPS` (0x27) | 0x07 | `GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c` |
| `CMD_GET_BARO` (0x28) | 0x08 | `BARO:hpa,baro_alt,temp_c` |
| `CMD_PICO_BEACON` (0x29) | 0x09 | `BEACON:ON` / `BEACON:OFF` |

All responses are wrapped in a CCSDS `cmd_ack` (APID `0x003`) by
`handle_pico_cmd()`: `[ccsdsCmd][status][response bytes...]`.

---

### 3.6 Autonomous GPS Beacon [SCK-DEV: BEACON]

Unchanged from SCK-915 — the Pico transmits a fused GPS+baro packet every
10 seconds (`GPS_BEACON_INTERVAL_MS = 10000`) when enabled and idle.

**Controlling the beacon from SCK-2400:**

```
CMD_PICO_BEACON (0x29) + payload 0x01 → ESP 0x09 + 0x01 → enable
CMD_PICO_BEACON (0x29) + payload 0x00 → ESP 0x09 + 0x00 → disable
```

> **Race condition:** because the bridge UART (Section 3.4) polls with a
> 5-second timeout, an autonomous beacon transmitted by the Pico during
> this window can be captured by `pico_send_recv()` instead of the
> intended command response — the Ground Station will then display the
> beacon's `GPS:...` string instead of the expected response (e.g.
> `TEMP:...`). **Always send `CMD_PICO_BEACON OFF` before running command
> sequences**, and `CMD_PICO_BEACON ON` afterward if continuous beaconing
> is desired.

Every beacon packet is also written to the SD flight log — see 3.7.

---

### 3.7 SD Card Flight Log Format [SCK-DEV: SD_FLIGHT_LOG]

Unchanged from SCK-915. One `.sckflight` JSON file per power cycle,
`FLT-001.sckflight`, `FLT-002.sckflight`, etc. See the
[SCK-915 Developer Guide Section 3.7](https://spacecommskit.com/docs)
for the full format and field-addition instructions — `write_flight_packet()`
in `main.py` is identical.

---

### 3.8 Watchdog Timer — LEO Fault Recovery [SCK-DEV: WATCHDOG]

The CC1352P hardware watchdog resets the board if any task deadlocks or
spins without yielding. This is the primary fault recovery mechanism for
LEO missions where hands-on recovery is impossible.

**Architecture:**

```
rfTask       (priority 2) ─┐
uartTask     (priority 2) ─┤── if any spin → watchdogTask starved
watchdogTask (priority 1) ─┘   → WDT expires → CC1352P reset (~65s)
```

`watchdogTask` runs at the lowest RTOS priority (1). Starvation IS the
detection mechanism — no explicit hang detection is required.

**SysConfig (`sck2400.syscfg`):** Add `CONFIG_WATCHDOG_0` under
TI Drivers → Watchdog. Period: `30000` ms. Peripheral: `Any(WDT0)`.

**Implementation (`main.c`):**

```c
// In mainThread() after SPI_init():
Watchdog_init();

void watchdogTask(UArg a0, UArg a1)
{
    Watchdog_Params p;
    Watchdog_Params_init(&p);
    p.resetMode     = Watchdog_RESET_ON;
    p.debugStallMode = Watchdog_DEBUG_STALL_ON;
    Watchdog_Handle h = Watchdog_open(CONFIG_WATCHDOG_0, &p);

    // CRITICAL: CC1352P watchdog stops in standby without this.
    Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY);

    while (1)
    {
        Watchdog_clear(h);   // kick every 10s within 30s window
        Task_sleep(10000);    // yields — starvation is the detection
    }
}
```

**CC1352P two-timeout reset:** The CC1352P fires an NMI on the first
timeout and only resets on the second timeout with the flag still
pending. Total time from hang to reset is **~65 seconds** at a 30-second
period — factor this into test timing.

**Debug stall:** With `Watchdog_DEBUG_STALL_ON`, any JTAG connection
(including a background CCS connection) pauses the watchdog hardware.
Disconnect JTAG after flashing for watchdog testing.

**Verified:** uart_task deliberate spin → LED went dark (watchdogTask
starved) → board reset at ~65s → post-reset `get_telem` nominal. ✓

**Configuration summary:**

| Parameter | Value |
|-----------|-------|
| Timeout | 30,000 ms (30 seconds) |
| Kick interval | 10,000 ms via `Task_sleep(10000)` |
| Reset mode | `Watchdog_RESET_ON` |
| Debug stall | `Watchdog_DEBUG_STALL_ON` |
| Power constraint | `PowerCC26XX_DISALLOW_STANDBY` |
| Task priority | 1 (lowest) |
| Task stack | 512 bytes |

---

### 3.9 RF Encryption (AES-128-CCM) [SCK-DEV: CRYPTO]

All RF communication uses AES-128-CCM per-packet encryption and
authentication. Implementation is in `crypto.c` — pure C, RFC 3610,
no TI hardware accelerator.

**Why software AES:** The TI AESCCM hardware driver cannot be safely
called from rfTask context (semaphore poisoning) and produces different
keystream than .NET `AesCcm`. Software AES is task-context safe and
matches .NET exactly.

**Wire format:**
```
[CCSDS Header 6B][Nonce 13B][Encrypted Payload N bytes][MAC 8B]
```

**CCM parameters:**

| Parameter | Value |
|-----------|-------|
| Algorithm | AES-128-CCM (RFC 3610) |
| Key length | 16 bytes |
| Nonce length | 13 bytes (from CCSDS seq counter, zero-padded) |
| MAC length | 8 bytes |
| Keys per pair | 3 (Option A fallback) |
| Overhead | 21 bytes per packet |

**Key provisioning:** Three keys per board pair. Generated in the Ground
Station Provision tab (🎲 Roll). Automatically patched into `security.h`
before each build. `security.h` is gitignored — never committed.

**Self-test on every boot:** `crypto_init()` verifies a known test vector.
6 fast LED blinks if it fails. The board continues but crypto is broken.

**Known test vector:**
```
Key: 0xFF×16  Nonce: 0x00×11+0xC0+0x07  AAD: 18 11 C0 07 00 15
Plaintext: 0x01  →  Ciphertext: 0x83  MAC: 85 84 6C 8D 8C 2A 0B 19
```

**Task context:** `crypto_decrypt()` and `crypto_encrypt()` are safe to
call from rfTask — pure C, no hardware dependencies. All decrypt →
dispatch → encrypt happens inline in rfTask with no cross-task handoff.

---

### 3.10 Non-Volatile Storage — Reset Counter and Safe Mode [SCK-DEV: NV_STORAGE]

Non-volatile reset counter in internal flash at `0x52000` (8KB, pages
82-83). Five consecutive non-power-on resets without a 5-minute clean
run triggers safe mode (beacon only).

**Flash layout:**
```
0x00000–0x03FFF  BIM (pages 0-3)
0x04000–0x51FFF  Application (0x4E000 bytes)
0x52000–0x53FFF  NVS region (pages 82-83, 8KB)  ← reset counter lives here
0x54000–0x55FFF  Reserved
0x56000–0x57FFF  CCFG
```

**Why direct flash (not TI NVS driver):** `NVS_write()` returns success
and `NVS_WRITE_POST_VERIFY` passes (reading from write buffer), but data
does not survive reset. Fix: `FlashSectorErase()` + `FlashProgram()` +
`FlashCheckFsmForReady()` polling + `Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY)`
+ VIMS cache disable around all flash operations.

**Deferred write:** `nv_storage_on_boot()` is called from `watchdogTask`
after a 10-second `Task_sleep()`. Use `Task_sleep()` not `usleep()` —
`usleep()` busy-spins on TI-RTOS7 and causes the clean-run timer to fire
immediately, resetting the counter to 0.

**Reset cause gating:** NVS operations run on `SYSRESET`, `WARMRESET`,
and `PIN`. Power-on is excluded to avoid NVS hangs on cold start. On the
LaunchPad, `CMD_REBOOT` asserts `RSTSRC_PIN_RESET` (not SYSRESET).

**Bench caveat:** XDS110 debugger erases NVS on every software reset.
Test NVS persistence with real power cycles (unplug/replug) on the bench.

---

## 4.0 Payload Board Hardware Reference

The SCK-PBL-1 payload board (Pico + camera + GPS + altimeter + SD) is
shared between SCK-915 and SCK-2400 without modification. See the
SCK-915 Developer Guide Section 4 for the full GPIO assignment table and
expansion header reference.

**SCK-2400-specific addition:** the Pico's UART0 (GPIO0/GPIO1), previously
wired to the SCK-915 CC1110, connects identically to the SCK-2400
CC1352P's `DEBUG_UART` (DIO5/DIO16) — same baud rate (115200), same ESP
framing, same physical pins on the Pico side.

---

## 5.0 C# Ground Station Extension Reference

### 5.1 Key Methods

| Method | Description |
|--------|-------------|
| `ActiveHwid` | Property: current APID from header bar |
| `IncCcsdsSeqCount()` | Increment and return next CCSDS sequence number |
| `FlushRxQueue()` | Discard queued received packets before sending |
| `WritePacket(byte[])` | Send raw bytes to serial port |
| `WaitForReply(apid, seq, ms)` | Async: wait for matching CCSDS ACK |
| `CcsdsProtocol.BuildCommand(seq, opcode, args, destApid)` | Build a CCSDS command packet |
| `CcsdsProtocol.BuildSimpleCommand(seq, opcode, destApid)` | Build a CCSDS command with no payload |
| `Log(message, color)` | Append line to main log panel |
| `LogTx(message, hwid)` | Log transmitted packet in yellow TX format |

### 5.2 Adding a Custom Command (C# Template)

```csharp
// In SendCustomCommandAsync(), SCK-2400 branch already handles this —
// just add the opcode to CcsdsProtocol.cs and CustomCommand.cs Defaults().

// CcsdsProtocol.cs
public const byte CMD_MY_COMMAND = 0x2A;

// CustomCommand.cs
new CustomCommand {
    Name = "My Command", Opcode = 0x2A, Payload = "",
    Notes = "Description — expects MYRESP:..."
},
```

For commands requiring custom response handling (e.g. routing to the map
or files UI), add a branch in the response-handling section of
`SendCustomCommandAsync()` similar to the existing `GPS:` prefix check:

```csharp
if (pkt.PicoPayload.StartsWith("GPS:"))
    HandleGpsPacket(pkt.PicoPayload);
```

### 5.3 Opcode Reference

| Opcode | Name | Description |
|--------|------|-------------|
| 0x01 | `CMD_GET_TELEM` | Request telemetry — response via `tlm_beacon` (APID 0x001) |
| 0x02 | `CMD_REBOOT` | Reboot the addressed board |
| 0x03 | `CMD_GET_CALLSIGN` | Request callsign string |
| 0x04 | `CMD_BEACON_CTRL` | Enable/disable the board's own RF beacon |
| 0x05 | `CMD_CLEAR_SAFE_MODE` | Reset NVS reset counter and exit safe mode |
| 0x06 | `CMD_GET_TIME` | Request board time |
| 0x10 | `CMD_OAD_START` | Begin OAD session |
| 0x11 | `CMD_OAD_CHUNK` | Stream one firmware chunk |
| 0x12 | `CMD_OAD_END` | Finalize, verify CRC, reboot |
| 0x13 | `CMD_OAD_ABORT` | Cancel OAD session |
| 0x14 | `CMD_OAD_STATUS` | Query OAD progress |
| 0x20 | `CMD_PICO_PING` | Payload bridge — Pico ping |
| 0x21 | `CMD_PICO_TEMP` | Payload bridge — temperature |
| 0x22 | `CMD_PICO_SNAP` | Payload bridge — camera snapshot |
| 0x23 | `CMD_PICO_LIST` | Payload bridge — list SD files |
| 0x27 | `CMD_GET_GPS` | GPS+baro fused — response in `cmd_ack` payload as `GPS:lat,lon,alt,sats,fix,hpa,baro_alt,temp_c` |
| 0x28 | `CMD_GET_BARO` | Barometric only — response as `BARO:hpa,baro_alt,temp_c` |
| 0x29 | `CMD_PICO_BEACON` | Payload beacon on/off |
| `CCSDS_APID_TLM_BEACON` (0x001) | — | Telemetry beacon responses |
| `CCSDS_APID_CMD_ACK` (0x003) | — | All command acknowledgements (including GPS/baro strings) |

---

## 6.0 Build and Flash Reference

### 6.1 Building Firmware (CCS Theia)

The Ground Station's Firmware tab drives a headless CCS build:

1. Set **Project Dir** to the `sck2400_firmware` folder
2. Set **Board Role** — patches `SCK_APID_THIS_BOARD` in `ccsds.h`
3. Set **RF Power Mode** — patches `TX_POWER` in `radio.h`
4. Click **Clean + Build**

This runs `gmake clean` then `gmake all`, generating SysConfig output,
compiling all sources, and linking `sck2400_firmware.out` /
`.hex` / `.map`.

> **Always Clean + Build** after modifying `ccsds.h`, `uart.h`, or
> `sck2400.syscfg`. Incremental builds can link against stale generated
> headers.

### 6.2 OAD Image Header Patching

After a successful build, the Ground Station automatically:

1. Reads `oad_entry_vec` address from the `.map` file
2. Computes `imgLen = end - start` from the linked sections
3. Patches `.prgEntry` and `.len` in `oad_image_header_app.c`
4. Triggers an incremental rebuild to bake the patched values into
   `.out` / `.hex`

```
OAD patch: oad_entry_vec at 0x0000D438 (summary table)
OAD patch: computed image len = 0x000096D4 (end=0x0000D6D4)
OAD patch: .prgEntry updated to 0x0000D438
OAD patch: .len updated to 0x000096D4
```

If the patch step reports **"already X or pattern not found"** for both
fields, the existing values already happen to match — this is benign. If
only one field reports this, investigate — it usually indicates a stale
`oad_image_header_app.c` from a different build.

### 6.3 Flashing

The **Flash** button merges the application hex with BIM
(`sck2400_merged.hex`) and programs the board via `srfprog` over the
XDS110 2-pin cJTAG interface:

```
srfprog -t soc(XDS-<serial>, CC1352P) -e all -p all -v rb -f sck2400_merged.hex
```

> **Verify the HEX File path matches your Project Dir.** If you change
> the Project Dir to a non-default location, confirm the HEX File field
> updates to match — it does not always update automatically (known GS
> issue, see Section 7).

### 6.4 Flashing Pico Firmware

```bash
mpremote cp main.py :main.py
mpremote cp sdcard.py :sdcard.py
```

Or hold BOOTSEL on power-up and drag-and-drop to the USB mass storage
drive.

---

## 7.0 Hard-Won Lessons

These lessons were accumulated during SCK-2400 bringup, building on the
SCK-915 lessons (which still apply where the payload board code is
shared). Each one cost real debugging time.

| # | Lesson | Impact |
|---|--------|--------|
| L1 | `OAD_CHUNK_SIZE` in firmware must exactly match the Ground Station's chunk size — a mismatch causes every chunk to fail silently with no useful diagnostic | Critical |
| L2 | During OAD, `rfTask` must sleep only 1ms (not 950ms) to drain the RX queue fast enough for streaming chunks — gated by `gOadActive` | Critical |
| L3 | Closing/reopening ext flash per chunk triggers Deep Power-Down cycling at streaming rates — hold flash open for the whole OAD session | High |
| L4 | Each payload command needs its own CCSDS opcode (0x20-0x29) — reusing one opcode with different payload bytes (the SCK-915 `PICO_MSG` pattern) does not work over CCSDS; the C# `SendCustomCommandAsync` dispatches on opcode alone | Critical |
| L5 | `ccsds.h` opcode `#define`s must be identical and present in BOTH the firmware project and the values the C# `CcsdsProtocol.cs` expects — an undefined opcode compiles silently as an unexpected value, causing dispatch to the wrong handler | Critical |
| L6 | `uart_task` stack must be ≥2048 bytes once payload handlers are added — `handle_pico_cmd`'s static buffers plus the dispatch call chain overflow the original 512-byte stack, causing a silent crash with no error output | Critical |
| L7 | Any large (`>~100 byte`) buffer in `uart_task` or its call chain must be `static`, not stack-local — stack overflows in TI-RTOS fail silently | Critical |
| L8 | Never call `UART2_open()` for the payload UART at boot — a floating RX line (Pico not connected) can block indefinitely. Open lazily on first command | High |
| L9 | `UART2_Mode_NONBLOCKING` requires "Enable Nonblocking Mode" checked in SysConfig for that UART instance — otherwise non-blocking reads behave as blocking | High |
| L10 | SysConfig UART2 **list order determines index number** — the first-listed instance is index 0. Reordering instances (even without changing pins) silently swaps which UART your `SCK_UART_IDX` points to | Critical |
| L11 | On this hardware, `PAYLOAD_UART` (despite its name) is the Ground Station link on DIO12/13 — `DEBUG_UART` on DIO5/16 is the one available for the Pico bridge. Do not "fix" this naming by changing pins — it breaks the GS link | Critical |
| L12 | The Pico's autonomous 10-second beacon races with payload command responses on the bridge UART — send `CMD_PICO_BEACON OFF` before command sequences | Medium |
| L13 | `oad_end` reboot needs a short delay (200ms) before `SysCtrlSystemReset()` so the ACK transmits — without it, the GS sees a clean timeout and must rely on beacon resumption to detect success | Medium |
| L14 | The Ground Station's HEX File path does not always follow Project Dir changes — verify before flashing to a custom build location | Medium |
| L15 | BIM is permanent and never updated by application builds or OAD — a corrupted OAD image is simply never copied, and the board boots the last good internal-flash image | High (safety property) |
| L16 | Always Clean + Build after modifying `ccsds.h`, `uart.h`, or `.syscfg` — incremental builds can silently link stale generated headers | High |
| L17 | CC1352P watchdog clock stops when the device enters standby — `Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY)` is mandatory or the timer never fires on a real hang | Critical |
| L18 | CC1352P watchdog does not reset on the first timeout — it fires an NMI first; reset only occurs if the flag is still pending at the second timeout. Total time from hang to reset is ~65s at a 30s period | High |
| L19 | `Watchdog_open()` returns NULL if `Watchdog_init()` was not called first in `mainThread()` — same init pattern required as `SPI_init()` | High |
| L20 | With CCS JTAG connected (even background connection without active debug session), `Watchdog_DEBUG_STALL_ON` pauses the watchdog hardware — the timer never fires. Disconnect JTAG after flash for watchdog testing | High |
| L21 | `reloadValue` passed to the TI Watchdog driver is in milliseconds — the driver calls `convertMsToTicks()` internally. 30000 = 30 seconds | High |
| L22 | TI AESCCM hardware driver produces different keystream than .NET `AesCcm` for identical inputs — use software AES (RFC 3610 pure C). The hardware driver also causes semaphore poisoning from rfTask context | Critical |
| L23 | AES ShiftRows operates on rows of the column-major state: row 3 indices [3,7,11,15] shift left 3 → `b[3],b[7],b[11],b[15] = b[15],b[3],b[7],b[11]`. Wrong ShiftRows produces incorrect keystream that passes basic tests but mismatches .NET | Critical |
| L24 | Moving crypto to uart_task required `UART2_Mode_NONBLOCKING`, which allocates a ring buffer from the BIOS heap at `UART2_open()` time, exhausting the heap and causing `RF_open()` to return NULL (solid LED at boot). Software AES in rfTask eliminates all cross-task crypto issues | Critical |
| L25 | `SysCtrlSystemReset()` (CMD_REBOOT) asserts `RSTSRC_PIN_RESET` on the LaunchPad XDS110 — not `RSTSRC_SYSRESET`. The NVS gate must include `SCK_RESET_CAUSE_PIN` or CMD_REBOOT never increments the reset counter | High |
| L26 | `usleep()` busy-spins on TI-RTOS7 — in watchdogTask, `usleep(10000000)` completes in microseconds, causing `cleanRunAccumMs` to hit 300000ms almost instantly and fire `nv_storage_on_clean_run()` right after `nv_storage_on_boot()`. Use `Task_sleep(10000)` | Critical |
| L27 | TI `NVS_write()` returns success and `NVS_WRITE_POST_VERIFY` passes (reading from write buffer), but data does not survive reset. Use direct `FlashSectorErase()` + `FlashProgram()` + `FlashCheckFsmForReady()` + `Power_setConstraint` + VIMS cache disable | Critical |
| L28 | NVS `regionSize` must be a multiple of 8192 bytes — any other size causes the linker to place `flashBuf0` at 0x0000 (BIM flash) via a synthetic `$BOUND$0x0` region. Every write then corrupts BIM | Critical |
| L29 | NVS `regionBase` in SysConfig must match `NVS_BASE` in the linker command file. The OAD image length filter in the GS app must exclude the NVS section (`origin < 0x52000`) — otherwise BIM erases NVS on every OAD boot | Critical |
| L30 | XDS110 debugger erases NVS flash on every software reset (CMD_REBOOT) on the bench. Test NVS persistence with real power cycles — unplug/replug USB | High |

---

## 8.0 Ground Station — SCK-2400 Integration Notes

### 8.1 Build-Time Patching

Every SCK-2400 build automatically patches four files before compiling:

| File | What's patched |
|------|----------------|
| `ccsds.h` | `SCK_APID_THIS_BOARD` — board APID |
| `radio.h` | `TX_POWER` — bench (0 dBm) or max (+20 dBm) |
| `security.h` | `SCK_AES_KEY_0/1/2` — 3 × 16-byte AES keys from Provision tab |
| `oad_image_header_app.c` | `.prgEntry` and `.len` — OAD image length, capped at `origin < 0x52000` to exclude NVS |

### 8.2 Encryption in C#

C# sends and receives plaintext CCSDS over USB. All encryption lives in
firmware. `CcsdsProtocol.cs` has no AES dependency. Keys never exist in
the C# process — they are patched into `security.h` at build time only.

### 8.3 GPS Response Routing

`CMD_GET_GPS` (0x27) returns its GPS string in a `cmd_ack` (APID 0x003)
payload. The payload begins with the opcode byte (`0x27` = ASCII `'`)
followed by `GPS:lat,lon,...`. Use `IndexOf("GPS:")` not `StartsWith` to
locate the string. The GPS/Map tab polls automatically every 10 seconds.

### 8.4 Board-Aware UI

Register board-specific controls in `_sck2400OnlyControls` or
`_sck915OnlyControls` during `Build*()` methods. `ApplyBoardUi()` greys
them out and sets tooltips ("SCK-2400 only" / "SCK-915 only").
Called on startup and on board dropdown change.

### 8.5 RF Crypto Status Indicator

`lblRfStatus` on the Home tab shows:
```
GS: 0x010  RF → 0x011 encrypted
```
Updated by `UpdateRfStatusLabel()` on connect, disconnect, and board
dropdown change. Primary indicator for diagnosing the most common setup
mistake — having the wrong board physically connected.

---

*SpaceCommsKit SCK-2400 Developer Guide v1.10*
*For updates and latest version see https://spacecommskit.com/docs*
