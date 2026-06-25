/*
 * telemetry.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   Telemetry data collection. Reads CC2652P internal die temperature
 *   and supply voltage via ADC, maintains link stats, builds telemetry
 *   payload for CCSDS beacon packets.
 *
 * CC2652P ADC channels:
 *   AUXADC_INPUT_TEMPSENS  -- internal temperature sensor
 *   AUXADC_INPUT_VDDS      -- supply voltage (battery/VCC monitor)
 *
 * Reference: CC2652P Technical Reference Manual Section 14 (AUX ADC)
 *            TI SimpleLink SDK: aux_adc.h
 */

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include "telemetry.h"
#include "main.h"

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

/* TI-RTOS */
#include <ti/sysbios/knl/Clock.h>

/* TI Driverlib -- AUX ADC (direct register access for temp/voltage) */
#include <ti/devices/DeviceFamily.h>
#include DeviceFamily_constructPath(driverlib/aux_adc.h)
#include DeviceFamily_constructPath(driverlib/sys_ctrl.h)

/* -----------------------------------------------------------------------
 * Module state -- protected by atomic access (single writer per field)
 * --------------------------------------------------------------------- */
static volatile int8_t   sLastRssi         = 0;
static volatile uint8_t  sLastLqi          = 0;
static volatile uint32_t sPacketsGood      = 0;
static volatile uint32_t sPacketsSent      = 0;
static volatile uint16_t sPacketsRejCksum  = 0;
static volatile uint16_t sPacketsRejOther  = 0;
static volatile uint32_t sUart0RxCount     = 0;
static volatile uint32_t sUart1RxCount     = 0;
static volatile uint8_t  sRxMode           = 0;
static volatile uint8_t  sTxMode           = 0;

/* -----------------------------------------------------------------------
 * telemetry_init()
 *
 * Initialize the AUX ADC for temperature and voltage reads.
 * The AUX ADC must be enabled before any reads.
 * Call once from mainThread() before tasks start.
 * --------------------------------------------------------------------- */
void telemetry_init(void)
{
    /* AUX ADC is powered up on demand in telemetry_collect().
     * No persistent initialization needed -- AUX ADC is shared
     * with other AUX domain functions and must be acquired/released
     * per-use to avoid conflicts. */
}

/* -----------------------------------------------------------------------
 * telemetry_read_die_temp()
 *
 * Read CC2652P internal die temperature via AUX ADC.
 * Returns raw ADC count. C# converts to °C using 0.0625°C/count.
 *
 * CC2652P temp sensor:
 *   - Connected to AUX ADC channel AUXADC_INPUT_TEMPSENS
 *   - 12-bit resolution
 *   - Sensitivity ~4 mV/°C (varies by device)
 *   - Reference: TI SWCU185 Section 14.2.2
 *
 * Note: For production, use the factory calibration values from FCFG1
 * to compute accurate absolute temperature. For bringup/telemetry
 * purposes the raw ADC value is sufficient to show trends.
 * --------------------------------------------------------------------- */
static int16_t telemetry_read_die_temp(void)
{
    int16_t result = 0;

    /* Enable AUX ADC -- single-shot, internal reference */
    AUXADCEnableSync(AUXADC_REF_FIXED,
                     AUXADC_SAMPLE_TIME_2P7_US,
                     AUXADC_TRIGGER_MANUAL);

    /* Select temperature sensor input */
    AUXADCSelectInput(ADC_COMPB_IN_DCOUPL);

    /* Trigger conversion and wait */
    AUXADCGenManualTrigger();
    result = (int16_t)AUXADCReadFifo();

    /* Disable AUX ADC to release the AUX domain */
    AUXADCDisable();

    return result;
}

/* -----------------------------------------------------------------------
 * telemetry_read_supply_mv()
 *
 * Read CC2652P VDDS supply voltage via AUX ADC.
 * Returns voltage in millivolts.
 *
 * CC2652P VDDS monitor:
 *   - Connected to AUX ADC channel AUXADC_INPUT_VDDS
 *   - 12-bit resolution, fixed reference 4.3V
 *   - Full scale = 4300 mV at ADC count 4095
 *   - Formula: mv = (adcCount * 4300) / 4095
 * --------------------------------------------------------------------- */
static uint16_t telemetry_read_supply_mv(void)
{
    uint16_t adcCount = 0;

    /* Enable AUX ADC -- single-shot, fixed reference */
    AUXADCEnableSync(AUXADC_REF_FIXED,
                     AUXADC_SAMPLE_TIME_2P7_US,
                     AUXADC_TRIGGER_MANUAL);

    /* Select VDDS input */
    AUXADCSelectInput(ADC_COMPB_IN_VDDS);

    /* Trigger and read */
    AUXADCGenManualTrigger();
    adcCount = (uint16_t)AUXADCReadFifo();

    AUXADCDisable();

    /* Convert ADC count to millivolts
     * Full scale reference is 4.3V = 4300 mV at count 4095 */
    uint32_t mv = ((uint32_t)adcCount * 4300UL) / 4095UL;
    return (uint16_t)mv;
}

/* -----------------------------------------------------------------------
 * telemetry_collect()
 *
 * Fill sck_telemetry_t with current sensor readings and link stats.
 * Called by rfTask() before building the beacon packet.
 * --------------------------------------------------------------------- */
void telemetry_collect(sck_telemetry_t *telem)
{
    if (telem == NULL) return;

    memset(telem, 0, sizeof(sck_telemetry_t));

    telem->uptime_sec        = telemetry_get_uptime_sec();
    telem->last_rssi_dbm     = sLastRssi;
    telem->last_lqi          = sLastLqi;
    telem->packets_good      = sPacketsGood;
    telem->packets_sent      = sPacketsSent;
    telem->packets_rej_cksum = sPacketsRejCksum;
    telem->packets_rej_other = sPacketsRejOther;
    telem->uart0_rx_count    = sUart0RxCount;
    telem->uart1_rx_count    = sUart1RxCount;
    telem->rx_mode           = sRxMode;
    telem->tx_mode           = sTxMode;
    telem->die_temp_raw      = telemetry_read_die_temp();
    telem->supply_mv         = telemetry_read_supply_mv();
}

/* -----------------------------------------------------------------------
 * telemetry_update_rx()
 * Update link stats after a received packet.
 * --------------------------------------------------------------------- */
void telemetry_update_rx(int8_t rssi, uint8_t lqi, bool goodCrc)
{
    sLastRssi = rssi;
    sLastLqi  = lqi;
    if (goodCrc)
        sPacketsGood++;
    else
        sPacketsRejCksum++;
}

/* -----------------------------------------------------------------------
 * telemetry_update_tx()
 * --------------------------------------------------------------------- */
void telemetry_update_tx(void)
{
    sPacketsSent++;
}

/* -----------------------------------------------------------------------
 * telemetry_update_uart_rx()
 * --------------------------------------------------------------------- */
void telemetry_update_uart_rx(uint32_t bytes)
{
    sUart0RxCount += bytes;
}

/* -----------------------------------------------------------------------
 * telemetry_get_uptime_sec()
 *
 * Returns seconds since firmware started using RTOS Clock tick count.
 * Clock_getTicks() returns ticks since RTOS started.
 * Default tick period is 1ms (1000 ticks/second).
 * --------------------------------------------------------------------- */
uint32_t telemetry_get_uptime_sec(void)
{
    /* Clock_getTicks() returns ticks since RTOS boot.
     * Tick period: 1000 microseconds (1ms) by default in tirtos7.
     * Divide by 1000 to get seconds. */
    return (uint32_t)(Clock_getTicks() / 1000UL);
}
