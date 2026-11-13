# 15 — Product requirements and traceability

## User outcomes
- **RW-P01** Build robots from modular bodies, beams, joints, mounts, wheels, servos, motors, sensors and MCU modules.
- **RW-P02** Work in Junior, Robotics Engineer or Advanced Workshop experiences built on the same underlying parts/simulation.
- **RW-P03** Write host control apps in VS Code and execute on the host PC (Python first). Main app is not placed in the world.
- **RW-P04** Compile real target MCU firmware and load it into one or more virtual STM32F407VG devices.
- **RW-P05** Firmware interacts with hardware through emulated CPU/peripherals and defined virtual connections; not through magic per-robot commands.
- **RW-P06** Main app uses familiar serial/network interfaces, initially a TCP-backed virtual UART, with transport adapter for physical serial.
- **RW-P07** Hardware buses respect configured nominal throughput, latency, timing, queues and failure modes within declared fidelity.
- **RW-P08** Observe a 3D world at all times, even when robots do nothing. Simulation correctness takes precedence over frame rate.
- **RW-P09** Diagnostics grouped Robot > MCU > Bus/Device, with color + text/icon status, cause vs downstream symptom, and drilldown.
- **RW-P10** Save design and load again; support coherent runtime checkpoints with explicit compatibility and limitation handling.
- **RW-P11** Use/manually create and later request AI-generated structured worlds. Room example: 20 m x 20 m, table and glass on top.
- **RW-P12** Use models of real popular parts with documented provenance and distinct interface-fidelity status.
- **RW-P13** One install and launcher manages viewport, simulation, MCU host and local external-controller listener.
- **RW-P14** Modular architecture is transferable to future authoritative, persistent cloud world and sandboxed player slots.
- **RW-P15** User can watch cloud players' robots in a shared world in future; do not implement cloud now.

## Prioritization
| Priority | Requirements | Stage |
|---|---|---|
| MUST prove | P03–P08, P13 | P0 |
| MUST design and partially implement | P01, P09, P10, P12 | P0/MVP |
| MVP | P01, P02, P09–P12 | MVP |
| Architectural constraint only | P14, P15 | Future |

## In scope P0
- One fixed 3D room and rover with two wheels; one selected MCU instance; simple UART firmware command protocol; motor, encoder or distance-sensor round trip; panel; sample code; local launcher; design save; performance traces.
- Component models minimal but defined through reusable schemas, not hardcoded rover-only logic.

## Explicitly out of P0
- Full Robot Shop, articulated arm/gripper, cloud deployment, shared worlds, AI generation UI, school accounts, offline model inference, real-to-sim calibration, exact CPU cycle accuracy, 100-robot guarantees, true arbitrary Python process checkpoint, arbitrary third-party component installation.

## Nonfunctional requirements
- **RW-N01** Isolation: host application only has protocol-based robot access, no direct physics mutations.
- **RW-N02** Evidence: benchmark measured simulation speed, physics ticks, hardware latency, renderer rate, queue overflow, component health.
- **RW-N03** Recoverability: unresponsive emulator yields a red fault diagnostic while viewport remains usable; restart route exists.
- **RW-N04** Portability: contract/data/clock/logic libraries free of Windows-specific APIs.
- **RW-N05** Versioning: all saved project and component definitions versioned; mismatches produce explicit error/migration.
- **RW-N06** No misleading realism: mark physical and peripheral fidelity by validated level and version.
- **RW-N07** No extra service-babysitting: install/uninstall/start/stop controlled by one desktop entry point.
- **RW-N08** Licensing/provenance for external data, binaries, 3D models and drivers.
