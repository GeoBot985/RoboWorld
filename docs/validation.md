# Validation record

This record summarizes accepted results from the Windows 11 x64 development machine. Exact integration runs use Godot 4.7.2, .NET SDK 10.0.403, Renode 1.17.0, and the firmware/tool versions listed in `toolchain.lock.json`. The repository does not contain the raw local `artifacts/evidence` tree; the tests below are the reproducible source of future results.

## Checks rerun for the public snapshot — 2026-11-13

| Check | Result |
|---|---|
| Release build of `RoboWorld.slnx` | Pass; 0 warnings, 0 errors |
| Domain tests | Pass; 161 assertions |
| Robot Service tests | Pass; 27 assertions |
| `tools/validate_examples.py` | Pass; 24 JSON documents |
| `tools/test-rs01a-authoring.ps1` | Pass; empty/partial/valid draft lifecycle and existing project compatibility |
| `tools/test-rs01d-warehouse.ps1` | Pass; saved camera-rover package and sensor-ray collision with `sensor-crate` at 3.115 m |

The source-workspace camera-rover E2E run recorded 0.4579 m chassis Z displacement, peak left/right wheel speeds of 10.8088/10.8157 rad/s, and eleven Godot ray samples spanning 0.54–2.47 m. Four firmware readings spanned 540–2132 mm; maximum error against the latest causally completed ray sample was 1.235 mm. The mounted camera follow check passed after 0.0112 m chassis motion and 0.0114 m camera motion, with zero measured relative position and rotation error. These are results for that run, not general accuracy guarantees.

The same camera-rover scenario did not complete in the duplicate public snapshot workspace: Robot Service did not reach its Ready endpoint within the test script's 30-second startup window under the observed headless load. The service regression suite did boot real STM32 firmware through Renode. The camera-follow result above remains the accepted source-workspace run; the slower duplicate-workspace attempt is not counted as a pass.

## Previously accepted integration evidence

- Exact camera-rover E2E (`tools/test-t04-e2e.ps1`) passed on the source workspace before snapshot curation; run measurements are summarized above.
- STM32F407 firmware loaded in Renode handled UART commands and produced TIM3 PWM/GPIO outputs that drove Godot wheel bodies. The six-second loaded-rover test measured 0.0426 m chassis displacement; signed wheel response matched the tested 220 RPM profile within ±0.6 rad/s over the tested duty range. Route-fault and MCU-halt tests verified that diagnostics and actuator outputs followed the measured peripheral state.
- The synchronized sensor path used Godot ray samples, a versioned event stream, timed PA0 pulses, STM32 TIM2 capture, and firmware UART replies. Tick IDs and integer-nanosecond boundaries were checked; pause/resume held the same simulation timestamp. UART socket transport does not model 115200-baud wire serialization, and the sensor result is not calibrated.
- Godot fixed-step world tests completed 1,201 steps in both 15 FPS and 60 FPS presentation runs; measured physics rates were 120.00 Hz and 120.76 Hz. This does not imply that the simulation meets real-time throughput under every workload.
- The Robot Shop domain suite covers empty and partial drafts, structural root assignment, snapping, mechanical/electrical graph validation, persistence, and launch readiness. Existing valid projects continue through the same runtime loader and firmware path.

## Scope and limits

The current source has broad domain and integration coverage, but not every product/release goal is complete. Runtime checkpoint/resume is deferred. Clean-machine packaging, Linux compatibility, sensor calibration, broad Fast-backend fidelity/repeatability, multi-controller Fast scaling, and the full performance target remain unverified or partial. These limits are retained in the README rather than hidden by passing narrower tests.
