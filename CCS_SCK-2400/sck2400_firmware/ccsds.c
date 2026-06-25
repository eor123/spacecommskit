/*
 * ccsds.c
 * SCK-2400 Firmware -- SpaceCommsKit
 *
 * Purpose:
 *   CCSDS Space Packet Protocol implementation.
 *   Header construction, APID extraction, and packet validation.
 *
 * Reference: SCK-2400_Firmware_Requirements.md Section 3.5
 *            CCSDS 133.0-B-2
 */

/* -----------------------------------------------------------------------
 * Includes
 * --------------------------------------------------------------------- */
#include "ccsds.h"

#include <stdint.h>
#include <stdbool.h>
#include <string.h>

/* -----------------------------------------------------------------------
 * ccsds_get_apid()
 * --------------------------------------------------------------------- */
uint16_t ccsds_get_apid(const ccsds_primary_header_t *hdr)
{
    if (hdr == NULL)
    {
        return 0;
    }
    /* APID is in the lower 11 bits of the first 16-bit word.
     * Header is big-endian on-wire; swap bytes if needed on CC2652P
     * (little-endian). */
    uint16_t word = ((uint16_t)((uint8_t *)hdr)[0] << 8) |
                     (uint8_t)((uint8_t *)hdr)[1];
    return word & CCSDS_APID_MASK;
}

/* -----------------------------------------------------------------------
 * ccsds_build_header()
 * --------------------------------------------------------------------- */
void ccsds_build_header(ccsds_primary_header_t *hdr,
                        uint16_t apid,
                        bool     isCmd,
                        uint16_t seqCount,
                        uint16_t dataLen)
{
    if (hdr == NULL)
    {
        return;
    }

    /* Word 0: version(000) | type | shf(1) | apid */
    uint16_t word0 = 0;
    word0 |= (isCmd ? 1U : 0U) << CCSDS_TYPE_SHIFT;
    word0 |= 1U << CCSDS_SHF_SHIFT;          /* secondary header present */
    word0 |= (apid & CCSDS_APID_MASK);

    /* Word 1: seq_flags(11) | seq_count */
    uint16_t word1 = 0;
    word1 |= (uint16_t)(CCSDS_SEQ_UNSEGMENTED) << CCSDS_SEQ_FLAGS_SHIFT;
    word1 |= (seqCount & CCSDS_SEQ_COUNT_MASK);

    /* Word 2: data length field = dataLen - 1 */
    uint16_t word2 = (dataLen > 0) ? (dataLen - 1) : 0;

    /* Store big-endian */
    uint8_t *p = (uint8_t *)hdr;
    p[0] = (uint8_t)(word0 >> 8);
    p[1] = (uint8_t)(word0 & 0xFF);
    p[2] = (uint8_t)(word1 >> 8);
    p[3] = (uint8_t)(word1 & 0xFF);
    p[4] = (uint8_t)(word2 >> 8);
    p[5] = (uint8_t)(word2 & 0xFF);
}

/* -----------------------------------------------------------------------
 * ccsds_validate()
 * --------------------------------------------------------------------- */
bool ccsds_validate(const uint8_t *buf, uint16_t bufLen)
{
    if (buf == NULL || bufLen < sizeof(ccsds_primary_header_t))
    {
        return false;
    }

    const ccsds_primary_header_t *hdr = (const ccsds_primary_header_t *)buf;

    /* Version field must be 0b000 (bits 15-13 of first word) */
    uint16_t word0 = ((uint16_t)buf[0] << 8) | buf[1];
    if ((word0 >> 13) != 0)
    {
        return false;
    }

    /* Check declared length vs actual buffer */
    uint16_t word2   = ((uint16_t)buf[4] << 8) | buf[5];
    uint16_t dataLen = word2 + 1;
    uint16_t totalExpected = sizeof(ccsds_primary_header_t) + dataLen;

    if (bufLen < totalExpected)
    {
        return false;
    }

    (void)hdr; /* suppress unused warning until fully implemented */
    return true;
}
