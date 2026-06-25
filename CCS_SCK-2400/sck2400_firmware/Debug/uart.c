/*
 * uart.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   UART payload interface implementation. Handles ESP framing,
 *   CCSDS command receive from ground station or Pico payload,
 *   command dispatch by sub-opcode, and CCSDS response transmission.
 *   Also provides RF-to-UART forwarding for the GS board role.
 *
 * Command flow (UART path -- GS board or direct USB):
 *   Ground station → ESP frame → UART0 → uart_receive()
 *   → uart_dispatch_ccsds_packet() → handler → uart_send_ccsds()
 *   → ESP frame → UART0 → Ground station
 *
 * Command flow (RF path -- remote board):
 *   GS board TX → RF → remote board radio_receive()
 *   → rfTask() → uart_dispatch_ccsds_packet() → handler
 *   → radio_transmit() → RF → GS board → uart_forward_rf_packet()
 *   → ESP frame → UART0 → Ground station
 *
 * SCK-DEV: PAYLOAD_UART -- This is the interface between the CC2652P
 *          and the payload processor (Pico/MicroPython) AND the ground
 *          station (via the GS board UART bridge). All data in and out
 *          goes through ESP framing on UART0.
 * SCK-DEV: ADD_COMMAND  -- To add a new command:
 *          1. Add CMD_* sub-opcode define in ccsds.h
 *          2. Add a case in uart_dispatch_ccsds_packet() below
 *          3. Implement the handler function
 *          4. Add matching APID in ccsds.h if new packet type needed
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.4
 */

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include "uart.h"
#include "ccsds.h"
#include "telemetry.h"
#include "main.h"
#include "radio.h"

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

/* TI-RTOS */
#include <ti/sysbios/knl/Task.h>

/* TI Drivers */
#include <ti/drivers/UART2.h>
/* TI common ext flash -- ext_flash.c and bsp_spi_cc13x2_cc26x2.c added to project */
#include "ti/common/flash/no_rtos/extFlash/ext_flash.h"

/* -----------------------------------------------------------------------
 * Static state
 * --------------------------------------------------------------------- */
static UART2_Handle sUartHandle  = NULL;
static uint8_t      sRxBuf[ESP_FRAME_HDR_LEN + ESP_MAX_PAYLOAD];
static uint16_t     sTxSeqCount  = 0;   /* 14-bit outgoing sequence counter */
static bool         sResponseViaRf = false; /* true when responding to RF command */

/* -----------------------------------------------------------------------
 * OAD session state -- Phase 3 RF image transport
 * Only meaningful on remote board (SCK_IS_GS_BOARD == false).
 * --------------------------------------------------------------------- */
#define OAD_CHUNK_SIZE      128             /* bytes per RF chunk          */
#define OAD_SLOT_OFFSET     0               /* ext flash offset for OAD    */

typedef struct {
    bool     active;          /* OAD session in progress        */
    uint32_t imgSize;         /* total image size in bytes       */
    uint32_t bytesReceived;   /* bytes written to ext flash      */
    uint16_t crc16Expected;   /* CRC16 from CMD_OAD_START        */
} oad_session_t;

static oad_session_t sOad = { 0 };

/* -----------------------------------------------------------------------
 * Forward declarations (module-private)
 * --------------------------------------------------------------------- */
static void handle_get_telem(uint16_t cmdSeq);
static void handle_cmd_ack(uint16_t cmdSeq, uint8_t subOpcode);
static void handle_oad_start(uint16_t cmdSeq, const uint8_t *data, uint16_t dataLen);
static void handle_oad_chunk(uint16_t cmdSeq, const uint8_t *data, uint16_t dataLen);
static void handle_oad_end(uint16_t cmdSeq, const uint8_t *data, uint16_t dataLen);
static void handle_oad_abort(uint16_t cmdSeq);
static void handle_oad_status(uint16_t cmdSeq);
static bool uart_send_ccsds(uint16_t apid, bool isCmd,
                             uint16_t seqCount,
                             const uint8_t *data, uint16_t dataLen);

/* -----------------------------------------------------------------------
 * uart_init()
 * --------------------------------------------------------------------- */
bool uart_init(void)
{
    UART2_Params uartParams;
    UART2_Params_init(&uartParams);

    /* SCK-DEV: PAYLOAD_UART -- 921600 8N1, no flow control.
     * DIO12 (RX) and DIO13 (TX) set in sck2400.syscfg. */
    uartParams.baudRate   = SCK_UART_BAUD;
    uartParams.dataLength = UART2_DataLen_8;
    uartParams.stopBits   = UART2_StopBits_1;
    uartParams.parityType = UART2_Parity_NONE;
    uartParams.readMode   = UART2_Mode_BLOCKING;
    uartParams.writeMode  = UART2_Mode_BLOCKING;

    sUartHandle = UART2_open(SCK_UART_IDX, &uartParams);
    return (sUartHandle != NULL);
}

/* -----------------------------------------------------------------------
 * uart_task()
 *
 * Purpose:
 *   Main UART receive loop. Initializes UART with retry, then loops
 *   forever reading ESP-framed CCSDS packets and dispatching commands.
 *
 *   On the GS board: receives commands from C# ground station via USB,
 *   routes them to this board's command handler or forwards over RF.
 *   Routing decision is made in rfTask() after radio_transmit() --
 *   not here. uart_task() only handles the UART receive path.
 *
 * Stack: SCK_TASK_STACK_UART (512 bytes)
 *
 * SCK-DEV: ADD_COMMAND -- Dispatch logic is in uart_dispatch_ccsds_packet().
 * --------------------------------------------------------------------- */
void uart_task(uintptr_t arg0, uintptr_t arg1)
{
    uint8_t  payload[ESP_MAX_PAYLOAD];
    uint16_t payloadLen = 0;

    /* Initialize UART -- retry on failure.
     * DIO12/13 conflict on LaunchPad when nothing is connected.
     * Resolves when GS board or Pico is attached. */
    while (!uart_init())
    {
        Task_sleep(500);
    }

    while (1)
    {
        if (uart_receive(payload, sizeof(payload), &payloadLen))
        {
            /* Update UART RX counter for telemetry */
            telemetry_update_uart_rx(payloadLen);

            /* Dispatch CCSDS command -- viaRf=false, response goes via UART */
            uart_dispatch_ccsds_packet(payload, payloadLen, false);
        }
    }
}

/* -----------------------------------------------------------------------
 * uart_dispatch_ccsds_packet()
 *
 * Purpose:
 *   Parse CCSDS header from received packet and dispatch to handler.
 *   Public function -- called from both uart_task() (UART path) and
 *   rfTask() (RF path) so command handling lives in one place.
 *
 *   The packet is the raw CCSDS data (header + data field), already
 *   stripped of ESP framing (UART path) or RF queue overhead (RF path).
 *
 *   For APID_COMMAND packets the first byte of the data field is the
 *   sub-opcode identifying the specific command -- same concept as
 *   SCK-915 opcode 0x20 (PICO_MSG) with sub-opcodes.
 *
 *   Note on board addressing: the routing decision (is this packet for
 *   this board, or should it be forwarded over RF?) is made BEFORE this
 *   function is called. rfTask() checks CCSDS_IS_FOR_THIS_BOARD(apid)
 *   and only calls uart_dispatch_ccsds_packet() if the packet is
 *   addressed to this board. UART path commands are always dispatched
 *   locally on the GS board.
 *
 * SCK-DEV: ADD_COMMAND -- Add new CMD_* sub-opcode cases here.
 * --------------------------------------------------------------------- */
void uart_dispatch_ccsds_packet(const uint8_t *buf, uint16_t len, bool viaRf)
{
    if (buf == NULL || len < sizeof(ccsds_primary_header_t))
    {
        return;
    }

    if (!ccsds_validate(buf, len))
    {
        return;
    }

    /* Set response path flag -- handlers call uart_send_ccsds() which
     * checks this flag to route response via RF or UART. */
    sResponseViaRf = viaRf;

    const ccsds_primary_header_t *hdr = (const ccsds_primary_header_t *)buf;
    uint16_t apid     = ccsds_get_apid(hdr);
    uint16_t seqCount = (((uint16_t)buf[2] << 8) | buf[3])
                        & CCSDS_SEQ_COUNT_MASK;

    /* User data field starts after 6-byte CCSDS primary header */
    const uint8_t *dataField    = buf + sizeof(ccsds_primary_header_t);
    uint16_t       dataFieldLen = len - sizeof(ccsds_primary_header_t);

    switch (apid)
    {
        /* Board address APIDs (0x010-0x01F) -- HWID equivalent.
         * Routing logic:
         *   GS board + own APID (0x010)   → dispatch locally
         *   GS board + remote APID        → forward over RF, do NOT dispatch
         *   Remote board + own APID       → dispatch locally (rfTask pre-filtered)
         *   CCSDS_APID_COMMAND (0x002)    → dispatch locally (legacy path)
         * SCK-DEV: CCSDS_APID -- Add new board address cases here if
         *          the board address range expands beyond 0x012. */
#if SCK_IS_GS_BOARD
        case SCK_APID_BOARD_REMOTE_1:
        case SCK_APID_BOARD_REMOTE_2:
            /* GS board received a command addressed to a remote board.
             * Forward over RF -- do not dispatch locally. */
            rf_forward_enqueue(buf, len);
            return;
#endif

        case SCK_APID_BOARD_GS:
#if !SCK_IS_GS_BOARD
        case SCK_APID_BOARD_REMOTE_1:
        case SCK_APID_BOARD_REMOTE_2:
#endif
        case CCSDS_APID_COMMAND:
        {
            if (apid != CCSDS_APID_COMMAND &&
                apid != SCK_APID_BOARD_BROADCAST &&
                apid != SCK_APID_THIS_BOARD)
            {
                return;
            }
            if (dataFieldLen < 1) return;
            uint8_t subOpcode = dataField[0];

            switch (subOpcode)
            {
                case CMD_GET_TELEM:
                    /* Ground station requesting telemetry.
                     * Respond with APID_TLM_BEACON + sck_telemetry_t. */
                    handle_get_telem(seqCount);
                    break;

                case CMD_REBOOT:
                    /* Acknowledge then reset.
                     * SCK-DEV: Add watchdog reset trigger for production. */
                    handle_cmd_ack(seqCount, subOpcode);
                    /* TODO: watchdog reset */
                    break;

                case CMD_BEACON_CTRL:
                    /* Enable/disable autonomous RF beacon.
                     * dataField[1]: 0x01=enable, 0x00=disable.
                     * TODO: set beacon enable flag read by rfTask. */
                    handle_cmd_ack(seqCount, subOpcode);
                    break;

                case CMD_GET_GPS:
                    /* Forward GPS request to Pico.
                     * TODO: implement Pico forward path. */
                    handle_cmd_ack(seqCount, subOpcode);
                    break;

                case CMD_GET_BARO:
                    /* Forward baro request to Pico.
                     * TODO: implement Pico forward path. */
                    handle_cmd_ack(seqCount, subOpcode);
                    break;

                case CMD_OAD_START:
                    handle_oad_start(seqCount, dataField + 1, dataFieldLen - 1);
                    break;

                case CMD_OAD_CHUNK:
                    handle_oad_chunk(seqCount, dataField + 1, dataFieldLen - 1);
                    break;

                case CMD_OAD_END:
                    handle_oad_end(seqCount, dataField + 1, dataFieldLen - 1);
                    break;

                case CMD_OAD_ABORT:
                    handle_oad_abort(seqCount);
                    break;

                case CMD_OAD_STATUS:
                    handle_oad_status(seqCount);
                    break;

                default:
                    /* Unknown sub-opcode -- ACK to prevent GS timeout.
                     * SCK-DEV: ADD_COMMAND -- Add new cases above here. */
                    handle_cmd_ack(seqCount, subOpcode);
                    break;
            }
            break;
        }

        default:
            /* Unknown APID -- discard silently.
             * On the GS board, remote board APIDs (0x011, 0x012) are
             * handled above via rf_forward_enqueue() before reaching here.
             * On the remote board, rfTask() pre-filters packets so only
             * packets addressed to this board arrive here. */
            break;
    }
}

/* -----------------------------------------------------------------------
 * uart_forward_rf_packet()
 *
 * Purpose:
 *   Forward a packet received over RF back to the ground station via
 *   UART0, wrapped in ESP framing. Only called on the GS board.
 *
 *   This closes the RF command routing loop:
 *     C# → USB → GS UART → RF TX → Remote board
 *     Remote board → RF TX → GS RF RX → uart_forward_rf_packet()
 *     → GS UART → USB → C#
 *
 *   The C# ground station sees the response exactly as if the remote
 *   board were connected directly via USB -- same CCSDS packet format,
 *   same ESP framing, same APID in the response header.
 *
 * Parameters:
 *   buf -- CCSDS packet as received from RF queue (no ESP framing)
 *   len -- packet length in bytes
 * Returns: true on successful UART send, false on error.
 * --------------------------------------------------------------------- */
bool uart_forward_rf_packet(const uint8_t *buf, uint16_t len)
{
    if (buf == NULL || len == 0)
    {
        return false;
    }
    return uart_send(buf, len);
}

/* -----------------------------------------------------------------------
 * uart_send_rf()
 * Send a CCSDS packet back over RF.
 * Used by remote board to respond to commands received via RF.
 * --------------------------------------------------------------------- */
bool uart_send_rf(const uint8_t *buf, uint16_t len)
{
    if (buf == NULL || len == 0) return false;
    return radio_transmit(buf, len);
}

/* -----------------------------------------------------------------------
 * handle_get_telem()
 *
 * Purpose:
 *   Collect current telemetry and send APID_TLM_BEACON response.
 *   Payload is sck_telemetry_t (32 bytes, little-endian) matching
 *   CcsdsProtocol.ParseTelem() in C# ground station byte-for-byte.
 * --------------------------------------------------------------------- */
static void handle_get_telem(uint16_t cmdSeq)
{
    sck_telemetry_t telem;
    telemetry_collect(&telem);
    uart_send_ccsds(CCSDS_APID_TLM_BEACON, false, cmdSeq,
                    (const uint8_t *)&telem, sizeof(sck_telemetry_t));
}

/* -----------------------------------------------------------------------
 * handle_cmd_ack()
 *
 * Purpose:
 *   Send APID_CMD_ACK to acknowledge a command.
 *   Payload is 1 byte: the sub-opcode being acknowledged.
 * --------------------------------------------------------------------- */
static void handle_cmd_ack(uint16_t cmdSeq, uint8_t subOpcode)
{
    uint8_t ackPayload[1] = { subOpcode };
    uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq,
                    ackPayload, sizeof(ackPayload));
}

/* -----------------------------------------------------------------------
 * uart_send_ccsds()
 *
 * Purpose:
 *   Build a CCSDS packet and send it ESP-framed over UART0.
 *   Internal send path used by all command handlers.
 * --------------------------------------------------------------------- */
static bool uart_send_ccsds(uint16_t apid, bool isCmd,
                             uint16_t seqCount,
                             const uint8_t *data, uint16_t dataLen)
{
    /* Static buffer -- keeps 262 bytes off the task stack */
    static uint8_t  pktBuf[sizeof(ccsds_primary_header_t) + ESP_MAX_PAYLOAD];
    uint16_t pktLen = sizeof(ccsds_primary_header_t) + dataLen;

    if (dataLen > ESP_MAX_PAYLOAD) return false;

    sTxSeqCount = (sTxSeqCount + 1) & CCSDS_SEQ_COUNT_MASK;

    ccsds_build_header((ccsds_primary_header_t *)pktBuf,
                       apid, isCmd, seqCount, dataLen);

    if (data != NULL && dataLen > 0)
    {
        memcpy(pktBuf + sizeof(ccsds_primary_header_t), data, dataLen);
    }

    /* Route response via RF (remote board responding to RF command)
     * or via UART (GS board responding to UART command). */
    if (sResponseViaRf)
    {
        return radio_transmit(pktBuf, pktLen);
    }
    return uart_send(pktBuf, pktLen);
}

/* -----------------------------------------------------------------------
 * uart_send()
 * --------------------------------------------------------------------- */
bool uart_send(const uint8_t *payload, uint16_t len)
{
    if (sUartHandle == NULL || payload == NULL || len == 0)
    {
        return false;
    }

    uint8_t hdr[ESP_FRAME_HDR_LEN];
    hdr[0] = ESP_FRAME_BYTE0;
    hdr[1] = ESP_FRAME_BYTE1;
    hdr[2] = (uint8_t)((len >> 8) & 0xFF);
    hdr[3] = (uint8_t)(len & 0xFF);

    size_t       written = 0;
    int_fast16_t ret;

    ret = UART2_write(sUartHandle, hdr, ESP_FRAME_HDR_LEN, &written);
    if (ret != UART2_STATUS_SUCCESS) return false;

    ret = UART2_write(sUartHandle, payload, len, &written);
    return (ret == UART2_STATUS_SUCCESS);
}

/* -----------------------------------------------------------------------
 * uart_receive()
 * --------------------------------------------------------------------- */
bool uart_receive(uint8_t *buf, uint16_t maxLen, uint16_t *rxLen)
{
    if (sUartHandle == NULL || buf == NULL || rxLen == NULL)
    {
        return false;
    }

    size_t       bytesRead = 0;
    int_fast16_t ret;

    /* Read 4-byte ESP header */
    ret = UART2_read(sUartHandle, sRxBuf, ESP_FRAME_HDR_LEN, &bytesRead);
    if (ret != UART2_STATUS_SUCCESS || bytesRead != ESP_FRAME_HDR_LEN)
    {
        return false;
    }

    /* Validate sync bytes */
    if (sRxBuf[0] != ESP_FRAME_BYTE0 || sRxBuf[1] != ESP_FRAME_BYTE1)
    {
        return false;
    }

    /* Extract 2-byte big-endian payload length */
    uint16_t payLen = ((uint16_t)sRxBuf[2] << 8) | sRxBuf[3];
    if (payLen == 0 || payLen > maxLen || payLen > ESP_MAX_PAYLOAD)
    {
        return false;
    }

    /* Read payload */
    ret = UART2_read(sUartHandle, buf, payLen, &bytesRead);
    if (ret != UART2_STATUS_SUCCESS || bytesRead != payLen)
    {
        return false;
    }

    *rxLen = payLen;
    return true;
}

/* -----------------------------------------------------------------------
 * OAD Handlers -- Phase 3 RF Image Transport
 *
 * Flow:
 *   GS sends CMD_OAD_START  → remote erases ext flash OAD slot
 *   GS sends CMD_OAD_CHUNK  → remote writes 128-byte chunk to ext flash
 *     (repeat for all chunks)
 *   GS sends CMD_OAD_END    → remote verifies CRC, sets BIM copy flag,
 *                             sends ACK, then reboots
 *   BIM on reboot            → finds valid OAD image, copies to internal
 *                             flash, boots new firmware
 *
 * Ext flash OAD slot layout:
 *   Offset 0x000000: OAD image metadata header (from oad_image_header.h)
 *   Offset 0x000000+: image data (firmware hex binary)
 *
 * All handlers send ACK with status byte:
 *   0x00 = success
 *   0x01 = error (flash open failed, write failed, etc.)
 * --------------------------------------------------------------------- */

/* CRC16-CCITT for chunk verification */
static uint16_t crc16_ccitt(const uint8_t *data, uint16_t len)
{
    uint16_t crc = 0xFFFF;
    while (len--)
    {
        crc ^= ((uint16_t)*data++) << 8;
        for (int i = 0; i < 8; i++)
            crc = (crc & 0x8000) ? (crc << 1) ^ 0x1021 : (crc << 1);
    }
    return crc;
}

/* -----------------------------------------------------------------------
 * handle_oad_start()
 * CMD_OAD_START data: [4B imgSize BE][2B crc16 BE]
 * Erases the OAD slot in ext flash and prepares session state.
 * --------------------------------------------------------------------- */
static void handle_oad_start(uint16_t cmdSeq,
                              const uint8_t *data, uint16_t dataLen)
{
    uint8_t status = 0x01; /* default: error */

    if (dataLen < 6) goto done;

    uint32_t imgSize = ((uint32_t)data[0] << 24) | ((uint32_t)data[1] << 16) |
                       ((uint32_t)data[2] <<  8) | (uint32_t)data[3];
    uint16_t crc16   = ((uint16_t)data[4] << 8) | data[5];

    if (imgSize == 0 || imgSize > 0x50000) goto done; /* max ~320KB */

    /* Open ext flash */
    if (!extFlashOpen()) goto done;

    /* Erase OAD slot -- round up to sector boundary */
    uint32_t eraseLen = ((imgSize + 0xFFF) / 0x1000) * 0x1000;
    if (!extFlashErase(OAD_SLOT_OFFSET, eraseLen))
    {
        extFlashClose();
        goto done;
    }

    extFlashClose();

    /* Initialize session */
    sOad.active        = true;
    sOad.imgSize       = imgSize;
    sOad.bytesReceived = 0;
    sOad.crc16Expected = crc16;
    status = 0x00;

done:
    /* ACK: [subOpcode][status] */
    {
        uint8_t ack[2] = { CMD_OAD_START, status };
        uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, ack, sizeof(ack));
    }
}

/* -----------------------------------------------------------------------
 * handle_oad_chunk()
 * CMD_OAD_CHUNK data: [4B offset BE][1B dataLen][data...]
 * Writes one chunk to ext flash at the given offset.
 * --------------------------------------------------------------------- */
static void handle_oad_chunk(uint16_t cmdSeq,
                              const uint8_t *data, uint16_t dataLen)
{
    uint8_t status = 0x01;

    if (!sOad.active)  goto done;
    if (dataLen < 6)   goto done;

    uint32_t offset   = ((uint32_t)data[0] << 24) | ((uint32_t)data[1] << 16) |
                        ((uint32_t)data[2] <<  8) | (uint32_t)data[3];
    uint8_t  chunkLen = data[4];
    const uint8_t *chunkData = data + 5;

    if (chunkLen == 0 || chunkLen > OAD_CHUNK_SIZE) goto done;
    if (dataLen < (uint16_t)(5 + chunkLen))         goto done;
    if (offset + chunkLen > sOad.imgSize)            goto done;

    /* Write chunk to ext flash */
    if (!extFlashOpen()) goto done;

    bool ok = extFlashWrite(OAD_SLOT_OFFSET + offset, chunkLen, chunkData);
    extFlashClose();

    if (!ok) goto done;

    sOad.bytesReceived += chunkLen;
    status = 0x00;

done:
    /* ACK: [subOpcode][status][4B offset echo] so GS knows which chunk landed */
    {
        uint8_t ack[6] = {
            CMD_OAD_CHUNK, status,
            (uint8_t)(sOad.bytesReceived >> 24),
            (uint8_t)(sOad.bytesReceived >> 16),
            (uint8_t)(sOad.bytesReceived >>  8),
            (uint8_t)(sOad.bytesReceived      ),
        };
        uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, ack, sizeof(ack));
    }
}

/* -----------------------------------------------------------------------
 * handle_oad_end()
 * CMD_OAD_END data: [4B crc32 BE]
 * Verifies full image, writes OAD metadata to signal BIM, then reboots.
 * --------------------------------------------------------------------- */
static void handle_oad_end(uint16_t cmdSeq,
                            const uint8_t *data, uint16_t dataLen)
{
    uint8_t status = 0x01;

    if (!sOad.active) goto done;
    if (dataLen < 4)  goto done;

    /* Verify we received the full image */
    if (sOad.bytesReceived != sOad.imgSize) goto done;

    /* Read back full image and verify CRC16 */
    if (!extFlashOpen()) goto done;

    uint16_t crc = 0xFFFF;
    uint8_t  readBuf[64];
    uint32_t remaining = sOad.imgSize;
    uint32_t offset    = OAD_SLOT_OFFSET;

    while (remaining > 0)
    {
        uint32_t chunk = (remaining > sizeof(readBuf)) ? sizeof(readBuf) : remaining;
        if (!extFlashRead(offset, chunk, readBuf))
        {
            extFlashClose();
            goto done;
        }
        crc        = crc16_ccitt(readBuf, (uint16_t)chunk);
        offset    += chunk;
        remaining -= chunk;
    }

    if (crc != sOad.crc16Expected)
    {
        extFlashClose();
        goto done;
    }

    /* Write OAD metadata header to ext flash at slot offset.
     * Sets imgCpStat = 0xFF (DEFAULT) which tells BIM to copy this image.
     * BIM checks OAD_EXTFL_ID_VAL and imgCpStat to decide whether to copy. */
    /* TODO: write proper OAD ext flash metadata header here
     * For now just set a flag byte at offset 0 that BIM can check.
     * Full metadata write requires including oad_image_header.h -- Phase 3b. */

    extFlashClose();

    status = 0x00;
    sOad.active = false;

done:
    {
        uint8_t ack[2] = { CMD_OAD_END, status };
        uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, ack, sizeof(ack));
    }

    if (status == 0x00)
    {
        /* Brief delay so ACK can be transmitted before reboot */
        Task_sleep(500);
        /* TODO: trigger watchdog or NVIC reset */
        /* SysCtrlSystemReset(); */
    }
}

/* -----------------------------------------------------------------------
 * handle_oad_abort()
 * CMD_OAD_ABORT -- clear session state, no ext flash changes needed.
 * --------------------------------------------------------------------- */
static void handle_oad_abort(uint16_t cmdSeq)
{
    sOad.active        = false;
    sOad.bytesReceived = 0;
    sOad.imgSize       = 0;

    uint8_t ack[2] = { CMD_OAD_ABORT, 0x00 };
    uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, ack, sizeof(ack));
}

/* -----------------------------------------------------------------------
 * handle_oad_status()
 * CMD_OAD_STATUS -- returns current OAD session progress.
 * Response: [1B active][4B bytesReceived][4B imgSize]
 * --------------------------------------------------------------------- */
static void handle_oad_status(uint16_t cmdSeq)
{
    uint8_t resp[9] = {
        CMD_OAD_STATUS,
        sOad.active ? 0x01 : 0x00,
        (uint8_t)(sOad.bytesReceived >> 24),
        (uint8_t)(sOad.bytesReceived >> 16),
        (uint8_t)(sOad.bytesReceived >>  8),
        (uint8_t)(sOad.bytesReceived      ),
        (uint8_t)(sOad.imgSize >> 24),
        (uint8_t)(sOad.imgSize >> 16),
        (uint8_t)(sOad.imgSize >>  8),
    };
    uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, resp, sizeof(resp));
}
