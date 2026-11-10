"""compare_readback.py -- compare a cc2538_bsl flash readback (.bin) with a hex file.
Usage: py compare_readback.py bim_readback.bin V3\sck2400_bim.hex [start_address]"""
import sys
binf, hexf = sys.argv[1], sys.argv[2]
start = int(sys.argv[3], 0) if len(sys.argv) > 3 else 0
data = open(binf, 'rb').read()
mem, base = {}, 0
for line in open(hexf):
    line = line.strip()
    if not line.startswith(':'):
        continue
    n, a, t = int(line[1:3], 16), int(line[3:7], 16), int(line[7:9], 16)
    d = bytes.fromhex(line[9:9 + 2 * n])
    if t == 0:
        for i, b in enumerate(d):
            mem[base + a + i] = b
    elif t == 4:
        base = int(line[9:13], 16) << 16
    elif t == 2:
        base = int(line[9:13], 16) << 4
checked = diff = 0
first = None
for i, b in enumerate(data):
    addr = start + i
    if addr in mem:
        checked += 1
        if mem[addr] != b:
            diff += 1
            if first is None:
                first = (addr, mem[addr], b)
print("Bytes compared: %d   mismatches: %d" % (checked, diff))
if first:
    print("First mismatch at 0x%05X: hex 0x%02X, chip 0x%02X" % first)
print("RESULT: %s" % ("MATCH -- chip flash equals the hex" if checked and not diff else "MISMATCH"))