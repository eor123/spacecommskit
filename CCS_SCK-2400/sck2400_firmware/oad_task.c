/*
 * oad_task.c -- no Power driver includes
 */
#include <stdint.h>
#include <stdbool.h>
#include "ext_flash.h"
#include "oad_task.h"

void oad_task_init(void) { }
void oad_task_func(uintptr_t arg0, uintptr_t arg1) { (void)arg0; (void)arg1; }

bool oad_task_request(oad_req_type_t type, uint32_t offset,
                      uint32_t length, uint8_t *buf)
{
    bool ok = false;
    switch (type) {
        case OAD_REQ_TEST:
            ok = extFlashOpen(); if (ok) extFlashClose(); break;
        case OAD_REQ_ERASE:
            ok = extFlashOpen();
            if (ok) { ok = extFlashErase(offset, length); extFlashClose(); }
            break;
        case OAD_REQ_WRITE:
            ok = extFlashOpen();
            if (ok) { ok = extFlashWrite(offset, length, buf); extFlashClose(); }
            break;
        case OAD_REQ_READ:
            ok = extFlashOpen();
            if (ok) { ok = extFlashRead(offset, length, buf); extFlashClose(); }
            break;
        default: break;
    }
    return ok;
}
