# 12 — Technical risk register

Severity scale: `Critical` threatens core product premise; `High` blocks release; `Medium` degrades scope/performance; `Low` manage routinely. State initially `OPEN` until an evidence-backed gate is closed.

| ID | Risk | Severity | Validation / mitigation | Gate |
|---|---|---|---|---|
| R01 | Renode STM32F407 timer/PWM output not sufficiently modeled | Critical | Firmware-driven duty test; targeted C# peripheral extension if feasible; no fake pass | G2 |
| R02 | Sensor bus models insufficient for real firmware driver | Critical | Firmware readback over GPIO/timer or I²C; model validated register/interrupt subset | G3 |
| R03 | Renode virtual time and Godot physics cannot maintain causal step synchronization | Critical | Dedicated time spike with measured barriers and drift; if blocked, explicit redesign | G5 |
| R04 | MCU CPU/peripheral timing not cycle-accurate | High | Disclose fidelity tier; empirical timing calibration; do not market perfect hardware realism | G1/G5 |
| R05 | Godot joints cannot sustain articulated arm constraints | Medium/High | Arm load and torque stress fixture once rover works; adapter isolates replacement | MVP |
| R06 | Full runtime save not coherent across engine/emulator/queues | High | Quiescent checkpoint with version locks, restore validation; design-save fallback labeled | G6 |
| R07 | GPU readback/camera streams stall physics | Medium/High | SubViewport benchmark, async/bounded frames, sampled fps, clear separation | MVP |
| R08 | Component models from internet incomplete or copyrighted | High | Provenance/license registry, human test/approval; generated model plugins not trusted by default | MVP |
| R09 | Sandboxed cloud player/firmware escapes isolation or starves world | Critical future | VM/sandbox isolation, quotas, capability-scoped world API, audit | Cloud |
| R10 | Godot/.NET desktop export packaging or Renode redistribution unexpected constraints | High | Toolchain pin, license inventory, clean Windows install gate | G0/G7 |
| R11 | Project model becomes tied to Godot scene files | High | Pure-domain JSON schemas and engine adapter tests; persist only canonical specs | Continuous |
| R12 | API bypass allows Python application to command physics directly | High | Separate gateway/authorization; E2E-004 negative test | Continuous |
| R13 | Physics stalls kill responsiveness or UI | Medium/High | Async services, render snapshots, watchdog; controlled slowdown and visible dashboard | G4/G5 |
| R14 | Large world/robot count outstrips 32GB / shared iGPU bandwidth | Medium | Memory budget, LOD, capped sensor frames, RTF observability | Benchmark |
| R15 | Hardware profile updates break saved robots | Medium | Immutable component versions, hashes, schema migrations and fixture tests | Continuous |
| R16 | AI-generated world object placement unstable | Medium future | Deterministic schema/asset whitelist/constraint solver plus preview | MVP |

## Risk triggers
- A 'real firmware' gate passes without a trace showing actual firmware executed: invalidate gate.
- A firmware-visible I²C/PWM component only works by writing a game-level variable: mark `functional`, not `driver_compatible`.
- RTF diverges under load without clear UI warning: release blocked.
- Saved checkpoint opens with a different pose/MCU command queue or incompatible Renode version: do not offer exact runtime resume.
