# 08 — Product UX, visual world and educational modes

## 8.1 Desktop app workspace
- Main 3D viewport **always present**, even if nothing moves, firmware is broken or no host controller is connected. Startup shows scene + explicit service initialization status.
- Left sidebar: Robot Shop (Bodies / Structures / Mounts / Joints / Actuators / Mobility / End Effectors / Electronics); component filter/search/fidelity badge.
- Right sidebar: hierarchical object/robot inspector and component diagnostics; tabular status (see 17).
- Bottom pane: simulation console, firmware output, UART trace, timeline, error log, runtime metrics; collapsible but accessible.
- Top command bar: Open/Save Project, Start/Pause/Reset Simulation, Load Firmware, Connect Host App, Camera/View, Diagnostics.
- First launch: guided 'Living Rover' sample, not an empty impossible-to-understand editor.

## 8.2 Construction workflow
1. Select chassis body (starter template or custom shape).
2. Drag structural parts and snap to compatible mount ports; 3D ghost preview validates pose.
3. Add rotational joints and wheels; assign actuator components to joints.
4. Mount MCU/driver/sensors and connect **logical** ports; auto-wire supporting passives.
5. Check assembly graph, pin occupancy, mechanical power/torque/stability warnings.
6. Save design; load compiled firmware to MCU; launch external host app in VS Code.
7. Watch the result. No auto-correction when bad design causes tipping/stalling; provide transparent diagnostics.

## 8.3 Diagnostics table requirements
Group `World > Robot > MCU > firmware/bus/device`, with hardware-only categories (camera/power) under robot. Status machine:
- `OPERATIONAL` (green) — health checks pass, component running/responsive.
- `FAILED` (red) — confirmed startup/run/hardware interface failure.
- `DEGRADED` (amber) — running with loss/errors or out of expected operating envelope.
- `INACTIVE` (grey) — intentionally stopped, disconnected by design, no current command.
- `UNKNOWN` (grey) — insufficient telemetry to classify; do not label green by default.
Support color plus icon/text (accessibility). Each row: component path, type/model, status, last update, relevant measurement, root-cause reference, details action.

**Fault propagation:** If MCU firmware is dead, its motor driver may be physically healthy but `DEGRADED/INACTIVE: controller unavailable`; only MCU is `FAILED`. Optional grouping highlights parent if any descendant failed, without changing child diagnostic truth.

## 8.4 Diagnostic drilldowns
- MCU: firmware hash/version, emulation state, virtual-time drift, interrupt/peripheral health, GDB attach link, reset/reload controls.
- UART: baud/framing, raw byte counters, queue fill/overflow, CRC/protocol errors, timestamps.
- Motor: input PWM, direction, drive voltage, target/actual RPM, torque, saturation/stall flags.
- Sensor: last reading, sample time, update rate, valid/invalid range, noise/fidelity tier.
- World: physics step duration, simulation speed (RTF), rendering FPS, worker health.
- Clicking row selects/highlights physical component in viewport, with fallback for not directly visible parts.

## 8.5 Developer experience
Main Python app remains a normal process. Godot can present endpoint discovery and copy target URL into user's environment; app must not absorb or execute the script as a game script. Firmware project is built and deployed separately. Provide examples for host and firmware. Later a thin VS Code extension and CLI may wrap same deployment API; extension is not required for P0.

## 8.6 Educational modes
- **Junior Engineer** (Grades 4–6): snapping, guided missions, simplified block coding (host-side), precompiled firmwares; learning feedback without hiding errors.
- **Robotics Engineer** (Grades 7–12): wiring modes, Python, motor models, constraints, sensor data, MCU basics.
- **Advanced Workshop**: real firmware in C/C++, GDB, hardware interfaces, host-vs-MCU IK, multi-controller robots, measurement.
One underlying robot model and world. Changing mode changes tools/explanations, never physical law. Saved projects are interoperable.

## 8.7 Missions and measurement
Missions validate explicit outcomes: follow line, maze, pick/place, navigate warehouse. Every lesson can emit `requirements`, `world_fixture`, `allowed_components`, `scoring_metrics`, `expected_failures`; do not conflate scoring with simulation authority. Future teacher assignments/dashboards use same metrics.

## 8.8 Accessibility and offline
- Keyboard navigation, scalable UI, no color-only statuses, readable large text and tooltips.
- All starter content and simulation available without internet or AI account.
- Docs/specs in English initially; architecture permits translation of UI labels and missions.
- Maintain student trust: simulation model fidelity badges; failed robot does not trigger invisible help routines.
