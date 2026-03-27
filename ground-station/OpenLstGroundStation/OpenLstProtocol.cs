// OpenLstProtocol.cs
// All OpenLST protocol constants, packet framing, and parsing.
// Flat static class — no inheritance, easy to extend.
// Source of truth: translator.py, commands.py, flash_constants.py

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace OpenLstGroundStation
{
    // ── Parsed inbound packet ──────────────────────────────────────────────
    public record RxPacket(ushort Hwid, ushort SeqNum, string OpName, int AckValue, byte[] RawPayload)
    {
        // If this is an ack with a printable ASCII payload (e.g. Pico response),
        // decode and expose it here. Otherwise null.
        public string PicoPayload
        {
            get
            {
                // Payload layout: hwid(2) seq(2) flag(1) opcode(1) [data...]
                // Data starts at index 6. Must have at least 1 data byte.
                if (RawPayload == null || RawPayload.Length < 7) return null;
                if (OpName != "ack") return null;
                // Extract data bytes after the header
                byte[] data = RawPayload.Skip(6).ToArray();
                if (data.Length == 0) return null;
                // Check all bytes are printable ASCII
                if (!data.All(b => b >= 0x20 && b <= 0x7E)) return null;
                return System.Text.Encoding.ASCII.GetString(data);
            }
        }
    }

    // ── Telem data (from translator.py telem Command definition) ──────────
    // Payload after opcode: reserved(1) uptime(4) uart0_rx(4) uart1_rx(4)
    //   rx_mode(1) tx_mode(1) adc0..9 (2 each = 20) last_rssi(1) last_lqi(1)
    //   last_freqest(1) pkts_sent(4) cs_count(4) pkts_good(4)
    //   pkts_rej_checksum(4) pkts_rej_reserved(4) pkts_rej_other(4)
    //   reserved0(4) reserved1(4) custom0(4) custom1(4)
    public record TelemData(
        uint   Uptime,
        uint   Uart0RxCount,
        uint   Uart1RxCount,
        byte   RxMode,
        byte   TxMode,
        short  Adc0, short Adc1, short Adc2, short Adc3, short Adc4,
        short  Adc5, short Adc6, short Adc7, short Adc8, short Adc9,
        sbyte  LastRssi,
        byte   LastLqi,
        sbyte  LastFreqest,
        uint   PacketsSent,
        uint   CsCount,
        uint   PacketsGood,
        uint   PacketsRejChecksum,
        uint   PacketsRejReserved,
        uint   PacketsRejOther,
        uint   Custom0,
        uint   Custom1
    );

    public static class OpenLstProtocol
    {
        // ── Flash constants (flash_constants.py) ───────────────────────────
        public const int FLASH_APP_START       = 0x0400;
        public const int FLASH_APP_END         = 0x6BFF;
        public const int FLASH_PAGE_SIZE       = 128;
        public const int FLASH_SIGNATURE_START = 0x6BF0;
        public const int FLASH_SIGNATURE_LEN   = 16;
        public const int MEM_SIZE              = 0x8000; // 32KB

        // ── Seqnum (commands.py) ───────────────────────────────────────────
        public const ushort SEQNUM_MIN = 16;
        public const ushort SEQNUM_MAX = 64000;

        public static ushort IncSeqNum(ushort current)
            => (ushort)Math.Max((current + 1) % SEQNUM_MAX, SEQNUM_MIN);

        // ── Opcodes (translator.py) ────────────────────────────────────────
        public static readonly Dictionary<string, byte> Opcodes = new()
        {
            { "ack",                   0x10 },
            { "nack",                  0xFF },
            { "reboot",                0x12 },
            { "get_callsign",          0x19 },
            { "set_callsign",          0x1A },
            { "callsign",              0x1B },
            { "get_telem",             0x17 },
            { "telem",                 0x18 },
            { "get_time",              0x13 },
            { "set_time",              0x14 },
            { "bootloader_ping",       0x00 },
            { "bootloader_erase",      0x0C },
            { "bootloader_ack",        0x01 },
            { "bootloader_nack",       0x0F },
            { "ascii",                 0x11 },
            { "bootloader_write_page", 0x02 },
        };

        public static string OpcodeName(byte op)
            => Opcodes.FirstOrDefault(kv => kv.Value == op).Key ?? $"0x{op:X2}";

        // ── Packet builder ─────────────────────────────────────────────────
        // Wire format (translator.py + SerialCommandHandler.send_message):
        //   0x22 0x69 <len> <payload>
        //   payload = hwid[lo] hwid[hi] seq[lo] seq[hi] 0x01 opcode [args...]
        //   NO checksum byte.

        public static byte[] BuildPacket(ushort hwid, ushort seq, byte opcode, byte[]? args = null)
        {
            var payload = new List<byte>
            {
                (byte)(hwid & 0xFF), (byte)((hwid >> 8) & 0xFF),
                (byte)(seq  & 0xFF), (byte)((seq  >> 8) & 0xFF),
                0x01,   // LST flag
                opcode
            };
            if (args != null) payload.AddRange(args);

            var frame = new List<byte> { 0x22, 0x69, (byte)payload.Count };
            frame.AddRange(payload);
            return frame.ToArray();
        }

        public static byte[] BuildSimpleCommand(ushort hwid, ushort seq, string commandName)
        {
            if (!Opcodes.TryGetValue(commandName, out byte op))
                throw new ArgumentException($"Unknown command: {commandName}");
            return BuildPacket(hwid, seq, op);
        }

        public static byte[] BuildBootloaderWritePage(ushort hwid, ushort seq, int pageNumber, string? hexData)
        {
            var args = new List<byte> { (byte)pageNumber };
            if (hexData != null)
                for (int i = 0; i < hexData.Length; i += 2)
                    args.Add(Convert.ToByte(hexData.Substring(i, 2), 16));
            return BuildPacket(hwid, seq, Opcodes["bootloader_write_page"], args.ToArray());
        }

        // ── ESP packet framer (esp_parser coroutine) ───────────────────────
        // Returns a list of complete RxPackets parsed out of the byte buffer.
        // The buffer is modified in-place — consumed bytes are removed.
        // Call this inside your _rxBufLock.

        public static List<RxPacket> FramePackets(List<byte> buf, Action<string> logRaw)
        {
            var results = new List<RxPacket>();

            while (true)
            {
                // Find 0x22 0x69 header
                int start = -1;
                for (int i = 0; i < buf.Count - 1; i++)
                {
                    if (buf[i] == 0x22 && buf[i + 1] == 0x69) { start = i; break; }
                }
                if (start < 0) { buf.Clear(); break; }
                if (start > 0) buf.RemoveRange(0, start);
                if (buf.Count < 3) break;

                int payloadLen = buf[2];
                int packetSize = 3 + payloadLen; // NO checksum byte
                if (buf.Count < packetSize) break;

                byte[] raw = buf.GetRange(0, packetSize).ToArray();
                buf.RemoveRange(0, packetSize);

                logRaw($"[RAW] {BitConverter.ToString(raw).Replace("-", " ")}");

                if (payloadLen < 6) { logRaw($"[WARN] Payload too short ({payloadLen}), skipping"); continue; }

                ushort rxHwid = (ushort)(raw[3] | (raw[4] << 8));
                ushort rxSeq  = (ushort)(raw[5] | (raw[6] << 8));
                // raw[7] = LST flag (0x01)
                byte   rxOp   = raw[8];

                string opName   = OpcodeName(rxOp);
                int    ackValue = payloadLen > 6 ? raw[9] : -1;

                // Extract full payload for callers that need to parse telem etc.
                byte[] rxPayload = raw.Skip(3).ToArray();

                results.Add(new RxPacket(rxHwid, rxSeq, opName, ackValue, rxPayload));
                // Check for Pico ASCII payload on ack
                var pkt = results[results.Count - 1];
                if (pkt.PicoPayload != null)
                    logRaw($"{opName} → \"{pkt.PicoPayload}\"  hwid={rxHwid:X4} seq={rxSeq}");
                else
                    logRaw($"{opName}{(ackValue >= 0 ? " " + ackValue : "")}  hwid={rxHwid:X4} seq={rxSeq}");
            }

            return results;
        }

        // ── Telem parser (translator.py telem Command definition) ──────────
        public static TelemData? ParseTelem(byte[] payload)
        {
            // payload starts at opcode byte (index 5 in raw frame, but here we pass rxPayload = raw[3..])
            // rxPayload layout: hwid(2) seq(2) flag(1) opcode(1) reserved(1) uptime(4) ...
            // So telem args start at index 7 of rxPayload
            try
            {
                int i = 7; // skip hwid(2)+seq(2)+flag(1)+opcode(1)+reserved(1)
                uint   uptime        = BitConverter.ToUInt32(payload, i); i += 4;
                uint   uart0Rx       = BitConverter.ToUInt32(payload, i); i += 4;
                uint   uart1Rx       = BitConverter.ToUInt32(payload, i); i += 4;
                byte   rxMode        = payload[i++];
                byte   txMode        = payload[i++];
                short  adc0          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc1          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc2          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc3          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc4          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc5          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc6          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc7          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc8          = BitConverter.ToInt16(payload, i);  i += 2;
                short  adc9          = BitConverter.ToInt16(payload, i);  i += 2;
                sbyte  lastRssi      = (sbyte)payload[i++];
                byte   lastLqi       = payload[i++];
                sbyte  lastFreqest   = (sbyte)payload[i++];
                uint   pktsSent      = BitConverter.ToUInt32(payload, i); i += 4;
                uint   csCount       = BitConverter.ToUInt32(payload, i); i += 4;
                uint   pktsGood      = BitConverter.ToUInt32(payload, i); i += 4;
                uint   rejCksum      = BitConverter.ToUInt32(payload, i); i += 4;
                uint   rejReserved   = BitConverter.ToUInt32(payload, i); i += 4;
                uint   rejOther      = BitConverter.ToUInt32(payload, i); i += 4;
                i += 8; // reserved0, reserved1
                uint   custom0       = BitConverter.ToUInt32(payload, i); i += 4;
                uint   custom1       = BitConverter.ToUInt32(payload, i);

                return new TelemData(uptime, uart0Rx, uart1Rx, rxMode, txMode,
                    adc0, adc1, adc2, adc3, adc4, adc5, adc6, adc7, adc8, adc9,
                    lastRssi, lastLqi, lastFreqest, pktsSent, csCount, pktsGood,
                    rejCksum, rejReserved, rejOther, custom0, custom1);
            }
            catch { return null; }
        }

        // ── AES-128 CBC-MAC signing (sign_radio.py) ────────────────────────
        public static byte[] SignFirmware(byte[] appSection, byte[] aesKey)
        {
            int inputLen = FLASH_SIGNATURE_START - FLASH_APP_START;
            byte[] input = new byte[inputLen];
            Array.Copy(appSection, FLASH_APP_START, input, 0, inputLen);

            using var aes = Aes.Create();
            aes.Key     = aesKey;
            aes.IV      = new byte[16];
            aes.Mode    = CipherMode.CBC;
            aes.Padding = PaddingMode.None;

            using var enc = aes.CreateEncryptor();
            byte[] cbc    = enc.TransformFinalBlock(input, 0, input.Length);

            byte[] mac = new byte[16];
            Array.Copy(cbc, cbc.Length - 16, mac, 0, 16);
            return mac;
        }

        // ── Intel HEX parser (intel_hex.py parse_hex_file) ────────────────
        public static byte[] ParseIntelHex(string path)
        {
            byte[] outbuff = Enumerable.Repeat((byte)0xFF, MEM_SIZE).ToArray();
            int lineCount = 0;
            foreach (string rawLine in System.IO.File.ReadLines(path))
            {
                string line = rawLine.Trim();
                lineCount++;
                if (!line.StartsWith(":"))
                    throw new Exception($"Line {lineCount} doesn't start with ':'");

                int byteCount  = Convert.ToInt32(line.Substring(1, 2), 16);
                int addrB1     = Convert.ToInt32(line.Substring(3, 2), 16);
                int addrB2     = Convert.ToInt32(line.Substring(5, 2), 16);
                int address    = (addrB1 << 8) | addrB2;
                int recordType = Convert.ToInt32(line.Substring(7, 2), 16);

                if (recordType == 1) return outbuff;
                if (recordType != 0) throw new Exception($"Unknown record type {recordType} on line {lineCount}");

                var data = new byte[byteCount];
                int expCs = byteCount + addrB1 + addrB2 + recordType;
                for (int i = 0; i < byteCount; i++) { data[i] = Convert.ToByte(line.Substring(9 + i * 2, 2), 16); expCs += data[i]; }
                int cs = Convert.ToInt32(line.Substring(9 + byteCount * 2, 2), 16);
                if ((expCs & 0xFF) != ((~cs + 1) & 0xFF)) throw new Exception($"Bad checksum on line {lineCount}");
                for (int i = 0; i < data.Length; i++) outbuff[address + i] = data[i];
            }
            throw new Exception("Expected EOF record");
        }
    }
}
