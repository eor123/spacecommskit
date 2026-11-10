"""check_ccfg.py -- decode the CCFG bootloader settings from a BIM or merged hex.
Usage: python check_ccfg.py <file.hex>
Expects the CC26x2/CC13x2 CCFG at 0x57FA8 (BL_CONFIG at +0x30)."""
import sys
mem, base = {}, 0
for line in open(sys.argv[1]):
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
addr = 0x57FA8 + 0x30
if addr not in mem:
    sys.exit("No CCFG at 0x57FA8 in this file")
bl = int.from_bytes(bytes(mem[addr + i] for i in range(4)), 'little')
en, lvl, pin, bden = bl >> 24, (bl >> 16) & 1, (bl >> 8) & 0xFF, bl & 0xFF
print("BL_CONFIG = 0x%08X" % bl)
print("  ROM bootloader : %s (0x%02X)" % ("ENABLED" if en == 0xC5 else "disabled", en))
print("  Backdoor       : %s (0x%02X)" % ("ENABLED" if bden == 0xC5 else "disabled", bden))
print("  Backdoor pin   : DIO%d, active %s" % (pin, "high" if lvl else "low"))
ok = en == 0xC5 and bden == 0xC5 and pin == 6 and lvl == 0
print("RESULT: %s" % ("OK -- DIO6 low at reset enters the ROM bootloader" if ok
                      else "NOT SET -- no ROM bootloader recovery path"))
sys.exit(0 if ok else 1)
