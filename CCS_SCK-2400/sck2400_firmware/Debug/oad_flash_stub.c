/*
 * oad_flash_stub.c
 * SCK-2400 Firmware -- OAD ext flash
 *
 * Phase 3b: RTOS SPI implementation.
 * extFlashOpen() uses CONFIG_SPI_0 -- called only from OAD handlers.
 */

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>

#include <ti/drivers/SPI.h>
#include <ti/drivers/GPIO.h>
#include <ti/sysbios/knl/Task.h>
#include "ti_drivers_config.h"
#include "ext_flash.h"

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

static const ExtFlashInfo_t sFlashTable[] = {
    { .deviceSize = 0x200000, .manfId = 0xC2, .devId = 0x15 },
    { .deviceSize = 0x100000, .manfId = 0xC2, .devId = 0x14 },
    { .deviceSize = 0x080000, .manfId = 0xEF, .devId = 0x12 },
    { .deviceSize = 0x040000, .manfId = 0xEF, .devId = 0x11 },
    { .deviceSize = 0,        .manfId = 0,    .devId = 0    },
};

static const ExtFlashInfo_t  sNoFlash   = {0, 0, 0};
static const ExtFlashInfo_t *spFlashInfo = NULL;
static SPI_Handle            sSpiHandle  = NULL;

static inline void csLow(void)  { GPIO_write(CONFIG_GPIO_SPI_0_CSN, 0); }
static inline void csHigh(void) { GPIO_write(CONFIG_GPIO_SPI_0_CSN, 1); }

static bool spiXfer(const void *tx, void *rx, size_t n)
{
    SPI_Transaction t = { .count = n, .txBuf = (void*)tx, .rxBuf = rx, .arg = NULL };
    return SPI_transfer(sSpiHandle, &t);
}

static bool spiCmd(uint8_t cmd, const uint8_t *txEx, size_t txExLen,
                   uint8_t *rxBuf, size_t rxLen)
{
    uint8_t tx[261], rx[261];
    size_t total = 1 + txExLen + rxLen;
    if (total > 261) return false;
    tx[0] = cmd;
    if (txEx && txExLen) memcpy(&tx[1], txEx, txExLen);
    if (rxLen) memset(&tx[1+txExLen], 0, rxLen);
    csLow();
    bool ok = spiXfer(tx, rx, total);
    csHigh();
    if (ok && rxBuf && rxLen) memcpy(rxBuf, &rx[1+txExLen], rxLen);
    return ok;
}

static bool waitReady(void)
{
    uint8_t sr;
    for (int i = 0; i < 10000; i++) {
        if (!spiCmd(MX25_CMD_RDSR, NULL, 0, &sr, 1)) return false;
        if (!(sr & MX25_STATUS_WIP)) return true;
        Task_sleep(1);
    }
    return false;
}

static bool writeEnable(void)
{
    uint8_t cmd = MX25_CMD_WREN;
    csLow(); bool ok = spiXfer(&cmd, NULL, 1); csHigh();
    return ok;
}

static bool verifyPart(void)
{
    const uint8_t addr[3] = {0xFF, 0xFF, 0x00};
    uint8_t info[2] = {0,0};
    if (!spiCmd(MX25_CMD_MDID, addr, 3, info, 2)) return false;
    spFlashInfo = sFlashTable;
    while (spFlashInfo->deviceSize > 0) {
        if (info[0] == spFlashInfo->manfId && info[1] == spFlashInfo->devId)
            return true;
        spFlashInfo++;
    }
    spFlashInfo = NULL;
    return false;
}

/* bspSpi stubs -- satisfy linker only */
void bspSpiOpen(uint32_t b, uint32_t c) { (void)b;(void)c; }
void bspSpiClose(void)                  { }
int  bspSpiWrite(const uint8_t *b, size_t l) { (void)b;(void)l; return -1; }
int  bspSpiRead(uint8_t *b, size_t l)        { (void)b;(void)l; return -1; }
void bspSpiFlush(void)                  { }

bool extFlashOpen(void)
{
    if (sSpiHandle) return true;
    SPI_Params p; SPI_Params_init(&p);
    p.bitRate  = 4000000;
    p.dataSize = 8;
    sSpiHandle = SPI_open(CONFIG_SPI_0, &p);
    if (!sSpiHandle) return false;
    csHigh();
    csLow(); csHigh(); /* CS toggle wakeup */
    Task_sleep(1);
    if (!waitReady() || !verifyPart()) {
        SPI_close(sSpiHandle); sSpiHandle = NULL; return false;
    }
    return true;
}

void extFlashClose(void)
{
    if (!sSpiHandle) return;
    uint8_t cmd = MX25_CMD_DP;
    csLow(); spiXfer(&cmd, NULL, 1); csHigh();
    SPI_close(sSpiHandle); sSpiHandle = NULL; spFlashInfo = NULL;
}

const ExtFlashInfo_t *extFlashInfo(void) { return spFlashInfo ? spFlashInfo : &sNoFlash; }

bool extFlashRead(size_t offset, size_t length, uint8_t *buf)
{
    if (!buf || !length || !sSpiHandle) return false;
    if (!waitReady()) return false;
    while (length > 0) {
        size_t chunk = length > 255 ? 255 : length;
        uint8_t addr[3] = {(offset>>16)&0xFF,(offset>>8)&0xFF,offset&0xFF};
        if (!spiCmd(MX25_CMD_READ, addr, 3, buf, chunk)) return false;
        offset += chunk; buf += chunk; length -= chunk;
    }
    return true;
}

bool extFlashWrite(size_t offset, size_t length, const uint8_t *buf)
{
    if (!buf || !length || !sSpiHandle) return false;
    while (length > 0) {
        if (!waitReady() || !writeEnable()) return false;
        size_t pb = MX25_PAGE_SIZE - (offset % MX25_PAGE_SIZE);
        if (length < pb) pb = length;
        uint8_t tx[4 + MX25_PAGE_SIZE];
        tx[0]=MX25_CMD_PP; tx[1]=(offset>>16)&0xFF;
        tx[2]=(offset>>8)&0xFF; tx[3]=offset&0xFF;
        memcpy(&tx[4], buf, pb);
        csLow(); bool ok = spiXfer(tx, NULL, 4+pb); csHigh();
        if (!ok) return false;
        offset += pb; buf += pb; length -= pb;
    }
    return true;
}

bool extFlashErase(size_t offset, size_t length)
{
    if (!length || !sSpiHandle) return true;
    size_t end = offset+length-1;
    offset = (offset/MX25_SECTOR_SIZE)*MX25_SECTOR_SIZE;
    size_t n = (end-offset+MX25_SECTOR_SIZE)/MX25_SECTOR_SIZE;
    for (size_t i = 0; i < n; i++) {
        if (!waitReady() || !writeEnable()) return false;
        uint8_t cmd[4] = {MX25_CMD_SE,(offset>>16)&0xFF,(offset>>8)&0xFF,offset&0xFF};
        csLow(); bool ok = spiXfer(cmd, NULL, 4); csHigh();
        if (!ok) return false;
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
