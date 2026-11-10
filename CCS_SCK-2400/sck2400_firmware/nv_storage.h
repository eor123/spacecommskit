/*
 * nv_storage.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   Non-volatile storage for reset counter and safe mode state.
 *   Provides persistent fault tracking across power cycles and watchdog
 *   resets -- critical for LEO fault recovery.
 *
 * Architecture:
 *   Uses the TI NVS (Non-Volatile Storage) driver to manage a small
 *   flash region separate from the firmware image and OAD region.
 *   The NVS driver handles wear leveling and safe write semantics.
 *
 *   Why NVS and not CCFG?
 *   - CCFG is a fixed hardware region with very limited write cycles
 *     and is used by the bootloader. Corrupting CCFG can brick the board.
 *   - NVS provides a managed, bounded flash region with safe access APIs.
 *   - NVS is the TI-recommended approach for application persistent data.
 *
 * Boot sequence (called from mainThread() before tasks start):
 *   1. nv_storage_init() -- open NVS, read/validate record
 *   2. nv_storage_on_boot() -- increment counter, detect safe mode
 *   3. nv_storage_get_safe_mode() -- check if safe mode is active
 *   4. Tasks start -- they check gSafeMode before complex operations
 *
 * Safe mode:
 *   If reset_counter >= SCK_SAFE_MODE_THRESHOLD boots in a row without
 *   a clean run (5 min stable uptime), the board enters safe mode.
 *   In safe mode: beacon and get_telem run normally, all other commands
 *   are blocked. Ground station sends CMD_CLEAR_SAFE_MODE to reset.
 *
 * Clean run:
 *   After SCK_CLEAN_RUN_MS (5 min) of stable uptime without a watchdog
 *   reset, the reset counter is cleared to 0. This prevents a board that
 *   had one bad session from being permanently hair-triggered into safe mode.
 *
 * Extension points for open-source contributors:
 *   - SCK_SAFE_MODE_THRESHOLD: make this a ground-station-configurable NV
 *     parameter by storing it in sck_nv_record_t and exposing a set command.
 *   - Add additional NV fields to sck_nv_record_t (expand reserved[] bytes).
 *   - nv_storage_get_reset_cause() returns the raw CC1352P hardware reset
 *     cause register -- useful for distinguishing watchdog vs power cycle
 *     vs brownout vs soft reset in ground station telemetry analysis.
 *
 * Target:    CC1352P1F3RGZ / CC2652P1FRGZ
 * Toolchain: TI Clang + TI-RTOS7
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.6
 *            TI SimpleLink SDK: ti/drivers/NVS.h
 *            CC13x2/CC26x2 Technical Reference Manual Section 8 (Flash)
 */

#ifndef NV_STORAGE_H
#define NV_STORAGE_H

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stdbool.h>

/* -----------------------------------------------------------------------
 * Configuration Constants
 *
 * SCK_SAFE_MODE_THRESHOLD:
 *   Number of consecutive resets (without a clean run) that triggers
 *   safe mode. Set to 5 -- conservative enough to catch real boot loops
 *   but tolerant of occasional watchdog trips during development.
 *
 *   Extension point: this could be stored in NV flash and configured
 *   via a ground station command (CMD_SET_SAFE_MODE_THRESHOLD). See
 *   the ADD_COMMAND pattern in ccsds.h for how to add a new command.
 *
 * SCK_CLEAN_RUN_MS:
 *   Milliseconds of stable uptime required to clear the reset counter.
 *   300000ms = 5 minutes. A board that runs cleanly through a full LEO
 *   pass (8-12 min) has clearly recovered -- the counter should reset.
 *   Monitored by watchdogTask which already has a 10s kick loop.
 * --------------------------------------------------------------------- */
#define SCK_SAFE_MODE_THRESHOLD     5U      /* resets before safe mode    */
#define SCK_CLEAN_RUN_MS            300000U /* 5 min clean run clears counter */

/* -----------------------------------------------------------------------
 * Reset Cause Codes
 * Mapped from CC1352P SYS_CTRL:RESETCTL RESET_SRC field.
 * Used in telemetry so ground station can distinguish reset types.
 *
 * Extension point: additional cause codes can be added here as new
 * hardware reset sources are characterized on future SCK products.
 * --------------------------------------------------------------------- */
#define SCK_RESET_CAUSE_POWER_ON    0x00    /* Cold power-on              */
#define SCK_RESET_CAUSE_PIN         0x01    /* External reset pin         */
#define SCK_RESET_CAUSE_VDDS        0x02    /* Supply voltage brownout    */
#define SCK_RESET_CAUSE_VDDR        0x03    /* Core voltage brownout      */
#define SCK_RESET_CAUSE_WARMRESET   0x04    /* Software warm reset        */
#define SCK_RESET_CAUSE_SYSRESET    0x05    /* System reset -- includes watchdog
                                             * Note: CC13x2 does not expose a
                                             * dedicated watchdog reset source.
                                             * Watchdog resets appear as SYSRESET
                                             * (RSTSRC_SYSRESET). This is a hardware
                                             * limitation of the CC13x2 family.   */
#define SCK_RESET_CAUSE_WATCHDOG    0x06    /* Reserved -- not returned by CC13x2
                                             * hardware. Kept for future use or
                                             * software-assisted watchdog detection
                                             * via NMI handler flag.              */
#define SCK_RESET_CAUSE_UNKNOWN     0xFF    /* Could not read cause register      */

/* -----------------------------------------------------------------------
 * NV Record Structure
 *
 * Stored in the NVS flash region. The magic value validates that the
 * record has been initialized (distinguishes fresh flash from written data).
 * CRC32 detects corruption from partial writes or flash errors.
 *
 * Total: 12 bytes. Reserved bytes available for future fields without
 * changing the NVS region layout.
 *
 * Extension point: add fields to the reserved[] bytes. Update the CRC
 * calculation in nv_storage.c and bump SCK_NV_MAGIC to invalidate old
 * records on firmware update if the layout changes.
 * --------------------------------------------------------------------- */
#define SCK_NV_MAGIC    0x534B3234UL    /* "SK24" -- SpaceCommsKit 2400  */

typedef struct __attribute__((packed))
{
    uint32_t magic;           /* SCK_NV_MAGIC -- validates record init    */
    uint8_t  reset_counter;   /* increments on every boot                 */
    uint8_t  reset_cause;     /* SCK_RESET_CAUSE_* from hardware register */
    uint8_t  safe_mode;       /* 1 = currently in safe mode, 0 = normal   */
    uint8_t  reserved[1];     /* future use -- set to 0                   */
    uint32_t crc32;           /* CRC32 of bytes [0..7] -- detects corruption */
} sck_nv_record_t;            /* 12 bytes total                           */

/* -----------------------------------------------------------------------
 * Public API
 * --------------------------------------------------------------------- */

/*
 * nv_storage_init()
 *
 * Initialize the NVS driver and read the current NV record.
 * If no valid record exists (fresh flash or corrupted), initializes
 * a clean record with counter=0, safe_mode=0.
 *
 * Call once from mainThread() before tasks start, after GPIO_init()
 * and SPI_init() but before nv_storage_on_boot().
 *
 * Returns: true if existing record was read successfully,
 *          false if record was freshly initialized (first boot or corruption).
 */
bool nv_storage_init(void);

/*
 * nv_storage_capture_reset_cause()
 *
 * Read the CC1352P hardware reset cause register and return the mapped
 * SCK_RESET_CAUSE_* code. Must be called as early as possible in
 * mainThread() before RF driver or power management init can clear it.
 *
 * Result is stored in gBootResetCause by the caller (main.c) and
 * consumed by nv_storage_on_boot() which runs later in watchdogTask.
 */
uint8_t nv_storage_capture_reset_cause(void);

/*
 * nv_storage_on_boot()
 *
 * Called from watchdogTask() on first kick after RTOS is fully running.
 * Reads gBootResetCause (captured in mainThread), increments reset counter
 * if appropriate, sets safe mode flag, writes updated record to flash.
 *
 * Must NOT be called from mainThread() -- NVS_write() can hang on a
 * degraded sector, and a hang here produces a completely dead board.
 * In watchdogTask, a hung write starves the watchdog and triggers a
 * hardware reset, self-recovering the board.
 */
void nv_storage_on_boot(void);

/*
 * nv_storage_clear_safe_mode()
 *
 * Reset counter to 0 and clear safe mode flag. Write to flash.
 * Called by CMD_CLEAR_SAFE_MODE handler in uart.c.
 * Board resumes normal operation on next boot.
 *
 * Extension point: a ground station operator can call this after
 * diagnosing the root cause of the boot loop via telemetry.
 */
void nv_storage_clear_safe_mode(void);

/*
 * nv_storage_on_clean_run()
 *
 * Called by watchdogTask after SCK_CLEAN_RUN_MS of stable uptime.
 * Resets counter to 0 if it is > 0. No-op if already 0.
 * Clears safe_mode flag if set.
 *
 * Rationale: a board that has run stably for 5 minutes has clearly
 * recovered from whatever caused previous resets. Clearing the counter
 * prevents a board from being permanently hair-triggered into safe mode
 * after one bad session.
 */
void nv_storage_on_clean_run(void);

/*
 * nv_storage_get_reset_counter()
 * Returns the current reset counter value from the in-memory record.
 * Does not read flash -- uses cached value from nv_storage_init().
 */
uint8_t nv_storage_get_reset_counter(void);

/*
 * nv_storage_get_reset_cause()
 * Returns the last reset cause (SCK_RESET_CAUSE_*) from in-memory record.
 */
uint8_t nv_storage_get_reset_cause(void);

/*
 * nv_storage_get_safe_mode()
 * Returns true if safe mode is currently active.
 * Used by uart_task and rfTask to gate complex operations.
 */
bool nv_storage_get_safe_mode(void);

#endif /* NV_STORAGE_H */
