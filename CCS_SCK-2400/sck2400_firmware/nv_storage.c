/*
 * nv_storage.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Direct flash implementation -- bypasses TI NVSCC26XX driver.
 * Uses FlashSectorErase() + FlashProgram() driverlib calls directly.
 * VIMS cache is disabled around all flash reads and writes to ensure
 * silicon data is used, not cached stale values.
 *
 * NVS region: SCK_NVS_BASE = 0x52000 (pages 41-42, 8KB).
 * Placed immediately after app flash end (0x4000 + 0x4E000 = 0x52000).
 * BIM copies exactly 0x4E000 bytes ending at 0x52000 -- NVS starts
 * exactly where BIM stops so OAD never erases the NVS region.
 */

#include "nv_storage.h"
#include "main.h"

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

#include <ti/devices/DeviceFamily.h>
#include DeviceFamily_constructPath(driverlib/sys_ctrl.h)
#include DeviceFamily_constructPath(driverlib/aon_batmon.h)
#include DeviceFamily_constructPath(driverlib/flash.h)
#include DeviceFamily_constructPath(driverlib/vims.h)
#include <ti/drivers/Power.h>
#include <ti/drivers/power/PowerCC26XX.h>

/* -----------------------------------------------------------------------
 * Flash region
 * --------------------------------------------------------------------- */
#define SCK_NVS_BASE    0x00052000U
#define SCK_NVS_SECTOR  0x00001000U  /* CC1352P flash sector = 4KB */

/* -----------------------------------------------------------------------
 * CRC32
 * --------------------------------------------------------------------- */
#define CRC32_DATA_LEN  (sizeof(sck_nv_record_t) - sizeof(uint32_t))

static uint32_t compute_crc32(const uint8_t *data, uint32_t len)
{
    uint32_t crc = 0xFFFFFFFFUL;
    for (uint32_t i = 0; i < len; i++)
    {
        crc ^= data[i];
        for (uint8_t bit = 0; bit < 8; bit++)
        {
            if (crc & 1U)
                crc = (crc >> 1) ^ 0xEDB88320UL;
            else
                crc >>= 1;
        }
    }
    return crc ^ 0xFFFFFFFFUL;
}

/* -----------------------------------------------------------------------
 * Module State
 * --------------------------------------------------------------------- */
static sck_nv_record_t sRecord     = { 0 };
static bool            sPendingInit = false;
static uint8_t         sInitStatus  = 0;

/* Exposed via telemetry uart0_rx_count for diagnostic verification */
uint32_t gNvsRegionBase = SCK_NVS_BASE;

/* -----------------------------------------------------------------------
 * vims_disable() / vims_restore()
 * Disable VIMS cache around flash operations to ensure reads go to
 * silicon and not the cache.
 * --------------------------------------------------------------------- */
static uint32_t vims_disable(void)
{
    uint32_t cfg = VIMSModeGet(VIMS_BASE);
    VIMSModeSet(VIMS_BASE, VIMS_MODE_DISABLED);
    while (VIMSModeGet(VIMS_BASE) != VIMS_MODE_DISABLED) {}
    return cfg;
}

static void vims_restore(uint32_t cfg)
{
    VIMSModeSet(VIMS_BASE, cfg);
}

/* -----------------------------------------------------------------------
 * flash_read_record()
 * Read NVS record directly from flash into sRecord.
 * --------------------------------------------------------------------- */
static void flash_read_record(void)
{
    uint32_t vimsCfg = vims_disable();
    memcpy(&sRecord, (const void *)SCK_NVS_BASE, sizeof(sck_nv_record_t));
    vims_restore(vimsCfg);
}

/* -----------------------------------------------------------------------
 * write_record()
 * Erase flash sector and program record. Verify from silicon after write.
 * --------------------------------------------------------------------- */
static bool write_record(void)
{
    sRecord.crc32 = compute_crc32((const uint8_t *)&sRecord, CRC32_DATA_LEN);

    /* Set Power constraint to prevent standby during flash write.
     * Without this, VDDR may be at standby voltage which is insufficient
     * for flash programming on CC1352P, causing silent write failures. */
    Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY);

    uint32_t vimsCfg = vims_disable();

    /* Erase sector and wait for FSM to complete */
    uint32_t er = FlashSectorErase(SCK_NVS_BASE);
    if (er != FAPI_STATUS_SUCCESS)
    {
        vims_restore(vimsCfg);
        Power_releaseConstraint(PowerCC26XX_DISALLOW_STANDBY);
        sRecord.reset_cause = (uint8_t)(0x80 | (uint8_t)(er & 0x3F));
        return false;
    }
    while (FlashCheckFsmForReady() != FAPI_STATUS_FSM_READY) {}

    /* Program record */
    uint32_t pr = FlashProgram((uint8_t *)&sRecord,
                                SCK_NVS_BASE,
                                sizeof(sck_nv_record_t));
    if (pr != FAPI_STATUS_SUCCESS)
    {
        vims_restore(vimsCfg);
        Power_releaseConstraint(PowerCC26XX_DISALLOW_STANDBY);
        sRecord.reset_cause = (uint8_t)(0x90 | (uint8_t)(pr & 0x3F));
        return false;
    }

    /* Wait for flash FSM to complete -- FlashProgram() returns when the
     * command is accepted, not when the write is physically committed to
     * the flash array. Without this poll, a reset immediately after
     * FlashProgram() loses the data even though the call returned success.
     * FlashCheckFsmForReady() returns FAPI_STATUS_FSM_READY when done. */
    while (FlashCheckFsmForReady() != FAPI_STATUS_FSM_READY) {}

    /* Verify from silicon */
    const volatile sck_nv_record_t *raw =
        (const volatile sck_nv_record_t *)SCK_NVS_BASE;
    bool ok = (raw->magic == SCK_NV_MAGIC &&
               raw->reset_counter == sRecord.reset_counter);

    vims_restore(vimsCfg);

    /* Release Power constraint */
    Power_releaseConstraint(PowerCC26XX_DISALLOW_STANDBY);

    if (!ok)
    {
        sRecord.reset_cause = 0xD0;
        return false;
    }
    return true;
}

/* -----------------------------------------------------------------------
 * init_fresh_record()
 * --------------------------------------------------------------------- */
static void init_fresh_record(void)
{
    memset(&sRecord, 0, sizeof(sck_nv_record_t));
    sRecord.magic         = SCK_NV_MAGIC;
    sRecord.reset_counter = 0;
    sRecord.reset_cause   = SCK_RESET_CAUSE_UNKNOWN;
    sRecord.safe_mode     = 0;
    sRecord.reserved[0]   = 0;
    sPendingInit = true;
}

/* -----------------------------------------------------------------------
 * reset cause hardware read
 * --------------------------------------------------------------------- */
static uint8_t read_reset_cause_hw(void)
{
    uint32_t src = SysCtrlResetSourceGet();
    switch (src)
    {
        case RSTSRC_PWR_ON:               return SCK_RESET_CAUSE_POWER_ON;
        case RSTSRC_PIN_RESET:            return SCK_RESET_CAUSE_PIN;
        case RSTSRC_VDDS_LOSS:            return SCK_RESET_CAUSE_VDDS;
        case RSTSRC_VDDR_LOSS:            return SCK_RESET_CAUSE_VDDR;
        case RSTSRC_CLK_LOSS:             return SCK_RESET_CAUSE_WARMRESET;
        case RSTSRC_SYSRESET:             return SCK_RESET_CAUSE_SYSRESET;
        case RSTSRC_WARMRESET:            return SCK_RESET_CAUSE_WARMRESET;
        default:                          return SCK_RESET_CAUSE_UNKNOWN;
    }
}

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

bool nv_storage_init(void)
{
    flash_read_record();

    if (sRecord.magic != SCK_NV_MAGIC)
    {
        sInitStatus = 3;  /* magic fail -- blank or corrupted */
        init_fresh_record();
        return false;
    }

    uint32_t expected = compute_crc32((const uint8_t *)&sRecord, CRC32_DATA_LEN);
    if (sRecord.crc32 != expected)
    {
        sInitStatus = 2;  /* CRC fail */
        init_fresh_record();
        return false;
    }

    sInitStatus = 1;  /* valid record */
    return true;
}

uint8_t nv_storage_capture_reset_cause(void)
{
    return read_reset_cause_hw();
}

void nv_storage_on_boot(void)
{
    uint8_t cause = gBootResetCause;
    /* Encode sInitStatus in upper nibble so telemetry shows both.
     * reset_cause bits [7:4] = sInitStatus, bits [3:0] = reset cause */
    sRecord.reset_cause = (uint8_t)((sInitStatus << 4) | (cause & 0x0F));
    sPendingInit = false;

    if (sRecord.reset_counter < 0xFF)
        sRecord.reset_counter++;

    if (sRecord.reset_counter >= SCK_SAFE_MODE_THRESHOLD)
        sRecord.safe_mode = 1;

    write_record();
}

void nv_storage_clear_safe_mode(void)
{
    sRecord.reset_counter = 0;
    sRecord.safe_mode     = 0;
    write_record();
}

void nv_storage_on_clean_run(void)
{
    if (sRecord.reset_counter == 0 && sRecord.safe_mode == 0)
        return;

    sRecord.reset_counter = 0;
    sRecord.safe_mode     = 0;
    write_record();
}

uint8_t nv_storage_get_reset_counter(void) { return sRecord.reset_counter; }
uint8_t nv_storage_get_reset_cause(void)   { return sRecord.reset_cause;   }
bool    nv_storage_get_safe_mode(void)     { return (sRecord.safe_mode != 0); }
