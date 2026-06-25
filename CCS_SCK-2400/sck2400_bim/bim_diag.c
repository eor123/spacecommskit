/*
 * bim_diag.c
 * Overrides setLowPowerMode() in bim_util.c with LED blink diagnostic.
 * If green LED blinks fast after reset: BIM ran but found no valid app.
 * If no LED: BIM crashed before reaching setLowPowerMode.
 */

/* Must match exact signature from bim_util.h */
void setLowPowerMode(void)
{
    /* Power up GPIO peripheral via PRCM */
    *((volatile unsigned int*)0x40082208) = 1;   /* GPIOCLKGR */
    *((volatile unsigned int*)0x40082028) = 1;   /* CLKLOADCTL */
    while(!(*((volatile unsigned int*)0x4008202C) & 1));

    /* IOC: configure DIO7 as GPIO output (green LED on LAUNCHXL-CC1352P-2) */
    *((volatile unsigned int*)0x4008101C) = 0x6000;

    /* Enable DIO7 output driver */
    *((volatile unsigned int*)0x40022400) |= (1<<7);  /* DOE31_0 */

    /* Fast blink forever -- confirms BIM reached setLowPowerMode */
    while(1) {
        *((volatile unsigned int*)0x40022090) ^= (1<<7);  /* DOUTTGL31_0 */
        for (volatile int d = 0; d < 200000; d++);
    }
}
