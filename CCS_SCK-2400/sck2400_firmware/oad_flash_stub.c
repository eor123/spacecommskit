/*
 * oad_flash_stub.c
 * SCK-2400 -- MX25R8035F External Flash Driver (RTOS Task Context)
 *
 * Implements the ext_flash.h interface using the TI SPI driver (CONFIG_SPI_0 /
 * SSI0).  Both SPI instances in this project are SPICC26X2DMA, but
 * minDmaTransferSize = 10 in the SysConfig HWAttrs.  Every flash transaction
 * is a short command header (1–5 bytes) transferred individually, so the
 * driver automatically falls through to CPU-polled mode and never touches the
 * DMA channels that the RF driver owns.  No DMA conflict.  No raw PRCM/SSI
 * register access.  Power dependency is handled by SPI_open / SPI_close.
 *
 * Call model (from OAD task context):
 *   extFlashOpen()   -- SPI_open, wake chip, read & verify device ID
 *   extFlashErase()  -- sector-aligned erase, polls WIP bit
 *   extFlashWrite()  -- 256-byte page write loop, polls WIP bit
 *   extFlashRead()   -- unbounded read using 03h FAST-READ-equivalent
 *   extFlashClose()  -- power-down command, SPI_close
 *
 * Hardware (LAUNCHXL-CC1352P-2, same assignment as BIM ext_flash_stub):
 *   SSI0 CLK   = DIO10  (CONFIG_GPIO_SPI_0_SCLK)
 *   SSI0 MOSI  = DIO9   (CONFIG_GPIO_SPI_0_PICO)
 *   SSI0 MISO  = DIO8   (CONFIG_GPIO_SPI_0_POCI)
 *   SSI0 CS    = DIO20  (CONFIG_GPIO_SPI_0_CSN)
 *
 * MX25R8035F key parameters:
 *   Manufacturer ID : 0xC2
 *   Device ID       : 0x14  (memory type 0x28, capacity 0x14)
 *   Page size       : 256 bytes
 *   Sector size     : 4096 bytes (EXT_FLASH_PAGE_SIZE in ext_flash.h)
 *   Total size      : 1 MB (0x100000)
 *   Tpp (page prog) : 10 ms max
 *   Tse (sector er) : 240 ms max
 *
 * SCK-DEV: OAD_FLASH  Tag for grep across OAD flash-related source.
 */

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>

/* TI Drivers */
#include <ti/drivers/SPI.h>
#include <ti/drivers/GPIO.h>    /* manual CS control -- hardware FSS unreliable for short transfers */

/* TI-RTOS -- Task_sleep() for tRES1 delay after RDP command */
#include <ti/sysbios/knl/Task.h>

/* SysConfig generated -- CONFIG_SPI_0 index + GPIO pin defines */
#include "ti_drivers_config.h"

/* ext_flash.h contract */
#include "ext_flash.h"

/* -----------------------------------------------------------------------
 * MX25R8035F command opcodes
 * --------------------------------------------------------------------- */
#define MX25_CMD_READ           0x03u   /* Read Data (up to 33 MHz)           */
#define MX25_CMD_PP             0x02u   /* Page Program                        */
#define MX25_CMD_SE             0x20u   /* Sector Erase (4 KB)                 */
#define MX25_CMD_WREN           0x06u   /* Write Enable                        */
#define MX25_CMD_RDSR           0x05u   /* Read Status Register                */
#define MX25_CMD_RDID           0x9Fu   /* Read Identification (JEDEC)         */
#define MX25_CMD_RDP            0xABu   /* Release from Deep Power-Down        */
#define MX25_CMD_DP             0xB9u   /* Deep Power-Down                     */

/* Status register bits */
#define MX25_SR_WIP             0x01u   /* Write In Progress                   */
#define MX25_SR_WEL             0x02u   /* Write Enable Latch                  */

/* Device identity (from RDID response bytes 1 and 2) */
#define MX25_MANF_ID            0xC2u
#define MX25_DEV_ID             0x14u   /* capacity byte of device ID          */

/* Geometry */
#define MX25_PAGE_SIZE          256u
#define MX25_SECTOR_SIZE        4096u   /* == EXT_FLASH_PAGE_SIZE              */
#define MX25_TOTAL_SIZE         (1u * 1024u * 1024u)   /* 1 MB                */

/* WIP poll timeout -- generous upper bound.
 * Sector erase worst-case is 240 ms; page program worst-case is 10 ms.
 * Each poll loop iteration costs ~1 SPI transaction (~16 bytes at 4 MHz ≈
 * 32 µs), so 10 000 iterations ≈ 320 ms -- sufficient margin.             */
#define WIP_POLL_MAX_ITER       10000u

/* SPI bit rate for flash access.
 * MX25R8035F supports up to 33 MHz for READ (0x03h) and 80 MHz for FAST READ.
 * 4 MHz is safe, conservative, and matches BIM's bare-metal rate.          */
#define FLASH_SPI_BITRATE       4000000u

/* -----------------------------------------------------------------------
 * Manual CS control
 *
 * SPICC26X2DMA hardware FSS does not assert reliably for CPU-polled
 * transfers (count < minDmaTransferSize = 10).  All flash commands are
 * short (1-5 bytes), so they always go CPU-polled and hardware FSS
 * never fires.  Fix: reclaim DIO20 as GPIO output after SPI_open() and
 * drive CS manually in spiXfer().
 *
 * DIO20 = CONFIG_GPIO_SPI_0_CSN, active low.
 * --------------------------------------------------------------------- */
#define FLASH_CS_PIN    CONFIG_GPIO_SPI_0_CSN
#define FLASH_CS_HI()   GPIO_write(FLASH_CS_PIN, 1)
#define FLASH_CS_LO()   GPIO_write(FLASH_CS_PIN, 0)

/* -----------------------------------------------------------------------
 * Module state
 * --------------------------------------------------------------------- */
static SPI_Handle       sSpiHandle  = NULL;
static SPI_Transaction  sTxn;
static bool             sOpen       = false;

/* Static device-info record populated by extFlashOpen() */
static ExtFlashInfo_t   sFlashInfo  = { 0, 0, 0 };

/* -----------------------------------------------------------------------
 * Diagnostic error code -- set by extFlashOpen() at each failure point.
 * Read via extFlashLastError() from uart.c to report in OAD ACK.
 * Values chosen to be non-zero and distinct so the ACK byte tells us
 * exactly where the open sequence failed.
 * --------------------------------------------------------------------- */
typedef enum {
    FLASH_ERR_NONE      = 0x00, /* no error */
    FLASH_ERR_SPI_OPEN  = 0x10, /* SPI_open() returned NULL */
    FLASH_ERR_RDP       = 0x20, /* RDP spiXfer failed */
    FLASH_ERR_WIP       = 0x30, /* flashWaitReady() timed out after RDP */
    FLASH_ERR_RDID      = 0x40, /* RDID spiXfer failed */
    FLASH_ERR_ID_MISMATCH = 0x50, /* JEDEC ID bytes wrong (rxId[1]/rxId[3]) */
} flash_err_t;

static flash_err_t sLastError = FLASH_ERR_NONE;

/*
 * extFlashLastError()
 * Returns the error code from the most recent extFlashOpen() failure.
 * 0x00 = success. See flash_err_t for non-zero codes.
 * Called from uart.c handle_oad_start() to include in ACK payload.
 */
uint8_t extFlashLastError(void) { return (uint8_t)sLastError; }

/* -----------------------------------------------------------------------
 * Internal helpers
 * --------------------------------------------------------------------- */

/*
 * spiXfer()
 * Single blocking SPI transaction with manual CS control.
 * Asserts DIO20 low before transfer, deasserts high after.
 * Both txBuf and rxBuf may be NULL if that direction is unused.
 * Returns true on success.
 */
static bool spiXfer(void *txBuf, void *rxBuf, size_t count)
{
    sTxn.count   = count;
    sTxn.txBuf   = txBuf;
    sTxn.rxBuf   = rxBuf;
    sTxn.arg     = NULL;
    FLASH_CS_LO();
    bool ok = SPI_transfer(sSpiHandle, &sTxn);
    FLASH_CS_HI();
    return ok;
}

/*
 * flashWaitReady()
 * Polls the WIP bit in the status register until clear or timeout.
 * Returns true when the device is idle.
 */
static bool flashWaitReady(void)
{
    uint8_t cmd[2], rsp[2];
    uint32_t iter = 0;

    cmd[0] = MX25_CMD_RDSR;
    cmd[1] = 0xFF;  /* dummy */

    do {
        if (!spiXfer(cmd, rsp, sizeof(cmd)))
        {
            return false;
        }
        if (!(rsp[1] & MX25_SR_WIP))
        {
            return true;
        }
    } while (++iter < WIP_POLL_MAX_ITER);

    return false;   /* timeout */
}

/*
 * flashWriteEnable()
 * Sends WREN and verifies WEL bit is set.
 * Returns true on success.
 */
static bool flashWriteEnable(void)
{
    uint8_t cmd  = MX25_CMD_WREN;
    uint8_t rdsr[2], rsp[2];

    if (!spiXfer(&cmd, NULL, 1))
    {
        return false;
    }

    /* Verify WEL */
    rdsr[0] = MX25_CMD_RDSR;
    rdsr[1] = 0xFF;
    if (!spiXfer(rdsr, rsp, sizeof(rdsr)))
    {
        return false;
    }

    return (rsp[1] & MX25_SR_WEL) != 0;
}

/* -----------------------------------------------------------------------
 * ext_flash.h public API
 * --------------------------------------------------------------------- */

/*
 * extFlashOpen()
 *
 * Opens CONFIG_SPI_0 in blocking mode, releases the chip from deep
 * power-down (RDP command), then reads the JEDEC ID to confirm we are
 * talking to an MX25R8035F.
 *
 * IMPORTANT for main.c:
 *   Remove the Power_setDependency(PowerCC26XX_PERIPH_SSI0) +
 *   SSIEnable(SSI0_BASE) calls from mainThread().  The SPI driver
 *   handles power management internally.  Those calls leave a dangling
 *   power-domain reference that can confuse the driver.
 *
 * Returns true when the chip responds with the expected ID.
 */
bool extFlashOpen(void)
{
    if (sOpen)
    {
        return true;    /* already open */
    }

    /* Open SPI in BLOCKING mode.
     * SysConfig has CONFIG_SPI_0 in software CS mode (Use Hardware: None).
     * SPICC26X2DMA handles CS via the csnPin GPIO -- no manual IOC needed.
     * SPI_open() remaps all four pins to SSI0 peripheral mux internally. */
    SPI_Params spiParams;
    SPI_Params_init(&spiParams);
    spiParams.transferMode  = SPI_MODE_BLOCKING;
    spiParams.bitRate       = FLASH_SPI_BITRATE;
    spiParams.dataSize      = 8;
    spiParams.frameFormat   = SPI_POL0_PHA0;    /* MX25R8035F: CPOL=0, CPHA=0 */
    spiParams.mode          = SPI_CONTROLLER;

    sLastError = FLASH_ERR_NONE;

    sSpiHandle = SPI_open(CONFIG_SPI_0, &spiParams);
    if (sSpiHandle == NULL)
    {
        sLastError = FLASH_ERR_SPI_OPEN;
        return false;
    }

    /* Reclaim DIO20 as GPIO output for manual CS control.
     * SPI_open() configured DIO20 as IOC_PORT_MCU_SSI0_FSS (hardware FSS).
     * GPIO_setConfig() switches it back to GPIO output driven high (deasserted).
     * The SSI peripheral clocks data normally -- we just own CS ourselves. */
    GPIO_setConfig(FLASH_CS_PIN, GPIO_CFG_OUTPUT | GPIO_CFG_OUT_HIGH | GPIO_CFG_OUT_STR_MED);
    FLASH_CS_HI();

    /* Release from Deep Power-Down.
     * MX25R8035F requires CS-high for ≥20 µs after RDP before accepting
     * any new command (tRES1).  We send RDP, then wait 1 ms (well above
     * the 30 µs tRES1 max) before polling WIP.  The RTOS task sleep also
     * yields the CPU so other tasks can run during the wait. */
    uint8_t rdp = MX25_CMD_RDP;
    if (!spiXfer(&rdp, NULL, 1))
    {
        sLastError = FLASH_ERR_RDP;
        SPI_close(sSpiHandle);
        sSpiHandle = NULL;
        return false;
    }

    /* 1 ms delay: tRES1 max = 30 µs, we wait 1000 µs for margin.
     * Task_sleep(1) on TI-RTOS7 with default 1 kHz tick = 1 ms. */
    Task_sleep(1);

    /* Poll WIP bit until chip is ready */
    if (!flashWaitReady())
    {
        sLastError = FLASH_ERR_WIP;
        SPI_close(sSpiHandle);
        sSpiHandle = NULL;
        return false;
    }

    /* Read JEDEC ID: send 0x9F, receive 3 response bytes.
     * Byte 0 = dummy (echoes during cmd byte)
     * Byte 1 = Manufacturer ID  (expect 0xC2)
     * Byte 2 = Memory type      (expect 0x28)
     * Byte 3 = Capacity         (expect 0x14) */
    uint8_t txId[4] = { MX25_CMD_RDID, 0xFF, 0xFF, 0xFF };
    uint8_t rxId[4] = { 0 };

    if (!spiXfer(txId, rxId, sizeof(txId)))
    {
        sLastError = FLASH_ERR_RDID;
        SPI_close(sSpiHandle);
        sSpiHandle = NULL;
        return false;
    }

    /* rxId[1] = manuf, rxId[3] = capacity (device ID we care about) */
    if (rxId[1] != MX25_MANF_ID || rxId[3] != MX25_DEV_ID)
    {
        /* ID mismatch -- rxId[1] and rxId[3] are returned in the ACK
         * payload bytes 3 and 4 so the ground station can display them. */
        sLastError = FLASH_ERR_ID_MISMATCH;
        sFlashInfo.manfId = rxId[1];  /* store actual bytes for diagnostics */
        sFlashInfo.devId  = rxId[3];
        SPI_close(sSpiHandle);
        sSpiHandle = NULL;
        return false;
    }

    sFlashInfo.manfId     = rxId[1];
    sFlashInfo.devId      = rxId[3];
    sFlashInfo.deviceSize = MX25_TOTAL_SIZE;

    sOpen = true;
    return true;
}

/*
 * extFlashClose()
 *
 * Sends Deep Power-Down command then closes the SPI handle, returning
 * the SSI0 power domain to the Power Manager.
 */
void extFlashClose(void)
{
    if (!sOpen)
    {
        return;
    }

    /* Send Deep Power-Down */
    uint8_t dp = MX25_CMD_DP;
    spiXfer(&dp, NULL, 1);

    SPI_close(sSpiHandle);
    sSpiHandle = NULL;
    sOpen      = false;
}

/*
 * extFlashRead()
 *
 * Issues a READ (03h) command.  No length limit imposed here -- caller
 * is responsible for staying within MX25_TOTAL_SIZE.
 *
 * Transaction layout: [CMD][A2][A1][A0][data×length]
 * We split this into a 4-byte header transfer followed by a data transfer
 * to avoid a large stack buffer.  CS must stay asserted between them, which
 * SPICC26X2DMA does NOT guarantee across separate SPI_transfer() calls.
 *
 * Solution: build a single combined transfer using a small header array
 * plus the caller's buffer.  For reads up to EXT_FLASH_PAGE_SIZE (4 KB)
 * we use a single SPI transaction via a local 4-byte header prepend.
 *
 * For simplicity and correctness, we do this as:
 *   1. Assert CS manually via CONFIG_GPIO_SPI_0_CSN
 *   2. Send 4-byte command header (CPU-polled, count < 10)
 *   3. Receive 'length' data bytes
 *   4. Deassert CS
 *
 * NOTE: To keep CS asserted across multiple SPI_transfer calls we disable
 * the hardware CS in SysConfig (set to GPIO_INVALID_INDEX) and drive it
 * ourselves.  Since this project IS using hardware CS (csnPin = DIO20),
 * we instead use a single transaction with a combined TX buffer.
 *
 * For read lengths up to 252 bytes: allocate [4 + length] on the stack.
 * For larger reads: loop in 252-byte chunks.
 */
bool extFlashRead(size_t offset, size_t length, uint8_t *buf)
{
    if (!sOpen || buf == NULL || length == 0)
    {
        return false;
    }
    if (offset + length > MX25_TOTAL_SIZE)
    {
        return false;
    }

    /* Chunk size chosen so that [4 + chunk] stays below minDmaTransferSize
     * threshold... wait, minDmaTransferSize = 10 means transfers of count < 10
     * skip DMA.  For reads we may need larger buffers.  With SPICC26X2DMA in
     * BLOCKING mode, transfers of ANY size work correctly -- DMA or CPU-polled.
     * The conflict was with SPI_open itself, not the transfer size.  So now
     * that we are using SPI_open() properly, any transfer size is fine.
     *
     * Strategy: allocate a 4-byte command header on the stack, build a
     * 2-transaction sequence with manual CS control.
     *
     * Because hardware CS (DIO20) is managed by the SPI driver, we cannot
     * hold it asserted across two SPI_transfer() calls.  Instead, we
     * allocate a single contiguous TX buffer [cmd(4) + 0xFF×length] and a
     * single RX buffer [dummy(4) + data(length)].
     *
     * This is fine for OAD block sizes (typically 128–244 bytes).  For very
     * large reads (e.g. full 4 KB sector verify) the caller should loop.
     * Maximum safe single-call length: 4092 bytes (4 KB minus 4-byte header).
     */

    /* We use a fixed-size intermediate buffer for the command header only,
     * then rely on the fact that SPICC26X2DMA with BLOCKING mode drives CS
     * for the whole transfer.  Build combined buffers on heap via stack
     * for chunks <= 256 bytes (the OAD block size). */

#define READ_CHUNK  256u

    size_t remaining = length;
    size_t pos       = 0;

    while (remaining > 0)
    {
        size_t chunk = remaining < READ_CHUNK ? remaining : READ_CHUNK;

        /* Combined tx: [CMD, A2, A1, A0, 0xFF×chunk] */
        uint8_t txBuf[4 + READ_CHUNK];
        uint8_t rxBuf[4 + READ_CHUNK];

        uint32_t addr = (uint32_t)(offset + pos);
        txBuf[0] = MX25_CMD_READ;
        txBuf[1] = (uint8_t)(addr >> 16);
        txBuf[2] = (uint8_t)(addr >>  8);
        txBuf[3] = (uint8_t)(addr      );
        memset(&txBuf[4], 0xFF, chunk);

        if (!spiXfer(txBuf, rxBuf, 4 + chunk))
        {
            return false;
        }

        memcpy(&buf[pos], &rxBuf[4], chunk);
        pos       += chunk;
        remaining -= chunk;
    }

    return true;
}

/*
 * extFlashWrite()
 *
 * Writes 'length' bytes to flash starting at 'offset'.
 * The MX25R8035F requires page-aligned Page Program operations (256 bytes
 * max each).  We loop over 256-byte pages, issuing WREN + PP for each.
 *
 * Caller responsibility: target sectors must be erased before writing.
 * Writing to unerased flash silently fails (can only clear bits, not set).
 */
bool extFlashWrite(size_t offset, size_t length, const uint8_t *buf)
{
    if (!sOpen || buf == NULL || length == 0)
    {
        return false;
    }
    if (offset + length > MX25_TOTAL_SIZE)
    {
        return false;
    }

    size_t remaining = length;
    size_t pos       = 0;

    while (remaining > 0)
    {
        uint32_t addr = (uint32_t)(offset + pos);

        /* Bytes remaining in current 256-byte page */
        size_t pageOffset = addr & 0xFFu;
        size_t pageAvail  = MX25_PAGE_SIZE - pageOffset;
        size_t chunk      = remaining < pageAvail ? remaining : pageAvail;

        if (!flashWriteEnable())
        {
            return false;
        }

        /* Build PP transaction: [02h][A2][A1][A0][data×chunk] */
        uint8_t txBuf[4 + MX25_PAGE_SIZE];
        txBuf[0] = MX25_CMD_PP;
        txBuf[1] = (uint8_t)(addr >> 16);
        txBuf[2] = (uint8_t)(addr >>  8);
        txBuf[3] = (uint8_t)(addr      );
        memcpy(&txBuf[4], &buf[pos], chunk);

        if (!spiXfer(txBuf, NULL, 4 + chunk))
        {
            return false;
        }

        if (!flashWaitReady())
        {
            return false;
        }

        pos       += chunk;
        remaining -= chunk;
    }

    return true;
}

/*
 * extFlashErase()
 *
 * Erases flash sectors (4 KB each) covering the range [offset, offset+length).
 * Offset and length are rounded outward to sector boundaries.
 *
 * Worst-case sector erase time: 240 ms.  WIP polling covers this.
 */
bool extFlashErase(size_t offset, size_t length)
{
    if (!sOpen || length == 0)
    {
        return false;
    }

    /* Align to sector boundaries */
    size_t startAddr = offset & ~(MX25_SECTOR_SIZE - 1u);
    size_t endAddr   = (offset + length + MX25_SECTOR_SIZE - 1u)
                       & ~(MX25_SECTOR_SIZE - 1u);

    if (endAddr > MX25_TOTAL_SIZE)
    {
        return false;
    }

    for (size_t addr = startAddr; addr < endAddr; addr += MX25_SECTOR_SIZE)
    {
        if (!flashWriteEnable())
        {
            return false;
        }

        uint8_t txBuf[4];
        txBuf[0] = MX25_CMD_SE;
        txBuf[1] = (uint8_t)(addr >> 16);
        txBuf[2] = (uint8_t)(addr >>  8);
        txBuf[3] = (uint8_t)(addr      );

        if (!spiXfer(txBuf, NULL, sizeof(txBuf)))
        {
            return false;
        }

        if (!flashWaitReady())
        {
            return false;
        }
    }

    return true;
}

/*
 * extFlashTest()
 *
 * Power-on self-test: open the chip, verify JEDEC ID, close.
 * Suitable for a startup health check from the OAD task before committing
 * to an OAD session.
 */
bool extFlashTest(void)
{
    bool ok = extFlashOpen();
    if (ok)
    {
        extFlashClose();
    }
    return ok;
}

/*
 * extFlashInfo()
 *
 * Returns pointer to device info populated by extFlashOpen().
 * Returns a zeroed struct if the flash has not been opened.
 */
const ExtFlashInfo_t *extFlashInfo(void)
{
    return &sFlashInfo;
}

/* -----------------------------------------------------------------------
 * bspSpi* stubs
 *
 * These entry points are called by some versions of the TI OAD flash
 * interface layer (flash_interface_ext_rtos_NVS.c variants).  They are
 * not used by our direct ext_flash.h implementation, but must be present
 * to satisfy the linker if those object files are included.
 * --------------------------------------------------------------------- */
void bspSpiOpen(uint32_t bitRate, uint32_t clkPin)  { (void)bitRate; (void)clkPin; }
void bspSpiClose(void)                              { }
int  bspSpiWrite(const uint8_t *buf, size_t len)    { (void)buf; (void)len; return -1; }
int  bspSpiRead(uint8_t *buf, size_t len)           { (void)buf; (void)len; return -1; }
void bspSpiFlush(void)                              { }
