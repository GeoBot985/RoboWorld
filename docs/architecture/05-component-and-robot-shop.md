# 05 — Robot Shop, mechanics, assembly and component catalogue

## 5.1 Design philosophy
Kerbal Space Program-style assembly with a small set of **parametric mechanical primitives**, a growing catalogue of **named electronics/devices** and a portable link/joint graph. The builder teaches meaningful mechanical/interface choices, not circuit-board resistor selection or full CAD.

## 5.2 Starter catalogue (approved categories)
| Category | V1 starter inventory | Key parameters |
|---|---|---|
| Bodies | rectangular chassis, box body, circular platform | SI size, density/mass, center of mass |
| Structures | beams, tubes, flat plates, L/U brackets | length/section, material |
| Mounts | universal mount, shaft hub, tool flange | local frame/port, allowed mate types |
| Joints | fixed, revolute, continuous, prismatic | axis, limits, damping, friction |
| Actuators | servo, DC gear motor, stepper, linear actuator | torque-speed limits, response, power draw |
| Mobility | wheel, caster, steerable wheel | radius, width, wheel friction |
| End effectors | two-finger gripper, generic tool flange | opening travel and force |
| Electronics | STM32 MCU, motor driver, encoder, distance sensor, IMU, battery | verified interface/pin/protocol profiles |

P0 implements enough of this to assemble a **differential-drive rover with two driven wheels and a passive rear ball caster** from versioned parts; the third contact prevents a physically unstable two-wheel chassis unless active balancing is intended. Full articulated arm/gripper is MVP+ with its own tests. User can add or remove categories later without changing core graph format.

## 5.3 Canonical representation
- `ComponentDefinition`: immutable `id`, `version`, provenance, capabilities, default parameters, optional variant names, physical geometry (canonical primitives or referenced glTF asset), mount ports, electrical ports, behaviour model key and fidelity tier.
- `ComponentInstance`: unique ID, catalogue reference + resolved version/hash, parameter overrides, local mount frame and owning `robot_id`.
- `RigidLink`: one rigid mechanical body aggregating attached structural components with mass/inertia computed from profiles; default one root link.
- `Joint`: parent and child link, type, axis, limits, pose frames and optional actuator binding.
- `MountConnection`: mechanical attachment between compatible ports, usually contributes to a rigid link or creates a joint.
- `ElectricalConnection`: typed endpoint pair, physical bus name and pin/function allocation; separate from mechanical hierarchy.
- `RobotDefinition`: rooted assembly DAG/tree for mechanics, arbitrary valid typed graph for electronics, plus firmware assignments and user-application endpoint metadata.

## 5.4 Spatial conventions
- Canonical coordinates: right-handed **X right, Y up, -Z forward** (matching Godot's common convention); gravity acts along `-Y`.
- Position in metres `[x,y,z]`. Orientation as unit quaternion `[x,y,z,w]`; never use stored Euler angles. Linear velocity m/s, angular velocity rad/s, torque N·m, force N, mass kg. Display can show degrees, centimetres or millimetres.
- `Joint.axis` is a unit vector in the **joint-local** frame. Geometry has a local origin and explicit local transform. All port frames transform through parent hierarchy.
- A fixed attachment can be represented by rigid aggregation instead of a needless physics joint when possible.

## 5.5 Snap/mount rules
- Every component provides one or more `MountPort` objects with orientation, size class, mating capabilities (`structural`, `shaft`, `joint`, `tool`, `sensor`) and allowed load recommendation.
- Drag component near port -> highlight compatible ports -> preview pose -> snap with user confirmation. Axis/rotation adjustable.
- Mandatory: no two independent rigid links occupy same unresolved connector, no mechanically disconnected parts unless user deliberately creates separate robot assemblies.
- Electrical auto-connect chooses compatible logical buses; validates protocol, address collision and pin function. Simplify supporting passives automatically, preserving pin assignment and firmware-visible configuration.
- Invalid engineering designs may be built if mechanically coherent: an underpowered motor should fail under load in simulation, not be disallowed.

## 5.6 Constructing a 3-axis arm on a rover
```text
rover.base_link (rectangular chassis, drive wheels)
  └─ rigid arm_mount -> arm.base_link
       └─ revolute yaw_joint (servo_yaw) -> arm.turntable
            └─ revolute shoulder_joint (servo_shoulder) -> arm.upper_link
                 └─ revolute elbow_joint (servo_elbow) -> arm.forearm
                      └─ fixed tool flange -> gripper base
                           └─ prismatic/revolute finger joints driven by gripper actuator
```
- The MCU(s) may be physically attached to `rover.base_link` yet logically command joints in attached arm assembly.
- `servo_elbow` maps to a firmware-visible PWM output via virtual motor driver; **the physics adapter** applies joint motor torque; never assume servo angle instantaneously becomes actual angle.
- Builder displays load, center of gravity, tipping risk and joint limit warnings. Ghost-target poses are UI predictions, separate from physical state.
- Kinematics and IK are computed by user host app, MCU firmware, or both; simulator supplies truth and defined sensors, not covert free IK.

## 5.7 Models of real parts
Starter candidate families (subject to docs/license/validation): SG90-like servo; Pololu-style geared DC motor/encoder; TB6612FNG H-bridge; VL53L0X ToF sensor; ST LSM6DSOX IMU. No 'driver-compatible' claim without firmware compatibility test. Each real part records manufacturer/product ID, datasheet, revision, date of retrieval, dynamic response and unit tolerances.

## 5.8 Custom subassemblies
Select a rooted set of components -> `Save as reusable module` -> module retains geometry, port map, joints and optional electronics bindings but generates fresh instance IDs when reused. Version immutable modules, nested modules resolved to graph for simulation; reference original definition for editing. Later community catalogue applies trust/licensing validation.

## 5.9 User experience modes
- Junior: snap compatible ports automatically, precompiled working firmware, drag-block host logic, missions; generated inspectable Python where practical.
- Robotics Engineer: expose pin allocations/actuator limits, write Python and first C/C++ firmware.
- Advanced: inspect all supported registers/buses, GDB, custom firmware, controller architecture and model fidelity.
Same design and simulation kernel in all modes; no arbitrary locked hardware tiers or impossible grade-dependent physics.

## 5.10 Schema and tests
- Canonical manifests in `contracts/schemas/component.schema.json` and `robot.schema.json`; sample in `examples/components/` and `examples/robots/`.
- Validation: unique IDs, nonnegative mass, finite numbers, normalized quaternions and joint axes (semantic validator), rooted mechanics, no cycles in rigid parent-child graph, valid mount mating and endpoint types, dependency pinning.
- Property testing: rigid pose composition, inverse transformations, joint-limit clamping, graph traversal and component serialization stability.
