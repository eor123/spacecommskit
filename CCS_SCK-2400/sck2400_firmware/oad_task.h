/*
 * oad_task.h
 * SCK-2400 Firmware -- OAD Task Interface
 *
 * The OAD task owns SSI0 exclusively. It is the only task that calls
 * extFlashOpen/Close. The UART task posts requests via oad_task_request()
 * and waits for the result.
 *
 * This isolation is required because:
 *  - SSI register access from UART/RF task context crashes (Power driver
 *    puts SSI0 to sleep between task switches)
 *  - SPI_open() causes UDMA conflict with RF driver
 *  - Power_setConstraint(DISALLOW_STANDBY) must be held for the full
 *    duration of flash access -- safe only in a dedicated task
 */

#ifndef OAD_TASK_H
#define OAD_TASK_H

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

/* -----------------------------------------------------------------------
 * OAD request types
 * --------------------------------------------------------------------- */
typedef enum {
    OAD_REQ_NONE   = 0,
    OAD_REQ_ERASE  = 1,   /* erase flash region */
    OAD_REQ_WRITE  = 2,   /* write chunk to flash */
    OAD_REQ_READ   = 3,   /* read chunk from flash */
    OAD_REQ_TEST   = 4,   /* open/close flash (verify chip present) */
} oad_req_type_t;

/* -----------------------------------------------------------------------
 * OAD request/result -- shared between uart_task and oad_task
 * --------------------------------------------------------------------- */
typedef struct {
    oad_req_type_t  type;
    uint32_t        offset;     /* flash offset */
    uint32_t        length;     /* byte count */
    uint8_t        *buf;        /* data buffer (caller-owned) */
    bool            result;     /* true = success */
} oad_request_t;

/* -----------------------------------------------------------------------
 * API
 * --------------------------------------------------------------------- */

/*
 * oad_task_init()
 * Initialize OAD task semaphores. Call from mainThread() before
 * creating the OAD task.
 */
void oad_task_init(void);

/*
 * oad_task_func()
 * OAD task entry point. Pass to Task_create() in mainThread().
 */
void oad_task_func(uintptr_t arg0, uintptr_t arg1);

/*
 * oad_task_request()
 * Submit a flash request from uart_task and block until complete.
 * Returns true on success. Safe to call from any task context.
 */
bool oad_task_request(oad_req_type_t type, uint32_t offset,
                      uint32_t length, uint8_t *buf);

#endif /* OAD_TASK_H */
