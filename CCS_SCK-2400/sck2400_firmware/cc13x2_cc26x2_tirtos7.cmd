/*
 * cc13x2_cc26x2_tirtos7.cmd
 * SCK-2400 Application Linker Script -- OAD enabled
 *
 * Flash layout:
 *   0x00000000 - 0x00003FFF  BIM (pages 0-1, 16KB)
 *   0x00004000 - 0x00055FFF  Application (pages 2-42, 328KB)
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
#define FLASH_SIZE              0x00052000  /* 328KB for app (pages 2-42) */
#define RAM_BASE                0x20000000
#define RAM_SIZE                0x14000
#define GPRAM_BASE              0x11000000
#define GPRAM_SIZE              0x2000

MEMORY
{
    FLASH (RX) : origin = FLASH_BASE, length = FLASH_SIZE
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
