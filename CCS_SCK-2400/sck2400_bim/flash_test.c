/*
 * flash_test.c
 * SCK-2400 -- MX25R8035F External Flash Diagnostic
 *
 * Granular LED diagnostic to pinpoint SPI failure:
 *
 *   Solid 2s on startup     = reached flash_test_main
 *   1 fast blink            = SPI init OK, entering extFlashOpen
 *   2 fast blinks           = RDP command sent
 *   3 fast blinks           = waitReady() returned true
 *   4 fast blinks           = verifyPart() called
 *
 *   Then result:
 *   10 fast + steady ON     = PASS (device ID matched)
 *   Slow blink loop (n)     = FAIL at step n
 */

#include <stdint.h>
#include <stddef.h>
#include <stdbool.h>
#include <string.h>

#include "ti/devices/cc13x2_cc26x2/startup_files/ccfg.c"

#include "ti/devices/cc13x2_cc26x2/driverlib/prcm.h"
#include "ti/devices/cc13x2_cc26x2/driverlib/gpio.h"
#include "ti/devices/cc13x2_cc26x2/driverlib/ioc.h"
#include "ti/devices/cc13x2_cc26x2/driverlib/ssi.h"
#include "ti/devices/cc13x2_cc26x2/driverlib/setup.h"

#include "ti/common/flash/no_rtos/extFlash/ext_flash.h"

extern void flash_test_main(void);

__attribute__((section(".resetVecs"), used))
static const uint32_t vectors[] = {
    0x20014000,
    (uint32_t)flash_test_main + 1,
    (uint32_t)flash_test_main + 1,
    (uint32_t)flash_test_main + 1,
};

#define LED_DIO  IOID_7

static void led_init(void)
{
    PRCMPowerDomainOn(PRCM_DOMAIN_PERIPH);
    while (PRCMPowerDomainsAllOn(PRCM_DOMAIN_PERIPH) != PRCM_DOMAIN_POWER_ON);
    PRCMPeripheralRunEnable(PRCM_PERIPH_GPIO);
    PRCMLoadSet();
    while (!PRCMLoadGet());
    IOCPinTypeGpioOutput(LED_DIO);
    GPIO_clearDio(LED_DIO);
}

static void delay(volatile uint32_t n) { while (n--); }

/* n fast blinks then pause -- checkpoint indicator */
static void checkpoint(int n)
{
    for (int i = 0; i < n; i++)
    {
        GPIO_setDio(LED_DIO);
        delay(120000);
        GPIO_clearDio(LED_DIO);
        delay(120000);
    }
    delay(600000);
}

/* slow blink n times forever -- failure code */
static void fail_blink(int n)
{
    while (1)
    {
        for (int i = 0; i < n; i++)
        {
            GPIO_setDio(LED_DIO);
            delay(700000);
            GPIO_clearDio(LED_DIO);
            delay(700000);
        }
        delay(2000000);
    }
}

/* SPI pins */
#define FLASH_CLK_IOID   IOID_8
#define FLASH_MISO_IOID  IOID_9
#define FLASH_MOSI_IOID  IOID_10
#define FLASH_CS_IOID    IOID_20
#define FLASH_SSI_BASE   SSI0_BASE
#define CPU_CLOCK_HZ     48000000UL
#define SPI_BITRATE      2000000UL

/* MX25 commands */
#define MX25_CMD_RDSR    0x05
#define MX25_CMD_RDID    0x90
#define MX25_CMD_RDP     0xAB

static inline void csLow(void)  { GPIO_clearDio(FLASH_CS_IOID); }
static inline void csHigh(void) { GPIO_setDio(FLASH_CS_IOID); }

static void spi_init(void)
{
    PRCMPowerDomainOn(PRCM_DOMAIN_SERIAL);
    while (PRCMPowerDomainsAllOn(PRCM_DOMAIN_SERIAL) != PRCM_DOMAIN_POWER_ON);
    PRCMPeripheralRunEnable(PRCM_PERIPH_SSI0);
    PRCMLoadSet();
    while (!PRCMLoadGet());

    /* DIO8 = SSI0 CLK output */
    IOCPortConfigureSet(FLASH_CLK_IOID,  0x00000007, IOC_STD_OUTPUT);
    /* DIO10 = SSI0 TX (MOSI) output */
    IOCPortConfigureSet(FLASH_MOSI_IOID, 0x00000009, IOC_STD_OUTPUT);
    /* DIO9 = SSI0 RX (MISO) input */
    IOCPortConfigureSet(FLASH_MISO_IOID, 0x00000008, IOC_STD_INPUT);

    /* CS as GPIO */
    IOCPinTypeGpioOutput(FLASH_CS_IOID);
    csHigh();

    SSIDisable(FLASH_SSI_BASE);
    SSIConfigSetExpClk(FLASH_SSI_BASE, CPU_CLOCK_HZ,
                       SSI_FRF_MOTO_MODE_0, SSI_MODE_MASTER,
                       SPI_BITRATE, 8);
    SSIEnable(FLASH_SSI_BASE);

    uint32_t d;
    while (SSIDataGetNonBlocking(FLASH_SSI_BASE, &d));
}

static void spi_write(uint8_t b)
{
    SSIDataPut(FLASH_SSI_BASE, b);
    uint32_t d;
    SSIDataGet(FLASH_SSI_BASE, &d);
}

static uint8_t spi_read(void)
{
    SSIDataPut(FLASH_SSI_BASE, 0x00);
    uint32_t d;
    SSIDataGet(FLASH_SSI_BASE, &d);
    return (uint8_t)(d & 0xFF);
}

static void spi_wait(void)
{
    while (SSIBusy(FLASH_SSI_BASE));
}

void flash_test_main(void)
{
    SetupTrimDevice();
    led_init();

    /* Solid 2s = alive */
    GPIO_setDio(LED_DIO);
    delay(2000000);
    GPIO_clearDio(LED_DIO);
    delay(500000);

    /* Step 1: SPI init */
    spi_init();
    checkpoint(1); /* 1 fast blink = SPI init done */

    /* Step 2: Send RDP (release from deep power down) */
    csLow();
    spi_write(MX25_CMD_RDP);
    csHigh();
    spi_wait();
    volatile uint32_t d = 5000; while(d--); /* 35us min */
    checkpoint(2); /* 2 fast blinks = RDP sent */

    /* Step 3: Read status register -- check WIP=0 */
    uint8_t status = 0xFF;
    for (int i = 0; i < 100; i++)
    {
        csLow();
        spi_write(MX25_CMD_RDSR);
        status = spi_read();
        csHigh();
        spi_wait();
        if (!(status & 0x01)) break;
        d = 1000; while(d--);
    }
    if (status & 0x01) fail_blink(3); /* stuck busy */
    checkpoint(3); /* 3 fast blinks = status reg read OK */

    /* Step 4: Read manufacturer + device ID */
    csLow();
    spi_write(MX25_CMD_RDID);
    spi_write(0xFF); /* dummy address byte 2 */
    spi_write(0xFF); /* dummy address byte 1 */
    spi_write(0x00); /* dummy address byte 0 */
    uint8_t manf_id = spi_read();
    uint8_t dev_id  = spi_read();
    csHigh();
    spi_wait();
    checkpoint(4); /* 4 fast blinks = RDID read */

    /* Step 5: Verify IDs */
    /* MX25R8035F: manf=0xC2, dev=0x14 */
    if (manf_id != 0xC2 || dev_id != 0x14)
    {
        /* Wrong ID -- blink manf_id count then dev_id count to show what we got */
        /* Encode: if manf_id=0 that means SPI not working at all → 5 slow blinks */
        if (manf_id == 0x00 || manf_id == 0xFF)
            fail_blink(5); /* SPI reading all 0s or all FFs -- wiring issue */
        else
            fail_blink(6); /* Wrong device -- unexpected chip */
    }

    /* PASS */
    for (int i = 0; i < 10; i++)
    {
        GPIO_setDio(LED_DIO);
        delay(100000);
        GPIO_clearDio(LED_DIO);
        delay(100000);
    }
    GPIO_setDio(LED_DIO);
    while (1);
}
