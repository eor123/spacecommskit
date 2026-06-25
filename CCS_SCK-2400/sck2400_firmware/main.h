/*
 * main.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   Top-level header for sck2400_firmware. Includes common defines,
 *   RTOS task handles, and shared state visible across modules.
 *
 * Target (dev):   CC1352P1F3RGZ on LAUNCHXL-CC1352P-2
 * Target (prod):  CC2652P1FRGZ on SCK-2400 custom board
 * Toolchain:      TI Clang + TI-RTOS7
 *
 * SCK-DEV: This file is the top-level include for all modules.
 *          Add new task handles and shared flags here.
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3, 7
 */

#ifndef MAIN_H
#define MAIN_H

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include <stdint.h>
#include <stdbool.h>

/* TI-RTOS */
#include <ti/sysbios/BIOS.h>
#include <ti/sysbios/knl/Task.h>

/* -----------------------------------------------------------------------
 * Firmware Version
 * --------------------------------------------------------------------- */
#define SCK2400_FW_VERSION_MAJOR    0
#define SCK2400_FW_VERSION_MINOR    1
#define SCK2400_FW_VERSION_PATCH    0

/* -----------------------------------------------------------------------
 * RTOS Task Stack Sizes (bytes)
 * Per requirements Section 7.
 * --------------------------------------------------------------------- */
#define SCK_TASK_STACK_RF       2048   /* increased -- dispatch chain uses ~400 bytes */
#define SCK_TASK_STACK_UART     2048   /* increased -- payload handlers need room */
#define SCK_TASK_STACK_MAIN      512
#define SCK_TASK_STACK_OAD      2048   /* OAD task -- owns SSI0 flash access */

/* -----------------------------------------------------------------------
 * RTOS Task Priorities
 * --------------------------------------------------------------------- */
#define SCK_TASK_PRI_RF          2
#define SCK_TASK_PRI_UART        2
#define SCK_TASK_PRI_MAIN        1
#define SCK_TASK_PRI_OAD         1   /* lower than RF/UART -- flash ops are non-urgent */

/* -----------------------------------------------------------------------
 * Task Handle Externs
 * Defined in main.c, referenced by other modules if needed.
 * --------------------------------------------------------------------- */
extern Task_Handle rfTaskHandle;
extern Task_Handle uartTaskHandle;
extern Task_Handle oadTaskHandle;
extern Task_Handle watchdogTaskHandle;

/* -----------------------------------------------------------------------
 * RF Forward Queue
 * Defined in main.c -- called from uart.c when a UART command is
 * addressed to a remote board APID and needs to be forwarded over RF.
 * Only active on GS board (SCK_IS_GS_BOARD). Remote board build
 * compiles this out via #if SCK_IS_GS_BOARD in uart_dispatch_ccsds_packet().
 * --------------------------------------------------------------------- */
extern void rf_forward_enqueue(const uint8_t *buf, uint16_t len);

#endif /* MAIN_H */
