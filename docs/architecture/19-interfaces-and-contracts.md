# 19 — Interface and data contracts

## 19.1 Contract versioning
- JSON manifests use `schema_version: "1.0"`, immutable component `id` and semantic `version`. Wire protocol uses major/minor in `roboworld.v1` protobuf package.
- A new incompatible major requires explicit migration/version negotiation; unknown optional fields can be ignored only within compatible versions.
- Use `UUID` strings for world/robot/component *runtime* identifiers when generated; examples use human-readable stable fixture IDs for clarity. No Godot instance IDs on wire or in files.
- Component definitions are immutable after publication; reference their SHA-256 in release catalogue. For bundled generic fixtures without published hashes, the P0 manifest validator may omit SHA only in development mode.

## 19.2 JSON schemas
- `contracts/schemas/component.schema.json`: catalogue identity, dimensions, mounts, electrical ports, simulation model/fidelity and provenance.
- `contracts/schemas/robot.schema.json`: rooted link/joint assembly, component references, firmware assignments and typed wiring.
- `contracts/schemas/world.schema.json`: world bounds, gravity, object definitions/relationships, transforms and semantics.
- `contracts/schemas/project.schema.json`: project pointers and settings.
- `contracts/schemas/ai-scene-request.schema.json`: strict LLM-produced intermediate spec, not arbitrary code.

Schema validation only verifies JSON shape/types. Implement semantic validators for: finite floats; quaternion normalization; axes unit norm; joint parent-child DAG; declared component/port references; pin capability and bus address conflicts; mount compatibility; no cycles in support relationships; non-overlapping default placement; asset content hashes and path traversal.

## 19.3 Canonical units and transforms
- X right; Y up; negative Z forward; right-handed. Unit linear m/s, angular rad/s, time ns, torque N·m, motor power W, mass kg, voltage V and current A.
- `transform.position_m` array `[x,y,z]`, `transform.rotation_xyzw` unit quaternion. UI display units configurable; serialized units never inferred.
- Timestamps are `int64 sim_time_ns`, physics ticks integer. Persist `world_seed` and reference component versions.

## 19.4 Process command contract
Internal gateway requests (via framed protobuf or typed in-process methods):
- `HELLO`/capability negotiation, `GET_HEALTH`, `GET_ENDPOINTS`.
- `LOAD_PROJECT`, `INSTANTIATE_ROBOT`, `LOAD_FIRMWARE`, `RESET_MCU`, `START`, `PAUSE`, `RESUME`, `STOP`.
- `SUBSCRIBE_STATE`/`UNSUBSCRIBE`, `SUBSCRIBE_DIAGNOSTICS`.
- `SAVE_DESIGN`, `REQUEST_CHECKPOINT` (only if gate passed), `LOAD_CHECKPOINT`.
- Commands have `request_id`, `session_id`, `robot_id` as applicable, `expected_revision`, deadline, actor privilege. Idempotent retry for safe commands; `START/STOP` guarded state machine.
- Responses carry `status`, `error_code`, reason and `revision`, never ambiguous bool-only success.

## 19.5 Protocol transport
`InternalGodot↔RobotService`: TCP loopback, 4-byte big-endian unsigned frame byte length then protobuf `Envelope` of that length. Hard max frame size e.g. 4 MiB for control/events; camera transfer uses a separate service/handle and bounded frames. No unbounded messages, all queued bytes counted. `HELLO` must precede privileged control. For P0, internal endpoint binds only loopback. In-process typed messages inside Godot do not need serialization but must follow equivalent semantics.

`Python↔virtual UART`: raw TCP byte stream; each discovered endpoint represents one selected firmware UART. The session discovery manifest maps logical endpoint IDs to port/protocol. Client data forwarded to Renode UART *subject to modeled hardware scheduling*. UART bytes are **opaque** to Robot Service. The P0 example firmware defines its own ASCII protocol; this is not universal.

## 19.6 Event model
- `ActuatorCommand {component_id, sim_time_ns, duty_cycle, direction, source_mcu_port}` resulting from **MCU peripheral event**, not host text.
- `SensorObservation {sensor_id, sim_time_ns, value, unit, validity}` derived from world state, passed to specified virtual hardware model.
- `WorldStateSnapshot {tick, sim_time_ns, robot_states, object_states, real_time_factor}` authoritative physical state.
- `DiagnosticEvent {source, severity, reason, root_fault, sim_time_ns, metric}`.
- `HardwareBusEvent {controller_id, port_id, sim_time_ns, data}` with protocol metadata and explicit event time precision/fidelity.

## 19.7 Compatibility and error taxonomy
Stable error strings: `E_SCHEMA_VERSION`, `E_COMPONENT_NOT_FOUND`, `E_FIRMWARE_TARGET`, `E_EMULATOR_UNAVAILABLE`, `E_UNSUPPORTED_PERIPHERAL`, `E_DEVICE_INCOMPATIBLE`, `E_UART_OVERRUN`, `E_WORLD_NOT_RUNNING`, `E_SIM_TIME_SYNC`, `E_CHECKPOINT_INCOMPATIBLE`, `E_PROJECT_INVALID`, `E_WORKER_CRASHED`, `E_UNAUTHORIZED`, `E_RESOURCE_LIMIT`.
Report errors at source and show them in diagnostics with context. Do not silently downgrade actual MCU/peripheral failures into host-side behaviour.

## 19.8 Test and backward-compatibility expectations
- Validate all `examples/**/*.json` against schemas; semantic validator added in T07.
- Verify protobuf codegen in supported .NET and Python targets, roundtrip tests across language boundary, unknown-field handling and major-version rejection.
- Golden samples and test firmware are immutable with hashes after P0 release. Every schema change requires tests and migration notes.

## 19.9 Project fixture IDs and instance mapping
Examples may use readable IDs like `rover-01`, `mcu-drive`, `wheel-left`. In runtime keep an ID map to Godot `RID`/nodes and Renode machine/peripheral identity. Mapping is not serialized as core project truth.
