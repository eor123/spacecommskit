@echo off
:: =============================================================================
:: build_bim_v3.bat
:: SCK-2400 BIM (Boot Image Manager) build script
:: Off-chip OAD, unsecured, CC2652P1FRGZ -- SCK-2400 V3 PCB
::
:: Same sources as build_bim.bat (LaunchPad, CC1352P). Only differences:
::   DeviceFamily_CC26X2 instead of DeviceFamily_CC13X2, so the BIM expects
::   OAD image ID "CC26x2R1" -- the ID the V3 app (built for CC2652P1F)
::   puts in its image header. The LaunchPad BIM expects "CC13x2R1" and
::   rejects the V3 app (found 11/01/26 on V3 board #1: BIM ran, app never
::   started).
::   Output goes to V3\ so the LaunchPad BIM in Debug\ is untouched.
::   CCFG comes from ccfg_sck.c (11/06/26): the SDK ccfg_app.c left the ROM
::   bootloader disabled (BL_CONFIG 0x00FFFFFF). The BIM CCFG is the one the
::   chip uses, so this is where the DIO6 backdoor has to be enabled.
::   Check the result with: python check_ccfg.py V3\sck2400_bim.hex
::   The GS app picks V3\sck2400_bim.hex when Hardware = SCK-2400 V3.
::
:: Usage: build_bim_v3.bat
:: Output: V3\sck2400_bim.out  (ELF)
::         V3\sck2400_bim.hex  (Intel HEX for flashing)
:: =============================================================================
set SDK=C:\ti\simplelink_cc13xx_cc26xx_sdk_8_32_00_07
set CC=C:\ti\ti_cgt_arm_llvm_3.2.2.LTS\bin\tiarmclang.exe
set HEX=C:\ti\ti_cgt_arm_llvm_3.2.2.LTS\bin\tiarmhex
set OUT=V3
:: Compiler flags
set CFLAGS=-c -mcpu=cortex-m4 -mfloat-abi=hard -mfpu=fpv4-sp-d16 -mlittle-endian -mthumb -Oz -gdwarf-3 -march=armv7e-m
:: Defines
set DEFS=-DDeviceFamily_CC26X2 -DBIM_OFFCHIP
:: Include paths
set INCS=-I"%SDK%\source" -I"%SDK%\source\ti\posix\ticlang" -I"%SDK%\source\ti\common\flash\no_rtos\extFlash" -I"%SDK%\source\ti\devices\cc13x2_cc26x2"
:: Create output directory
if not exist %OUT% mkdir %OUT%
echo.
echo ========================================
echo   SCK-2400 BIM Build -- V3 (CC2652P, CC26x2R1)
echo ========================================
echo.
:: Compile each source file
echo Compiling startup_ticlang_local.c...
"%CC%" %CFLAGS% %DEFS% %INCS% -o "%OUT%\startup.o" "startup_ticlang_local.c"
if errorlevel 1 goto :error

echo Compiling bim_offchip_main_local.c...
"%CC%" %CFLAGS% %DEFS% %INCS% -o "%OUT%\bim_offchip_main.o" "bim_offchip_main_local.c"
if errorlevel 1 goto :error

echo Compiling ccfg_sck.c (SDK ccfg_app.c + ROM bootloader backdoor on DIO6)...
"%CC%" %CFLAGS% %DEFS% %INCS% -Werror=macro-redefined -o "%OUT%\ccfg_app.o" "ccfg_sck.c"
if errorlevel 1 goto :error

echo Compiling bim_util_local.c...
"%CC%" %CFLAGS% %DEFS% %INCS% -o "%OUT%\bim_util.o" "bim_util_local.c"
if errorlevel 1 goto :error

echo Compiling ext_flash_stub.c...
"%CC%" %CFLAGS% %DEFS% %INCS% -o "%OUT%\ext_flash.o" "ext_flash_stub.c"
if errorlevel 1 goto :error

echo Skipping bsp_spi (stub build)...

echo Compiling flash_interface_internal.c...
"%CC%" %CFLAGS% %DEFS% %INCS% -o "%OUT%\flash_interface_internal.o" "%SDK%\source\ti\common\cc26xx\flash_interface\internal\flash_interface_internal.c"
if errorlevel 1 goto :error

echo Compiling crc32.c...
"%CC%" %CFLAGS% %DEFS% %INCS% -o "%OUT%\crc32.o" "%SDK%\source\ti\common\cc26xx\crc\crc32.c"
if errorlevel 1 goto :error

:: Link
echo.
echo Linking...
"%CC%" -mcpu=cortex-m4 -mfloat-abi=hard -mfpu=fpv4-sp-d16 -mlittle-endian -mthumb -Oz -gdwarf-3 -march=armv7e-m ^
    -Wl,-m"%OUT%\sck2400_bim.map" ^
    -Wl,-i"%SDK%\source" ^
    -Wl,-i"%SDK%\source\ti\devices\cc13x2_cc26x2\driverlib\bin\ticlang" ^
    -Wl,-i"C:\ti\ti_cgt_arm_llvm_3.2.2.LTS\lib" ^
    -Wl,--diag_wrap=off ^
    -Wl,--display_error_number ^
    -Wl,--warn_sections ^
    -Wl,--rom_model ^
    -Wl,--entry_point=ResetISR ^
    -o "%OUT%\sck2400_bim.out" ^
    "%OUT%\startup.o" ^
    "%OUT%\bim_offchip_main.o" ^
    "%OUT%\ccfg_app.o" ^
    "%OUT%\bim_util.o" ^
    "%OUT%\ext_flash.o" ^
    "%OUT%\crc32.o" ^
    "%OUT%\flash_interface_internal.o" ^
    "sck2400_bim.cmd" ^
    -Wl,-ldriverlib.lib ^
    -Wl,-llibc.a
if errorlevel 1 goto :error

:: Generate Intel HEX for flashing
echo.
echo Generating HEX file...
"%HEX%" -order MS --memwidth=8 --romwidth=8 --intel -o "%OUT%\sck2400_bim.hex" "%OUT%\sck2400_bim"
if errorlevel 1 goto :error

echo.
echo ========================================
echo   BUILD SUCCEEDED
echo   Output: %OUT%\sck2400_bim.hex
echo ========================================
goto :end

:error
echo.
echo ========================================
echo   BUILD FAILED
echo ========================================
exit /b 1

:end
