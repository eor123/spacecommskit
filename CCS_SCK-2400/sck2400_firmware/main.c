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
 * Target (dev):   CC1352P1F3RGZ on LAUNCHXL-CC1352P-2
 * Target (prod):  CC2652P1FRGZ on SCK-2400 custom board
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

/* gOadActive: set by uart.c during OAD, read here by rfTask sleep logic */
extern volatile bool gOadActive;

/*
 * rf_forward_enqueue()
 * Called from uart_dispatch_ccsds_packet() when a command arrives via
 * UART addressed to a remote board APID. Queues it for rfTask to TX.
 * Parameters:
 *   buf -- CCSDS packet to forward
 *   len -- packet length
 */
void rf_forward_enqueue(const uint8_t *buf, uint16_t len)
{
    if (buf == NULL || len == 0 ||
        len > sizeof(sRfForwardBuf) || sRfForwardPending)
    {
        return;  /* drop if busy -- GS will retry on timeout */
    }
    memcpy(sRfForwardBuf, buf, len);
    sRfForwardLen     = len;
    sRfForwardPending = true;
}

static void rfTask(uintptr_t arg0, uintptr_t arg1)
{
    bool rfOk = radio_init();

    if (!rfOk)
    {
        /* RF init failed -- fast blink status LED to indicate fault.
         * In production this should trigger a watchdog reset.
         * For bringup: set a breakpoint here to catch RF init failure. */
        while (1)
        {
            GPIO_write(CONFIG_GPIO_GLED, 1);
            usleep(100000);   /* 100ms on -- fast blink indicates RF fault */
            GPIO_write(CONFIG_GPIO_GLED, 0);
            usleep(100000);   /* 100ms off */
        }
    }

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
                    uart_dispatch_ccsds_packet(sRxPktBuf, rxLen, true);
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
            usleep(500000);   /* 500ms response window */
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

        /* Beacon interval sleep.
         * During OAD: sleep only 1ms so rfTask drains the RX queue fast
         * enough to keep up with incoming chunks (~50 Hz at 20ms interval).
         * The RX queue holds 4 entries; without this the queue fills and
         * chunks are silently dropped while rfTask sleeps 950ms.
         * Outside OAD: full 950ms gives a 1Hz beacon rate. */
#if !SCK_IS_GS_BOARD
        usleep(gOadActive ? 1000 : SCK_BEACON_REST_US);
#else
        usleep(SCK_BEACON_REST_US);
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
    wdParams.resetMode      = Watchdog_RESET_ON;       /* hardware reset on timeout */
    wdParams.debugStallMode = Watchdog_DEBUG_STALL_ON; /* pause watchdog in debugger */

    sWatchdogHandle = Watchdog_open(CONFIG_WATCHDOG_0, &wdParams);
    if (sWatchdogHandle == NULL)
    {
        /* Watchdog failed to open -- fault condition.
         * Should never happen in production. Spin here so debugger
         * can catch it on bringup. */
        while (1) {}
    }

    /* Prevent device from entering standby -- watchdog clock stops in standby
     * on CC1352P, so the timer never expires and the reset never fires.
     * This constraint must be held for the lifetime of the watchdog task. */
    Power_setConstraint(PowerCC26XX_DISALLOW_STANDBY);

    while (1)
    {
        Watchdog_clear(sWatchdogHandle);
        usleep(10000000);  /* kick every 10s -- well within 30s timeout */
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
    GPIO_write(RF_SW_2G4, 0);
    GPIO_write(RF_SW_PA,  0);

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