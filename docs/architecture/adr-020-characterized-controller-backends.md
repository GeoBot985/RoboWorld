# ADR-020: Optional characterized controller backends

- **Status:** Accepted for implementation under Goal 03, 2026-11-08
- **Decision owner:** RoboWorld product owner
- **Related:** ADR-019 (runtime checkpoint/resume deferred); Goal 02 (performance/release); Goal 03 (automatic controller characterization)

## Context

The exact runtime executes the compiled controller firmware in the pinned Renode build and exchanges timestamped events with authoritative Godot physics. Goal 02's matched 10-minute visible sensor/actuation runs measured 0.531 RTF and 7.874 median FPS before the rendering correction. After the correction, a 10-minute Release run measured 0.406 RTF and 60.009 median FPS with exact shared tick boundaries. It still measured 1,647.791 ms p95 command-to-visible-motion. The renderer is now independent in measured frame intervals, but the exact controller path remains too slow for the desired interactive experience.

The product owner directs investigation of an optional deterministic execution model induced from observed behavior. This is behavioral equivalence over a declared, tested input/configuration domain, not general reverse engineering or proof of arbitrary firmware equivalence.

## Decision

Introduce an engine-independent controller instance contract with two backends: exact execution in Renode and an optional bounded deterministic model evaluator. Exact Renode execution remains first-class, selectable, and the reference oracle. A model is opt-in only after characterization and held-out acceptance. `Auto` may select a model only when its complete identity and declared input/configuration envelope match; otherwise it selects exact Renode before run with an explicit diagnostic. A user may choose exact mode at any time. Backend changes require a simulation reset/start; no mid-run state transfer or state-equivalence claim is made.

The logical contract carries initial/reset state, ordered simulation-time-stamped inputs, `advance_to(sim_time_ns)`, timestamped outputs, state and diagnostics. It must preserve the current canonical nanosecond clock, ordered event causality, tick acknowledgments and bounded backpressure. It does not own Godot objects, rendering, whole-robot motion or physical sensor generation. Godot continues to compute live physics; versioned sensor/device models produce physical signals and timing independently of firmware behavior. The renderer consumes committed state/snapshots and does not determine physics or MCU time.

Only serializable data using a small, versioned, bounded operator vocabulary may be evaluated. No generated source execution, dynamic plugin loading or arbitrary network access is permitted. Initial model candidates include constant/affine, deadband/clamp, piecewise linear/table, explicit delays/rate limits/hysteresis, and finite-state machines with timers/latches. A fitter must select the simplest model meeting all required gates; it may return `unsupported` rather than fit. A hand-authored approximation cannot be presented as induced from traces.

## Profile identity and invalidation

A profile is valid only if all identity fields match: firmware ELF SHA-256; MCU/board and peripheral contract; validated controller/component/electrical/mount graph; immutable actuator/sensor definition versions and hashes; reset/initial conditions; characterization schema, trace corpus and generator/fitter version; model schema/evaluator version; and the explicitly validated input/configuration envelope. Any mismatch invalidates the profile. Unknown hardware limits remain unknown. Out-of-domain inputs are rejected or cause a declared exact-mode fallback **before simulation starts**; the evaluator never extrapolates silently.

### Fault ownership

Fault behavior follows the architecture's existing authority boundaries. The controller model may represent a measured MCU/controller transition when the fault is an input to that controller instance. The first induced transition is `MCU_HALTED`: preserve the most recent PWM/direction state already routed to the device model, suppress later firmware UART replies, and require an explicit session reset before normal controller behavior resumes. GPIO alternate-function/route failures remain owned by the physical device/runtime path, which reports the route fault and applies its existing safe-stop; they are not encoded as firmware rules. Unrecognized controller fault codes remain outside the model envelope and fail closed.

## Validation and evidence

Characterization starts Renode from a reproducible reset/boot condition and records timestamped inputs, outputs, faults and relevant peripheral state. The corpus is bounded and deterministic. Held-out validation separates whole input sequences from fitting data; adjacent samples from the same trajectory may not leak across the split. Required tests cover valid/invalid command grammar, duty/direction, timing, state/history, reset, supported fault/latch/recovery, sensor response, trajectory against live Godot physics, and deterministic replay. Thresholds are set before fitting as defined in Goal 03; negative/fault gates are mandatory, not averaged into aggregate accuracy. Profile artifacts retain hashes, corpus split and scores. A failed gate means the profile is unqualified and exact Renode remains available.

Initial supported behavior is limited to the existing STM32 rover firmware and mapped PWM/direction/UART interface with the current sensor stimulus. No claims are made for arbitrary firmware paths, undocumented registers/interrupts, unmodeled peripherals, cameras, high-bandwidth sensors or unseen inputs. Those cases use exact execution or are explicitly unsupported.

## Consequences

- Exact-mode T01–T07 acceptance remains unchanged; fast-model evidence is reported separately and cannot replace it.
- Saved projects may reference a profile identity but never persist ephemeral MCU state or promise runtime checkpoint/resume. ADR-019's A-011 deferral remains.
- Profiles are immutable versioned data and safe to invalidate/rebuild. Characterization is budgeted and cancellable, with bounded queues, timeouts and cleanup.
- The deterministic evaluator adds a new trusted component that requires schema validation, finite-value/range checks, bounded state/memory/operations, deterministic serialization/evaluation, and negative tests.
- Failure to produce a qualifying model is a valid outcome. The project remains runnable in exact Renode mode, but no performance claim is implied.

## Rejected alternatives

- Removing Renode or relabeling a hand-tuned motor imitation as firmware equivalence: violates exact-mode and induction requirements.
- Fitting arbitrary opaque scripts or unconstrained ML: not inspectable, bounded or safely replayable under this product contract.
- Replacing the physical world with recorded trajectories: removes live collisions, actuator dynamics and sensor causality.
- Swapping backends in a running simulation: would require proven hidden-state equivalence and is explicitly out of scope.
- Reducing canonical physics frequency or changing the renderer/time relationship as a substitute for controller acceleration: separate question requiring Goal 02's controlled validation.
