# Makefile
# SCK-2400 Firmware -- SpaceCommsKit
#
# Purpose:
#   Command-line build system for sck2400_firmware.
#   Invoked by the C# ground station "Clean + Build" button.
#   Does NOT require CCS to be open.
#
# Usage:
#   make all          -- build bench firmware (0 dBm)
#   make field        -- build field firmware (+20 dBm)
#   make clean        -- clean all build artifacts
#   make flash        -- flash bench build via DSLite
#   make flash-field  -- flash field build via DSLite
#
# Reference: SCK-2400_Firmware_Requirements.md Section 1.1, 6

# -----------------------------------------------------------------------
# Tool paths (CCS 20.5.1 default install locations)
# -----------------------------------------------------------------------
CCS_ROOT     = C:/ti/ccs2051/ccs
COMPILER_BIN = $(CCS_ROOT)/tools/compiler/ti-cgt-armllvm_4.0.4.LTS/bin
DSLITE       = $(CCS_ROOT)/ccs_base/DebugServer/bin/DSLite.exe
CC           = $(COMPILER_BIN)/tiarmclang.exe
AR           = $(COMPILER_BIN)/tiarmar.exe

# -----------------------------------------------------------------------
# Project settings
# -----------------------------------------------------------------------
PROJECT      = sck2400_firmware
DEVICE       = CC1352P1F3RGZ
BOARD        = LAUNCHXL-CC1352P-2
SDK_ROOT     = C:/ti/simplelink_cc13xx_cc26xx_sdk_7_41_00_17

# -----------------------------------------------------------------------
# Source files
# -----------------------------------------------------------------------
SRCS = main.c \
       radio.c \
       uart.c  \
       ccsds.c \
       main_tirtos.c

OBJS = $(SRCS:.c=.obj)

# -----------------------------------------------------------------------
# Include paths
# -----------------------------------------------------------------------
INCLUDES = \
    -I. \
    -I$(SDK_ROOT)/source \
    -I$(SDK_ROOT)/source/ti/drivers \
    -I$(SDK_ROOT)/kernel/tirtos7/packages

# -----------------------------------------------------------------------
# Compiler flags
# -----------------------------------------------------------------------
CFLAGS_COMMON = \
    --target=thumbv7em-ti-none-eabi \
    -mcpu=cortex-m4 \
    -mfloat-abi=hard \
    -mfpu=fpv4-sp-d16 \
    -Os \
    -g \
    -Wall \
    $(INCLUDES)

# -----------------------------------------------------------------------
# Build targets
# -----------------------------------------------------------------------

# Default: bench build (0 dBm)
all: CFLAGS = $(CFLAGS_COMMON) -DTX_POWER=BENCH
all: $(PROJECT)_bench.out $(PROJECT)_bench.hex
	@echo "Bench build complete: $(PROJECT)_bench.hex"

# Field build (+20 dBm)
field: CFLAGS = $(CFLAGS_COMMON) -DTX_POWER=FIELD
field: $(PROJECT)_field.out $(PROJECT)_field.hex
	@echo "Field build complete: $(PROJECT)_field.hex"

# -----------------------------------------------------------------------
# Compile
# -----------------------------------------------------------------------
%.obj: %.c
	$(CC) $(CFLAGS) -c $< -o $@

# -----------------------------------------------------------------------
# Link
# -----------------------------------------------------------------------
$(PROJECT)_bench.out: $(OBJS)
	$(CC) $(CFLAGS) $(OBJS) -o $@

$(PROJECT)_field.out: $(OBJS)
	$(CC) $(CFLAGS) $(OBJS) -o $@

# -----------------------------------------------------------------------
# Convert .out to .hex
# -----------------------------------------------------------------------
$(PROJECT)_bench.hex: $(PROJECT)_bench.out
	$(COMPILER_BIN)/tiarmhex.exe $< -o $@

$(PROJECT)_field.hex: $(PROJECT)_field.out
	$(COMPILER_BIN)/tiarmhex.exe $< -o $@

# -----------------------------------------------------------------------
# Flash targets (DSLite)
# -----------------------------------------------------------------------
DSLITE_CFG   = targetConfigs/$(DEVICE).ccxml

flash: all
	$(DSLITE) --config=$(DSLITE_CFG) --flash=$(PROJECT)_bench.hex
	@echo "Bench firmware flashed."

flash-field: field
	$(DSLITE) --config=$(DSLITE_CFG) --flash=$(PROJECT)_field.hex
	@echo "Field firmware flashed."

# -----------------------------------------------------------------------
# Clean
# -----------------------------------------------------------------------
clean:
	del /Q *.obj *.out *.hex 2>nul || true
	@echo "Clean complete."

.PHONY: all field clean flash flash-field
