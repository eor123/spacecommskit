/*
 * boot_counter.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Monotonic boot counter in external flash. See boot_counter.h for the
 * storage layout and why it exists.
 */

#include "boot_counter.h"
#include "ti/common/flash/no_rtos/extFlash/ext_flash.h"

#include <stddef.h>

#define BOOTCTR_EMPTY       0xFFFFFFFFu
#define BOOTCTR_MAX_VALUE   0xFFFFFF00u   /* stop well short of the erased value */
#define BOOTCTR_READ_SLOTS  32u           /* slots per SPI read (256 bytes)     */

typedef struct
{
    uint32_t count;
    uint32_t check;     /* == ~count when the slot is valid */
} bootctr_slot_t;

/* Scan one page.
 *   *maxValue -- largest valid count on the page (0 if none)
 *   *nextFree -- index just after the last slot that is not fully erased
 *                (where the next slot may be programmed) */
static bool scan_page(uint32_t pageAddr, uint32_t *maxValue, uint32_t *nextFree)
{
    static bootctr_slot_t buf[BOOTCTR_READ_SLOTS];   /* static: 256 B off the stack */
    uint32_t maxV = 0;
    uint32_t next = 0;

    for (uint32_t base = 0; base < SCK_BOOTCTR_SLOTS; base += BOOTCTR_READ_SLOTS)
    {
        if (!extFlashRead(pageAddr + base * sizeof(bootctr_slot_t),
                          sizeof(buf), (uint8_t *)buf))
            return false;

        for (uint32_t i = 0; i < BOOTCTR_READ_SLOTS; i++)
        {
            const bootctr_slot_t *s = &buf[i];
            if (s->count != BOOTCTR_EMPTY || s->check != BOOTCTR_EMPTY)
                next = base + i + 1u;                 /* slot has been touched */

            if (s->check == ~s->count && s->count != 0u &&
                s->count <= BOOTCTR_MAX_VALUE && s->count > maxV)
                maxV = s->count;
        }
    }

    *maxValue = maxV;
    *nextFree = next;
    return true;
}

uint32_t boot_counter_next(void)
{
    uint32_t maxA = 0, freeA = 0, maxB = 0, freeB = 0;

    if (!extFlashOpen())
        return 0;

    if (!scan_page(SCK_BOOTCTR_PAGE_A, &maxA, &freeA) ||
        !scan_page(SCK_BOOTCTR_PAGE_B, &maxB, &freeB))
    {
        extFlashClose();
        return 0;
    }

    uint32_t last = (maxA >= maxB) ? maxA : maxB;
    if (last >= BOOTCTR_MAX_VALUE)
    {
        extFlashClose();
        return 0;   /* ~4 billion boots: not reachable in practice */
    }

    /* Active page = the one holding the highest count (A on fresh flash). */
    bool     activeIsA = (maxA >= maxB);
    uint32_t activePg  = activeIsA ? SCK_BOOTCTR_PAGE_A : SCK_BOOTCTR_PAGE_B;
    uint32_t otherPg   = activeIsA ? SCK_BOOTCTR_PAGE_B : SCK_BOOTCTR_PAGE_A;
    uint32_t freeSlot  = activeIsA ? freeA : freeB;

    uint32_t addr;
    if (last != 0u && freeSlot < SCK_BOOTCTR_SLOTS)
    {
        /* Room left on the active page. */
        addr = activePg + freeSlot * sizeof(bootctr_slot_t);
    }
    else
    {
        /* Active page full, or fresh/unreadable flash: start a clean page.
         * The full page keeps its (smaller) counts until this one fills in
         * turn -- reading always takes the largest valid count. */
        uint32_t pg = (last == 0u) ? SCK_BOOTCTR_PAGE_A : otherPg;
        if (!extFlashErase(pg, SCK_BOOTCTR_PAGE_SIZE))
        {
            extFlashClose();
            return 0;
        }
        addr = pg;
    }

    bootctr_slot_t slot = { last + 1u, ~(last + 1u) };
    bool ok = extFlashWrite(addr, sizeof(slot), (const uint8_t *)&slot);

    /* Read back: a count we failed to store must not be used, or the next
     * boot would reuse it (and with it, nonces). */
    bootctr_slot_t check = { 0, 0 };
    if (ok)
        ok = extFlashRead(addr, sizeof(check), (uint8_t *)&check) &&
             check.count == slot.count && check.check == slot.check;

    extFlashClose();
    return ok ? slot.count : 0u;
}
