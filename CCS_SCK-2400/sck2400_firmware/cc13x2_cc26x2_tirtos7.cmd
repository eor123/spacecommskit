/*
 * cc13x2_cc26x2_tirtos7.cmd
 * SCK-2400 Application Linker Script -- OAD enabled
 *
 * Flash layout:
 *   0x00000000 - 0x00003FFF  BIM (pages 0-1, 16KB)
 *   0x00004000 - 0x00051FFF  Application (pages 2-40, 312KB)
 *   0x00052000 - 0x00053FFF  NVS (pages 41-42, 8KB)
 *   0x00054000 - 0x00055FFF  Reserved
 *   0x00056000 - 0x00057FFF  CCFG (page 43, 8KB)
 *
 * The OAD image header (_imgHdr) is placed at the very start of the
 * application region (0x00004000). BIM reads it to validate and boot
 * the application.
 *
 * The OAD entry vector (oad_entry_vec) immediately follows the header.
 * BIM jumpToPrgEntry reads SP+ResetISR from this vector.
 */

--stack_size=0x600   /* C stack is also used for ISR stack */

HEAPSIZE = 0x4000;  /* Size of heap buffer used by HeapMem */

--retain "*(.resetVecs)"
--retain "*(.image_header)"
--retain "*(.oad_entry_vec)"

/* Override default entry point */
--entry_point ResetISR
/* Allow main() to take args */
--args 0x8
/* Suppress warnings and errors */
--diag_suppress=10063,16011,16012
--diag_remark=10068

#define FLASH_BASE              0x00004000  /* Application starts after BIM */
#define FLASH_SIZE              0x0004E000  /* 312KB for app (pages 2-40) */
#define RAM_BASE                0x20000000
#define RAM_SIZE                0x14000
#define GPRAM_BASE              0x11000000
#define GPRAM_SIZE              0x2000

/* NVS region -- immediately after app flash, at 0x52000 (pages 41-42).
 * Placed AFTER the app flash region end (0x4000+0x4E000=0x52000) so BIM
 * never erases it -- BIM copies exactly FLASH_SIZE bytes starting at
 * FLASH_BASE, ending at 0x52000. NVS starts where BIM stops.
 * srfprog -e all erases these pages on every flash cycle (expected).
 * SCK-DEV: NV_STORAGE -- do not place code or initialized data here. */
#define NVS_BASE                0x00052000
#define NVS_SIZE                0x00002000  /* 8KB = two 4KB sectors (0x52000-0x53FFF) */

MEMORY
{
    FLASH (RX) : origin = FLASH_BASE, length = FLASH_SIZE
    NVS   (RW) : origin = NVS_BASE,   length = NVS_SIZE
    SRAM (RWX) : origin = RAM_BASE, length = RAM_SIZE
    GPRAM (RWX): origin = GPRAM_BASE, length = GPRAM_SIZE
    LOG_DATA (R) : origin = 0x90000000, length = 0x40000
    LOG_PTR  (R) : origin = 0x94000008, length = 0x40000
}

SECTIONS
{
    /* OAD image header MUST be first -- BIM reads it at FLASH_BASE */
    .image_header   :   > FLASH_BASE

    /* OAD entry vector -- immediately after header */
    .oad_entry_vec  :   > FLASH

    .resetVecs      :   > FLASH
    .text           :   >> FLASH
    .TI.ramfunc     : {} load=FLASH, run=SRAM, table(BINIT)
    .const          :   >> FLASH
    .constdata      :   >> FLASH
    .rodata         :   >> FLASH
    .binit          :   > FLASH
    .cinit          :   > FLASH
    .pinit          :   > FLASH
    .init_array     :   > FLASH
    .emb_text       :   >> FLASH
    .ccfg           :   > FLASH (HIGH)

    /* NVS data region -- pinned to fixed address in NVS memory region.
     * The TI NVSInternal driver places its flash data in .nvs sections.
     * Pinning here ensures srfprog -e all always erases this region and
     * the linker cannot accidentally place code here. */
    .nvs            :   > NVS, type = NOLOAD

    .ramVecs        :   > SRAM, type = NOLOAD, ALIGN(256)
    .data           :   > SRAM
    .bss            :   > SRAM
    .sysmem         :   > SRAM
    .stack          :   > SRAM (HIGH)
    .nonretenvar    :   > SRAM
    .priheap   : {
        __primary_heap_start__ = .;
        . += HEAPSIZE;
        __primary_heap_end__ = .;
    } > SRAM align 8
    .gpram          :   > GPRAM
    .log_data       :   > LOG_DATA, type = COPY
    .log_ptr        : { *(.log_ptr*) } > LOG_PTR align 4, type = COPY
}

--symbol_map __TI_STACK_SIZE=__STACK_SIZE
--symbol_map __TI_STACK_BASE=__stack

-u_c_int00
