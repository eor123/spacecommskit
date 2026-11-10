/*
 * ccfg_sck.c -- SCK-2400 BIM CCFG
 *
 * The CCFG the chip uses comes from the BIM (0x57FA8), not from the app.
 * The SDK's bim/ccfg_app.c leaves the ROM serial bootloader disabled
 * (BL_CONFIG = 0x00FFFFFF), so the DIO6 backdoor in the app syscfg never
 * took effect. Enable it here, then pull in the SDK file unchanged.
 *
 * Backdoor: hold DIO6 (V3 J4.5 / module J1.14, 10k pull-up R40) LOW through
 * any reset or power-up -> ROM bootloader on UART1 (DIO12 RX / DIO13 TX).
 * The ROM bootloader lives in ROM and cannot be erased by OAD or a bad BIM:
 * it is the last-resort recovery path (bench, or a host/OBC in flight).
 *
 * Built with -Werror=macro-redefined: if the SDK file ever defines these
 * itself, the build fails instead of silently keeping the SDK values.
 */
#define SET_CCFG_BL_CONFIG_BOOTLOADER_ENABLE    0xC5   /* ROM bootloader enabled  */
#define SET_CCFG_BL_CONFIG_BL_LEVEL             0x0    /* backdoor active low     */
#define SET_CCFG_BL_CONFIG_BL_PIN_NUMBER        0x6    /* DIO6                    */
#define SET_CCFG_BL_CONFIG_BL_ENABLE            0xC5   /* backdoor enabled        */

#include <ti/common/bim/ccfg_app.c>
