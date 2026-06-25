/*
 * sck2400_bim.cmd
 * Linker command file for SCK-2400 BIM (Boot Image Manager)
 * Off-chip OAD variant -- unsecured
 *
 * CC1352P1F3RGZ memory map:
 *   Internal flash: 352KB (0x00000000 - 0x00057FFF)
 *   Flash page size: 8KB (0x2000)
 *   Total pages: 44 (pages 0-43)
 *   CCFG: last flash page, page 43 (0x00056000 - 0x00057FFF)
 *   SRAM: 80KB (0x20000000 - 0x20013FFF)
 *
 * BIM flash layout (off-chip OAD):
 *   BIM occupies pages 0-1 (0x00000000 - 0x00003FFF, 16KB)
 *   Application starts at page 2 (0x00004000)
 *   CCFG occupies page 43 (last page)
 *
 * Note: BIM is a no-RTOS bare-metal application.
 *       It must NOT use TI-RTOS or any RTOS-dependent drivers.
 */
--retain "*(.resetVecs)"
--stack_size=256
--heap_size=0

/* Suppress warnings about unused sections */
--diag_suppress=10068

MEMORY
{
    /* BIM region: pages 0-1, 16KB */
    FLASH_BIM   (RX)  : origin = 0x00000000, length = 0x00004000

    /* CCFG region: last flash page (page 43) */
    FLASH_CCFG  (RX)  : origin = 0x00056000, length = 0x00002000

    /* SRAM: 80KB -- BIM uses very little RAM */
    SRAM        (RWX) : origin = 0x20000000, length = 0x00014000
}

SECTIONS
{
    .resetVecs      : > 0x00000000
    .text           : > FLASH_BIM
    .const          : > FLASH_BIM
    .constdata      : > FLASH_BIM
    .rodata         : > FLASH_BIM
    .cinit          : > FLASH_BIM
    .pinit          : > FLASH_BIM
    .init_array     : > FLASH_BIM
    .emb_text       : > FLASH_BIM

    .ccfg           : > FLASH_CCFG (HIGH)

    .data           : > SRAM
    .bss            : > SRAM
    .sysmem         : > SRAM
    .stack          : > SRAM (HIGH)
    .nonretenvar    : > SRAM
}
