# 04 — MCU emulation, wiring, device fidelity

## 4.1 V1 MCU contract
- MCU identity **STM32F407VG**, selected target platform **STM32F4 Discovery**.
- Emulator **Renode**, pinned exact release + platform script and fork/extension SHA if modified.
- Firmware artifact: ARM ELF with debug symbols optionally retained, target triple/ABI/toolchain metadata, SHA-256 and source commit.
- Boot/stop/reset/reload/GDB/health managed by Robot Service. User may write firmware in VS Code, compile with pinned toolchain, deploy ELF by selection or CLI. No direct Python execution inside emulator.
- One MCU *type*, any practical count of *instances* subject to measured capacity. Each instance has stable ID and separate firmware, UART endpoints and peripheral state.

## 4.2 Board/peripheral evidence boundary
| Peripheral/path | Intended behavior | Current evidence | Remaining scope |
|---|---|---|---|
| ARM Cortex-M4 core/flash/RAM | Run compiled STM32F407 firmware | Boot, UART interaction, and debugger scenarios exercised in the pinned Renode setup | Broader firmware compatibility |
| UART selected USART | Host command/reply | Tested through Renode's socket endpoint; byte transport works | Physical 115200-baud serialization is not modeled |
| GPIO direction/output | Motor driver | Tested for the sample motor mapping | Other pin maps and peripheral configurations |
| Hardware timer/PWM | Control motor torque | TIM3 PWM tested in the sample path | Other timer modes and MCU variants |
| GPIO/timer encoder | Wheel speed return | Not implemented as a firmware-visible encoder path | Model and acceptance tests |
| I²C slave sensor | Example sensor model | Not integrated into the target bus | Model and acceptance tests |
| SPI, ADC, DMA, USB | Future peripherals | Not claimed as implemented | Individual acceptance before catalogue support |

**Do not count a driver as compatible solely because it compiles.** Run firmware that actually exercises init, read, update, interrupt and edge cases.

## 4.3 Real firmware and functional electronics
- Firmware executes real instructions. The MCU's memory-map, clock/reset behaviours, interrupts and target peripheral registers are modelled by Renode or validated extensions.
- External motor driver is a **functional electrical component**, not a transistor-level circuit. It accepts modeled PWM+direction/control pins and outputs motor drive voltage/current limited by selected profile.
- In the first educational catalogue, a single logical `motor_channel` connection represents the two physical H-bridge-to-motor wires; the underlying component model handles polarity and drive behaviour. It does not imply actual wiring uses one conductor.
- Motor is a **mechanical/electrical model** with speed-torque curve, winding resistance, back EMF, inertia, gearing and efficiency when specified; simple first implementation may use an explicitly labeled approximation.
- Encoder derives pulses from actual wheel/joint displacement and passes pulses through validated MCU interface, with quantization/latency/noise options.
- Distance sensor gets raycast from Godot world at sample time, applies range/field-of-view/noise/timing and populates its firmware-visible I²C/trigger-echo behaviour.
- Simplify pull-ups, regulators, passive resistors, PCB and cable layout; **do not simplify away interface compatibility**. A starter battery uses a functional `power` bus. Necessary MCU onboard regulation is assumed by the board model; validate logical power compatibility, but do not simulate a resistor/regulator circuit in P0.

## 4.4 Example component fidelity labels
- `functional`: modeled physics, simplified protocol. Cannot claim real-world driver compatibility.
- `driver_compatible`: tested firmware driver or equivalent register transaction suite passes for named MCU/emulator version and operating modes.
- `calibrated`: measured physical timing/accuracy/response against real device under defined conditions.
- `experimental`: known incomplete / not validated; conspicuous Shop label.
Store provenance, allowed modes, test firmware hashes, supported register subset and limitations for each model version.

## 4.5 Renode integration options
1. Use stock Renode platform peripheral + UART sockets and GDB first.
2. For missing GPIO/PWM/I²C bridge, create targeted Renode **C# peripheral/plugin extension** exposing timestamped events to Robot Service. Avoid memory-mapped magic registers absent on actual STM32 firmware unless labeled an integration-only engineering test.
3. Renode's Python scripting/mocks can prototype peripheral logic, but avoid treating an IronPython mock as adequate for production-scale complex peripheral models.
4. Control via documented monitor/external API with exact tested commands. Treat the monitor interface as privileged (loopback + limited access), never exposed to user apps.

## 4.6 Motor model initial equations
At the lowest functional model: applied PWM and H-bridge direction determine average terminal voltage; motor torque is a function of current (limited) and speed; joint torque affects Godot rigid-body physics. Use a stable numerical integrator and store mechanical parameters in SI units. Do not invent motor specifications for commercial devices. Document whether `stall_torque` and `no_load_rpm` are approximate or based on datasheet. Include static friction, freewheel/brake mode and saturation tests.

## 4.7 Device model authoring pipeline
`Manufacturer datasheet + supplier docs + open driver source -> extracted metadata -> proposed component profile + interface model + tests -> human/automated validation -> versioned catalogue entry`.
- Record exact URL, retrieved date, license, product revision and source commit/hash.
- Don't redistribute copyrighted libraries without checking license; factual specs can be transcribed with provenance.
- Generated interface models get register read/write tests, init tests, bad-command tests and integration firmware test.
- Never silently update a component profile used by saved robot; new version required.

## 4.8 P0 physical chain fixture
`Python -> UART bytes -> real firmware -> timer/PWM/GPIO -> TB6612FNG-like functional driver -> geared motor -> wheel joint -> encoder/virtual sensor -> STM32 input -> telemetry UART -> Python`.
For P0, may use a generic **explicitly labeled** motor-driver profile rather than claim complete TB6612FNG validation. The logic path remains accurate. Example PA0/PA1 pin assignments are not assumed: determine actual STM32 mapping and use generated validated board pin capability registry.

## 4.9 Acceptance
- Wrong firmware target rejected.
- Firmware missing or faulty: MCU status FAILED, attached devices show root cause vs inactivity.
- UART overflow/failure visible in diagnostics.
- GDB breakpoint demonstrably halts actual MCU firmware.
- An invalid I²C/UART connection rejected by builder.
- Replacing device parameters changes physical response without changing motor model source code.
- Time-limited samples reflect simulated time, not render frame count.
