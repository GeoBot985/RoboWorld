# Roadmap

RoboWorld is an owner-directed, evidence-led engineering project. Work is prioritized against measured behavior and explicit acceptance criteria.

## Current foundation

- A visible Godot 4 .NET world with a fixed-step physics adapter.
- Engine-independent versioned robot, component, and world definitions.
- Empty-to-runnable robot authoring, mechanical and electrical graphs, and design persistence.
- Exact Renode execution for the tested STM32F407 firmware path, with UART-controlled actuation and a simulated distance sensor.
- A mounted Robot View and versioned warehouse scenery.

## Next engineering priorities

1. Complete clean-machine application packaging and validate startup/shutdown without relying on the developer's local toolchain layout.
2. Improve measured real-time performance and repeatability while preserving the acknowledged physics/firmware clock boundary.
3. Broaden tests for sensor behavior, controller combinations, faults, and multi-controller resource bounds.
4. Add a bounded camera-frame path to host applications only after measured capture/transfer performance and queue behavior are defined.
5. Grow the component catalogue through versioned models with provenance, explicit fidelity labels, and executable compatibility tests.

## Longer-term directions

Classroom-focused modes, constrained world authoring, more physical devices, and cloud-hosted shared worlds are possible future directions. They are not implemented by this source snapshot and are not treated as acceptance for the current desktop prototype.
