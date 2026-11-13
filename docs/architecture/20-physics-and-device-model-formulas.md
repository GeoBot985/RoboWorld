# 20 — Initial physics and hardware model specification

This document defines intentionally simple **functional** physical models for the starter catalogue. It does not claim manufacturer-grade calibration. Use SI units and discrete-time integration with an explicit `dt_seconds = delta_sim_ns / 1e9`; never use rendered frame delta. Profile parameters must come from documented part specifications or be clearly labeled generic fixture parameters.

## 20.1 Brushed DC motor + gearbox (starter model)
For angular speed `ω_motor` and effective voltage `V_motor`, use equations for a first approximation:

- `V_motor = sign(direction) * duty * V_supply`, where `duty ∈ [0,1]` and H-bridge drop/current limit can reduce effective voltage.
- `I_ideal = (V_motor - K_e * ω_motor) / R`.
- `I = clamp(I_ideal, -I_limit, +I_limit)`; torque `τ_motor = K_t * I - τ_friction(ω_motor)`.
- `ω_motor = gear_ratio * ω_output` (magnitude; sign handled by orientation convention).
- `τ_output = gear_ratio * gearbox_efficiency * τ_motor` in motoring mode; reverse driving/regeneration requires careful sign/efficiency handling and may initially be documented as unsupported.
- Apply `τ_output` to corresponding Godot rotational joint or torqued rigid body through adapter at a physics boundary. Wheel angular acceleration emerges from physical inertia and load rather than an instantaneous wheel-angle assignment.

For models supplied only with `stall_torque`, `no_load_rpm` and nominal voltage, fit a first-order linear torque-speed curve, **mark fidelity `functional`**, and document derivation; do not imply fitted winding resistance/back-EMF parameters are measured.

### Controls and edge cases
- H-bridge states: coast/freewheel, forward, reverse and brake; any unsupported model state explicitly documented.
- PWM updates have MCU peripheral event timestamps. PWM frequency limits and averaging over physics step must be modelled/declared.
- Stall with excessive torque load; no instant motion, no infinite torque or traction.
- Test 0% duty, saturation, forward/reverse, stalled wheel and inertia under different loads.

## 20.2 Servo-driven revolute joint
- Firmware-visible servo input may be pulse width or supported digital bus register sequence.
- Servo target angle derived from validated command mapping; do not jump actual joint angle to target.
- A constrained PID/PD controller computes torque from error and angular velocity, saturating by rated torque and speed. Servo response/hold torque/no-load speed approximated from known part data.
- Joint limits and stop behaviour applied as physics constraints; include `stall`/`saturated` diagnostics.
- Kinematics/IK are run in host app or MCU code; the servo model does not solve IK.

## 20.3 Encoders
For `pulses_per_output_revolution` and angular displacement `Δθ` in radians, ideal count increment `Δcount = Δθ/(2π) * PPR` with fractional residual carried across ticks. Quadrature edges, phase and interrupt timing must match the selected firmware-visible interface; don't generate an arbitrary convenient RPM variable in MCU memory. Include configurable direction and optional missed pulses/noise. Model actual wheel/joint displacement, not commanded motor speed.

## 20.4 Distance sensor
World physics samples a ray or cone with origin, orientation, min/max range, occlusion and collision masks, at sensor's configured update interval. Reading is measurement function `range = clamp(true_distance + bias + noise, valid_range)` with invalid/no-return state beyond limit. Sensor protocol implementation drives firmware-visible pins/registers with validated timing. Deterministic noise seeded per component. A ray model is not a full photonics/acoustic simulation; label limitation.

## 20.5 IMU (MVP+)
- Angular velocity from physical body frame; linear acceleration from change in velocity and gravity handling defined by specific accelerometer semantics.
- Bias, noise, sample rate, saturation and update intervals configurable.
- MCU I²C/SPI register representation must be separately verified with a firmware driver; physical truth and firmware protocol are distinct model layers.

## 20.6 Physics material / contact
- Canonical ground plane and rigid bodies with mass, inertia, friction and collision masks. Use Godot physics configured at fixed 120 Hz target; record actual.
- Two driven wheels plus a passive caster support the initial rover in a statically stable contact configuration. Wheels contact ground through Godot collision shapes and torque; initial tyre friction simplified (not slip-free kinematic wheel). Track wheel-ground contact and load.
- Heavy arm attached to light rover changes center of mass, inertia and stability; no special anti-tip assistance.
- Define default gravity `[0,-9.81,0]` m/s².

## 20.7 Functional battery (P0)
The starter battery exposes a logical power bus with nominal voltage, maximum current and capacity; use a simple source with current limit and remaining state of charge. Electronics auto-wire required regulation within board profiles. No circuit-level resistor/capacitor modelling. Under depleted capacity or overload, driver behavior should be visibly degraded/stopped with diagnostics; P0 may use constant nominal voltage until energy model test is implemented, but clearly label fidelity.

## 20.8 Camera output (MVP+)
A physically mounted camera has resolution, field of view, mounting transform, frame rate and quality/noise settings. Capture time based on simulation clock, not UI FPS. Frame transfer uses dedicated device transport with bounded queues and measured GPU readback/copy cost. Camera should see true visible world and occlusions; no direct world truth available to Python without sensor.

## 20.9 Numerical stability and tests
- Clamp nonphysical values and log saturation. Never accept NaN/Infinity in JSON/wire, physics or motor state.
- Integrate on fixed sim timestep or a documented stable substep; carry residual fractional timing for periodic samples and encoders.
- Unit tests for energy/power sign conventions, monotonic unloaded response, load-induced speed reduction, maximum torque, deterministic seeded noise and update/sample timing.
- Compare simulated motor curve to manufacturer points where present, record fit error and profile provenance.
