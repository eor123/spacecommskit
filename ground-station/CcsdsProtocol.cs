// CcsdsProtocol.cs
// SCK-2400 Ground Station — CCSDS Space Packet Protocol layer
// Mirrors OpenLstProtocol interface so MainForm branching is minimal.
//
// Protocol stack:
//   C# ↔ GS board (USB serial):  ESP frame wrapping CCSDS packet
//   GS board ↔ remote (RF):      Raw CCSDS packet over 2.4GHz PHY
//
// ESP frame format (SCK-2400 — 2-byte length field):
//   0x22 0x69 LEN_HI LEN_LO [CCSDS packet bytes...]
//
//   NOTE: SCK-915 uses 1-byte length (0x22 0x69 LEN [payload]).
//   SCK-2400 uses 2-byte length to support larger CCSDS packets.
//   Both share the same sync bytes but the framing is NOT compatible.
//
// CCSDS primary header (6 bytes, big-endian):
//   Bits 15-13: Version (000)
//   Bit  12:    Type (0=telemetry, 1=telecommand)
//   Bit  11:    Secondary header flag
//   Bits 10-0:  APID (11-bit application process identifier)
//   Bits 15-14: Sequence flags (11 = standalone)
//   Bits 13-0:  Sequence count (14-bit)
//   Bits 15-0:  Packet data length - 1
//
// Reference: SCK-2400_Firmware_Requirements.md Section 3.5
//            SCK-2400_Project_Notes.md Section 30

using System;
using System.Collections.Generic;
using System.Linq;

namespace OpenLstGroundStation
{
    // ══════════════════════════════════════════════════════════════════════
    //  CCSDS TELEMETRY DATA
    //  Mirrors TelemData record for UpdateTelemDisplay() compatibility.
    // ══════════════════════════════════════════════════════════════════════
    public class CcsdsTelemData
    {
        public uint   Uptime              { get; set; }  // seconds
        public sbyte  LastRssi            { get; set; }  // dBm
        public byte   LastLqi             { get; set; }
        public uint   PacketsGood         { get; set; }
        public uint   PacketsSent         { get; set; }
        public uint   PacketsRejChecksum  { get; set; }
        public uint   PacketsRejOther     { get; set; }
        public uint   Uart0RxCount        { get; set; }
        public uint   Uart1RxCount        { get; set; }
        public byte   RxMode              { get; set; }
        public byte   TxMode              { get; set; }
        public float  DieTempC            { get; set; }  // CC2652P internal temp
        public float  SupplyVoltage       { get; set; }  // VDDS in volts

        // LEO fault recovery fields (added Session 13)
        // These fields provide ground station visibility into boot-loop
        // detection and safe mode state. See nv_storage.h for full details.
        public byte   ResetCounter        { get; set; }  // NV reset counter
        public byte   ResetCause          { get; set; }  // SCK_RESET_CAUSE_* code
        public bool   SafeModeActive      { get; set; }  // true = safe mode
        public byte   UptimeMinutes       { get; set; }  // clean run progress (0-255 min)

        // Human-readable reset cause string for display in ground station UI
        public string ResetCauseString => ResetCause switch
        {
            0x00 => "Power-On",
            0x01 => "Reset Pin",
            0x02 => "VDDS Brownout",
            0x03 => "VDDR Brownout",
            0x04 => "Warm Reset",
            0x05 => "System Reset",
            0x06 => "Watchdog",
            0xFF => "Unknown",
            _    => $"0x{ResetCause:X2}"
        };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  CCSDS PROTOCOL
    // ══════════════════════════════════════════════════════════════════════
    public static class CcsdsProtocol
    {
        // ── ESP framing constants ──────────────────────────────────────
        // SCK-2400 uses 2-byte length field (vs SCK-915 1-byte)
        public const byte  ESP_SYNC_0     = 0x22;
        public const byte  ESP_SYNC_1     = 0x69;
        public const int   ESP_HEADER_LEN = 4;      // 2 sync + 2 length bytes
        public const int   ESP_MAX_PACKET = 512;    // max CCSDS packet size

        // ── CCSDS header constants ─────────────────────────────────────
        public const int   CCSDS_HEADER_LEN = 6;
        public const int   CCSDS_SEQ_UNSEG  = 0x3;  // standalone packet (11)

        // ── Packet type APID assignments (11-bit, 0x001–0x00F range) ──
        // These identify WHAT kind of data is in the packet.
        // Matches ccsds.h firmware defines exactly.
        public const ushort APID_TLM_BEACON  = 0x001; // Telemetry beacon
        public const ushort APID_COMMAND     = 0x002; // Uplink command (TC)
        public const ushort APID_CMD_ACK     = 0x003; // Command acknowledgement
        public const ushort APID_GPS_TLM     = 0x004; // GPS telemetry
        public const ushort APID_BARO_TLM    = 0x005; // Barometer telemetry
        public const ushort APID_IMAGE_CHUNK = 0x006; // Image data chunk
        public const ushort APID_FILE_LIST   = 0x007; // File list
        public const ushort APID_TEST        = 0x1FF; // Reserved / test

        // ── Board address APIDs (0x010–0x01F range) — HWID equivalent ─
        // These identify WHICH physical board a command is addressed to.
        // Operator selects target board in the APID field in the header bar.
        // Matches SCK_APID_BOARD_* defines in ccsds.h exactly.
        public const ushort APID_BOARD_GS        = 0x010; // Ground station board
        public const ushort APID_BOARD_REMOTE_1  = 0x011; // Remote board 1 (CubeSat)
        public const ushort APID_BOARD_REMOTE_2  = 0x012; // Remote board 2
        public const ushort APID_BOARD_BROADCAST = 0x000; // All boards respond

        // ── Command sub-opcodes (first byte of command packet payload) ─
        // Matches CMD_* defines in ccsds.h firmware exactly.
        // SCK-915 equivalent: sub-opcodes under opcode 0x20 (PICO_MSG).
        public const byte CMD_GET_TELEM       = 0x01; // Request telemetry
        public const byte CMD_ACK             = 0x02; // Acknowledge / ping
        public const byte CMD_REBOOT          = 0x03; // Soft reset
        public const byte CMD_BEACON_CTRL     = 0x04; // Enable/disable RF beacon
        public const byte CMD_CLEAR_SAFE_MODE = 0x05; // Clear safe mode + reset counter
                                                       // Send after reviewing telemetry
                                                       // to restore full board operation.

        // ── Payload board commands (0x20-0x29) ──────────────────────────
        // Bridge: CCSDS (RF) → CC1352P → ESP framing → Pico main.py
        // Mapping: CCSDS opcode 0x2N → Pico ESP sub-opcode 0x0N
        // [SCK-DEV: ADD_COMMAND] — add new payload opcodes here
        public const byte CMD_PICO_PING   = 0x20; // → ESP 0x00 → "PICO:ACK"
        public const byte CMD_PICO_TEMP   = 0x21; // → ESP 0x01 → "TEMP:xx.xxC"
        public const byte CMD_PICO_SNAP   = 0x22; // → ESP 0x02 → "SNAP:OK:..."
        public const byte CMD_PICO_LIST   = 0x23; // → ESP 0x03 → "LIST:..."
        public const byte CMD_PICO_INFO   = 0x24; // → ESP 0x04 → "INFO:..."
        public const byte CMD_PICO_CHUNK  = 0x25; // → ESP 0x05 → "CHUNK:..."
        public const byte CMD_PICO_DELETE = 0x26; // → ESP 0x06 → "DEL:..."
        public const byte CMD_GET_GPS     = 0x27; // → ESP 0x07 → "GPS:..."
        public const byte CMD_GET_BARO    = 0x28; // → ESP 0x08 → "BARO:..."
        public const byte CMD_PICO_BEACON = 0x29; // → ESP 0x09 → "BEACON:ON/OFF"
        // Pico payload commands — forwarded to Pico processor by firmware.
        // Same workflow as SCK-915. Pico main.py handling TBD (separate session).
        public const byte CMD_GET_FILES   = 0x07; // Request file list from Pico
        public const byte CMD_GET_FILE    = 0x08; // Request file download
        public const byte CMD_GET_CHUNK   = 0x09; // Request next file chunk
        public const byte CMD_DEL_FILE    = 0x0A; // Delete file on Pico
        // OAD Over-Air Download commands — RF image transport (Phase 3)
        // Matches CMD_OAD_* in ccsds.h exactly.
        public const byte CMD_OAD_START   = 0x10; // Begin OAD: [4B imgSize BE][2B crc16 BE]
        public const byte CMD_OAD_CHUNK   = 0x11; // Chunk:     [4B offset BE][1B len][data]
        public const byte CMD_OAD_END     = 0x12; // Finalize:  [4B crc32 BE] verify+reboot
        public const byte CMD_OAD_ABORT   = 0x13; // Abort OAD session, clear slot
        public const byte CMD_OAD_STATUS  = 0x14; // Query: returns [1B active][4B rxd][4B total]

        // ── APID name map for log display ──────────────────────────────
        public static readonly Dictionary<ushort, string> ApidNames = new()
        {
            { APID_TLM_BEACON,       "tlm_beacon"   },
            { APID_COMMAND,          "command"       },
            { APID_CMD_ACK,          "cmd_ack"       },
            { APID_GPS_TLM,          "gps_tlm"       },
            { APID_BARO_TLM,         "baro_tlm"      },
            { APID_IMAGE_CHUNK,      "image_chunk"   },
            { APID_FILE_LIST,        "file_list"     },
            { APID_TEST,             "test"          },
            { APID_BOARD_GS,         "board_gs"      },
            { APID_BOARD_REMOTE_1,   "board_remote1" },
            { APID_BOARD_REMOTE_2,   "board_remote2" },
        };

        public static string ApidToOpName(ushort apid)
            => ApidNames.TryGetValue(apid, out string? name) ? name : $"apid_0x{apid:X3}";

        // ── Sequence counter ───────────────────────────────────────────
        public const ushort SEQCOUNT_MIN = 0x0001;
        public const ushort SEQCOUNT_MAX = 0x3FFF;  // 14-bit max

        public static ushort IncSeqCount(ushort current)
        {
            ushort next = (ushort)(current + 1);
            return next > SEQCOUNT_MAX ? SEQCOUNT_MIN : next;
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD OUTGOING PACKET
        //  Builds ESP-framed CCSDS packet for serial TX to GS board.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Build an ESP-framed CCSDS packet.
        /// </summary>
        /// <param name="apid">Target APID</param>
        /// <param name="seqCount">14-bit sequence counter (caller maintains)</param>
        /// <param name="isCommand">true = telecommand, false = telemetry</param>
        /// <param name="payload">User data field bytes</param>
        public static byte[] BuildPacket(ushort apid, ushort seqCount,
            bool isCommand = true, byte[]? payload = null)
        {
            payload ??= Array.Empty<byte>();
            int dataLen = payload.Length;

            // Build CCSDS primary header (6 bytes, big-endian)
            byte[] ccsds = new byte[CCSDS_HEADER_LEN + dataLen];

            // Word 0: version(000) | type | shf(1) | apid[10:0]
            ushort word0 = (ushort)(
                ((isCommand ? 1 : 0) << 12) |   // 1=TC, 0=TM
                (1 << 11)             |          // secondary header flag
                (apid & 0x07FF));
            ccsds[0] = (byte)(word0 >> 8);
            ccsds[1] = (byte)(word0 & 0xFF);

            // Word 1: seq_flags(11=standalone) | seq_count[13:0]
            ushort word1 = (ushort)(
                (CCSDS_SEQ_UNSEG << 14) |
                (seqCount & 0x3FFF));
            ccsds[2] = (byte)(word1 >> 8);
            ccsds[3] = (byte)(word1 & 0xFF);

            // Word 2: packet data length - 1 (0 if no payload)
            ushort word2 = (ushort)(dataLen > 0 ? dataLen - 1 : 0);
            ccsds[4] = (byte)(word2 >> 8);
            ccsds[5] = (byte)(word2 & 0xFF);

            // User data field
            if (dataLen > 0)
                Array.Copy(payload, 0, ccsds, CCSDS_HEADER_LEN, dataLen);

            // Wrap in ESP frame: 0x22 0x69 LEN_HI LEN_LO [ccsds...]
            int totalLen = ccsds.Length;
            byte[] frame = new byte[ESP_HEADER_LEN + totalLen];
            frame[0] = ESP_SYNC_0;
            frame[1] = ESP_SYNC_1;
            frame[2] = (byte)(totalLen >> 8);
            frame[3] = (byte)(totalLen & 0xFF);
            Array.Copy(ccsds, 0, frame, ESP_HEADER_LEN, totalLen);
            return frame;
        }

        /// <summary>
        /// Build a simple uplink command with a single sub-opcode byte.
        /// destApid is the target board address (APID_BOARD_GS, APID_BOARD_REMOTE_1, etc.)
        /// This is the HWID equivalent for CCSDS -- operator selects in the APID field.
        /// </summary>
        public static byte[] BuildSimpleCommand(ushort seqCount, byte subOpcode,
            ushort destApid = APID_BOARD_GS)
            => BuildPacket(destApid, seqCount, true, new byte[] { subOpcode });

        /// <summary>
        /// Build an uplink command with sub-opcode and additional arguments.
        /// destApid is the target board address (APID_BOARD_GS, APID_BOARD_REMOTE_1, etc.)
        /// Mirrors OpenLstProtocol.BuildPacket(hwid, seq, opcode, args) pattern.
        /// </summary>
        public static byte[] BuildCommand(ushort seqCount, byte subOpcode,
            byte[]? args = null, ushort destApid = APID_BOARD_GS)
        {
            byte[] payload = args == null
                ? new byte[] { subOpcode }
                : new byte[] { subOpcode }.Concat(args).ToArray();
            return BuildPacket(destApid, seqCount, true, payload);
        }

        // ════════════════════════════════════════════════════════════════
        //  PARSE INCOMING PACKETS
        //  Strips ESP frame, validates CCSDS header, returns RxPacket list.
        //  Modifies rxBuf in place — consumed bytes removed.
        //  Returns RxPacket (same type as OpenLST path) for WaitForReply compat.
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Parse incoming byte stream into RxPacket list.
        /// Uses CCSDS ESP framing (2-byte length).
        /// Returns the same RxPacket type as OpenLstProtocol for MainForm compat.
        /// APID is mapped to RxPacket.Hwid so WaitForReply() works unchanged.
        /// </summary>
        /// <summary>
        /// Parse incoming byte stream into RxPacket list.
        /// GS board firmware handles all RF decryption -- C# receives plaintext.
        /// </summary>
        public static List<RxPacket> FramePackets(List<byte> buf, Action<string> logRaw)
        {
            var results = new List<RxPacket>();

            while (true)
            {
                // Scan for ESP sync bytes 0x22 0x69
                int syncIdx = -1;
                for (int i = 0; i <= buf.Count - 2; i++)
                {
                    if (buf[i] == ESP_SYNC_0 && buf[i + 1] == ESP_SYNC_1)
                    { syncIdx = i; break; }
                }

                if (syncIdx < 0)
                {
                    // No sync found — keep last byte (might be first sync byte)
                    if (buf.Count > 1) buf.RemoveRange(0, buf.Count - 1);
                    break;
                }

                // Discard bytes before sync
                if (syncIdx > 0) buf.RemoveRange(0, syncIdx);

                // Need full ESP header (4 bytes)
                if (buf.Count < ESP_HEADER_LEN) break;

                // SCK-2400: 2-byte big-endian length
                int payloadLen = (buf[2] << 8) | buf[3];

                if (payloadLen <= 0 || payloadLen > ESP_MAX_PACKET)
                {
                    // Bad length — skip sync bytes and resync
                    logRaw($"[CCSDS] Bad ESP length {payloadLen} — resyncing");
                    buf.RemoveRange(0, 2);
                    continue;
                }

                // Wait for complete packet
                int totalExpected = ESP_HEADER_LEN + payloadLen;
                if (buf.Count < totalExpected) break;

                // Extract CCSDS packet bytes
                byte[] ccsdsBytes = buf.Skip(ESP_HEADER_LEN).Take(payloadLen).ToArray();
                buf.RemoveRange(0, totalExpected);

                logRaw($"[CCSDS RAW] {BitConverter.ToString(ccsdsBytes).Replace("-", " ")}");

                // Parse CCSDS header
                if (ccsdsBytes.Length < CCSDS_HEADER_LEN)
                {
                    logRaw($"[CCSDS] Packet too short: {ccsdsBytes.Length} bytes");
                    continue;
                }

                // Word 0: version | type | shf | apid
                ushort word0   = (ushort)((ccsdsBytes[0] << 8) | ccsdsBytes[1]);
                int    version = (word0 >> 13) & 0x07;
                bool   isTc    = ((word0 >> 12) & 0x01) == 1;
                ushort apid    = (ushort)(word0 & 0x07FF);

                if (version != 0)
                {
                    logRaw($"[CCSDS] Bad version {version} — skipping");
                    continue;
                }

                // Word 1: seq_flags | seq_count
                ushort word1     = (ushort)((ccsdsBytes[2] << 8) | ccsdsBytes[3]);
                ushort seqCount  = (ushort)(word1 & 0x3FFF);

                // User data field (after 6-byte header)
                byte[] userDataField = ccsdsBytes.Length > CCSDS_HEADER_LEN
                    ? ccsdsBytes.Skip(CCSDS_HEADER_LEN).ToArray()
                    : Array.Empty<byte>();

                // Decode ASCII payload for GPS:, BARO:, ACK: etc.
                string? picoPayload = null;
                if (userDataField.Length > 0)
                {
                    string ascii = System.Text.Encoding.ASCII.GetString(
                        userDataField.Where(b => b >= 0x20 && b < 0x7F).ToArray());
                    if (ascii.Length >= 3)
                        picoPayload = ascii;
                }

                string opName = ApidToOpName(apid);
                logRaw($"{opName}  apid=0x{apid:X3} seq={seqCount}" +
                       (picoPayload != null ? $" → \"{picoPayload}\"" : ""));

                // Return RxPacket with APID mapped to Hwid field.
                // This means WaitForReply(APID_TLM_BEACON, seq, timeout)
                // works exactly like WaitForReply(hwid, seq, timeout).
                results.Add(new RxPacket(
                    Hwid:       apid,
                    SeqNum:     seqCount,
                    OpName:     opName,
                    AckValue:   -1,
                    RawPayload: userDataField
                ));
            }

            return results;
        }

        // ════════════════════════════════════════════════════════════════
        //  PARSE TELEMETRY BEACON
        //  Decodes APID_TLM_BEACON user data field into CcsdsTelemData.
        //
        //  Firmware telemetry payload format (little-endian):
        //    uint32  uptime_sec          4 bytes
        //    int8    last_rssi_dbm       1 byte
        //    uint8   last_lqi            1 byte
        //    uint32  packets_good        4 bytes
        //    uint32  packets_sent        4 bytes
        //    uint16  packets_rej_cksum   2 bytes
        //    uint16  packets_rej_other   2 bytes
        //    uint32  uart0_rx_count      4 bytes
        //    uint32  uart1_rx_count      4 bytes
        //    uint8   rx_mode             1 byte
        //    uint8   tx_mode             1 byte
        //    int16   die_temp_raw        2 bytes  (ADC counts)
        //    uint16  supply_mv           2 bytes  (millivolts)
        //    -- LEO fault recovery fields (added Session 13) --
        //    uint8   reset_counter       1 byte   (NV reset counter)
        //    uint8   reset_cause         1 byte   (SCK_RESET_CAUSE_*)
        //    uint8   safe_mode_active    1 byte   (0=normal, 1=safe mode)
        //    uint8   uptime_minutes      1 byte   (clean run progress)
        //    Total: 36 bytes
        //
        //  IMPORTANT: If firmware telemetry struct changes, update this
        //  parser to match. The static assert in telemetry.h enforces
        //  the firmware side. This parser must be updated manually.
        // ════════════════════════════════════════════════════════════════
        public static CcsdsTelemData? ParseTelem(byte[]? payload)
        {
            if (payload == null || payload.Length < 36) return null;

            try
            {
                int i  = 0;
                var td = new CcsdsTelemData();

                td.Uptime             = BitConverter.ToUInt32(payload, i); i += 4;
                td.LastRssi           = (sbyte)payload[i++];
                td.LastLqi            = payload[i++];
                td.PacketsGood        = BitConverter.ToUInt32(payload, i); i += 4;
                td.PacketsSent        = BitConverter.ToUInt32(payload, i); i += 4;
                td.PacketsRejChecksum = BitConverter.ToUInt16(payload, i); i += 2;
                td.PacketsRejOther    = BitConverter.ToUInt16(payload, i); i += 2;
                td.Uart0RxCount       = BitConverter.ToUInt32(payload, i); i += 4;
                td.Uart1RxCount       = BitConverter.ToUInt32(payload, i); i += 4;
                td.RxMode             = payload[i++];
                td.TxMode             = payload[i++];

                // CC2652P die temperature — 12-bit ADC, ~0.0625°C/count
                short tempRaw   = BitConverter.ToInt16(payload, i); i += 2;
                td.DieTempC     = tempRaw * 0.0625f;

                // Supply voltage millivolts → volts
                ushort supplyMv  = BitConverter.ToUInt16(payload, i); i += 2;
                td.SupplyVoltage = supplyMv / 1000.0f;

                // LEO fault recovery fields
                td.ResetCounter   = payload[i++];
                td.ResetCause     = payload[i++];
                td.SafeModeActive = payload[i++] != 0;
                td.UptimeMinutes  = payload[i++];

                return td;
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════════
        //  CRYPTO NOTE
        //
        //  AES-128 CCM encryption/decryption is handled entirely in
        //  firmware (crypto.c). C# sends and receives plaintext CCSDS
        //  packets over USB to the GS board. The GS board firmware
        //  encrypts outgoing RF packets and decrypts incoming RF packets
        //  before forwarding to C#.
        //
        //  Key material lives only in security.h (firmware side).
        //  C# has no crypto dependency.
        // ════════════════════════════════════════════════════════════════
    }
}
