/*
 *  ======== ti_drivers_config.h ========
 *  Configured TI-Drivers module declarations
 *
 *  The macros defines herein are intended for use by applications which
 *  directly include this header. These macros should NOT be hard coded or
 *  copied into library source code.
 *
 *  Symbols declared as const are intended for use with libraries.
 *  Library source code must extern the correct symbol--which is resolved
 *  when the application is linked.
 *
 *  DO NOT EDIT - This file is generated for the CC1352P_2_LAUNCHXL
 *  by the SysConfig tool.
 */
#ifndef ti_drivers_config_h
#define ti_drivers_config_h

#define CONFIG_SYSCONFIG_PREVIEW

#define CONFIG_CC1352P_2_LAUNCHXL
#ifndef DeviceFamily_CC13X2
#define DeviceFamily_CC13X2
#endif

#include <ti/devices/DeviceFamily.h>

#include <stdint.h>

/* support C++ sources */
#ifdef __cplusplus
extern "C" {
#endif


/*
 *  ======== CCFG ========
 */


/*
 *  ======== GPIO ========
 */
extern const uint_least8_t RF_SW_2G4_CONST;
#define RF_SW_2G4 28

extern const uint_least8_t RF_SW_PA_CONST;
#define RF_SW_PA 29

extern const uint_least8_t CONFIG_GPIO_GLED_CONST;
#define CONFIG_GPIO_GLED 7

/* Owned by CONFIG_SPI_0 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_0_SCLK_CONST;
#define CONFIG_GPIO_SPI_0_SCLK 10

/* Owned by CONFIG_SPI_0 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_0_POCI_CONST;
#define CONFIG_GPIO_SPI_0_POCI 8

/* Owned by CONFIG_SPI_0 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_0_PICO_CONST;
#define CONFIG_GPIO_SPI_0_PICO 9

/* Owned by CONFIG_SPI_1 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_1_SCLK_CONST;
#define CONFIG_GPIO_SPI_1_SCLK 23

/* Owned by CONFIG_SPI_1 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_1_POCI_CONST;
#define CONFIG_GPIO_SPI_1_POCI 25

/* Owned by CONFIG_SPI_1 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_1_PICO_CONST;
#define CONFIG_GPIO_SPI_1_PICO 24

/* Owned by PAYLOAD_UART as  */
extern const uint_least8_t CONFIG_GPIO_PAYLOAD_UART_TX_CONST;
#define CONFIG_GPIO_PAYLOAD_UART_TX 13

/* Owned by PAYLOAD_UART as  */
extern const uint_least8_t CONFIG_GPIO_PAYLOAD_UART_RX_CONST;
#define CONFIG_GPIO_PAYLOAD_UART_RX 12

/* Owned by DEBUG_UART as  */
extern const uint_least8_t CONFIG_GPIO_DEBUG_UART_TX_CONST;
#define CONFIG_GPIO_DEBUG_UART_TX 5

/* Owned by DEBUG_UART as  */
extern const uint_least8_t CONFIG_GPIO_DEBUG_UART_RX_CONST;
#define CONFIG_GPIO_DEBUG_UART_RX 16

/* Owned by CONFIG_SPI_0 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_0_CSN_CONST;
#define CONFIG_GPIO_SPI_0_CSN 20

/* Owned by CONFIG_SPI_1 as  */
extern const uint_least8_t CONFIG_GPIO_SPI_1_CSN_CONST;
#define CONFIG_GPIO_SPI_1_CSN 26

/* The range of pins available on this device */
extern const uint_least8_t GPIO_pinLowerBound;
extern const uint_least8_t GPIO_pinUpperBound;

/* LEDs are active high */
#define CONFIG_GPIO_LED_ON  (1)
#define CONFIG_GPIO_LED_OFF (0)

#define CONFIG_LED_ON  (CONFIG_GPIO_LED_ON)
#define CONFIG_LED_OFF (CONFIG_GPIO_LED_OFF)




/*
 *  ======== SPI ========
 */

/*
 *  PICO: DIO9
 *  POCI: DIO8
 *  SCLK: DIO10
 *  CSN: DIO20
 */
extern const uint_least8_t              CONFIG_SPI_0_CONST;
#define CONFIG_SPI_0                    0
/*
 *  PICO: DIO24
 *  POCI: DIO25
 *  SCLK: DIO23
 *  CSN: DIO26
 */
extern const uint_least8_t              CONFIG_SPI_1_CONST;
#define CONFIG_SPI_1                    1
#define CONFIG_TI_DRIVERS_SPI_COUNT     2


/*
 *  ======== UART2 ========
 */

/*
 *  TX: DIO13
 *  RX: DIO12
 */
extern const uint_least8_t                  PAYLOAD_UART_CONST;
#define PAYLOAD_UART                        0
/*
 *  TX: DIO5
 *  RX: DIO16
 */
extern const uint_least8_t                  DEBUG_UART_CONST;
#define DEBUG_UART                          1
#define CONFIG_TI_DRIVERS_UART2_COUNT       2


/*
 *  ======== Watchdog ========
 */

extern const uint_least8_t                  CONFIG_WATCHDOG_0_CONST;
#define CONFIG_WATCHDOG_0                   0
#define CONFIG_TI_DRIVERS_WATCHDOG_COUNT    1


/*
 *  ======== Board_init ========
 *  Perform all required TI-Drivers initialization
 *
 *  This function should be called once at a point before any use of
 *  TI-Drivers.
 */
extern void Board_init(void);

/*
 *  ======== Board_initGeneral ========
 *  (deprecated)
 *
 *  Board_initGeneral() is defined purely for backward compatibility.
 *
 *  All new code should use Board_init() to do any required TI-Drivers
 *  initialization _and_ use <Driver>_init() for only where specific drivers
 *  are explicitly referenced by the application.  <Driver>_init() functions
 *  are idempotent.
 */
#define Board_initGeneral Board_init

#ifdef __cplusplus
}
#endif

#endif /* include guard */
