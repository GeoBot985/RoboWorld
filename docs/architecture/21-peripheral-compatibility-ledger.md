# 21 — Peripheral/firmware compatibility ledger template

**Do not turn this template into green check marks without actual firmware evidence.** The same MCU family and board model may have gaps for individual mapped peripherals and configured pin modes.

## Board target
- Product identity: STM32F407VG / STM32F4 Discovery.
- Renode executable version/build SHA: `UNPINNED — T00`.
- Board platform `.repl/.resc` file SHA and URI: `UNPINNED — T00`.
- ARM GCC/GDB version and target flags: `UNPINNED — T00`.
- Reference firmware SHA: `UNBUILT — T01`.

## Compatibility matrix
| MCU peripheral | Firmware operation | Expected observation | Renode model class / notes | Test file / evidence | Status |
|---|---|---|---|---|---|
| UART selected USART | Initialize/send/receive/interrupt/poll | Same UART byte sequence and FIFO behaviour | Probe script first | G1 | NOT TESTED |
| GPIO output | Set/clear and read output state | External bridge sees correct level change | Identify mapped pin/alternate function | G2 | NOT TESTED |
| Timer PWM channel | Configure frequency/duty/start/stop | External bridge receives correct PWM/duty at correct sim time | Verify timer and pin path | G2 | NOT TESTED |
| GPIO/timer encoder | Rising/falling edges/counter | Firmware sees counts proportional to wheel rotation | Validate edge/interrupt semantics | G3 | NOT TESTED |
| I²C target channel | Start/read/write/ack/status | Firmware driver initializes device and reads register data | May need C# model and bus wiring | G3 later | NOT TESTED |
| SPI target channel | Transaction/CS/IRQ | Correct device data, transfer timing | MVP+ | — | NOT TESTED |
| ADC | Voltage sample from sensor | Conversion sequence and result registers | MVP+ | — | NOT TESTED |
| USB / DMA | Transfer | Real driver behaviour | Not assumed in first project | — | OUT OF SCOPE |

## Required per-device compatibility report
```text
Component model ID and version:
Real part / silicon revision:
Source datasheet, driver URL, driver commit/licence:
MCU firmware binary SHA, toolchain, tested MCU board:
Renode executable version and peripheral plugin SHA:
Registers and peripheral operations tested:
Timing limits tested:
Tests run with stdout/trace artifact paths:
Expected and measured outputs:
Unsupported commands or timings:
Fidelity: experimental / functional / driver_compatible / calibrated
Approver/Date:
```

## Testing rubric
1. Driver can detect hardware and initialize from reset, not just read a fake value.
2. Driver correctly handles periodic measurements and status transitions.
3. Defined failure modes: no device, bad address, timeout, out-of-range, overflow.
4. Repeatability and timing measurement with synchronized sim timestamps.
5. No dependence on the visual viewport FPS or hidden application command handler.
