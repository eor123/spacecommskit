/*
 * radio.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   RF layer interface. Declares TX/RX functions, power control,
 *   and RF switch control for the SKY13317-373LF.
 *
 * RF Configuration:
 *   Frequency:   2440 MHz (Channel 18)
 *   PHY:         2-GFSK, 250 kbps, 125 kHz deviation
 *   TX Power:    0 dBm bench / +20 dBm field
 *   High PA:     Enabled (txPowerTable_2400_pa20, SDK 8_32 separate tables)
 *   RF Switch:   SKY13317 -- DIO28 (2.4GHz), DIO29 (PA enable)
 *
 * SCK-DEV: RF_CONFIG -- Change frequency/PHY in sck2400.syscfg,
 *          then update SCK_CMD_SETUP and SCK_CMD_FS below.
 * SCK-DEV: TX_POWER  -- Change bench/field power in radio.c
 *          RF_setTxPower() call. See Section 3.2 of requirements.
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.2, 4.2, 4.3
 */

#ifndef RADIO_H
#define RADIO_H

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stdbool.h>

/* -----------------------------------------------------------------------
 * SysConfig Symbol Aliases
 * SysConfig generates suffixed names for custom PHY. All application
 * code uses these aliases so a PHY rename only touches this block.
 * SCK-DEV: RF_CONFIG -- If PHY suffix changes in SysConfig, update here.
 * --------------------------------------------------------------------- */
#define SCK_RF_MODE      RF_prop_2gfsk250kbps_0
#define SCK_CMD_SETUP    RF_cmdPropRadioDivSetup_2gfsk250kbps_0
#define SCK_CMD_FS       RF_cmdFs_2gfsk250kbps_0
#define SCK_CMD_TX       RF_cmdPropTx_2gfsk250kbps_0
#define SCK_CMD_RX       RF_cmdPropRx_2gfsk250kbps_0

/* SDK 8_32 generates two separate PA tables (not a combined pa5_20 table).
 * STD = standard PA up to 5 dBm (bench), HP = high PA up to 20 dBm (field).
 * SCK-DEV: TX_POWER -- radio_setTxPower() selects the correct table
 *          based on requested dBm level. */
#define SCK_TX_PWR_TABLE_STD  txPowerTable_2400_pa5    /* bench, 0-5 dBm  */
#define SCK_TX_PWR_TABLE_HP   txPowerTable_2400_pa20   /* field, 20 dBm   */

/* -----------------------------------------------------------------------
 * TX Power Levels
 * Build system passes TX_POWER=BENCH or TX_POWER=FIELD via Makefile.
 * SCK-DEV: TX_POWER -- These map to RF_setTxPower() calls in radio.c.
 * --------------------------------------------------------------------- */
#define SCK_TX_POWER_BENCH_DBM      0       /* 0 dBm  -- bench/lab use  */
#define SCK_TX_POWER_FIELD_DBM      20      /* +20 dBm -- field/flight  */

#define TX_POWER BENCH
#ifndef TX_POWER
#endif

/* -----------------------------------------------------------------------
 * RF Switch GPIO (SKY13317-373LF)
 * Controlled directly via GPIO driver -- not RF switch plugin.
 * DIO28 = 2.4GHz path select
 * DIO29 = PA enable
 * --------------------------------------------------------------------- */
#define SCK_RF_SW_2G4_DIO   28
#define SCK_RF_SW_PA_DIO    29

/* -----------------------------------------------------------------------
 * RX Configuration
 * --------------------------------------------------------------------- */

/* Maximum CCSDS packet size the RX queue will accept, in bytes.
 * 255 is the hard limit -- maxPktLen is a uint8_t field in CMD_PROP_RX.
 * RFQueue helper (RFQueue.h / RFQueue.c) manages the data queue internals.
 * Copy RFQueue.h and RFQueue.c from:
 *   SDK/examples/rtos/CC1352P_2_LAUNCHXL/prop_rf/rfPacketRx/
 * into the sck2400_firmware project folder and add to CCS build. */
#define SCK_RX_BUF_SIZE     255

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

/*
 * radio_init()
 * Initialize the RF driver, open the RF handle, configure the PHY,
 * set TX power per build-time TX_POWER define, and configure RF switch.
 * Must be called once from the RF task before any TX/RX.
 * Returns: true on success, false on RF driver error.
 */
bool radio_init(void);

/*
 * radio_transmit()
 * Transmit a packet over the air.
 * Parameters:
 *   buf  -- pointer to packet data (CCSDS-framed)
 *   len  -- length in bytes
 * Returns: true on successful TX, false on RF error.
 * SCK-DEV: TIMING -- TX blocks until complete. Max ~4ms at 250kbps
 *          for a 128-byte packet. Cancels active RX command, then
 *          RX resumes on next radio_receive() call.
 */
bool radio_transmit(const uint8_t *buf, uint16_t len);

/*
 * radio_rxReady()
 * Non-blocking poll -- returns true if a packet is waiting in the
 * RX data queue. Call from RF task poll loop before radio_receive()
 * to avoid copy overhead when queue is empty.
 * Returns: true if packet ready, false if queue empty.
 */
bool radio_rxReady(void);

/*
 * radio_isRxRunning()
 * Returns true if CMD_PROP_RX is currently posted and listening.
 * Used by rfTask to detect when RX needs to be restarted after TX.
 */
bool radio_isRxRunning(void);

/*
 * radio_receive()
 * Copy one received packet out of the RX data queue into caller's buffer.
 * Starts the background RX command on first call -- subsequent calls
 * are non-blocking polls. RF core listens continuously in background.
 * Parameters:
 *   buf     -- buffer to write received data into
 *   maxLen  -- size of buf (should be >= SCK_RX_BUF_SIZE)
 *   rxLen   -- actual bytes received (output)
 * Returns: true if packet copied, false if queue empty or error.
 */
bool radio_receive(uint8_t *buf, uint16_t maxLen, uint16_t *rxLen);

/*
 * radio_setTxPower()
 * Set TX power at runtime (used internally by radio_init).
 * Parameters:
 *   dbm -- power level in dBm (0 or 20)
 */
void radio_setTxPower(int8_t dbm);

/*
 * radio_rfSwitchSet()
 * Drive SKY13317 RF switch GPIOs for selected path.
 * Called internally before TX and RX.
 * Parameters:
 *   txMode -- true for TX path, false for RX path
 */
void radio_rfSwitchSet(bool txMode);

#endif /* RADIO_H */