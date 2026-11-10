#!/usr/bin/env python3
"""
tinysa_qa_scan.py
SpaceCommsKit -- RF QA tooling

Purpose:
    Minimal, self-contained tinySA scan tool for RF QA. Connects over
    USB serial, runs a single scan command, and writes frequency/power
    data to a CSV file in the same shape the existing C# QA pipeline
    already expects from ParseRtlPowerCsv() -- so the C# side needs
    zero changes to consume this output.

    This is deliberately a small, "essentials only" prototype -- one
    scan command, one CSV output. The plan is to validate this against
    real hardware, then port the same handful of serial commands
    directly into native C# (System.IO.Ports.SerialPort) so the final
    QA tool has no Python runtime dependency at all.

Protocol reference (confirmed against tinySA firmware source AND real
device output on 2026-08-06 -- not just theory):
    scan {start_hz} {stop_hz} [points] [outmask]
    outmask bit 1 (value 1) = output frequency (integer Hz)
    outmask bit 2 (value 2) = output measured power -- emits TWO values
        per firmware source ("%e %f "): the real measurement in
        scientific notation (e.g. -9.918750e+01), plus a trailing
        placeholder value observed as a constant 0.0. This script
        parses the frequency and the real measurement, and ignores
        the trailing placeholder.
    outmask=3 -> three space-separated values per line: freq, power
        (scientific notation), placeholder

Usage:
    python tinysa_qa_scan.py --port COM4 --start 902e6 --stop 928e6 \
        --points 290 --output scan_result.csv

    python tinysa_qa_scan.py --port COM4 --center 2440e6 --span 26e6 \
        --output scan_result.csv

Output CSV format (verified against the real ParseRtlPowerCsv() in
MainForm.cs, not assumed -- matches rtl_power's own native single-row
format so the existing C# parser needs zero changes to consume this):
    date,time,hz_low,hz_high,hz_step,samples,dbm0,dbm1,dbm2,...,dbmN
    One row per scan. date/time/samples columns are filled with
    placeholder values (the real parser ignores them) -- only hz_low
    (column 2) and hz_step (column 4) are read as real values, with
    every column from 6 onward treated as sequential dBm bins.

Exit codes:
    0  -- success, CSV written
    1  -- serial port error (not found, can't open, etc.)
    2  -- no response / timeout from device
    3  -- response received but no valid data parsed
    4  -- bad arguments
"""

from __future__ import annotations

import argparse
import re
import sys
import time

try:
    import serial
except ImportError:
    print("ERROR: pyserial is required. Install with: pip install pyserial",
          file=sys.stderr)
    sys.exit(1)


# -----------------------------------------------------------------------
# Serial communication
# -----------------------------------------------------------------------

# tinySA is a USB CDC (virtual COM) device -- the baud rate value itself
# is not meaningful to the hardware UART (there isn't a real one), but
# pyserial requires SOME value to open the port. 115200 is the
# conventional value used across every tinySA tool referenced during
# this script's development (tinySA-App, tinySA_python, nanovna-tools).
BAUD_RATE = 115200

# How long to wait for a scan response before giving up. A scan across
# a wide span with a narrow RBW can legitimately take many seconds on
# tinySA hardware -- this needs to be generous, not tuned for speed.
RESPONSE_TIMEOUT_S = 15.0

# Regex for a single scanned data point line. Confirmed against REAL
# device output (not assumed): tinySA emits three space-separated
# values per line when outmask=3 -- frequency in Hz (integer), power
# in dBm using scientific notation (e.g. -9.918750e+01), and a third
# trailing placeholder value (observed as a constant 0.0) that this
# script doesn't need and ignores. Matches from the start of the line
# rather than anchoring the end, so it's tolerant of trailing content.
DATA_LINE_RE = re.compile(
    r"^\s*(\d+)\s+(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)")


def open_tinysa(port: str) -> "serial.Serial":
    """Open the tinySA's serial port. Raises on failure -- caller decides
    how to report it."""
    return serial.Serial(port, BAUD_RATE, timeout=1)


def send_command(ser: "serial.Serial", command: str) -> str:
    """Send a single console command and read back everything the
    device sends until RESPONSE_TIMEOUT_S of silence, or a total cap
    is hit. Returns the raw decoded response text.

    Reading by "silence" rather than a specific prompt string is
    deliberate -- tinySA's exact prompt/echo behavior varies slightly
    across firmware versions, and this approach is robust to that
    without needing to match it exactly."""
    ser.reset_input_buffer()
    ser.write((command + "\r").encode("ascii"))
    ser.flush()

    buf = bytearray()
    last_data_time = time.time()
    start_time = time.time()

    while True:
        chunk = ser.read(4096)
        now = time.time()
        if chunk:
            buf.extend(chunk)
            last_data_time = now
        else:
            # No data this read cycle -- check if we've gone quiet
            # long enough to consider the response complete.
            if now - last_data_time > 1.5 and len(buf) > 0:
                break
        if now - start_time > RESPONSE_TIMEOUT_S:
            break

    return buf.decode("ascii", errors="replace")


def parse_scan_response(raw_text: str) -> list[tuple[float, float]]:
    """Parse a scan command's response text into (freq_hz, dbm) tuples.
    Silently skips lines that don't match the expected data-point
    pattern (command echo, prompt characters, blank lines, etc.) --
    this is intentional, not an error condition, since the device
    echoes the command and may print other incidental text."""
    points = []
    for line in raw_text.splitlines():
        m = DATA_LINE_RE.match(line)
        if m:
            freq_hz = float(m.group(1))
            dbm = float(m.group(2))
            points.append((freq_hz, dbm))
    return points


# -----------------------------------------------------------------------
# Main
# -----------------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", required=True,
        help="Serial port the tinySA is connected on, e.g. COM4")
    ap.add_argument("--start", type=float,
        help="Sweep start frequency in Hz")
    ap.add_argument("--stop", type=float,
        help="Sweep stop frequency in Hz")
    ap.add_argument("--center", type=float,
        help="Sweep center frequency in Hz (alternative to --start/--stop)")
    ap.add_argument("--span", type=float,
        help="Sweep span in Hz (used with --center)")
    ap.add_argument("--points", type=int, default=290,
        help="Number of scan points (default 290, matches tinySA's own default)")
    ap.add_argument("--output", required=True,
        help="Path to write the result CSV to")

    args = ap.parse_args()

    # Resolve start/stop from either explicit values or center/span.
    if args.start is not None and args.stop is not None:
        start_hz, stop_hz = args.start, args.stop
    elif args.center is not None and args.span is not None:
        start_hz = args.center - (args.span / 2.0)
        stop_hz  = args.center + (args.span / 2.0)
    else:
        print("ERROR: must supply either --start/--stop or --center/--span",
              file=sys.stderr)
        return 4

    if start_hz >= stop_hz:
        print(f"ERROR: start ({start_hz} Hz) must be less than stop ({stop_hz} Hz)",
              file=sys.stderr)
        return 4

    # ── Connect ──────────────────────────────────────────────────────
    try:
        ser = open_tinysa(args.port)
    except serial.SerialException as e:
        print(f"ERROR: could not open {args.port}: {e}", file=sys.stderr)
        return 1

    try:
        # outmask=3 -> frequency (bit 1) + power (bit 2) per point.
        cmd = f"scan {int(start_hz)} {int(stop_hz)} {args.points} 3"
        raw = send_command(ser, cmd)
    finally:
        ser.close()

    if not raw.strip():
        print("ERROR: no response from device -- check port, cable, "
              "and that the device is a tinySA (not in DFU mode).",
              file=sys.stderr)
        return 2

    points = parse_scan_response(raw)
    if not points:
        print("ERROR: response received but no valid data points parsed. "
              "Raw response follows for diagnosis:", file=sys.stderr)
        print(raw, file=sys.stderr)
        return 3

    # ── Write CSV in rtl_power's actual native format ─────────────────
    # Verified against the real ParseRtlPowerCsv() in MainForm.cs, not
    # assumed: one row per whole sweep --
    #   date,time,hz_low,hz_high,hz_step,samples,dbm0,dbm1,dbm2,...
    # The parser only reads columns 2 (hz_low) and 4 (hz_step) plus
    # every column from 6 onward as sequential dBm bins -- date/time/
    # samples (0,1,5) are ignored by the parser, filled with sane
    # placeholder values here.
    hz_step = (stop_hz - start_hz) / (len(points) - 1) if len(points) > 1 else 0
    now = time.strftime("%Y-%m-%d, %H:%M:%S")
    dbm_values = ",".join(f"{dbm:.2f}" for _, dbm in points)
    with open(args.output, "w", encoding="ascii") as f:
        f.write(f"{now},{int(start_hz)},{int(stop_hz)},{hz_step:.6f},"
                f"{len(points)},{dbm_values}\n")

    print(f"OK: {len(points)} points written to {args.output}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
