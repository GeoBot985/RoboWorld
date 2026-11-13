# 10 — Test strategy, benchmarks and quality gates

## 10.1 Testing layers
| Layer | Target | Examples |
|---|---|---|
| Schema/semantic | Every manifest and component | Duplicate IDs, negative dimensions, incompatible ports, invalid relative paths, broken refs |
| Pure .NET unit | Assembly graph, numerical device models, scheduler | Joint axis normalization, torque saturation, ticks, UART FIFO/drop policy |
| Firmware integration | Pinned Renode board + real ELF | Boot, UART, PWM/GPIO, sensor reads, GDB, checkpoint |
| Godot physics integration | Scene and joints | Wheel actuation, sensor raycast, collision, mass/torque impact |
| End-to-end desktop | 1-click launcher with worker | Python command → firmware → motion → telemetry → panel |
| Soak/robustness | 30 min smoke, 4 h stability baseline | Worker crash/restart, invalid firmware, bad project, save during run |
| Performance | Reference 8945HS/780M/32GB | 120 Hz physics, RTF, viewer FPS, 4 MCU stress, IPC p95, memory/copies |
| Security/local | Process and API access | Loopback only, path traversal, oversized data, port collisions, untrusted project |

## 10.2 Authoritative E2E scenarios
- **E2E-001 Firmware provenance:** Arduino-like host app controls an external Python process; emulator GDB trace proves STM32 ELF actually parses command.
- **E2E-002 Motion:** `MOTOR LEFT 50; MOTOR RIGHT 50` over virtual UART changes timer/MCU outputs -> motor torque -> rover moves in world; compare 0% vs 50% distances within measured tolerance.
- **E2E-003 Sensor:** place wall at known range, move rover, firmware reads sensor via an emulated interface, Python receives changing distance telemetry.
- **E2E-004 Isolation:** send `MOVE` to diagnostic/control endpoint; must NOT move robot except via correct firmware/hardware path.
- **E2E-005 Slow graphics:** cap visual FPS 15 vs 60 and compare 10-second simulated trajectory; quantify positional difference and tick count.
- **E2E-006 Failure:** terminate Renode; visible 3D world stays up, correct `FAILED` status, user can restart.
- **E2E-007 Multi-MCU:** two separate firmware builds/instances, one faults; the other continues as designed, ports isolated.
- **E2E-008 Design save:** create robot/world, save, reopen without firmware corruption or lost connections.
- **E2E-009 Checkpoint:** only when G6 proven, pause/save/load with MCU queue/device/physics coherence.

## 10.3 Quantitative targets (test, not promise)
- Initial 1080p room/rover: target average >=60 rendered FPS, but **do not fail simulator correctness when FPS is lower**.
- Godot physics fixed 120 Hz configured; track actual tick count, long-step distribution and RTF. Under overload, RTF reduces rather than dropping/duplicating physical time.
- End-to-end device delay compare commanded hardware protocol bound (UART 115200 8N1 expected 86.8 ms for 1,000 bytes before higher-layer overhead) with observed emulator delivery; list discrepancies and causes.
- Raw local gateway added overhead p95 <=1 ms under isolated 256-byte ping/probe, excluding simulated hardware transit. Record p50/p95/p99 separately.
- RAM/process/CPU/GPU allocations captured across 1/2/4 MCU runs; no unbounded queues or resource leaks.
- Startup, shutdown and recovery times measured on reference machine; acceptance thresholds finalized after G0 baseline; do not assume impossible time budget.

## 10.4 Test data policy
- Deterministic fixture world (`examples/worlds/room-20x20.world.json`), rover (`examples/robots/rover-01.robot.json`), firmware build hashes, known commands and seed.
- Never test only using easy perfect sensor inputs; include sensor out-of-range, obstruction, dropped UART, stalled motor, wrong firmware, absent MCU, old component versions and high load.
- Metrics reports store `git_sha`, `toolchain.lock.json` hash, exact firmware hash, board script hash, Godot/driver version, hardware, test seed, sim duration, wall duration and distribution statistics.

## 10.5 Fidelity scorecard (per component version)
| Domain | Evidence |
|---|---|
| Interface | Real driver library initialization and read/write tests |
| Timing | Protocol event sequence timing/frequencies and queue capacity |
| Physics | Known motor/servo test curve under load compared to datasheet / bench |
| Environment | Sensor range, noise, update period under controlled world geometry |
| Limits | Invalid register, out-of-range, power/brake/stall and failure behavior |

Report level (`functional`, `driver_compatible`, `calibrated`, `experimental`) and limitations. No green badges from tests not executed.

## 10.6 Acceptance evidence
An implementation claim is accepted only when it is supported by passing tests, regression results, reproducible commands, and recorded evidence. Reports identify any path that bypasses firmware execution or hardware timing assumptions. A blocked criterion records its cause and the decision needed to proceed.
