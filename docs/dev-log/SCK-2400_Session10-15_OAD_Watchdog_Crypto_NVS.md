# SCK-2400 Mini Log — OAD Streaming & CCSDS↔ESP Payload Bridge

**Sessions 10–15 — June–July 2026**

> This is the bringup narrative behind the
> [Hard-Won Lessons table](../SCK-2400_Developer_Guide.md#70-hard-won-lessons)
> in the SCK-2400 Developer Guide. Sessions 10–11 cover OAD and the
> payload bridge. Sessions 12–15 cover the watchdog timer, AES-128-CCM
> RF encryption, and non-volatile safe mode — added as follow-on sections
> below. If you're hitting one of these same walls, the play-by-play
> may save you the hours it cost us.

---

## Session 10: OAD Phase 3 — Streaming Transport

Going into this session, OAD (Over-Air Download) could already transfer a
firmware image with per-chunk acknowledgement — but at **168 seconds for
a 335KB image (2.0 KB/s)**, that's 35% of an 8-minute LEO pass spent just
on the firmware update. We needed it faster.

### The chunk size mismatch

The first attempt at streaming (no per-chunk ACK, just fire chunks at a
fixed delay) failed completely — every single chunk came back NACK'd.
After a lot of fruitless poking at the RF layer, the actual bug was
embarrassingly simple: the firmware's `OAD_CHUNK_SIZE` was `128`, but the
Ground Station was sending **240-byte chunks**. Every chunk failed the
`chunkLen > OAD_CHUNK_SIZE` bounds check before it even got close to being
written to flash.

Fix: change `OAD_CHUNK_SIZE` to `240` to match the Ground Station. One
constant, hours of debugging.

### The RX queue starvation

With chunk size fixed, streaming worked — for the first handful of
chunks. Then it would stall. The RX queue on the remote board holds only
4 entries, and `rfTask`'s main loop was sleeping **950ms** between beacon
checks. At a 10-20ms inter-chunk delay, that's 50-100 chunks arriving
during a single sleep — the queue overflows and chunks silently vanish.

Fix: a `volatile bool gOadActive` flag, set when `CMD_OAD_START` is
received and cleared on `CMD_OAD_END` / `CMD_OAD_ABORT`. When active,
`rfTask` sleeps only 1ms instead of 950ms, draining the queue fast enough
to keep up.

### The flash power-cycling problem

Even with the queue fixed, throughput was inconsistent. The OAD chunk
handler was calling `extFlashOpen()` / `extFlashClose()` on every single
chunk — and `extFlashClose()` puts the MX25R8035F into Deep Power-Down
mode. At streaming rates (50-100 chunks/sec), the flash chip was cycling
in and out of Deep Power-Down 50 times a second. It mostly worked, but
not reliably.

Fix: open ext flash once in `CMD_OAD_START`, leave it open for the entire
session, close it only in `CMD_OAD_END` / `CMD_OAD_ABORT`.

### The result

With all three fixes in place:

| Mode | Time (335KB) | Throughput | % of 8-min pass |
|------|--------------|-----------|------------------|
| Per-chunk ACK (before) | 168s | 2.0 KB/s | 35% |
| Streaming, 20ms delay | 43.5s | 7.7 KB/s | 9% |
| **Streaming, 10ms delay** | **24.1s** | **13.9 KB/s** | **5%** |
| Streaming, 10ms, **encrypted** | **23.0s** | **13.9 KB/s** | **5%** |

10ms became the production default in the Ground Station. Later, when
AES-128-CCM encryption was added (Sessions 14-15), OAD was retested over
the encrypted RF link — 1,332 chunks, 23.0 seconds, 13.9 KB/s, zero
NACKs. The crypto overhead in rfTask adds no measurable latency to chunk
processing.

---

## Session 11: The CCSDS↔ESP Payload Bridge

The goal: let the CC1352P remote board transparently bridge CCSDS
commands (RF side) to ESP-framed commands (Pico side) — so the existing
SCK-915 `main.py` works on SCK-2400 **completely unchanged**.

This session took considerably longer than expected, and almost every
hour of it was self-inflicted by a SysConfig surprise we didn't know
about going in.

### Round 1: the board goes completely dead

After adding the payload UART code and a new SysConfig UART2 instance,
we did a clean build, flashed, and... nothing. `get_telem` stopped
responding entirely. Not "times out occasionally" — completely silent,
every time, on both USB and RF.

We assumed the new UART code was crashing the board and spent a long time
trying to isolate it — stubbing out functions, reverting `uart.c` to a
backup, reverting `uart.h`. Nothing helped. Even a full restore of the
backed-up project (source files, SysConfig, the works) still didn't
respond.

### The actual problem: SysConfig, not code

The real issue had nothing to do with our new code. While adding the
Pico UART, we had reassigned pins on an *existing* SysConfig UART2
instance called `PAYLOAD_UART` — and despite the name, **`PAYLOAD_UART`
was actually the Ground Station link** (DIO27/22, the XDS110
backchannel), not a payload interface at all. We'd broken the one UART
the Ground Station app talks over.

Worse: at one point during recovery we deleted `PAYLOAD_UART` from
SysConfig entirely, then re-added it — but listed it **second** instead
of first. It turns out **SysConfig UART2 list order determines the
driver index number** (`PAYLOAD_UART` listed first = index 0). Our
firmware's `SCK_UART_IDX = 0` now pointed at the wrong UART.

The fix, once we understood it: restore `PAYLOAD_UART` with its original
pins (DIO27/22) and make sure it's listed **first** in the UART2 instance
list, with `DEBUG_UART` (DIO5/16) second. That's it — the GS link came
back instantly.

**Lesson for anyone reusing this design:** the names `PAYLOAD_UART` and
`DEBUG_UART` are historical and backwards from what you'd guess.
`PAYLOAD_UART` = Ground Station link. `DEBUG_UART` = the one that's
actually free for a payload board. Don't "fix" the naming by moving
pins — just use them as-is.

### Round 2: the board crashes on the very first ping

With the GS link restored, we wired up the Pico (DIO5/16 → Pico
GPIO0/1) and sent `PICO Ping`. The board went silent again — but this
time `get_telem` also stopped responding *after* the ping, and only
recovered after several retries. Something in the new code was crashing
the RTOS task.

To isolate it, we replaced `pico_send_recv()` with a pure stub that did
nothing but `memcpy` a hardcoded `"PICO:ACK"` into the response buffer —
zero UART calls, zero blocking. **It still crashed.**

That ruled out the UART code entirely. The crash had to be in
`handle_pico_cmd()` itself, or the dispatch path leading to it. Looking
at `uart_task`'s stack allocation: `SCK_TASK_STACK_UART` was **512
bytes**, and `uart_task` declared `uint8_t payload[256]` as a *stack*
local — that's half the stack gone before a single function call. Add
the new dispatch chain (`uart_dispatch_ccsds_packet` →
`handle_pico_cmd` → static 220+222 byte buffers → `uart_send_ccsds`) and
the stack overflowed silently, corrupting RTOS task state.

Fix: bump `SCK_TASK_STACK_UART` to 2048, and make the `payload[256]`
array `static` so it lives in `.bss` instead of on the stack.

### Round 3: every command says PICO:ACK

With the stack fixed, the real `pico_send_recv()` went back in — and the
board stopped crashing! But every custom command, regardless of which
button we pressed, returned `PICO:ACK`.

Two separate bugs stacked here:

1. **The Ground Station was sending OpenLST packets, not CCSDS**, for
   *all* custom commands — `SendCustomCommandAsync()` had no SCK-2400
   branch, so it used the SCK-915 `OpenLstProtocol.BuildPacket()` path
   regardless of board type. The CC1352P firmware never even saw these
   packets as CCSDS.

2. Once that was fixed and packets started arriving as CCSDS, **every
   command still mapped to `CMD_PICO_PING`** — because the project's
   `ccsds.h` didn't define `CMD_PICO_TEMP`, `CMD_GET_GPS`, etc. at all.
   The dispatch `switch` statement's `case CMD_PICO_TEMP:` was comparing
   against an *undefined symbol*, which the compiler happily treated as
   `0` — colliding with `CMD_PICO_PING = 0x20`.

Fix: add the SCK-2400 CCSDS branch to `SendCustomCommandAsync()` using
`CcsdsProtocol.BuildCommand()`, and copy the complete `0x20-0x29` opcode
block into the project's `ccsds.h`.

### The payoff

Once both fixes landed, every payload command worked first try:

```
PICO Ping       → PICO:ACK
PICO Read Temp  → TEMP:12.06C
PICO List Files → LIST:FLT-001.sckflight,...,FLT-011.sckflight
PICO Get GPS    → GPS:36.058844,-87.384020,245.9,9,1,987.80,214.1,20.91
PICO Get Baro   → BARO:987.81,214.0,20.97
PICO Beacon ON/OFF → BEACON:ON / BEACON:OFF
```

Round trip over USB: **~28ms**.

### One more wrinkle: the autonomous beacon race

The Pico transmits its own GPS+baro beacon every 10 seconds, unchanged
from SCK-915. Occasionally a command response would come back containing
*part of a beacon packet* mixed in with the expected response — e.g.
`PICO Get GPS` returning `TEMP:12.06C` instead of GPS data.

This is a straightforward race: the bridge's `pico_send_recv()` reads
whatever ESP frame arrives next on the UART, and if the Pico's beacon
timer fires during that window, its frame wins. The fix is operational,
not code: send `PICO Beacon OFF` (CCSDS `0x29` / payload `0x00`) before
running a sequence of payload commands, and `PICO Beacon ON` afterward if
continuous telemetry is wanted.

### RF end-to-end (pre-encryption)

With the bridge working over USB, we moved the remote board + Pico across
the room and routed everything through the Ground Station board over
2.4GHz RF. This was before AES-128-CCM encryption was added — the
command path was authenticated by CCSDS sequence counter only:

```
PICO Get GPS    → GPS:36.058750,-87.384020,268.8,6,1,987.87,213.5,20.39
PICO Beacon OFF → BEACON:OFF
PICO Get Baro   → BARO:987.88,213.4,20.39
PICO Get GPS    → GPS:36.058750,-87.384020,265.6,6,1,987.87,213.5,20.39
PICO Read Temp  → TEMP:14.87C
```

Round trip over RF: **~2 seconds**. Zero errors.

```
GS (C#) → CCSDS → 0x010 → 2.4GHz RF → 0x011 → ESP/115200 → Pico
Pico → ESP → 0x011 → 2.4GHz RF → 0x010 → CCSDS → GS (C#)
```

After encryption was added (see Session 14 section below), the RF hop
became AES-128-CCM encrypted end-to-end. The payload bridge and Pico
never see the encryption layer — it is entirely transparent to them.

The SCK-915 payload board — GPS, barometric altimeter, camera, SD card
flight logging — now works on SCK-2400 over CCSDS and 2.4GHz RF with
*zero changes* to `main.py`.

---

## Session 10–11 Takeaways

If you're extending this design, the two things most likely to bite you from these sessions:

1. **SysConfig UART2 instance names and list order are load-bearing.**
   `PAYLOAD_UART` is the Ground Station link regardless of what the name
   suggests, and it must be listed first. Don't reorder or rename.
2. **Any buffer over ~100 bytes in a TI-RTOS task's call chain must be
   `static`**, not a stack local — especially in tasks with small stack
   allocations. Stack overflows on this platform fail silently with no
   diagnostic output, which makes them brutal to debug from symptoms
   alone.

See the full
[Hard-Won Lessons table](../SCK-2400_Developer_Guide.md#70-hard-won-lessons)
for the complete list, including items from all sessions.

---

## Session 12: Watchdog Timer — LEO Fault Recovery

With OAD and the payload bridge verified, the next critical gap for a
real LEO deployment was fault recovery. A hung task with no watchdog means
a stuck spacecraft — unrecoverable without a ground contact that may never
come.

### The architecture choice

The design is deliberately simple: a dedicated `watchdogTask` at the
lowest RTOS priority (1) kicks the hardware watchdog every 10 seconds
within a 30-second timeout window. If any higher-priority task deadlocks
or busy-spins without yielding, `watchdogTask` is starved and the timer
expires. Starvation IS the detection mechanism — no explicit hang
detection code is required.

```
rfTask       (priority 2) ─┐
uartTask     (priority 2) ─┤── any spin → watchdogTask starved
watchdogTask (priority 1) ─┘   → WDT expires → CC1352P reset
```

### The surprises

Three things were not obvious from the documentation:

**1. The watchdog clock stops in standby.** The CC1352P watchdog timer
is gated by the power domain — if the device enters standby, the clock
stops and the watchdog never fires even if a task is genuinely hung.
`Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY)` in `watchdogTask`
prevents standby for the session lifetime. Without this, a hung board
would just sleep forever.

**2. CCS JTAG stalls the watchdog.** `Watchdog_DEBUG_STALL_ON` is the
right setting for production (prevents spurious resets during debugging),
but ANY active JTAG connection — even a background connection with no
active debug session — pauses the watchdog hardware. For testing, you
must disconnect JTAG entirely after flashing.

**3. The CC1352P resets on the second timeout, not the first.** The first
expiry fires an NMI interrupt. The reset only occurs if the watchdog flag
is still pending at the second timeout. Total time from hang to reset is
approximately **65 seconds** at a 30-second period — not 30 seconds as
you'd expect.

### The verified test

After accounting for all three surprises, the test ran cleanly:
- `uart_task` entered a deliberate spin loop after a 5-second head start
  for `watchdogTask` to arm
- LED went dark when `watchdogTask` was starved
- Board reset at ~65 seconds
- Post-reset `get_telem` confirmed normal operation ✓

**Watchdog timer: LEO fault recovery operational.**

---

## Sessions 14–15: AES-128-CCM RF Encryption and NVS Safe Mode

These were the most debugging-intensive sessions of the entire bringup.
Both are documented here in narrative form for readers who may hit the
same walls.

### Why software AES

The obvious first approach was the TI AESCCM hardware driver
(`AESCCMCC26XX`). It failed in two independent ways:

1. **Semaphore poisoning from rfTask.** The TI AESCCM driver uses a
   shared semaphore (`CryptoResourceCC26XX_accessSemaphore`). The RF
   driver holds the crypto power domain during RF receive operations.
   Calling `AESCCM_open()` from rfTask while the RF driver is active
   causes the semaphore to become permanently poisoned — the driver
   returns NULL on every subsequent call.

2. **Keystream mismatch.** Even when called from uart_task (outside the
   RF context), the TI hardware driver produced different CTR keystream
   output than .NET `System.Security.Cryptography.AesCcm` for identical
   inputs. This was confirmed with a known test vector. The root cause
   was never fully identified — likely a difference in the CBC-MAC flags
   byte or the CTR counter formatting.

Both issues pointed to the same solution: implement AES-128-CCM in pure
C (RFC 3610), bypassing the hardware entirely. The software implementation
is task-context safe (no semaphores, no power domain dependency) and
produces output that matches .NET `AesCcm` and Python `cryptography`
exactly.

**ShiftRows was the subtle bug.** AES operates on a 4×4 byte state in
column-major order. ShiftRows rotates rows — but the byte indices for
each row in column-major layout are not what you'd intuitively expect:

```
Row 0: bytes  0,  4,  8, 12  (no shift)
Row 1: bytes  1,  5,  9, 13  (left 1: b[1],b[5],b[9],b[13] = b[5],b[9],b[13],b[1])
Row 2: bytes  2,  6, 10, 14  (left 2)
Row 3: bytes  3,  7, 11, 15  (left 3: b[3],b[7],b[11],b[15] = b[15],b[3],b[7],b[11])
```

Getting row 3 wrong produces incorrect keystream that still passes basic
sanity checks but mismatches the reference implementation.

### The task context trap

Once software AES was working, the question was where to call it.
The first attempt moved all crypto to `uart_task` via a queue
(`sRxEncryptedBuf` / `sRxDecryptPending`). This required
`UART2_Mode_NONBLOCKING` on the UART so `uart_task` could poll the queue
between reads. That seemed fine — until boards started failing with a
solid LED at boot (RF open failure).

The root cause: `UART2_Mode_NONBLOCKING` allocates a ring buffer from
the BIOS heap at `UART2_open()` time. This allocation exhausted the heap
before `RF_open()` could allocate its own resources — `RF_open()` returned
NULL, rfTask entered its fault loop, and the LED went solid.

The fix was architecturally simpler than the workaround: software AES is
pure C with no task restrictions, so `crypto_decrypt()` and
`crypto_encrypt()` can run directly in rfTask. The entire decrypt →
dispatch → encrypt chain happens inline with no cross-task handoff. The
queue and the `NONBLOCKING` UART change were both reverted.

### The NVS story

Non-volatile storage for the reset counter was the hardest single problem
of the entire bringup. The symptoms were consistent: `NVS_write()` returned
success, `NVS_WRITE_POST_VERIFY` passed, same-session read-back showed
valid data — but after every reset, the flash read back as `0xFFFFFFFF`.

**The diagnostic that unlocked it:** reading the raw flash address via a
volatile pointer dereference in the same session showed valid data.
Reading the same address after reset showed `0xFFFFFFFF`. This proved the
write buffer was being exposed as readable memory in-session, but the
physical flash array was never actually being programmed.

The fix required three things applied together:
1. `FlashCheckFsmForReady()` polling after `FlashProgram()` — the FSM
   accepts the command before physical programming completes
2. `Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY)` before the write
   — VDDR must be at write voltage; standby brings it below the threshold
3. VIMS cache disabled around all flash reads and writes — otherwise
   cached stale data is returned on read-back

Additionally, the TI NVS driver was abandoned entirely in favor of direct
`FlashSectorErase()` + `FlashProgram()` driverlib calls. The NVS driver
abstracts away the exact operations needed (FSM polling, power
constraint) in a way that can't be easily patched.

**The OAD image length trap.** Once the NVS region was placed at
`0x52000` (immediately after app flash end), OAD stopped working —
the image would transfer and verify, but after reboot the NVS region
read as `0xFFFFFFFF`. The cause: the Ground Station computed OAD image
length by scanning the map file for the highest flash address, which
included the `.nvs` NOLOAD section at `0x52000`. BIM was copying
`0x52000` bytes — right through the NVS region, erasing it on every
OAD update. The fix was a one-line change to the image length filter:
`origin < 0x52000` instead of `origin < 0x55000`.

**The XDS110 bench caveat.** After all of the above was fixed, NVS
persistence worked correctly across USB power cycles but not across
`CMD_REBOOT`. The XDS110 debugger intercepts the reset signal and
reapplies its cached flash image, erasing the NVS region. This is
bench-only behavior — in field deployment without a debugger, all
reset types persist correctly.

### The complete encrypted RF stack

With software AES, direct flash writes, and the OAD image length fix all
in place, the full encrypted RF stack was verified in a single session:

```
09:05:18  → get_telem dest=0x011
09:05:20  ← tlm_beacon  ✓  (1.4s round trip, encrypted)
09:05:40  → PICO Get GPS
09:05:41  ← GPS:0.000000,0.000000,0.0,0,0,992.41,175.0,19.66  ✓
```

OAD over the encrypted RF link: **1,332 chunks, 23.0 seconds, 13.9 KB/s,
zero NACKs.**

The architecture that finally worked cleanly:

```
C# (plaintext) → USB → GS board → crypto_encrypt() → RF → Remote board
Remote board → crypto_decrypt() → dispatch → handle → crypto_encrypt() → RF → GS board
GS board → crypto_decrypt() → uart_send() → USB → C# (plaintext)
```

All encryption and decryption happens in rfTask. The Pico never sees the
encryption layer. The C# app has no AES dependency.

---

*SCK-2400 Mini Log — Sessions 10–15*
*For the full hard-won lessons table see the [SCK-2400 Developer Guide](../SCK-2400_Developer_Guide.md)*
