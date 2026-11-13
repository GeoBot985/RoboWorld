# STM32F407VG single-axis arm firmware

This sample builds a distinct Cortex-M4 image for the `arm-01` example robot. It reuses the bounded UART command protocol (`PING`, `STATUS?`, and `MOTOR <axis1> <axis2>`), configures TIM3 channel 1 on PA6 and direction output PD2, and holds channel 2 at zero. The simulated H-bridge connects only to the shoulder motor.

Install the pinned Arm GNU Toolchain and Renode versions from `toolchain.lock.json` into the local paths described in [Developer setup](../../../DEV_SETUP.md). Build from the repository root:

```powershell
.\samples\firmware\stm32-arm\build.ps1
.\tools\test-t07-arm-actuation.ps1
```

The second command sends a UART motor command through Renode and measures the resulting shoulder rotation in Godot. Generated firmware stays in the ignored local `build/` directory.
