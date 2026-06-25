/*
 * telemetry.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   Telemetry data collection and packet building.
 *   Reads CC2652P internal sensors (die temperature, supply voltage),
 *   maintains link stats (RSSI, packets good/sent), and builds the
 *   CCSDS telemetry beacon payload sent over RF and UART.
 *
 * Telemetry payload format (matches CcsdsProtocol.ParseTelem() in C#):
 *   All fields little-endian, packed struct, 32 bytes total.
 *
 *   uint32  uptime_sec          -- seconds since power-on
 *   int8    last_rssi_dbm       -- RSSI of last received packet (dBm)
 *   uint8   last_lqi            -- LQI of last received packet
 *   uint32  packets_good        -- packets received with valid CRC
 *   uint32  packets_sent        -- packets transmitted
 *   uint16  packets_rej_cksum   -- packets rejected bad checksum
 *   uint16  packets_rej_other   -- packets rejected other reason
 *   uint32  uart0_rx_count      -- bytes received on UART0
 *   uint32  uart1_rx_count      -- bytes received on UART1 (J_BOOT)
 *   uint8   rx_mode             -- current RX mode (0=off, 1=on)
 *   uint8   tx_mode             -- current TX mode (0=idle, 1=transmitting)
 *   int16   die_temp_raw        -- CC2652P ADC raw temp reading
 *   uint16  supply_mv           -- VDDS supply voltage in millivolts
 *   Total: 32 bytes
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.2
 *            CcsdsProtocol.cs ParseTelem() -- must match exactly
 */

#ifndef TELEMETRY_H
#define TELEMETRY_H

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stdbool.h>

/* -----------------------------------------------------------------------
 * Telemetry Payload Struct
 * Packed -- matches CcsdsProtocol.ParseTelem() byte-for-byte.
 * Little-endian (CC2652P is little-endian).
 *
 * IMPORTANT: If you add or reorder fields here, update ParseTelem()
 * in CcsdsProtocol.cs to match. The C# ground station parses this
 * struct by byte offset.
 * --------------------------------------------------------------------- */
typedef struct __attribute__((packed))
{
    uint32_t uptime_sec;          /* seconds since power-on              */
    int8_t   last_rssi_dbm;       /* RSSI of last RX packet (dBm)        */
    uint8_t  last_lqi;            /* LQI of last RX packet               */
    uint32_t packets_good;        /* RX packets with valid CRC           */
    uint32_t packets_sent;        /* TX packets transmitted              */
    uint16_t packets_rej_cksum;   /* RX rejected -- bad CRC              */
    uint16_t packets_rej_other;   /* RX rejected -- other reason         */
    uint32_t uart0_rx_count;      /* bytes received on UART0 (payload)   */
    uint32_t uart1_rx_count;      /* bytes received on UART1 (J_BOOT)    */
    uint8_t  rx_mode;             /* 0=RX off, 1=RX active               */
    uint8_t  tx_mode;             /* 0=idle, 1=transmitting              */
    int16_t  die_temp_raw;        /* CC2652P ADC raw temp count          */
    uint16_t supply_mv;           /* VDDS supply voltage in millivolts   */
} sck_telemetry_t;                /* 32 bytes total                      */

/* Verify size at compile time */
_Static_assert(sizeof(sck_telemetry_t) == 32,
    "sck_telemetry_t must be 32 bytes -- update CcsdsProtocol.ParseTelem() if changed");

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

/*
 * telemetry_init()
 * Initialize the ADC driver for die temperature and supply voltage reads.
 * Call once from mainThread() before tasks start.
 */
void telemetry_init(void);

/*
 * telemetry_collect()
 * Populate a sck_telemetry_t struct with current values.
 * Reads die temperature and supply voltage from CC2652P ADC.
 * Copies link stats from shared telemetry state.
 * Parameters:
 *   telem -- pointer to struct to fill (caller allocates)
 */
void telemetry_collect(sck_telemetry_t *telem);

/*
 * telemetry_update_rx()
 * Update link stats after a received packet.
 * Called from the RF RX callback.
 * Parameters:
 *   rssi    -- RSSI of received packet in dBm
 *   lqi     -- LQI of received packet
 *   goodCrc -- true if CRC was valid
 */
void telemetry_update_rx(int8_t rssi, uint8_t lqi, bool goodCrc);

/*
 * telemetry_update_tx()
 * Increment TX packet counter after successful transmission.
 */
void telemetry_update_tx(void);

/*
 * telemetry_update_uart_rx()
 * Increment UART0 receive byte counter.
 * Parameters:
 *   bytes -- number of bytes received
 */
void telemetry_update_uart_rx(uint32_t bytes);

/*
 * telemetry_get_uptime_sec()
 * Returns seconds since firmware started.
 * Uses RTOS Clock tick count.
 */
uint32_t telemetry_get_uptime_sec(void);

#endif /* TELEMETRY_H */
