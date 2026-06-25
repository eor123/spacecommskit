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

/* Driverlib -- SysCtrlSystemReset() for post-OAD reboot */
#include <ti/devices/DeviceFamily.h>
#include DeviceFamily_constructPath(driverlib/sys_ctrl.h)

/* OAD ext flash layout -- ExtImageInfo_t, OAD_EFL_MAGIC, EFL_ADDR_META */
#include "ti/common/cc26xx/oad/ext_flash_layout.h"

/* TI Drivers */
#include <ti/drivers/UART2.h>
/* TI common ext flash -- ext_flash.c and bsp_spi_cc13x2_cc26x2.c added to project */
#include "ti/common/flash/no_rtos/extFlash/ext_flash.h"

/* Beacon enable flag -- defined in main.c, set via CMD_BEACON_CTRL */
extern volatile bool gBeaconEnabled;

/* OAD active flag -- defined in main.c, read by rfTask sleep logic.
 * Set/cleared here alongside sOad.active. */
extern volatile bool gOadActive;

/* Diagnostic error code from oad_flash_stub.c -- set at each failure point
 * in extFlashOpen() so the ACK payload tells us exactly where it failed. */
extern uint8_t extFlashLastError(void);

/* -----------------------------------------------------------------------
 * Static state
 * --------------------------------------------------------------------- */
static UART2_Handle sUartHandle        = NULL;  /* GS link (PAYLOAD_UART idx 0) */
static UART2_Handle sPayloadUartHandle = NULL;  /* Pico link (DEBUG_UART idx 1) */
static uint8_t      sRxBuf[ESP_FRAME_HDR_LEN + ESP_MAX_PAYLOAD];
static uint16_t     sTxSeqCount  = 0;   /* 14-bit outgoing sequence counter */
static bool         sResponseViaRf = false; /* true when responding to RF command */

/* -----------------------------------------------------------------------
 * OAD session state -- Phase 3 RF image transport
 * Only meaningful on remote board (SCK_IS_GS_BOARD == false).
 * --------------------------------------------------------------------- */
#define OAD_CHUNK_SIZE      128             /* bytes per RF chunk          */

/* Ext flash OAD layout:
 *   0x000000 (page 0): ExtImageInfo_t metadata header -- BIM reads this
 *   0x001000 (page 1+): image data -- chunks written here by handle_oad_chunk
 *
 * OAD_SLOT_OFFSET: base of the OAD slot (metadata page start)
 * OAD_IMG_OFFSET:  where image data starts (must be past metadata page)
 * Erase in handle_oad_start covers from OAD_SLOT_OFFSET for imgSize+4KB
 * to clear both the metadata page and all image data pages. */
#define OAD_SLOT_OFFSET     0x000000        /* ext flash OAD slot base     */
#define OAD_IMG_OFFSET      0x001000        /* image data: past metadata   */

typedef struct {
    volatile bool active;     /* OAD session in progress — volatile so rfTask
                               * sleep check sees writes from handle_oad_start */
    uint32_t imgSize;         /* total image size in bytes       */
    uint32_t bytesReceived;   /* bytes written to ext flash      */
    uint16_t crc16Expected;   /* CRC16 from CMD_OAD_START        */
} oad_session_t;

static oad_session_t sOad = { 0 };

/* -----------------------------------------------------------------------
 * Forward declarations (module-private)
 * --------------------------------------------------------------------- */
static void handle_get_telem(uint16_t cmdSeq);
static bool payload_uart_open(void);
static uint16_t pico_send_recv(uint8_t subOpcode, const uint8_t *args,
                               uint8_t argsLen, uint8_t *respBuf,
                               uint16_t respBufSize, uint32_t timeoutMs);
static void handle_pico_cmd(uint16_t cmdSeq, uint8_t picoSub,
                            const uint8_t *args, uint8_t argsLen,
                            uint8_t ccsdsCmd, uint16_t respMax);
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
    static uint8_t  payload[ESP_MAX_PAYLOAD];  /* static -- off the task stack */
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
                     * dataField[1] (after subOpcode): 0x01=enable, 0x00=disable.
                     * gBeaconEnabled is read by rfTask on every loop iteration. */
                    if (dataFieldLen >= 2)
                        gBeaconEnabled = (dataField[1] != 0x00);
                    handle_cmd_ack(seqCount, subOpcode);
                    break;

                case CMD_PICO_PING:
                    handle_pico_cmd(seqCount, 0x00, NULL, 0, CMD_PICO_PING, 64);
                    break;

                case CMD_PICO_TEMP:
                    handle_pico_cmd(seqCount, 0x01, NULL, 0, CMD_PICO_TEMP, 64);
                    break;

                case CMD_PICO_SNAP:
                    handle_pico_cmd(seqCount, 0x02, NULL, 0, CMD_PICO_SNAP, 64);
                    break;

                case CMD_PICO_LIST:
                    handle_pico_cmd(seqCount, 0x03, NULL, 0, CMD_PICO_LIST, 200);
                    break;

                case CMD_PICO_INFO:
                    handle_pico_cmd(seqCount, 0x04,
                                    dataField + 1, (uint8_t)(dataFieldLen - 1),
                                    CMD_PICO_INFO, 64);
                    break;

                case CMD_PICO_CHUNK:
                    handle_pico_cmd(seqCount, 0x05,
                                    dataField + 1, (uint8_t)(dataFieldLen - 1),
                                    CMD_PICO_CHUNK, 220);
                    break;

                case CMD_PICO_DELETE:
                    handle_pico_cmd(seqCount, 0x06,
                                    dataField + 1, (uint8_t)(dataFieldLen - 1),
                                    CMD_PICO_DELETE, 64);
                    break;

                case CMD_GET_GPS:
                    handle_pico_cmd(seqCount, 0x07, NULL, 0, CMD_GET_GPS, 128);
                    break;

                case CMD_GET_BARO:
                    handle_pico_cmd(seqCount, 0x08, NULL, 0, CMD_GET_BARO, 64);
                    break;

                case CMD_PICO_BEACON:
                    handle_pico_cmd(seqCount, 0x09,
                                    dataField + 1, (uint8_t)(dataFieldLen - 1),
                                    CMD_PICO_BEACON, 32);
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
 * Payload UART — Pico bridge
 *
 * Architecture: CC1352P bridges CCSDS (RF/UART) ↔ ESP framing (Pico UART)
 *   GS sends CCSDS command → CC1352P → ESP frame → Pico
 *   Pico responds ESP frame → CC1352P → CCSDS ACK → GS
 *
 * The Pico runs main.py unchanged — it never sees CCSDS.
 * DEBUG_UART (index 1, DIO5 TX / DIO16 RX) is used for the Pico link.
 * Opened lazily on first payload command — floating RX at boot is safe.
 *
 * [SCK-DEV: ADD_COMMAND] — to add a new payload command:
 *   1. Add CMD_* opcode in ccsds.h (0x20-0x2F range)
 *   2. Add case in uart_dispatch_ccsds_packet() calling handle_pico_cmd()
 *   3. Add matching sub-opcode handling in Pico main.py
 * --------------------------------------------------------------------- */
#define PICO_ESP_BYTE0   0x22
#define PICO_ESP_BYTE1   0x69
#define PICO_TIMEOUT_MS  5000

/*
 * payload_uart_open() -- lazy init, safe to call multiple times.
 * Opens DEBUG_UART (index 1, DIO5 TX / DIO16 RX) for Pico communication.
 * Not called at boot — floating RX line won't block startup.
 */
static bool payload_uart_open(void)
{
    if (sPayloadUartHandle != NULL) return true;

    UART2_Params p;
    UART2_Params_init(&p);
    p.baudRate   = SCK_PAYLOAD_UART_BAUD;
    p.dataLength = UART2_DataLen_8;
    p.stopBits   = UART2_StopBits_1;
    p.parityType = UART2_Parity_NONE;
    p.readMode   = UART2_Mode_NONBLOCKING;
    p.writeMode  = UART2_Mode_BLOCKING;
    sPayloadUartHandle = UART2_open(SCK_PAYLOAD_UART_IDX, &p);
    return (sPayloadUartHandle != NULL);
}

/*
 * pico_send_recv() -- send ESP frame to Pico, receive ESP response.
 * Returns response length on success, 0 on timeout or UART error.
 */
static uint16_t pico_send_recv(uint8_t subOpcode,
                                const uint8_t *args,    uint8_t  argsLen,
                                uint8_t       *respBuf, uint16_t respBufSize,
                                uint32_t       timeoutMs)
{
    if (!payload_uart_open()) return 0;

    /* Send ESP frame: [0x22][0x69][len][subOpcode][args...] */
    uint8_t hdr[3] = { PICO_ESP_BYTE0, PICO_ESP_BYTE1,
                        (uint8_t)(1 + argsLen) };
    size_t w = 0;
    if (UART2_write(sPayloadUartHandle, hdr, 3, &w) != UART2_STATUS_SUCCESS)
        return 0;
    uint8_t sub = subOpcode;
    if (UART2_write(sPayloadUartHandle, &sub, 1, &w) != UART2_STATUS_SUCCESS)
        return 0;
    if (argsLen > 0 && args != NULL)
        UART2_write(sPayloadUartHandle, args, argsLen, &w);

    /* Receive ESP response — non-blocking poll at 1ms intervals */
    uint8_t  state = 0;  /* 0=sync0 1=sync1 2=len 3=payload */
    uint8_t  payLen = 0, b = 0;
    uint16_t received = 0;
    size_t   bytesRead = 0;
    uint32_t elapsed = 0;

    while (elapsed < timeoutMs)
    {
        bytesRead = 0;
        UART2_read(sPayloadUartHandle, &b, 1, &bytesRead);
        if (bytesRead == 0) { usleep(1000); elapsed++; continue; }

        switch (state)
        {
            case 0: if (b == PICO_ESP_BYTE0) state = 1; break;
            case 1:
                if      (b == PICO_ESP_BYTE1) state = 2;
                else if (b == PICO_ESP_BYTE0) state = 1;
                else                          state = 0;
                break;
            case 2:
                payLen = b; received = 0;
                state  = (payLen > 0) ? 3 : 0;
                break;
            case 3:
                if (received < respBufSize - 1) respBuf[received] = b;
                received++;
                if (received >= payLen)
                {
                    uint16_t n = (received < respBufSize) ? received
                                                          : respBufSize - 1;
                    respBuf[n] = '\0';
                    return n;
                }
                break;
        }
    }
    return 0;  /* timeout */
}

/*
 * handle_pico_cmd() -- generic payload command handler.
 * Sends ESP frame to Pico with picoSub opcode, wraps response in CCSDS ACK.
 * All payload handlers call this — no per-command stack allocation needed.
 * respMax caps the response buffer size; static storage avoids stack overflow.
 */
static void handle_pico_cmd(uint16_t cmdSeq, uint8_t picoSub,
                              const uint8_t *args, uint8_t argsLen,
                              uint8_t ccsdsCmd, uint16_t respMax)
{
    /* Static buffer — largest needed is 220 bytes for CHUNK response.
     * Safe because uart_task processes one command at a time. */
    static uint8_t resp[220];
    static uint8_t ack[222];

    if (respMax > sizeof(resp)) respMax = sizeof(resp);

    uint16_t len = pico_send_recv(picoSub, args, argsLen,
                                  resp, respMax, PICO_TIMEOUT_MS);
    ack[0] = ccsdsCmd;
    ack[1] = (len > 0) ? 0x00 : 0x01;
    if (len > 0) memcpy(ack + 2, resp, len);
    uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, ack, 2 + len);
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

/* CRC16-CCITT for chunk/image verification.
 * Pass crc=0xFFFF for first call, then pass previous return value to
 * accumulate across multiple buffers (e.g. reading image in chunks). */
static uint16_t crc16_ccitt(const uint8_t *data, uint16_t len, uint16_t crc)
{
    while (len--)
    {
        crc ^= ((uint16_t)*data++) << 8;
        for (int i = 0; i < 8; i++)
            crc = (crc & 0x8000) ? (crc << 1) ^ 0x1021 : (crc << 1);
    }
    return crc;
}

/*
 * oad_session_active()
 * Returns true while an OAD session is in progress.
 * Called by rfTask in main.c to skip the 950ms beacon sleep during OAD,
 * allowing the RX queue to drain fast enough for streaming chunk delivery.
 */
__attribute__((noinline))
bool oad_session_active(void)
{
    return sOad.active;
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

    if (imgSize == 0 || imgSize > 0xC0000) goto done; /* max 768KB -- MX25R8035F is 1MB */

    /* Open ext flash */
    if (!extFlashOpen()) goto done;

    /* Erase OAD slot -- covers metadata page (4KB) + all image data pages.
     * Start at OAD_SLOT_OFFSET (0x000000), length = imgSize + one extra
     * 4KB sector for the metadata page, rounded up to sector boundary. */
    uint32_t eraseLen = (((imgSize + 0x1000) + 0xFFF) / 0x1000) * 0x1000;
    if (!extFlashErase(OAD_SLOT_OFFSET, eraseLen))
    {
        extFlashClose();
        goto done;
    }

    /* Leave flash open for the duration of the OAD session.
     * Closing and reopening on every chunk sends Deep Power-Down and
     * wakes the chip (1ms RDP delay) 50+ times per second — unreliable
     * at streaming speeds.  We close once in handle_oad_end/abort. */

    /* Initialize session */
    sOad.active        = true;
    sOad.imgSize       = imgSize;
    sOad.bytesReceived = 0;
    sOad.crc16Expected = crc16;
    gOadActive         = true;   /* signal rfTask to drop beacon sleep to 1ms */
    status = 0x00;

done:
    /* ACK: [subOpcode][status][flashErrCode][manfId][devId]
     * On failure, flashErrCode tells ground station which step failed:
     *   0x10 = SPI_open() returned NULL
     *   0x20 = RDP spiXfer failed
     *   0x30 = WaitReady timed out after RDP
     *   0x40 = RDID spiXfer failed
     *   0x50 = JEDEC ID mismatch (manfId/devId bytes follow)
     * On success, all three extra bytes are 0x00.
     */
    {
        uint8_t flashErr = (status == 0x00) ? 0x00 : extFlashLastError();
        const ExtFlashInfo_t *fi = extFlashInfo();
        uint8_t ack[5] = {
            CMD_OAD_START,
            status,
            flashErr,
            fi->manfId,   /* actual JEDEC manf byte read (0x00 if SPI_open failed) */
            fi->devId,    /* actual JEDEC dev byte read  (0x00 if SPI_open failed) */
        };
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

    /* DIAGNOSTIC: if imgSize is set but active somehow cleared, re-arm.
     * This should never be needed -- active is set in handle_oad_start
     * and cleared only in handle_oad_end/abort. If this fixes the problem,
     * something is clearing active between those calls. */
    if (!sOad.active && sOad.imgSize > 0)
        sOad.active = true;

    if (!sOad.active)  goto done;
    if (dataLen < 6)   goto done;

    uint32_t offset   = ((uint32_t)data[0] << 24) | ((uint32_t)data[1] << 16) |
                        ((uint32_t)data[2] <<  8) | (uint32_t)data[3];
    uint8_t  chunkLen = data[4];
    const uint8_t *chunkData = data + 5;

    if (chunkLen == 0 || chunkLen > OAD_CHUNK_SIZE) goto done;
    if (dataLen < (uint16_t)(5 + chunkLen))         goto done;
    if (offset + chunkLen > sOad.imgSize)            goto done;

    /* Write chunk to ext flash.
     * Flash should be held open from handle_oad_start.
     * extFlashOpen() is idempotent: returns true immediately if sOpen=true.
     * If sOpen is somehow false it will re-open fully (RDP + JEDEC check). */
    bool openOk = extFlashOpen();
    if (!openOk) goto done;
    bool ok = extFlashWrite(OAD_IMG_OFFSET + offset, chunkLen, chunkData);
    if (!ok) goto done;

    /* Advance high-water mark — idempotent for any retries */
    if (offset + chunkLen > sOad.bytesReceived)
        sOad.bytesReceived = offset + chunkLen;

    /* Streaming OAD: no per-chunk ACK.
     * GS streams all chunks; single CRC verification at CMD_OAD_END.
     * Any response here would arrive at the GS out of order while it is
     * still transmitting and confuse the WaitForReply logic. */
    return;

done:
    /* Only reached on error — send diagnostic NACK.
     * Payload: [subOpcode][0x01][flashErrCode][4B bytesReceived]
     * flashErrCode lets us pinpoint exactly which SPI operation failed. */
    {
        /* Diagnostic NACK: [subOp][0x01][flashErr][offset3][offset2][offset1][offset0]
         * flashErr: 0x00=sOpen was false (write guard), 0x10..0x50=extFlashOpen failed,
         *           0xEE=open succeeded but write failed. */
        uint8_t flashErr = extFlashLastError();
        /* If open succeeded but write failed, encode 0xEE to distinguish */
        if (openOk && !ok) flashErr = 0xEE;
        uint8_t nack[7] = {
            CMD_OAD_CHUNK,
            0x01,
            flashErr,
            (uint8_t)(offset >> 24),
            (uint8_t)(offset >> 16),
            (uint8_t)(offset >>  8),
            (uint8_t)(offset      ),
        };
        uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, nack, sizeof(nack));
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

    /* Read back full image and verify CRC16.
     * Flash is still open from handle_oad_start. */

    uint16_t crc = 0xFFFF;
    uint8_t  readBuf[64];
    uint32_t remaining = sOad.imgSize;
    uint32_t offset    = OAD_IMG_OFFSET;    /* image data starts past metadata page */

    while (remaining > 0)
    {
        uint32_t chunk = (remaining > sizeof(readBuf)) ? sizeof(readBuf) : remaining;
        if (!extFlashRead(offset, chunk, readBuf))
        {
            extFlashClose();
            goto done;
        }
        crc        = crc16_ccitt(readBuf, (uint16_t)chunk, crc);
        offset    += chunk;
        remaining -= chunk;
    }

    if (crc != sOad.crc16Expected)
    {
        extFlashClose();
        goto done;
    }

    /* Write OAD metadata header (ExtImageInfo_t) to ext flash page 0.
     *
     * BIM's ext flash scan (isLastMetaData / checkImagesExtFlash) reads
     * page 0 offset 0 looking for OAD_EFL_MAGIC in imgID[0..7].  If found
     * it reads the full ExtImageInfo_t and checks:
     *   - imgCpStat == NEED_COPY (0xFF) → image needs to be copied
     *   - bimVer == BIM_VER (0x03)
     *   - metaVer == META_VER (0x01)
     *   - crcStat != CRC_INVALID
     *   - extFlAddr → start address of image data in ext flash
     *
     * Layout we write:
     *   Offset 0x000000: ExtImageInfo_t metadata (page 0, 4KB sector)
     *   Offset 0x001000: Image data (immediately after metadata page)
     *
     * Chunks were already written to OAD_IMG_OFFSET (0x001000) by
     * handle_oad_chunk().  Metadata goes to page 0 (0x000000).
     *
     */
    {
        /* Build ExtImageInfo_t metadata struct on stack.
         * Zero-initialise first so reserved/unused fields are 0xFF-safe. */
        ExtImageInfo_t meta;
        memset(&meta, 0xFF, sizeof(meta)); /* 0xFF = erased flash default */

        /* imgID: OAD_EFL_MAGIC identifies this as an ext flash metadata entry.
         * BIM's metadataIDCheck() looks for exactly these 8 bytes. */
        const uint8_t eflMagic[OAD_EFL_MAGIC_SZ] = OAD_EFL_MAGIC;
        memcpy(meta.fixedHdr.imgID, eflMagic, OAD_EFL_MAGIC_SZ);

        /* Core header fields BIM validates */
        meta.fixedHdr.bimVer    = BIM_VER;                  /* 0x03 */
        meta.fixedHdr.metaVer   = META_VER;                 /* 0x01 */
        meta.fixedHdr.imgCpStat = NEED_COPY;                /* 0xFF -- triggers BIM copy */
        meta.fixedHdr.crcStat   = CRC_VALID;                /* 0xFE -- skip CRC recheck */
        meta.fixedHdr.imgType   = OAD_IMG_TYPE_APPSTACKLIB; /* 0x07 */
        meta.fixedHdr.imgNo     = 0x00;
        meta.fixedHdr.imgVld    = 0xFFFFFFFF;
        meta.fixedHdr.len       = sOad.imgSize;
        meta.fixedHdr.prgEntry  = 0xFFFFFFFF; /* BIM reads from internal flash after copy */
        meta.fixedHdr.techType  = OAD_WIRELESS_TECH_PROPRF; /* 0xFFBF */
        meta.fixedHdr.imgEndAddr = 0x00004000 + sOad.imgSize - 1;

        /* extFlAddr: where image data lives in ext flash.
         * Image data starts at page 1 (0x001000), right after metadata page. */
        meta.extFlAddr = OAD_IMG_OFFSET;

        /* counter: unused, leave as 0xFFFFFFFF */
        meta.counter = 0xFFFFFFFF;

        /* Write metadata to page 0 (sector must already be erased from
         * handle_oad_start -- we erased from offset 0 covering page 0). */
        bool metaOk = extFlashWrite(EFL_ADDR_META, sizeof(ExtImageInfo_t),
                                    (const uint8_t *)&meta);
        if (!metaOk)
        {
            extFlashClose();
            goto done;
        }
    }

    extFlashClose();

    status = 0x00;
    sOad.active = false;
    gOadActive  = false;

done:
    {
        uint8_t ack[2] = { CMD_OAD_END, status };
        uart_send_ccsds(CCSDS_APID_CMD_ACK, false, cmdSeq, ack, sizeof(ack));
    }

    if (status == 0x00)
    {
        /* Delay 500ms so ACK packet is fully transmitted before reset.
         * At 921600 baud an ESP-framed 8-byte packet takes ~0.1ms --
         * 500ms gives the RF layer time to TX the response before reboot. */
        Task_sleep(500);
        SysCtrlSystemReset();
    }
}

/* -----------------------------------------------------------------------
 * handle_oad_abort()
 * CMD_OAD_ABORT -- clear session state, no ext flash changes needed.
 * --------------------------------------------------------------------- */
static void handle_oad_abort(uint16_t cmdSeq)
{
    sOad.active        = false;
    gOadActive         = false;
    sOad.bytesReceived = 0;
    sOad.imgSize       = 0;

    /* Close flash if OAD session was holding it open */
    extFlashClose();

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
