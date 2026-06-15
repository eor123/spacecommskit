# SCK-2400 Dev Log — OAD Streaming & CCSDS↔ESP Payload Bridge

**Sessions 10–11 — June 2026**

> This is the bringup narrative behind the
> [Hard-Won Lessons table](../SCK-2400_Developer_Guide.md#70-hard-won-lessons)
> in the SCK-2400 Developer Guide. If you're hitting one of these same
> walls, the play-by-play below may save you the hours it cost us.

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

10ms became the production default in the Ground Station — `cmbOadDelay`
index 0.

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

### RF end-to-end

With the bridge working over USB, we moved the remote board + Pico across
the room and routed everything through the Ground Station board over
2.4GHz RF:

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

The SCK-915 payload board — GPS, barometric altimeter, camera, SD card
flight logging — now works on SCK-2400 over CCSDS and 2.4GHz RF with
*zero changes* to `main.py`.

---

## Takeaways

If you're extending this design, the two things most likely to bite you:

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
for the complete list, including items not covered in this narrative.
