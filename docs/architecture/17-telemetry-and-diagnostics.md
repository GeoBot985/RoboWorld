# 17 — Diagnostics, status and tracing contract

## 17.1 Hierarchical component status
Every component has immutable `id`, parent path, kind, model/version, health status, health reason code, last successful poll/sample, last error, metrics and root-cause references. UI groups by `Robot > MCU > Bus > Device`, with separate host-attached camera, battery/power and structural categories.

Status enum:
| Code | UI | Definition |
|---|---|---|
| `OPERATIONAL` | green icon/text | Running and confirmed responsive within its intended role |
| `FAILED` | red icon/text | Error confirmed (firmware crash, missing required peripheral, bus stuck, process stopped unexpectedly) |
| `DEGRADED` | amber icon/text | Running but incorrect/partial response, queue overrun or timing outside target |
| `INACTIVE` | grey icon/text | Intentionally disabled, no command, no motion expected, or disconnected as configured |
| `UNKNOWN` | neutral icon/text | Insufficient evidence to classify |

**Never equate zero RPM, zero bytes, or no movement with a failure.** This is an important educational requirement: doing nothing may be valid or merely a firmware logic bug.

## 17.2 Fault attribution
- Each fault event gets `fault_id`, `source_component_id`, `reason`, `first_sim_ns`, `last_sim_ns`, `count` and optional `caused_by_fault_id`.
- Descendant components have their own status; optionally annotate `BLOCKED_BY: stm32-drive firmware fault` without falsely marking motor physically FAILED.
- Group status is derived from descendants (`FAILED` if any confirmed critical child failed), but always preserve actual child health and reason.

## 17.3 Baseline metrics
| Component | Minimum metrics |
|---|---|
| World | sim ns, real-time factor, physics ticks/s, render FPS, physics step duration p95, world snapshot latency |
| Robot | speed, position, active firmware assignments, faults, last command age |
| MCU | emulator process state, firmware SHA, MCU model, virtual time, event count, debugger attach, queue wait |
| UART | rx/tx bytes, baud, framing, FIFO occupancy, dropped bytes, last activity |
| Motor driver | input duty/direction, output voltage/current if modelled, thermal/overload flag |
| Motor | angular velocity RPM, torque N·m, angle, load/stall, command time |
| Encoder/sensor | value, units, sample period, timestamp, error/timeout, fidelity label |
| Camera | resolution, requested/achieved capture FPS, queue utilization, dropped frames |

## 17.4 Telemetry envelope and transport
Use `contracts/proto/roboworld_runtime_v1.proto` for event envelope. Required fields: schema version/sequence/sim ns, world/robot/component UUID-like IDs, payload kind, trace ID and optional fault reference. UI subscribes and can filter by robot/path; rate-limit high-frequency updates without altering physics. Persist key fault/clock information in playback logs.

## 17.5 UX sample
```text
Rover-01                          DEGRADED (1 confirmed fault)
  STM32 #1 — Drive                OPERATIONAL  fw=drivexxx; 1.00x MCU
    Firmware                      OPERATIONAL  loop alive
    USART2                        OPERATIONAL  RX 1243 B / TX 644 B
    Driver A                      OPERATIONAL  duty=0.50
    Left motor                    OPERATIONAL  81 RPM
    Right motor                   DEGRADED     command stale (cause: PWM missing)
  STM32 #2 — Sensors              FAILED       emulator process exited
    VL53L0X                       INACTIVE     no live MCU connection
  USB camera                      OPERATIONAL  640x480 @10 FPS
```
Rows selectable: highlight physical part in viewport; expand for timestamps/errors and underlying firmware register/transport traces as available.
