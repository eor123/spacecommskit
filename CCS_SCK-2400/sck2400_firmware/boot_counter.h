/*
 * boot_counter.h
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   Monotonic boot counter kept in external flash. Every boot gets a
 *   number larger than any boot before it, which makes the AES-CCM nonce
 *   unique across reboots (see crypto.c, build_nonce()).
 *
 * Storage:
 *   Two 4 KB pages at the top of the MX25R8035F, used ping-pong:
 *     SCK_BOOTCTR_PAGE_A  0x0FE000
 *     SCK_BOOTCTR_PAGE_B  0x0FF000
 *   Each page holds up to 512 8-byte slots { count, ~count } written in
 *   order (1 -> 0 bit programming only, no erase per boot). A page is
 *   erased only when the other page fills, so each page is erased once
 *   every 1024 boots. A slot counts only if its second word is the exact
 *   complement of the first, so a slot cut short by a power loss, or a
 *   page left half-erased, is ignored -- it can never read as a huge
 *   count. The stored count is the largest valid slot on either page.
 *
 *   External flash is not touched by the ROM bootloader or a J2 reflash,
 *   so the counter survives J4 / J2 recovery.
 *
 * SCK-DEV: FLASH_MAP -- these two pages must stay outside the OAD
 *   candidate (0x001000-0x04FFFF) and golden (0x080000-0x0CDFFF) areas.
 */

#ifndef BOOT_COUNTER_H
#define BOOT_COUNTER_H

#include <stdint.h>
#include <stdbool.h>

#define SCK_BOOTCTR_PAGE_A      0x0FE000u
#define SCK_BOOTCTR_PAGE_B      0x0FF000u
#define SCK_BOOTCTR_PAGE_SIZE   0x1000u
#define SCK_BOOTCTR_SLOTS       (SCK_BOOTCTR_PAGE_SIZE / 8u)   /* 512 */

/*
 * boot_counter_next()
 * Read the stored boot count, store count + 1, and return it.
 * Call once per boot, from rfTask, before the first RF transmit.
 *
 * Returns the new boot count (>= 1) on success.
 * Returns 0 if external flash could not be read or written; the caller
 * still runs (crypto uses boot count 0), but peers that have already
 * seen a higher count will reject this board until their replay table is
 * cleared (CMD_CRYPTO_RESET_REPLAY on the GS board, or the 6-hour
 * silence reset on a remote).
 */
uint32_t boot_counter_next(void);

#endif /* BOOT_COUNTER_H */
