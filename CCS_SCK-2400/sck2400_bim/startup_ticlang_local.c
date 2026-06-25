/******************************************************************************
*  startup_ticlang_local.c
*  Startup code for CC26x2 / CC13x2 device family - TI Clang
*  Local copy for SCK-2400 BIM build.
******************************************************************************/

#if !(defined(__clang__))
#error "startup_ticlang.c: Unsupported compiler!"
#endif

#include "driverlib/prcm.h"
#include "driverlib/gpio.h"
#include "driverlib/ioc.h"
#include "inc/hw_types.h"
#include "driverlib/setup.h"

void        ResetISR( void );
static void NmiSR( void );
static void FaultISR( void );
static void IntDefaultHandler( void );
extern int  main(void);
extern void _c_int00(void);
extern unsigned long __STACK_END;

__attribute__ ((section(".resetVecs"), retain))
void (* const g_pfnVectors[])(void) =
{
    (void (*)(void))((unsigned long)&__STACK_END),  //  0 Initial stack pointer
    ResetISR,                                        //  1 Reset handler
    NmiSR,                                           //  2 NMI handler
    FaultISR,                                        //  3 Hard fault handler
    IntDefaultHandler,                               //  4 MemManage Fault
    IntDefaultHandler,                               //  5 Bus Fault
    IntDefaultHandler,                               //  6 Usage Fault
    0,                                               //  7 Reserved
    0,                                               //  8 Reserved
    0,                                               //  9 Reserved
    0,                                               // 10 Reserved
    IntDefaultHandler,                               // 11 SVCall
    IntDefaultHandler,                               // 12 Debug Monitor
    0,                                               // 13 Reserved
    IntDefaultHandler,                               // 14 PendSV
    IntDefaultHandler,                               // 15 SysTick
    IntDefaultHandler,                               // 16 AON edge detect
    IntDefaultHandler,                               // 17 I2C
    IntDefaultHandler,                               // 18 RF Core CPE1
    IntDefaultHandler,                               // 19 PKA
    IntDefaultHandler,                               // 20 AON RTC
    IntDefaultHandler,                               // 21 UART0
    IntDefaultHandler,                               // 22 AUX SW event 0
    IntDefaultHandler,                               // 23 SSI0
    IntDefaultHandler,                               // 24 SSI1
    IntDefaultHandler,                               // 25 RF Core CPE0
    IntDefaultHandler,                               // 26 RF Core HW
    IntDefaultHandler,                               // 27 RF Core CMD ACK
    IntDefaultHandler,                               // 28 I2S
    IntDefaultHandler,                               // 29 AUX SW event 1
    IntDefaultHandler,                               // 30 Watchdog
    IntDefaultHandler,                               // 31 Timer 0A
    IntDefaultHandler,                               // 32 Timer 0B
    IntDefaultHandler,                               // 33 Timer 1A
    IntDefaultHandler,                               // 34 Timer 1B
    IntDefaultHandler,                               // 35 Timer 2A
    IntDefaultHandler,                               // 36 Timer 2B
    IntDefaultHandler,                               // 37 Timer 3A
    IntDefaultHandler,                               // 38 Timer 3B
    IntDefaultHandler,                               // 39 Crypto
    IntDefaultHandler,                               // 40 uDMA SW
    IntDefaultHandler,                               // 41 uDMA Error
    IntDefaultHandler,                               // 42 Flash
    IntDefaultHandler,                               // 43 SW Event 0
    IntDefaultHandler,                               // 44 AUX combined
    IntDefaultHandler,                               // 45 AON programmable 0
    IntDefaultHandler,                               // 46 PRCM
    IntDefaultHandler,                               // 47 AUX Comparator A
    IntDefaultHandler,                               // 48 AUX ADC
    IntDefaultHandler,                               // 49 TRNG
    IntDefaultHandler,                               // 50 Oscillator control
    IntDefaultHandler,                               // 51 AUX Timer2 event 0
    IntDefaultHandler,                               // 52 UART1
    IntDefaultHandler                                // 53 Battery monitor
};

void ResetISR(void)
{
    /* Minimal diagnostic: 1-second solid LED before anything else */
    PRCMPowerDomainOn(PRCM_DOMAIN_PERIPH);
    while(PRCMPowerDomainsAllOn(PRCM_DOMAIN_PERIPH) != PRCM_DOMAIN_POWER_ON);
    PRCMPeripheralRunEnable(PRCM_PERIPH_GPIO);
    PRCMLoadSet();
    while(!PRCMLoadGet());
    IOCPinTypeGpioOutput(IOID_7);
    GPIO_setDio(IOID_7);
    volatile uint32_t _d = 2000000; while(_d--);
    GPIO_clearDio(IOID_7);
    _d = 500000; while(_d--);

    SetupTrimDevice();

    __asm("    .global _c_int00\n"
          "    b.w     _c_int00");

    FaultISR();
}

static void NmiSR(void)             { while(1) {} }
static void FaultISR(void)          { while(1) {} }
static void IntDefaultHandler(void) { while(1) {} }
