# 07 — Persistence, save/load, runtime checkpoints and replay

## 7.1 Four distinct persistence concepts
| Type | Content | Goal | Delivery |
|---|---|---|---|
| `Project Design` | Canonical world, robot assembly, wiring, firmware references, assets, settings | Edit/reopen same design | P0 |
| `Runtime Checkpoint` | Paused world poses/velocities, device states, Renode state, queued events, sim clock | Resume same simulation point | Gate G6; MVP if proven |
| `Telemetry Recording` | Timestamped sensor/actuator/health/events/visual state samples | Inspect performance and compare | P0 minimum logs; MVP richer |
| `Input Replay` | Deterministic seed, external host commands with timestamps, initial design/assets/versions | Repeat an experiment | MVP, fidelity qualified |

A snapshot of **external Python process memory** is *not* part of standard runtime checkpoint. On resume, user code reconnects/restarts and must handle the resumed world, unless later special-purpose sandbox checkpointing exists.

## 7.2 Project structure
```text
MyRoboWorldProject/
  project.json                 # schema/version/owner/world references
  worlds/main.world.json       # authoritative world definition
  robots/rover-01.robot.json   # authoritative assembly/firmware binding
  components/                 # project-owned immutable custom definitions
  firmware/stm32-drive.elf     # content-addressed/pinned binary
  assets/                     # approved glTF/textures with manifest/license
  checkpoints/                # runtime snapshots (optional gated)
  recordings/                 # opt-in experiment recordings
```
Users may create folders; canonical paths are relative and sanitized against traversal. Keep `project.json` human readable, not only a database. Project can be zipped as `.roboworld` later, with safe extraction and size limits.

## 7.3 Immutable referential integrity
- Component and asset references use `(id, version, sha256)`. Load missing/incompatible dependencies with explicit error and migration suggestion; do not silently substitute latest.
- Firmware images stored by SHA-256; record toolchain and target board. Never deserialize executing code from untrusted project as a privileged host executable.
- Schema migrations are numbered, additive where possible, run on copy with backup; tests load all historical fixtures.

## 7.4 Atomic save protocol
**Design save:** validate graph -> serialize to temporary dir/file -> fsync where supported -> atomic rename -> log version/hash. Never overwrite last valid project after partial write.

**Runtime checkpoint attempt:**
1. Request consistent pause at authoritative simulation boundary; block new host commands or queue them with a clear policy.
2. Confirm physics, device models and Renode instances are quiescent at a compatible simulated time.
3. Capture Godot physical snapshot, Robot Service device state and queues, Renode `Save` state files, emulator version/build hash, RNG seeds, clock.
4. Write checkpoint manifest with per-file hashes, dependencies and compatibility constraints; commit atomically.
5. Resume the world.
6. On load, validate versions/hashes, recreate processes/bridges, load Renode state and physics, restore queues/clock, verify device endpoints, then resume.
**Gate:** if exact cross-process consistency is not demonstrated, label saved result 'design only' or 'restart checkpoint' and don't promise exact continuation.

## 7.5 Renode consideration
Renode documents `Save`/`Load`, but warns that save files may be incompatible across Renode versions. Our checkpoint manifest MUST pin emulator version, board platform script, firmware hash and plugin versions. Test opening UART bridges after restore, as external connections are not automatically equivalent to in-process state.

## 7.6 Replay specification
- `recording.json` manifest: world/project hash, controller source/build hash if supplied, firmware hashes, seed, sim duration, device profile versions, event count and clock fidelity.
- Binary event log: `(sequence, sim_time_ns, event_type, robot_id, component_id, payload)`; time-order and CRC/hash per chunk.
- Replays can be **visual state playback** (shows recorded positions, not re-simulated physics) or **simulation re-run** (replays input commands and re-simulates). Label clearly. User always has a visible 3D world during playback.
- For re-simulation, compare state hashes at checkpoints; report drift rather than silently asserting determinism.

## 7.7 P0 acceptance
1. Save design, close app, relaunch and visually reconstruct same world/robot/firmware assignment.
2. Invalid manifest/version rejected gracefully with human-readable error.
3. No project load launches arbitrary external binaries as privileged child processes.
4. Crash during save leaves last good design intact.
5. Runtime checkpoint only marked PASS after MCU+world+bridge restore test.
