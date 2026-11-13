# STM32F407VG drive and sensor firmware

This bare-metal Cortex-M4 sample runs in Renode's STM32F4 Discovery machine. The firmware implements the bounded UART command protocol used by the RoboWorld drive and sensor examples. Renode transports bytes; the firmware parses commands and configures the MCU peripherals.

## Supported sample behavior

- UART4 on PD0/PD1 at the configured 115200-baud rate from the reset HSI clock.
- `PING`, `STATUS?`, and bounded `MOTOR <left> <right>` commands.
- TIM3 PWM outputs on PA6/PA7 and direction GPIO outputs on PD2/PD3 for the tested motor-driver mapping.
- A PA0 input pulse measured with TIM2 for the current distance-sensor path.
- Invalid, overlength, and incomplete input handling as implemented in `main.c`.

These are tested sample behaviors, not a claim of complete STM32F407 peripheral coverage or cycle-accurate UART timing. See [validation results](../../../docs/validation.md).

## Build and run

Install the pinned Arm GNU Toolchain and Renode versions from `toolchain.lock.json` into the local paths described in [Developer setup](../../../DEV_SETUP.md). Then run from the repository root:

```powershell
.\samples\firmware\stm32-drive\build.ps1
.\tools\test-t01.ps1
```

The test script runs the firmware protocol checks through Renode. `tools/test-t03-e2e.ps1` exercises the firmware-driven motor path in Godot. Generated ELF and build metadata remain local under the ignored `build/` directory.
