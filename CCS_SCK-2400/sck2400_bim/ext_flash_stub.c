/*
 * ext_flash_stub.c
 * SCK-2400 BIM -- External flash driver for MX25R8035F
 *
 * Implements extFlash* and bspSpi* interfaces using CC13x2 SSI0 driverlib.
 *
 * Pin assignments from bsp.h and confirmed by ti_drivers_config.c:
 *   SSI0 CLK  = DIO10  (BSP_SPI_CLK_FLASH)
 *   SSI0 MOSI = DIO9   (BSP_SPI_MOSI / PICO)
 *   SSI0 MISO = DIO8   (BSP_SPI_MISO / POCI)
 *   SSI0 CS   = DIO20  (BSP_IOID_FLASH_CS)
 *
 * Wakeup sequence from Board_wakeUpExtFlash() in ti_drivers_config.c:
 *   Toggle CS low briefly then high, wait 35us.
 *   Do NOT send 0xAB -- CS toggle is the wakeup mechanism.
 *
 * MX25R8035F: 1MB, manfId=0xC2, devId=0x14
 */

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>

#include <ti/devices/DeviceFamily.h>
#include DeviceFamily_constructPath(driverlib/ssi.h)
#include DeviceFamily_constructPath(driverlib/prcm.h)
#include DeviceFamily_constructPath(driverlib/ioc.h)
#include DeviceFamily_constructPath(driverlib/gpio.h)
#include DeviceFamily_constructPath(driverlib/rom.h)
#include DeviceFamily_constructPath(driverlib/cpu.h)

#include "ti/common/flash/no_rtos/extFlash/ext_flash.h"

/* -----------------------------------------------------------------------
 * Pin assignments -- confirmed from bsp.h + ti_drivers_config.c
 * --------------------------------------------------------------------- */
#define FLASH_CLK_IOID      IOID_10   /* BSP_SPI_CLK_FLASH */
#define FLASH_MOSI_IOID     IOID_9    /* BSP_SPI_MOSI      */
#define FLASH_MISO_IOID     IOID_8    /* BSP_SPI_MISO      */
#define FLASH_CS_IOID       IOID_20   /* BSP_IOID_FLASH_CS */

#define FLASH_SSI_BASE      SSI0_BASE
#define FLASH_SPI_BITRATE   4000000UL
#define CPU_CLOCK_HZ        48000000UL

/* -----------------------------------------------------------------------
 * MX25R8035F command codes
 * --------------------------------------------------------------------- */
#define MX25_CMD_READ       0x03
#define MX25_CMD_PP         0x02
#define MX25_CMD_SE         0x20
#define MX25_CMD_WREN       0x06
#define MX25_CMD_RDSR       0x05
#define MX25_CMD_MDID       0x90
#define MX25_CMD_DP         0xB9
#define MX25_STATUS_WIP     0x01
#define MX25_PAGE_SIZE      256
#define MX25_SECTOR_SIZE    4096

/* -----------------------------------------------------------------------
 * Known flash device table
 * --------------------------------------------------------------------- */
static const ExtFlashInfo_t sFlashTable[] =
{
    { .deviceSize = 0x200000, .manfId = 0xC2, .devId = 0x15 },
    { .deviceSize = 0x100000, .manfId = 0xC2, .devId = 0x14 },
    { .deviceSize = 0x080000, .manfId = 0xEF, .devId = 0x12 },
    { .deviceSize = 0x040000, .manfId = 0xEF, .devId = 0x11 },
    { .deviceSize = 0,        .manfId = 0,    .devId = 0    },
};

static const ExtFlashInfo_t  sNoFlash   = {0, 0, 0};
static const ExtFlashInfo_t *spFlashInfo = NULL;

/* -----------------------------------------------------------------------
 * CS helpers
 * --------------------------------------------------------------------- */
static inline void csLow(void)  { GPIO_clearDio(FLASH_CS_IOID); }
static inline void csHigh(void) { GPIO_setDio(FLASH_CS_IOID); }

/* -----------------------------------------------------------------------
 * bspSpi* -- low-level SPI using ROM SSI functions (same as TI BSP)
 * --------------------------------------------------------------------- */
void bspSpiOpen(uint32_t bitRate, uint32_t clkPin)
{
    (void)clkPin;

    /* Power SERIAL domain + enable SSI0 */
    PRCMPowerDomainOn(PRCM_DOMAIN_SERIAL);
    while (PRCMPowerDomainsAllOn(PRCM_DOMAIN_SERIAL) != PRCM_DOMAIN_POWER_ON);
    ROM_PRCMPeripheralRunEnable(PRCM_PERIPH_SSI0);
    PRCMLoadSet();
    while (!PRCMLoadGet());

    /* Disable interrupts */
    SSIIntDisable(FLASH_SSI_BASE, SSI_RXOR | SSI_RXFF | SSI_RXTO | SSI_TXFF);
    SSIIntClear(FLASH_SSI_BASE, SSI_RXOR | SSI_RXTO);

    /* Configure SSI0 */
    ROM_SSIConfigSetExpClk(FLASH_SSI_BASE,
                           CPU_CLOCK_HZ,
                           SSI_FRF_MOTO_MODE_0,
                           SSI_MODE_MASTER,
                           bitRate,
                           8);

    /* Configure pins using ROM function -- exactly as TI BSP does it */
    ROM_IOCPinTypeSsiMaster(FLASH_SSI_BASE,
                            FLASH_MISO_IOID,   /* rx */
                            FLASH_MOSI_IOID,   /* tx */
                            IOID_UNUSED,       /* fss -- manual CS */
                            FLASH_CLK_IOID);   /* clk */

    /* CS as GPIO output, deasserted */
    IOCPinTypeGpioOutput(FLASH_CS_IOID);
    csHigh();

    SSIEnable(FLASH_SSI_BASE);

    /* Drain residual RX */
    uint32_t buf;
    while (SSIDataGetNonBlocking(FLASH_SSI_BASE, &buf));
}

void bspSpiClose(void)
{
    ROM_PRCMPeripheralRunDisable(PRCM_PERIPH_SSI0);
    PRCMLoadSet();
    while (!PRCMLoadGet());
}

int bspSpiWrite(const uint8_t *buf, size_t len)
{
    while (len > 0)
    {
        uint32_t ul;
        SSIDataPut(FLASH_SSI_BASE, *buf);
        ROM_SSIDataGet(FLASH_SSI_BASE, &ul);
        len--;
        buf++;
    }
    return 0;
}

int bspSpiRead(uint8_t *buf, size_t len)
{
    while (len > 0)
    {
        uint32_t ul;
        if (!ROM_SSIDataPutNonBlocking(FLASH_SSI_BASE, 0))
            return -1;
        ROM_SSIDataGet(FLASH_SSI_BASE, &ul);
        *buf = (uint8_t)ul;
        len--;
        buf++;
    }
    return 0;
}

void bspSpiFlush(void)
{
    uint32_t ul;
    while (ROM_SSIDataGetNonBlocking(FLASH_SSI_BASE, &ul));
}

/* -----------------------------------------------------------------------
 * Internal helpers
 * --------------------------------------------------------------------- */
static bool waitReady(void)
{
    const uint8_t cmd = MX25_CMD_RDSR;
    uint8_t status;

    /* Flush garbage first */
    csLow();
    bspSpiFlush();
    csHigh();

    for (;;)
    {
        csLow();
        bspSpiWrite(&cmd, 1);
        bspSpiRead(&status, 1);
        csHigh();
        if (!(status & MX25_STATUS_WIP)) return true;
    }
}

static bool writeEnable(void)
{
    const uint8_t cmd = MX25_CMD_WREN;
    csLow();
    bspSpiWrite(&cmd, 1);
    csHigh();
    return true;
}

static bool verifyPart(void)
{
    const uint8_t wbuf[4] = { MX25_CMD_MDID, 0xFF, 0xFF, 0x00 };
    uint8_t info[2] = {0, 0};

    csLow();
    if (bspSpiWrite(wbuf, sizeof(wbuf)) != 0) { csHigh(); return false; }
    if (bspSpiRead(info, 2) != 0)             { csHigh(); return false; }
    csHigh();

    spFlashInfo = sFlashTable;
    while (spFlashInfo->deviceSize > 0)
    {
        if (info[0] == spFlashInfo->manfId && info[1] == spFlashInfo->devId)
            return true;
        spFlashInfo++;
    }
    spFlashInfo = NULL;
    return false;
}

/* -----------------------------------------------------------------------
 * extFlash* public API
 * --------------------------------------------------------------------- */

bool extFlashOpen(void)
{
    bspSpiOpen(FLASH_SPI_BITRATE, FLASH_CLK_IOID);

    /* Wakeup sequence from Board_wakeUpExtFlash() in ti_drivers_config.c:
     * Toggle CS low for ~20ns then high, wait 35us minimum.
     * This is the correct wakeup -- do NOT use 0xAB RDP command. */
    IOCPinTypeGpioOutput(FLASH_CS_IOID);
    csHigh();

    /* Toggle CS for ~20ns to wake */
    csLow();
    CPUdelay(1);   /* ~62ns @ 48MHz */
    csHigh();
    CPUdelay(560); /* ~35us @ 48MHz */

    /* Verify part responds */
    if (!waitReady())   return false;
    if (!verifyPart())  return false;

    return true;
}

void extFlashClose(void)
{
    /* Put chip into deep power down */
    const uint8_t cmd = MX25_CMD_DP;
    csLow();
    bspSpiWrite(&cmd, 1);
    csHigh();

    bspSpiClose();
    spFlashInfo = NULL;
}

const ExtFlashInfo_t *extFlashInfo(void)
{
    return spFlashInfo ? spFlashInfo : &sNoFlash;
}

bool extFlashRead(size_t offset, size_t length, uint8_t *buf)
{
    if (!buf || length == 0) return false;
    if (!waitReady()) return false;

    uint8_t cmd[4];
    cmd[0] = MX25_CMD_READ;
    cmd[1] = (offset >> 16) & 0xFF;
    cmd[2] = (offset >>  8) & 0xFF;
    cmd[3] = (offset      ) & 0xFF;

    csLow();
    if (bspSpiWrite(cmd, 4) != 0) { csHigh(); return false; }
    int r = bspSpiRead(buf, length);
    csHigh();
    return (r == 0);
}

bool extFlashWrite(size_t offset, size_t length, const uint8_t *buf)
{
    if (!buf || length == 0) return false;
    while (length > 0)
    {
        if (!waitReady())   return false;
        if (!writeEnable()) return false;

        size_t pageBytes = MX25_PAGE_SIZE - (offset % MX25_PAGE_SIZE);
        if (length < pageBytes) pageBytes = length;

        uint8_t cmd[4];
        cmd[0] = MX25_CMD_PP;
        cmd[1] = (offset >> 16) & 0xFF;
        cmd[2] = (offset >>  8) & 0xFF;
        cmd[3] = (offset      ) & 0xFF;

        csLow();
        if (bspSpiWrite(cmd, 4) != 0)        { csHigh(); return false; }
        if (bspSpiWrite(buf, pageBytes) != 0) { csHigh(); return false; }
        csHigh();

        offset += pageBytes;
        buf    += pageBytes;
        length -= pageBytes;
    }
    return true;
}

bool extFlashErase(size_t offset, size_t length)
{
    if (length == 0) return true;

    size_t end    = offset + length - 1;
    offset        = (offset / MX25_SECTOR_SIZE) * MX25_SECTOR_SIZE;
    size_t nsects = (end - offset + MX25_SECTOR_SIZE) / MX25_SECTOR_SIZE;

    for (size_t i = 0; i < nsects; i++)
    {
        if (!waitReady())   return false;
        if (!writeEnable()) return false;

        uint8_t cmd[4];
        cmd[0] = MX25_CMD_SE;
        cmd[1] = (offset >> 16) & 0xFF;
        cmd[2] = (offset >>  8) & 0xFF;
        cmd[3] = (offset      ) & 0xFF;

        csLow();
        if (bspSpiWrite(cmd, 4) != 0) { csHigh(); return false; }
        csHigh();

        offset += MX25_SECTOR_SIZE;
    }
    return waitReady();
}

bool extFlashTest(void)
{
    bool ok = extFlashOpen();
    if (ok) extFlashClose();
    return ok;
}
