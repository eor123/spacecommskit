/*
 * uart.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   UART payload interface. ESP framing, CCSDS command dispatch,
 *   and RF-to-UART forwarding for ground station board role.
 *
 * SCK-DEV: PAYLOAD_UART -- DIO12 (RX), DIO13 (TX), 921600 8N1.
 *          Same ESP framing as SCK-915 -- Pico firmware compatible.
 * SCK-DEV: ADD_COMMAND  -- To add a new command:
 *          1. Add CMD_* sub-opcode define in ccsds.h
 *          2. Add a case in uart_dispatch_ccsds_packet() in uart.c
 *          3. Implement the handler function in uart.c
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.4
 */

#ifndef UART_H
#define UART_H

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stdbool.h>

/* TI-RTOS */
#include <ti/sysbios/knl/Task.h>

/* -----------------------------------------------------------------------
 * UART Configuration
 *
 * GS link:     PAYLOAD_UART (index 0, UART1) TX=DIO13 RX=DIO12
 *              XDS110 backchannel — connects to C# ground station
 * Pico link:   DEBUG_UART   (index 1, UART0) TX=DIO5  RX=DIO16
 *              Payload board interface — connects to Pico UART0
 *
 * LaunchPad wiring (CC1352P → Pico):
 *   DIO5  (pin 10) TX → Pico GPIO1 (UART0 RX, pin 2)
 *   DIO16 (pin 32) RX → Pico GPIO0 (UART0 TX, pin 1)
 *   GND            →   Pico GND
 *
 * No SysConfig changes needed — DEBUG_UART already on DIO5/16.
 * Pico UART opened lazily on first payload command — not at boot.
 * [SCK-DEV: PAYLOAD_UART]
 * --------------------------------------------------------------------- */
#define SCK_UART_IDX          0    /* PAYLOAD_UART — GS↔CC1352P (DIO12/13)  */
#define SCK_PAYLOAD_UART_IDX  1    /* DEBUG_UART   — CC1352P↔Pico (DIO5/16) */
#define SCK_UART_BAUD         921600
#define SCK_PAYLOAD_UART_BAUD 115200  /* Match Pico main.py baudrate         */

/* -----------------------------------------------------------------------
 * ESP Frame Constants
 * Same framing as SCK-915 -- ground station and Pico compatible.
 *
 * Frame format:
 *   [0x22] [0x69] [LEN_HI] [LEN_LO] [payload...]
 *   Sync bytes: 0x22 0x69
 *   Length: 2-byte big-endian, payload bytes only (not including header)
 * --------------------------------------------------------------------- */
#define ESP_FRAME_BYTE0     0x22
#define ESP_FRAME_BYTE1     0x69
#define ESP_FRAME_HDR_LEN   4               /* 2 sync + 2 length bytes    */
#define ESP_MAX_PAYLOAD     256             /* max CCSDS packet size       */

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

/*
 * uart_task()
 * UART receive task entry point. Called by RTOS at startup.
 * Initializes UART with retry, then loops receiving and dispatching
 * ESP-framed CCSDS commands from ground station or Pico payload.
 * Stack: SCK_TASK_STACK_UART (512 bytes)
 */
void uart_task(uintptr_t arg0, uintptr_t arg1);

/*
 * uart_init()
 * Open UART2 driver with SCK payload parameters (921600 8N1).
 * Called internally by uart_task() with retry loop.
 * Returns: true on success, false if UART2_open() fails.
 */
bool uart_init(void);

/*
 * uart_send()
 * Send a payload over UART0 wrapped in ESP framing.
 * Parameters:
 *   payload -- data bytes to send
 *   len     -- number of bytes
 * Returns: true on success, false on UART write error.
 */
bool uart_send(const uint8_t *payload, uint16_t len);

/*
 * uart_receive()
 * Blocking read of one ESP-framed packet from UART0.
 * Validates sync bytes and length field before returning payload.
 * Parameters:
 *   buf    -- buffer to write payload into
 *   maxLen -- size of buf
 *   rxLen  -- actual payload bytes written (output)
 * Returns: true if valid frame received, false on sync/length error.
 */
bool uart_receive(uint8_t *buf, uint16_t maxLen, uint16_t *rxLen);

/*
 * uart_dispatch_ccsds_packet()
 * Parse and dispatch a CCSDS packet to the appropriate command handler.
 * Called by uart_task() for packets arriving via UART, and by rfTask()
 * for packets arriving over RF addressed to this board.
 * Shared dispatch path -- keeps command handling in one place.
 * Parameters:
 *   buf   -- CCSDS packet buffer (header + data field, ESP framing removed)
 *   len   -- total packet length in bytes
 *   viaRf -- true if packet arrived over RF; response goes back via RF.
 *            false if packet arrived via UART; response goes via UART.
 */
void uart_dispatch_ccsds_packet(const uint8_t *buf, uint16_t len, bool viaRf);

/*
 * uart_send_rf()
 * Send a CCSDS packet back over RF. Used by remote board to respond
 * to commands received over RF. Only meaningful on remote board.
 * Parameters:
 *   buf -- CCSDS packet buffer
 *   len -- packet length in bytes
 * Returns: true on successful TX, false on error.
 */
bool uart_send_rf(const uint8_t *buf, uint16_t len);

/*
 * uart_forward_rf_packet()
 * Forward a packet received over RF back to the ground station via UART.
 * Only meaningful on the GS board (SCK_IS_GS_BOARD == true).
 * Called by rfTask() when an RF response arrives addressed to the GS
 * or when routing a remote board response back to C#.
 * Parameters:
 *   buf -- CCSDS packet buffer (as received over RF)
 *   len -- packet length in bytes
 * Returns: true on successful UART send, false on error.
 */
bool uart_forward_rf_packet(const uint8_t *buf, uint16_t len);

#endif /* UART_H */
