/*
 * oad_image_header_app.c
 * SCK-2400 Firmware -- OAD Image Header
 *
 * Placed at the very start of application flash (0x00004000).
 * BIM reads this header to validate and boot the application.
 *
 * oad_image_tool fills in: crc32, imgCpStat, crcStat, imgVld,
 * len, imgSegLen, and imgEndAddr automatically after build.
 *
 * Reference: source/ti/common/cc26xx/oad/oad_image_header.h
 */

#include <stdint.h>
#include <ti/devices/DeviceFamily.h>
#include "ti/common/cc26xx/oad/oad_image_header.h"

extern void ResetISR(void);

/* OAD entry vector -- BIM jumpToPrgEntry reads SP+Reset from here */
__attribute__((section(".oad_entry_vec"), used, aligned(4)))
static const uint32_t oad_entry_vec[2] = {
    0x20013A00,              /* Initial SP */
    (uint32_t)ResetISR + 1, /* Reset handler = ResetISR (Thumb) */
};

/* Place header at start of application flash region.
 * TI Clang uses __attribute__ instead of legacy #pragma. */
__attribute__((section(".image_header")))
__attribute__((used))
const imgHdr_t _imgHdr =
{
    .fixedHdr =
    {
        .imgID      = OAD_IMG_ID_VAL,           /* {'C','C','1','3','x','2','R','1'} */
        .crc32      = DEFAULT_CRC,              /* 0xFFFFFFFF -- filled by oad_image_tool */
        .bimVer     = BIM_VER,                  /* 0x03 */
        .metaVer    = META_VER,                 /* 0x01 */
        .techType   = OAD_WIRELESS_TECH_PROPRF, /* 0xFFBF -- proprietary RF */
        .imgCpStat  = COPY_DONE,                /* 0xFC -- image already in internal flash */
        .crcStat    = CRC_VALID,                /* 0xFE -- CRC valid, BIM will boot this image */
        .imgType    = OAD_IMG_TYPE_APPSTACKLIB, /* 0x07 -- app+stack linked together */
        .imgNo      = 0x00,
        .imgVld     = 0xFFFFFFFF,               /* filled by oad_image_tool */
        .len        = 0x00009D90,               /* image length: last section end (0xD5D8) - start (0x4000) -- auto-patched by ground station */
        .prgEntry   = 0x0000DAF0,               /* reset vector table -- jumpToPrgEntry reads SP+PC from here */
        .softVer    = {0x00, 0x01, 0x00, 0x00}, /* version 0.1.0.0 */
        .imgEndAddr = 0xFFFFFFFF,               /* filled by oad_image_tool */
        .hdrLen     = OAD_IMG_FULL_HDR_LEN,
        .rfu        = 0xFFFF,
    },
    .imgPayload =
    {
        .segTypeImg  = IMG_PAYLOAD_SEG_ID,      /* 0x01 */
        .wirelessTech = OAD_WIRELESS_TECH_PROPRF,
        .rfu         = DEFAULT_STATE,
        .imgSegLen   = 0xFFFFFFFF,              /* filled by oad_image_tool */
        .startAddr   = 0x00004000,              /* application start address */
    },
};
