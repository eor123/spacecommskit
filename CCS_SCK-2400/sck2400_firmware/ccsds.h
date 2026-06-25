/*
 * ccsds.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   CCSDS Space Packet Protocol definitions and framing utilities.
 *   Implements CCSDS 133.0-B-2 primary header, APID assignments,
 *   board address APIDs (HWID equivalent), and routing macros.
 *
 * SCK-DEV: CCSDS_APID -- To add a new packet type:
 *          1. Add a new CCSDS_APID_* define below
 *          2. Add a handler case in uart_dispatch_ccsds_packet() (uart.c)
 *          3. Add a build function ccsds_build_<type>() here if needed
 *
 * SCK-DEV: ADD_COMMAND -- To add a new command sub-opcode:
 *          1. Add a CMD_* define in the Command Opcodes block below
 *          2. Add a case in uart_dispatch_ccsds_packet() (uart.c)
 *          3. Implement the handler function in uart.c
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.5
 */

#ifndef CCSDS_H
#define CCSDS_H

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stdbool.h>

/* -----------------------------------------------------------------------
 * CCSDS Primary Header
 * CCSDS 133.0-B-2, 6 bytes total.
 *
 *   Bits 15-13: Version (3 bits, always 0b000)
 *   Bit  12:    Type (0=telemetry, 1=command)
 *   Bit  11:    Secondary header flag
 *   Bits 10-0:  APID (11 bits, 0x000-0x7FF)
 *
 *   Bits 15-14: Sequence flags (2 bits)
 *   Bits 13-0:  Sequence count (14 bits)
 *
 *   Bits 15-0:  Packet data length - 1
 * --------------------------------------------------------------------- */
typedef struct __attribute__((packed))
{
    uint16_t version_type_shf_apid;  /* version[3] type[1] shf[1] apid[11] */
    uint16_t seq_flags_count;        /* seq_flags[2] seq_count[14]          */
    uint16_t data_length;            /* packet data length - 1              */
} ccsds_primary_header_t;            /* 6 bytes total                       */

/* -----------------------------------------------------------------------
 * Packet Type APID Assignments (11-bit, 0x000-0x00F range)
 * These identify WHAT kind of data is in the packet.
 * Used in response/telemetry packet headers.
 * SCK-DEV: CCSDS_APID -- Add new packet types here.
 * --------------------------------------------------------------------- */
#define CCSDS_APID_TLM_BEACON       0x001   /* Telemetry beacon           */
#define CCSDS_APID_COMMAND          0x002   /* Uplink command             */
#define CCSDS_APID_CMD_ACK          0x003   /* Command acknowledgement    */
#define CCSDS_APID_GPS_TLM          0x004   /* GPS telemetry              */
#define CCSDS_APID_BARO_TLM         0x005   /* Barometer telemetry        */
#define CCSDS_APID_IMAGE_CHUNK      0x006   /* Image data chunk           */
#define CCSDS_APID_FILE_LIST        0x007   /* File list                  */
#define CCSDS_APID_TEST             0x1FF   /* Reserved / test            */

/* -----------------------------------------------------------------------
 * Board Address APIDs (0x010-0x01F range) -- HWID equivalent
 * These identify WHICH physical board a command is addressed to.
 * Used in command packet headers to route packets to the right board.
 *
 * In OpenLST, HWID was a 2-byte board address baked into firmware.
 * Here the CCSDS APID field serves the same purpose. Packet type APIDs
 * (0x001-0x00F) and board address APIDs (0x010-0x01F) never appear in
 * the same packet field at the same time -- no collision possible.
 *
 * SCK-DEV: CCSDS_APID -- To add a new board:
 *          1. Add SCK_APID_BOARD_* define below
 *          3. C# ground station: add board address to APID dropdown
 * --------------------------------------------------------------------- */
#define SCK_APID_BOARD_GS           0x010   /* Ground station (USB connected) */
#define SCK_APID_BOARD_REMOTE_1     0x011   /* Remote board 1 (CubeSat)       */
#define SCK_APID_BOARD_REMOTE_2     0x012   /* Remote board 2                 */
#define SCK_APID_BOARD_BROADCAST    0x000   /* All boards respond             */

/* Board address set by ground station build tool — do not edit manually. */
#define SCK_APID_THIS_BOARD         0x010   /* GS Board  (APID 0x010) */






























































































































/* -----------------------------------------------------------------------
 * Command Opcodes -- sub-opcode byte in CCSDS_APID_COMMAND data field
 * First byte of the CCSDS user data field for command packets.
 * Equivalent to OpenLST sub-opcodes under opcode 0x20 (PICO_MSG).
 * SCK-DEV: ADD_COMMAND -- Add new command codes here.
 * --------------------------------------------------------------------- */
#define CMD_GET_TELEM               0x01    /* Request telemetry response */
#define CMD_ACK                     0x02    /* Acknowledge / ping         */
#define CMD_REBOOT                  0x03    /* Soft reset                 */
#define CMD_BEACON_CTRL             0x04    /* Enable/disable RF beacon   */


/* Pico payload commands -- forwarded to payload processor over UART.
 * Same workflow as SCK-915 OpenLST PICO_MSG sub-opcodes.
 * Firmware receives these and forwards to Pico via UART2.
 * Pico main.py handles them and responds -- work TBD (separate session).
 * SCK-DEV: ADD_COMMAND -- Add new Pico commands here and in main.py. */
#define CMD_GET_FILES               0x07    /* Request file list from Pico  */
#define CMD_GET_FILE                0x08    /* Request file download        */
#define CMD_GET_CHUNK               0x09    /* Request next file chunk      */
#define CMD_DEL_FILE                0x0A    /* Delete file on Pico          */

/* OAD Over-Air Download commands -- RF image transport.
 * Sent from ground station to remote board to deliver new firmware.
 * SCK-DEV: ADD_COMMAND -- OAD Phase 3 commands. */
#define CMD_OAD_START               0x10    /* Begin OAD: [4B imgSize][2B crc16] */
#define CMD_OAD_CHUNK               0x11    /* Chunk: [4B offset][1B len][data]  */
#define CMD_OAD_END                 0x12    /* Finalize: [4B crc32] verify+reboot*/
#define CMD_OAD_ABORT               0x13    /* Abort OAD session, clear slot     */
#define CMD_OAD_STATUS              0x14    /* Query: returns bytes received      */

/* ── Payload board commands (0x20-0x29) ─────────────────────────────────
 * Bridge: CCSDS opcode 0x2N → CC1352P → ESP sub-opcode 0x0N → Pico
 * [SCK-DEV: ADD_COMMAND] */
#define CMD_PICO_PING               0x20    /* → ESP 0x00 → "PICO:ACK"      */
#define CMD_PICO_TEMP               0x21    /* → ESP 0x01 → "TEMP:xx.xxC"   */
#define CMD_PICO_SNAP               0x22    /* → ESP 0x02 → "SNAP:OK:..."   */
#define CMD_PICO_LIST               0x23    /* → ESP 0x03 → "LIST:..."      */
#define CMD_PICO_INFO               0x24    /* → ESP 0x04 → "INFO:..."      */
#define CMD_PICO_CHUNK              0x25    /* → ESP 0x05 → "CHUNK:..."     */
#define CMD_PICO_DELETE             0x26    /* → ESP 0x06 → "DEL:..."       */
#define CMD_GET_GPS                 0x27    /* → ESP 0x07 → "GPS:..."       */
#define CMD_GET_BARO                0x28    /* → ESP 0x08 → "BARO:..."      */
#define CMD_PICO_BEACON             0x29    /* → ESP 0x09 → "BEACON:ON/OFF" */

/* -----------------------------------------------------------------------
 * Routing Helper Macros
 * Evaluated at compile time -- zero runtime overhead.
 * --------------------------------------------------------------------- */

/* Is this packet addressed to this board (or broadcast)? */
#define CCSDS_IS_FOR_THIS_BOARD(apid) \
    ((apid) == SCK_APID_THIS_BOARD || (apid) == SCK_APID_BOARD_BROADCAST)
/* Is this board the ground station?
 * Used to gate the RF-to-UART forward path in rfTask. */
#define SCK_IS_GS_BOARD \
    (SCK_APID_THIS_BOARD == SCK_APID_BOARD_GS)

/* Is this APID a remote board address (not GS, not broadcast)?
 * Used by GS board to decide whether to forward a command over RF. */
#define CCSDS_IS_REMOTE_BOARD_APID(apid) \
    ((apid) >= SCK_APID_BOARD_REMOTE_1 && (apid) <= SCK_APID_BOARD_REMOTE_2)

/* -----------------------------------------------------------------------
 * Header Field Masks and Shifts
 * --------------------------------------------------------------------- */
#define CCSDS_APID_MASK             0x07FF
#define CCSDS_TYPE_SHIFT            12
#define CCSDS_SHF_SHIFT             11
#define CCSDS_SEQ_COUNT_MASK        0x3FFF
#define CCSDS_SEQ_FLAGS_SHIFT       14

/* Sequence flags */
#define CCSDS_SEQ_UNSEGMENTED       0x3     /* standalone packet */

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

/*
 * ccsds_get_apid()
 * Extract 11-bit APID from primary header.
 * Parameters:
 *   hdr -- pointer to CCSDS primary header
 * Returns: APID value (0x000-0x7FF)
 */
uint16_t ccsds_get_apid(const ccsds_primary_header_t *hdr);

/*
 * ccsds_build_header()
 * Fill a CCSDS primary header struct.
 * Parameters:
 *   hdr       -- pointer to header to fill
 *   apid      -- 11-bit APID
 *   isCmd     -- true for command packet, false for telemetry
 *   seqCount  -- 14-bit sequence counter (caller maintains)
 *   dataLen   -- length of data field in bytes (header writes dataLen-1)
 */
void ccsds_build_header(ccsds_primary_header_t *hdr,
                        uint16_t apid,
                        bool     isCmd,
                        uint16_t seqCount,
                        uint16_t dataLen);

/*
 * ccsds_validate()
 * Basic sanity check on a received CCSDS packet.
 * Parameters:
 *   buf    -- raw packet buffer (including header)
 *   bufLen -- total bytes in buf
 * Returns: true if header version is valid and length field is consistent.
 */
bool ccsds_validate(const uint8_t *buf, uint16_t bufLen);

#endif /* CCSDS_H */
