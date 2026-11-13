# 03 — Simulation clock, runtime concurrency and transports

## 3.1 Two kinds of time
- `sim_time_ns`: signed 64-bit nanoseconds from the shared scenario epoch. Every physical event, sensor sample, MCU transaction and authoritative state carries this. If Exact firmware needs a bounded pre-simulation boot interval, validated-model mode adopts the same resulting epoch before the startup barrier releases; Godot world physics remains paused until that common epoch is installed.
- `host_time_ns`: monotonic host timestamp (for performance metrics and transport I/O only). Never use system wall date/time for physics.
- `real_time_factor = delta_sim_time / delta_monotonic_time` (1.0 target interactive).
- `physics_step_ns`: nominal `8_333_333` ns for 120 Hz, with rational/remainder accumulation to avoid long-run nanosecond rounding drift.
- `render_fps`: independently chosen; view interpolates snapshots for smooth output but must not invent physics contacts.

## 3.2 Ordering contract
For interval `[T,T+Δ)`, target logical order:
1. Drain already timestamped host-to-UART ingress events scheduled by simulation clock. Enforce baud framing and receive FIFO capacity.
2. Advance MCU virtual time within bounded interval; observe GPIO/timer/bus events emitted with effective timestamps (timing resolution may vary by model).
3. Run device dynamics and apply validated motor outputs to Godot physics for the next integration interval.
4. Godot advances physical state (engine-controlled fixed step or verified explicit stepping).
5. Sample sensor world observations at their defined sample times; schedule peripheral input event for a subsequent MCU boundary.
6. Publish immutable state snapshot with clock tick, physical state hash, events and health diagnostics.
Causal commands can be delayed by one boundary; **never deliver a sensor reading from the future**. This precise scheme is a proposal, to be adjusted based on the tested Godot/Renode stepping capabilities.

## 3.3 Backpressure
- Each UART has physical nominal baud, framing (`8N1` by default), FIFO depths and receive/transmit staging. Time per N-byte transfer at 115200 baud, 8N1 is approx `N*10/115200` seconds excluding handling; sample value, not a blanket MCU performance guarantee.
- Gateway ingress capacity and TTL must be explicitly bounded. For data beyond capacity, choose a documented policy (serial receiver overflow, simulated UART FIFO overflow, or client-side backpressure) and surface `bytes_dropped` and cause.
- Reliable control/commands: ordered, ack/timeout-aware, never silently dropped. Diagnostic telemetry: bounded lossy channel permitted. Frames: bounded latest-frame ring permitted.
- Camera frames transported via **shared memory** later only after a frame-copy benchmark; first use a fixed-size, bounded local transport with documented format.

## 3.4 Transport channels
| Channel | Purpose | P0 concrete choice | Constraint |
|---|---|---|---|
| Python → virtual STM32 | Raw firmware protocol | TCP local raw byte stream mapped to virtual UART | Python may use `pyserial.serial_for_url('socket://127.0.0.1:<port>')` or socket; no magical application parser |
| Godot ↔ Robot Service | Internal control/events | Length-prefixed protobuf over TCP loopback | Version handshake, bounded frames, startup discovery |
| UI → lifecycle | Start/pause/save/diagnose | In-process typed command bus | No direct physics mutation from UI |
| Telemetry → panels | Status and metrics | Subscription snapshots | Low overhead; rate limit |
| Future client ↔ cloud world | World visual state | Versioned interest-filtered replication, TLS | Not P0 |
| Future cloud player app ↔ MCU | Internal gateway | Sandbox-local transport | Same logical robot/firmware API |

## 3.5 Version handshake
On connection exchange: `protocol_major`, `protocol_minor`, `session_id`, `world_id`, `client_kind`, capabilities. Reject incompatible major versions and unauthorised endpoints. Limit packet bytes, event times, count and rate. Use IDs not transient Godot object pointers.

## 3.6 Parallelism and priority
- Simulation thread/physics phase has highest logical priority, followed by MCU synchronization and device model processing.
- Rendering may skip frames or lower quality; don't pause physical time simply because viewport is occluded or camera points elsewhere. World remains visible.
- File I/O, source compiling, telemetry retention and thumbnails go onto background workers; no heavy work on Godot physics callback.
- On sustained overload, real-time factor falls below 1.0 and **all simulation domains slow coherently**. Do not silently skip MCU or physics events.
- Threading details must respect Godot's documented thread-safety constraints; no accidental calls to scene tree/graphics APIs from worker threads.

## 3.7 Reproducibility
- Seed procedural noise by `(world_seed, component_id, stream_id)`; store seed+component version in project/checkpoint.
- Record external application command stream with sim-time arrival for replay. External Python program itself may have wall-clock nondeterminism; do not claim it is checkpointed.
- Stamp every diagnostic with session ID, robot ID, component ID, sim ns, host monotonic ns and sequence number.

## 3.8 Initial SLOs (targets, not observed)
| Item | Design target / measurement |
|---|---|
| Physics | 120 Hz fixed-step attempt, record achieved tick rate/RTF |
| View | 60 FPS desirable on reference system; keep viewport visible at any achieved rate |
| P0 MCU | 1 fully working instance; 4-instance stress test target after G5 |
| Local gateway added overhead | p95 below 1 ms under simple ping/pong **excluding virtual UART timing** |
| Event queue | No unbounded growth under sustained 2× producer overload |
| Simulation time | No backward clock jumps or causal inversions; measured drift and precision disclosed |
| Fault isolation | MCU crash does not terminate Godot window; robust restart |

## 3.9 Important limitation
Renode virtual-time and configured CPU MIPS are *not* proof of cycle-accurate STM32 timing. Bus timings, DMA, interrupt jitter, clock trees and device fidelity must be individually validated. Any SLO with no benchmark evidence is marked TARGET rather than guaranteed.
