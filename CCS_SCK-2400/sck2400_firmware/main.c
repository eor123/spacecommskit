/*
 * main.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   Entry point and RTOS task creation for sck2400_firmware.
 *   Initializes all drivers, creates RF and UART tasks, then
 *   returns to allow the RTOS scheduler to run.
 *
 * Task structure:
 *   rfTask   -- RF init, TX beacon loop, RX poll, command routing
 *   uartTask -- Payload UART ESP frame receive and dispatch
 *
 * RF Command Routing:
 *   The rfTask implements OpenLST-style board addressing using CCSDS
 *   APID in place of HWID. Each board has a unique APID set at compile
 *   time via SCK_APID_THIS_BOARD in ccsds.h.
 *
 *   GS board (SCK_APID_BOARD_GS):
 *     - UART receives command from C# → checks destination APID
 *     - If APID is remote board: forward over RF via radio_transmit()
 *     - If RF response arrives: forward back to C# via uart_forward_rf_packet()
 *
 *   Remote board (SCK_APID_BOARD_REMOTE_*):
 *     - Receives RF packet → checks destination APID
 *     - If addressed to this board: dispatch to command handler
 *     - Response sent over RF back to GS
 *
 * Target (dev):   CC1352P1F3RGZ on LAUNCHXL-CC1352P-2  (sck2400.syscfg)
 * Target (prod):  CC2652P1FRGZ on SCK-2400 Mini V3     (sck2400_v3.syscfg,
 *                 -DRF_SWITCH_SKYA21001=1)
 * Toolchain:      TI Clang + TI-RTOS7
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.1, 7
 */

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <unistd.h>

/* TI Drivers */
#include <ti/drivers/GPIO.h>
#include <ti/drivers/SPI.h>     /* SPI_init() -- must be called before extFlashOpen() */

/* TI Driverlib -- SSI0 pre-init before RTOS scheduler */
#include <ti/devices/DeviceFamily.h>
#include DeviceFamily_constructPath(driverlib/prcm.h)
#include DeviceFamily_constructPath(driverlib/ssi.h)
#include DeviceFamily_constructPath(driverlib/ioc.h)
#include DeviceFamily_constructPath(driverlib/rom.h)
#include <ti/drivers/Power.h>
#include <ti/drivers/power/PowerCC26XX.h>
#include <ti/drivers/Watchdog.h>

/* TI-RTOS */
#include <ti/sysbios/BIOS.h>
#include <ti/sysbios/knl/Task.h>

/* SysConfig generated */
#include "ti_drivers_config.h"

/* SCK-2400 modules */
#include "main.h"
#include "radio.h"
#include "uart.h"
#include "ccsds.h"
#include "crypto.h"
#include "boot_counter.h"   /* nonce boot count (11/2026) */

/* RF_SWITCH_SKYA21001, SCK_TXPWR_MODE and SCK_RF_CW_TEST defaults now
 * live in radio.h only (included above) so the two files cannot disagree. */

#include "nv_storage.h"

#include <ti/sysbios/knl/Clock.h>

/* -----------------------------------------------------------------------
 * sck_sleep_ms()
 *
 * Bug C (10/20/26): POSIX usleep() only accepts values below 1,000,000.
 * usleep(1500000) in the GS forward path returned immediately with an
 * error, so the 1.5 s response window never happened.
 *
 * Task_sleep() counts Clock ticks, not milliseconds. Clock_tickPeriod
 * is in microseconds and depends on the kernel config, so the
 * conversion is done here instead of assuming 1 tick = 1 ms.
 * --------------------------------------------------------------------- */
static void sck_sleep_ms(uint32_t ms)
{
    Task_sleep((ms * 1000U) / Clock_tickPeriod);
}

/* -----------------------------------------------------------------------
 * Task stacks
 * Statically allocated -- sizes defined in main.h per requirements Sec 7.
 * --------------------------------------------------------------------- */
static uint8_t sRfTaskStack[SCK_TASK_STACK_RF];
static uint8_t sUartTaskStack[SCK_TASK_STACK_UART];
static uint8_t sOadTaskStack[SCK_TASK_STACK_OAD];
static uint8_t sWatchdogTaskStack[512];

/* -----------------------------------------------------------------------
 * Task handles (extern declared in main.h)
 * --------------------------------------------------------------------- */
Task_Handle rfTaskHandle;
Task_Handle uartTaskHandle;
Task_Handle oadTaskHandle;
Task_Handle watchdogTaskHandle;

/* -----------------------------------------------------------------------
 * Beacon packet
 * CCSDS primary header + fixed test payload.
 * --------------------------------------------------------------------- */
#define BEACON_PAYLOAD_LEN  8
static uint8_t   sBeaconBuf[sizeof(ccsds_primary_header_t) + BEACON_PAYLOAD_LEN];
static uint16_t  sBeaconSeqCount = 0;

/* -----------------------------------------------------------------------
 * RF RX buffer
 * Receives packets from radio_receive() in rfTask poll loop.
 * Sized to SCK_RX_BUF_SIZE -- lives on RF task stack.
 * --------------------------------------------------------------------- */
static uint8_t sRxPktBuf[SCK_RX_BUF_SIZE];

/* -----------------------------------------------------------------------
 * Status LED
 * DIO7 = green LED on LAUNCHXL-CC1352P-2.
 * One short flash per beacon TX -- confirms RF task is alive.
 * --------------------------------------------------------------------- */
#define STATUS_LED_FLASH_US 50000   /* 50ms flash per TX */

/* -----------------------------------------------------------------------
 * Beacon interval
 * Total beacon period = STATUS_LED_FLASH_US + SCK_BEACON_REST_US = 1000ms
 * --------------------------------------------------------------------- */
#define SCK_BEACON_REST_US  950000  /* rest period after LED flash */

/* Runtime beacon enable flag -- default off, controlled via CMD_BEACON_CTRL.
 * Declared volatile so rfTask reads it fresh each loop iteration.
 * Defined here (main.c), declared extern in uart.c. */
volatile bool gBeaconEnabled = false;

/* OAD session flag -- set true by uart.c handle_oad_start(),
 * cleared by handle_oad_end/abort().  Read by rfTask to switch
 * from 950ms beacon sleep to 1ms chunk-drain sleep.
 * Declared volatile; defined here, extern in uart.c. */
volatile bool gOadActive = false;

/* Safe mode flag -- set by nv_storage_on_boot() if reset counter
 * exceeds SCK_SAFE_MODE_THRESHOLD. Cleared by CMD_CLEAR_SAFE_MODE.
 * In safe mode: beacon and get_telem always run; OAD, payload commands,
 * and other complex operations are blocked until ground station clears.
 * Declared volatile; defined here, extern declared in main.h. */
volatile bool gSafeMode = false;

/* Boot reset cause -- captured in mainThread() from the CC1352P hardware
 * reset cause register before any driver init can clear it.
 * Consumed by watchdogTask on first kick to write the updated NV record.
 *
 * Why a global instead of a parameter?
 * Task functions take only two uintptr_t args. Passing reset cause
 * through arg0 would work but is less readable than a named global.
 * This value is written once at boot and read once in watchdogTask --
 * no concurrency concern.
 *
 * SCK_RESET_CAUSE_UNKNOWN is the safe default before mainThread() writes it. */
volatile uint8_t gBootResetCause = SCK_RESET_CAUSE_UNKNOWN;

/* Beacon enable -- set to 1 for normal operation, 0 to disable during testing */
#define SCK_BEACON_ENABLED  0

/* -----------------------------------------------------------------------
 * rfTask()
 *
 * Purpose:
 *   RF layer task. Two responsibilities run in the same loop:
 *
 *   1. Beacon TX (once per second):
 *      Builds and transmits a CCSDS beacon packet. Status LED flashes
 *      on each TX to confirm the RF task is alive.
 *
 *   2. RX poll + command routing (every loop iteration):
 *      Checks the RF RX queue for incoming packets. Routes received
 *      packets based on destination APID and board role:
 *
 *      Remote board receives RF packet addressed to this board:
 *        → uart_dispatch_ccsds_packet() → command handler
 *        → handler builds response → radio_transmit() back to GS
 *
 *      GS board receives RF response from remote board:
 *        → uart_forward_rf_packet() → ESP frame → UART0 → C#
 *
 *      GS board receives UART command for remote board APID:
 *        → uart_task() calls radio_transmit() [see note below]
 *
 *   Note on GS outbound routing: commands from C# destined for a remote
 *   board arrive via uart_task(). uart_dispatch_ccsds_packet() checks
 *   CCSDS_IS_FOR_THIS_BOARD(apid) -- if false on GS board, it means the
 *   command is for a remote. In that case uart_task forwards to rfTask
 *   via the sRfForwardBuf/sRfForwardPending shared state below. rfTask
 *   picks it up on the next loop iteration and calls radio_transmit().
 *
 * Stack:    SCK_TASK_STACK_RF (1024 bytes)
 * Priority: SCK_TASK_PRI_RF (2)
 *
 * SCK-DEV: TIMING -- Beacon interval is ~1000ms. Each TX blocks ~4-5ms.
 *   RX poll is non-blocking -- radio_rxReady() returns immediately.
 *   Adjust SCK_BEACON_REST_US for different beacon rates.
 * --------------------------------------------------------------------- */

/* Shared state for GS outbound RF forwarding.
 * uart_task() writes here when a command is destined for a remote board.
 * rfTask() reads here and calls radio_transmit().
 * Simple flag + buffer -- no queue needed for single-command flow. */
static uint8_t  sRfForwardBuf[sizeof(ccsds_primary_header_t) + ESP_MAX_PAYLOAD];
static uint16_t sRfForwardLen     = 0;
static volatile bool sRfForwardPending = false;

/*
 * rf_forward_enqueue()
 * Called from uart_dispatch_ccsds_packet() when a command arrives via
 * UART addressed to a remote board APID. Queues it for rfTask to TX.
 * Parameters:
 *   buf -- CCSDS packet to forward
 *   len -- packet length
 */
/* 11/09/26: commands that get no reply (CMD_OAD_CHUNK) are forwarded
 * without the 1500 ms response window, so OAD streaming can run at the
 * GS inter-chunk rate instead of ~1 chunk per 1.5 s (the rest were dropped
 * here while sRfForwardPending was set). */
static volatile bool sRfForwardExpectReply = true;

void rf_forward_enqueue_ex(const uint8_t *buf, uint16_t len, bool expectReply)
{
    if (buf == NULL || len == 0 ||
        len > sizeof(sRfForwardBuf) || sRfForwardPending)
    {
        return;  /* drop if busy -- GS will retry on timeout */
    }
    memcpy(sRfForwardBuf, buf, len);
    sRfForwardLen         = len;
    sRfForwardExpectReply = expectReply;
    sRfForwardPending     = true;
}

void rf_forward_enqueue(const uint8_t *buf, uint16_t len)
{
    rf_forward_enqueue_ex(buf, len, true);
}

static void rfTask(uintptr_t arg0, uintptr_t arg1)
{
    bool rfOk = radio_init();

    if (!rfOk)
    {
        /* RF init failed -- fast blink status LED to indicate fault. */
        while (1)
        {
            GPIO_write(CONFIG_GPIO_GLED, 1);
            usleep(100000);
            GPIO_write(CONFIG_GPIO_GLED, 0);
            usleep(100000);
        }
    }

    /* Initialize AES-128 CCM crypto after RF driver is open.
     *
     * AESCCM_open() on CC1352P uses the crypto power domain which is
     * shared with the RF driver. Calling crypto_init() before radio_init()
     * causes AESCCM_open() to return NULL because the power domain
     * sequencing conflicts. Calling it here after radio_init() succeeds
     * ensures the power domain is fully initialized and AESCCM_open()
     * gets a valid handle.
     *
     * A false return means the AESCCM hardware failed to open.
     * Blink 3 fast pulses to signal crypto fault (distinct from RF fault
     * which is continuous fast blink). Board continues without encryption
     * for development -- in production this should halt.
     *
     * SCK-DEV: CRYPTO -- Do not move crypto_init() before radio_init(). */
    bool cryptoOk = crypto_init();
    if (!cryptoOk)
    {
        /* 6 fast blinks = crypto self-test failed.
         * Visible on every boot including watchdog reboots.
         * Board continues -- no battery drain loop. */
        for (int i = 0; i < 6; i++)
        {
            GPIO_write(CONFIG_GPIO_GLED, 1); usleep(80000);
            GPIO_write(CONFIG_GPIO_GLED, 0); usleep(80000);
        }
    }

    /* Nonce identity: this board's APID + a boot count that is larger than
     * on any previous boot (external flash, boot_counter.c). Must run before
     * the first crypto_encrypt(). A 0 boot count means external flash failed;
     * the board still runs, and peers recover via CMD_CRYPTO_RESET_REPLAY or
     * the replay silence timeout. */
    crypto_set_identity(SCK_APID_THIS_BOARD, boot_counter_next());

    /* OAD trial boot / golden image state (uart.c). Arms the trial timeout
     * if this is a newly installed image that has not been confirmed by a
     * ground command yet. */
    oad_trial_init();

#if SCK_RF_CW_TEST
    /* Bring-up CW mode (radio.h SCK_RF_CW_TEST). Carrier at 2440 MHz,
     * power per SCK_TXPWR_MODE.
     *   Slow 1 Hz blink            = keyed, TX power accepted by the driver.
     *   Double flash every second  = keyed, but RF_setTxPower() failed or
     *                                read back a different PA (power wrong).
     *   3 fast blinks then dark    = CMD_TX_TEST failed to post. */
    if (radio_cwStart())
    {
        bool pwrOk = radio_txPowerOk();
        while (1)
        {
            if (pwrOk)
            {
                GPIO_write(CONFIG_GPIO_GLED, 1); sck_sleep_ms(500);
                GPIO_write(CONFIG_GPIO_GLED, 0); sck_sleep_ms(500);
            }
            else
            {
                GPIO_write(CONFIG_GPIO_GLED, 1); sck_sleep_ms(100);
                GPIO_write(CONFIG_GPIO_GLED, 0); sck_sleep_ms(150);
                GPIO_write(CONFIG_GPIO_GLED, 1); sck_sleep_ms(100);
                GPIO_write(CONFIG_GPIO_GLED, 0); sck_sleep_ms(650);
            }
        }
    }
    for (int i = 0; i < 3; i++)
    {
        GPIO_write(CONFIG_GPIO_GLED, 1); usleep(80000);
        GPIO_write(CONFIG_GPIO_GLED, 0); usleep(80000);
    }
    while (1) { sck_sleep_ms(1000); }
#endif

    /* RF init succeeded -- enter main loop.
     * Beacon is controlled by SCK_BEACON_ENABLED define below.
     * RX runs continuously in background via RF callback. */
    while (1)
    {
        /* -----------------------------------------------------------
         * RX poll -- check for incoming RF packets.
         * Restarts RX after TX (sRxRunning=false after radio_transmit).
         * Non-blocking -- returns immediately if no packet ready.
         * --------------------------------------------------------- */
        if (!radio_isRxRunning() || radio_rxReady())
        {
            uint16_t rxLen = 0;
            if (radio_receive(sRxPktBuf, sizeof(sRxPktBuf), &rxLen) && rxLen > 0)
            {
                uint16_t rxApid = 0;
                if (rxLen >= sizeof(ccsds_primary_header_t))
                {
                    rxApid = ccsds_get_apid(
                                 (const ccsds_primary_header_t *)sRxPktBuf);
                }

                if (CCSDS_IS_FOR_THIS_BOARD(rxApid))
                {
#if SCK_IS_GS_BOARD
                    /* GS board receiving its own packets -- should not happen
                     * in normal operation, but dispatch if it does. No decrypt
                     * needed -- GS board receives responses from remote board
                     * via uart_forward_rf_packet() in the else branch below. */
                    uart_dispatch_ccsds_packet(sRxPktBuf, rxLen, true);
#else
                    /* Remote board: decrypt and dispatch directly from rfTask.
                     * Software AES (crypto.c) has no TI hardware dependency --
                     * it is pure C and safe to call from any task context.
                     * No queue needed -- decrypt inline and dispatch immediately. */
                    {
                        static uint8_t sDecBuf[SCK_RX_BUF_SIZE];
                        uint16_t decLen   = 0;
                        uint8_t  keyIndex = 0;

                        if (crypto_decrypt(sRxPktBuf, rxLen,
                                           sDecBuf, &decLen, &keyIndex))
                        {
                            uart_dispatch_ccsds_packet(sDecBuf, decLen, true);
                        }
                    }
#endif
                }
#if SCK_IS_GS_BOARD
                else
                {
                    uart_forward_rf_packet(sRxPktBuf, rxLen);
                }
#endif
            }
        }

        /* -----------------------------------------------------------
         * Beacon TX -- skip if forward is pending to avoid RF collision.
         * When a forward is queued, send it instead of the beacon so the
         * remote board is guaranteed to be in RX when the command arrives.
         * --------------------------------------------------------- */
#if SCK_IS_GS_BOARD
        if (sRfForwardPending)
        {
            radio_transmit(sRfForwardBuf, sRfForwardLen);
            sRfForwardPending = false;
            if (sRfForwardExpectReply)
                sck_sleep_ms(1500);  /* 1500ms response window (Bug C: was usleep(1500000)).
                               * Remote board decrypts in uart_task (up to 100ms)
                               * then rfTask wakes from 950ms sleep to transmit.
                               * Worst case: 100ms + 950ms + margin = 1500ms. */
        }
        else
        {
#endif

        if (gBeaconEnabled)
        {
            ccsds_build_header((ccsds_primary_header_t *)sBeaconBuf,
                               CCSDS_APID_TLM_BEACON,
                               false,
                               sBeaconSeqCount,
                               BEACON_PAYLOAD_LEN);

            uint8_t *payload = sBeaconBuf + sizeof(ccsds_primary_header_t);
            payload[0] = 0x5C;
            payload[1] = 0x24;
            payload[2] = (uint8_t)(sBeaconSeqCount >> 8);
            payload[3] = (uint8_t)(sBeaconSeqCount & 0xFF);
            payload[4] = 0xDE;
            payload[5] = 0xAD;
            payload[6] = 0xBE;
            payload[7] = 0xEF;

            radio_transmit(sBeaconBuf, sizeof(sBeaconBuf));
            sBeaconSeqCount = (sBeaconSeqCount + 1) & 0x3FFF;

            /* Status LED -- 1Hz flash when beacon enabled */
            GPIO_write(CONFIG_GPIO_GLED, 1);
            usleep(STATUS_LED_FLASH_US);
            GPIO_write(CONFIG_GPIO_GLED, 0);
        }

#if SCK_IS_GS_BOARD
        } /* end else (no forward pending) */
#endif

        /* Beacon interval sleep. */
#if !SCK_IS_GS_BOARD
        usleep(gOadActive ? 1000 : SCK_BEACON_REST_US);
#else
        /* GS board: poll fast so queued forwards (OAD chunks every few ms)
         * and remote replies move without waiting a whole beacon period.
         * Keep the 1 Hz period only when the GS beacon is enabled. */
        usleep(gBeaconEnabled ? SCK_BEACON_REST_US : 2000);
#endif
    }
}

/* -----------------------------------------------------------------------
 * watchdogTask()
 *
 * Purpose:
 *   Services the hardware watchdog timer to prevent spurious resets
 *   during normal operation. Runs at lowest priority -- if any higher
 *   priority task deadlocks or spins, this task never runs, the watchdog
 *   expires, and the CC1352P resets. This is intentional behavior for
 *   LEO fault recovery.
 *
 *   Timeout: 30000ms (set in SysConfig CONFIG_WATCHDOG_0).
 *   Kick interval: 10000ms -- well within timeout margin.
 *
 * Stack:    512 bytes (minimal -- no complex ops)
 * Priority: 1 (lowest -- must be lower than rfTask and uartTask)
 *
 * SCK-DEV: WATCHDOG -- Do not move watchdog kicks into rfTask or uartTask.
 *   The whole point is that a hang in those tasks stops the kicks.
 *   Do not raise this task's priority above SCK_TASK_PRI_RF or
 *   SCK_TASK_PRI_UART -- it must be preemptable by all work tasks.
 * --------------------------------------------------------------------- */
static Watchdog_Handle sWatchdogHandle = NULL;

static void watchdogTask(uintptr_t arg0, uintptr_t arg1)
{
    Watchdog_Params wdParams;
    Watchdog_Params_init(&wdParams);
    wdParams.resetMode      = Watchdog_RESET_ON;        /* hardware reset on timeout */
    wdParams.debugStallMode = Watchdog_DEBUG_STALL_OFF; /* watchdog runs even under JTAG.
                                                          * STALL_ON causes the XDS110 to
                                                          * suppress the watchdog clock when
                                                          * JTAG is asserted -- on a USB
                                                          * power cycle the XDS110 re-enumerates
                                                          * and holds JTAG before firmware boots,
                                                          * preventing any LED activity or task
                                                          * execution. STALL_OFF is correct for
                                                          * both testing and LEO production. */

    sWatchdogHandle = Watchdog_open(CONFIG_WATCHDOG_0, &wdParams);
    if (sWatchdogHandle == NULL)
    {
        /* Watchdog failed to open -- fault condition.
         * Should never happen in production. Spin here so debugger
         * can catch it on bringup. */
        while (1) {}
    }

    /* Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY) is set in mainThread()
     * before any task is created -- not here. Deferring it to watchdogTask
     * creates a cold-boot race: rfTask and uart_task (priority 2) run before
     * this task (priority 1) and can trigger a standby entry that freezes the
     * watchdog clock before it is ever armed. See mainThread() for the fix. */

    /* Kick immediately after arming -- gives a full 30-second window for
     * the NVS init sequence below. Without this kick, the timer starts
     * at Watchdog_open() and NVS operations that take >30s (degraded
     * sector) would expire the first timeout before we ever enter the
     * kick loop. CC1352P requires two timeouts to reset (NMI + reset),
     * so a pre-loop hang would take ~65s to recover -- unacceptable.
     * Kicking here resets the timer to a fresh 30s. */
    Watchdog_clear(sWatchdogHandle);


    /* Only open NVS on confirmed warm resets (watchdog or soft reset).
     *
     * NVS_open() hangs on cold power-on with a written NVS record regardless
     * of settle delay. The LaunchPad XDS110 does not assert RSTSRC_PWR_ON on
     * USB power cycle -- it asserts RSTSRC_PIN_RESET or RSTSRC_SYSRESET.
     * So we cannot gate on "is power-on" -- we must gate on "is warm reset."
     *
     * Only RSTSRC_SYSRESET (watchdog) needs NVS -- that's the only case
     * where we need to increment the counter and read back safe mode state.
     * All other reset causes use RAM defaults (gSafeMode=false, counter=0).
     *
     * SCK-DEV: NV_STORAGE -- Root cause: NVSInternal driver incompatible
     *   with cold-start flash access on CC1352P LaunchPad hardware. */
    if (gBootResetCause == SCK_RESET_CAUSE_SYSRESET  ||
        gBootResetCause == SCK_RESET_CAUSE_WARMRESET  ||
        gBootResetCause == SCK_RESET_CAUSE_PIN)
    {
        /* SYSRESET  = hardware watchdog expiry.
         * WARMRESET = software warm reset path.
         * PIN       = SysCtrlSystemReset() on CC1352P LaunchPad --
         *             confirmed by telemetry diagnostic: CMD_REBOOT
         *             asserts RSTSRC_PIN_RESET on this hardware.
         * All three are non-clean restarts that increment the counter.
         * POWER_ON is excluded -- NVS hangs on cold start. */
        sck_sleep_ms(500);
        nv_storage_init();
        gSafeMode = nv_storage_get_safe_mode();
    }

    uint32_t cleanRunAccumMs  = 0;
    bool     cleanRunReported = false;
    bool     bootWriteDone    = false;

    while (1)
    {
        Watchdog_clear(sWatchdogHandle);

        /* Sleep 10 seconds -- well within 30s watchdog timeout.
         * sck_sleep_ms() converts ms to Clock ticks using Clock_tickPeriod.
         * The old Task_sleep(10000) assumed 1 tick = 1 ms; if the kernel
         * tick is 10 us (common on CC26xx), that was only 100 ms, and the
         * 5-minute clean-run credit (cleanRunAccumMs below) was granted
         * after ~3 s of real run time.
         * Must not use usleep(10000000) -- usleep rejects >= 1 s (Bug C). */
        sck_sleep_ms(10000);

        /* Replay-table silence timeout (crypto.c): clears the table after
         * SCK_CRYPTO_SILENCE_RESET_S with no authenticated packet. */
        crypto_idle_tick(10u);

        /* Deferred boot write -- after first 10s kick+sleep.
         * Only on SYSRESET (watchdog) -- NVS is only open on that path. */
        if (!bootWriteDone)
        {
            bootWriteDone = true;
            if (gBootResetCause == SCK_RESET_CAUSE_SYSRESET  ||
                gBootResetCause == SCK_RESET_CAUSE_WARMRESET  ||
                gBootResetCause == SCK_RESET_CAUSE_PIN)
            {
                nv_storage_on_boot();
                gSafeMode = nv_storage_get_safe_mode();
            }
        }

        /* Accumulate clean run time.
         * Each kick loop iteration = ~10 seconds.
         * SCK_CLEAN_RUN_MS = 300000ms (5 minutes) = ~30 iterations. */
        if (!cleanRunReported)
        {
            cleanRunAccumMs += 10000U;
            if (cleanRunAccumMs >= SCK_CLEAN_RUN_MS)
            {
                /* Board has run stably for 5 minutes -- clear reset counter.
                 * nv_storage_on_clean_run() is a no-op if counter is already 0. */
                nv_storage_on_clean_run();
                /* Also clear gSafeMode in case it was set at boot but the
                 * board has now proven itself healthy for a full clean run. */
                gSafeMode        = false;
                cleanRunReported = true;  /* only do this once per boot */
            }
        }
    }
}

/* -----------------------------------------------------------------------
 * mainThread()
 *
 * Purpose:
 *   Called by main_tirtos.c after RTOS starts.
 *   Initializes drivers, creates all tasks, returns to RTOS scheduler.
 * --------------------------------------------------------------------- */
void *mainThread(void *arg0)
{
    Task_Params taskParams;

    /* Initialize GPIO driver FIRST.
     * GPIO_init() configures SPI pins as GPIO -- we reconfigure them
     * for SSI0 immediately after. */
    GPIO_init();
    GPIO_setConfig(CONFIG_GPIO_GLED, GPIO_CFG_OUT_STD | GPIO_CFG_OUT_LOW);
    GPIO_write(CONFIG_GPIO_GLED, 0);

    /* RF switch boot-safe state -- variant-dependent, see radio.c
     * top-of-file comment for the build flag this depends on.
     *
     * SKY13317-373LF: 0,0 is a real "Shutdown" state (all three
     * original control lines low) -- safe, defined boot default.
     *
     * SKYA21001: has no "off" state at all -- it always resolves to
     * one throw or the other whenever powered. Boot default is the
     * RX-safe state instead: antenna routed to the low-power direct
     * path (J1-J3), PA path deselected. Matches radio_rfSwitchSet()'s
     * RX branch in radio.c. */
#if RF_SWITCH_SKYA21001
    GPIO_write(RF_SW_2G4, 1);   /* select 2.4GHz-direct path (RX-safe) */
    GPIO_write(RF_SW_PA,  0);   /* deselect PA path                    */
#else
    GPIO_write(RF_SW_2G4, 0);
    GPIO_write(RF_SW_PA,  0);
#endif

    /* Initialize SPI driver subsystem.
     * SPI_init() zeroes the spiCC26X2DMAObjects[] array and marks all
     * instances as closed so SPI_open() can succeed.  Must be called once
     * before any SPI_open() call.  Board_init() in ti_drivers_config.c
     * does not call it automatically -- we do it here.
     * extFlashOpen() calls SPI_open(CONFIG_SPI_0, ...) from uart_task;
     * without this, SPI_open() returns NULL every time. */
    SPI_init();

    /* Initialize Watchdog driver subsystem.
     * Must be called before Watchdog_open() in watchdogTask.
     * Same pattern as SPI_init() -- Board_init() does not call this. */
    Watchdog_init();

    /* Prevent standby immediately after watchdog init -- before any task runs.
     * On CC1352P the watchdog clock stops when the device enters standby, so
     * the timer never expires and a hung task cannot trigger a reset.
     * This constraint must be set here in mainThread(), NOT deferred to
     * watchdogTask(), because on a cold power-up the scheduler can dispatch
     * rfTask or uart_task (both priority 2) before watchdogTask (priority 1)
     * ever runs. The device can enter standby in that window, freezing the
     * watchdog clock permanently. Setting the constraint before Task_create()
     * closes the race. watchdogTask holds it for the lifetime of the session.
     *
     * SCK-DEV: WATCHDOG -- Do not remove this or move it into watchdogTask.
     *   This is the fix for the USB cold-power-cycle hang (boards 010/011). */
    Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY);

    /* ---------------------------------------------------------------
     * Capture hardware reset cause NOW -- before any driver init.
     *
     * SysCtrlResetSourceGet() reads PMCTL:RESETCTL. Some reset sources
     * are cleared by RF driver or power manager init. Capturing here
     * guarantees we see the real cause regardless of what happens next.
     *
     * NVS_init(), nv_storage_init(), and nv_storage_on_boot() are all
     * deferred to watchdogTask. Any NVS_write() that hangs in task
     * context will starve watchdogTask, the watchdog fires in ~30s,
     * and the board self-recovers. The same hang in mainThread() before
     * any tasks start produces a completely dead board with no recovery.
     *
     * gSafeMode defaults false -- watchdogTask sets it correctly after
     * reading NVS. Tasks start in normal mode; safe mode gates in
     * uart_task and rfTask take effect once watchdogTask writes the
     * authoritative value (within the first 10s kick interval).
     * ------------------------------------------------------------- */
    gBootResetCause = nv_storage_capture_reset_cause();
    gSafeMode       = false;  /* conservative default -- watchdogTask corrects */

    /* Power/SSI0 pre-init removed -- SPI driver manages power internally.
     * Power_setDependency(PowerCC26XX_PERIPH_SSI0) + SSIEnable(SSI0_BASE)
     * left here as commented reference only. */
    /*Power_setDependency(PowerCC26XX_PERIPH_SSI0);
    SSIEnable(SSI0_BASE);*/

    /* ---------------------------------------------------------------
     * Create RF task
     * radio_init() is called inside rfTask -- not here.
     * RF driver must be opened from the task that will use it.
     * ------------------------------------------------------------- */
    Task_Params_init(&taskParams);
    taskParams.stackSize = SCK_TASK_STACK_RF;
    taskParams.stack     = sRfTaskStack;
    taskParams.priority  = SCK_TASK_PRI_RF;
    taskParams.arg0      = 0;
    taskParams.arg1      = 0;
    rfTaskHandle = Task_create(rfTask, &taskParams, NULL);

    /* ---------------------------------------------------------------
     * Create UART task
     * uart_init() is called inside uart_task -- not here.
     * ------------------------------------------------------------- */
    Task_Params_init(&taskParams);
    taskParams.stackSize = SCK_TASK_STACK_UART;
    taskParams.stack     = sUartTaskStack;
    taskParams.priority  = SCK_TASK_PRI_UART;
    taskParams.arg0      = 0;
    taskParams.arg1      = 0;
    uartTaskHandle = Task_create(uart_task, &taskParams, NULL);

    /* ---------------------------------------------------------------
     * Create Watchdog task
     * Lowest priority -- intentionally preemptable by all work tasks.
     * A hang in rfTask or uartTask starves this task, watchdog expires,
     * CC1352P resets. Required for LEO fault recovery.
     * ------------------------------------------------------------- */
    Task_Params_init(&taskParams);
    taskParams.stackSize = 512;
    taskParams.stack     = sWatchdogTaskStack;
    taskParams.priority  = 1;  /* lowest -- below rfTask and uartTask */
    taskParams.arg0      = 0;
    taskParams.arg1      = 0;
    watchdogTaskHandle = Task_create(watchdogTask, &taskParams, NULL);

    /* ---------------------------------------------------------------
     * Create OAD task
     * Owns SSI0 exclusively -- all ext flash access goes through here.
     * Uses Power_setConstraint(DISALLOW_STANDBY) during flash ops.
     * ------------------------------------------------------------- */

    return NULL;
}