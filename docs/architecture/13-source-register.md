# 13 — Upstream technical evidence register

These upstream sources were reviewed while preparing the architecture (2026-11-07). They describe external capabilities, not RoboWorld implementation readiness. The pinned tool version and executable test results determine which capabilities are accepted in this project.

| ID | Source | Relevant claim / caution |
|---|---|---|
| S01 | [Godot — C#/.NET basics](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html) | Godot 4 .NET desktop target; `.NET` editor and SDK needed; C# web export unsupported (not relevant to desktop-first target) |
| S02 | [Godot — PhysicsServer3D](https://docs.godotengine.org/en/stable/classes/class_physicsserver3d.html) | Godot low-level 3D physics interface, bodies and joints exist; this does not prove manual stepping or articulated robot stability |
| S03 | [Godot — Thread-safe APIs](https://docs.godotengine.org/en/stable/tutorials/performance/thread_safe_apis.html) | Physics/render thread settings and scene/API restrictions; separate render thread has caveats |
| S04 | [Godot — Dedicated server export](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_dedicated_servers.html) | Future authoritative Godot simulation can run without a desktop display; user-facing desktop clients still always display 3D |
| S05 | [Godot — SubViewport](https://docs.godotengine.org/en/stable/classes/class_subviewport.html) | Candidate for off-screen robot camera rendering; host GPU readback must be benchmarked |
| S06 | [Renode — Supported boards](https://renode.readthedocs.io/en/latest/introduction/supported-boards.html) | STM32F4 Discovery is documented as supported platform; peripheral completeness still unverified |
| S07 | [Renode — UART host integration](https://renode.readthedocs.io/en/latest/host-integration/uart.html) | Socket UART available cross-platform; pty on Linux/macOS; serial baud fidelity NOT automatically proven |
| S08 | [Renode — Peripheral modelling](https://renode.readthedocs.io/en/latest/advanced/writing-peripherals.html) | C# peripheral modelling/extensions possible; exact target MCU mappings need testing |
| S09 | [Renode — Virtual time framework](https://renode.readthedocs.io/en/latest/advanced/time_framework.html) | Virtual time synchronization quanta and MIPS settings; co-simulation with Godot still an unproven integration |
| S10 | [Renode — Saving/loading state](https://renode.readthedocs.io/en/latest/basic/saving.html) | Renode Save/Load documented; warnings about version compatibility and UART/analyzer reattachment |
| S11 | [Renode — GDB](https://renode.readthedocs.io/en/latest/debugging/gdb.html) | Real firmware GDB debugging and pause semantics |
| S12 | [Renode — Running modes](https://renode.readthedocs.io/en/latest/basic/running.html) | Monitor can be exposed on socket; must be restricted to privileged process |
| S13 | [Renode — Using Python](https://renode.readthedocs.io/en/latest/basic/using-python.html) | Renode Python/Mock peripheral options for quick model prototypes |
| S14 | [Godot license](https://godotengine.org/license/) | Engine MIT license; preserve required notices and third-party license obligations |
| S15 | [Renode license](https://github.com/renode/renode/blob/master/LICENSE) | Main Renode MIT license; inspect bundled dependencies and redistribution obligations |
| S16 | [RIOT OS STM32F4 Discovery board page](https://api.riot-os.org/group__boards__stm32f4discovery.html) | Oct 2026 note: board Renode demonstration and USB limitations; prefer UART for first proof |
| S17 | [Godot Windows export](https://docs.godotengine.org/en/stable/tutorials/export/exporting_for_windows.html) | Windows packaged Godot app export fundamentals |

## Research provenance policy
Imported-part records include the source URL and file type, manufacturer and revision, driver source commit, retrieval date, redistribution license, units, related test evidence, and simulator implementation commit. Product data extracted with AI tools is a draft until independently checked.

## Known gaps
- The accepted Renode path verifies the tested STM32F407 UART, GPIO/PWM actuation, and timed sensor-input scenarios; it does not establish coverage of every peripheral mode or MCU firmware.
- I²C sensor and encoder peripheral paths have not been qualified.
- Tests cover the implemented Godot/Renode synchronization path, but do not establish broad real-time throughput across workloads or hosts.
- Multi-controller scaling, clean-machine packaging, and host-delivered camera frame throughput remain unqualified.

See [the validation record](../validation.md) for the measured scenarios and limits.
