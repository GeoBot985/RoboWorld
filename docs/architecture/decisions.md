# Architecture decisions

These decisions describe the current product direction. Implementation status is kept separately in [validation](../validation.md).

| Decision | Rationale |
|---|---|
| Native desktop experience with Godot 4 .NET | Keeps a visible 3D workspace and local process supervision in one application. Windows x64 is the verified target; other platforms remain unvalidated. |
| Godot fixed-step physics with explicit simulation time | Rendering frame rate must not become the physical clock. The current Exact path advances Renode at acknowledged integer-nanosecond boundaries. |
| Real STM32 firmware in Renode | The tested actuator path must be produced by compiled Cortex-M firmware and modeled MCU peripherals, not a host-side command-to-transform shortcut. |
| Host application runs outside the simulated robot | Python communicates through a UART-compatible endpoint; it has no direct authoritative physics mutation path. |
| Engine-independent component and graph definitions | Persisted models, schemas, catalogue versions, mechanical links, and electrical edges remain usable independently of Godot scene objects. |
| Separate authoring and runnable validation | Incomplete designs can be saved. Existing runtime invariants are checked when a user launches a simulation. |
| Functional electronics before circuit CAD | The first scope models typed connectivity and supported firmware-visible signals; a PCB/resistor design editor is not part of the prototype. |
| Design persistence is separate from runtime restoration | Project packages and authoring drafts can be saved/reopened. Coordinated MCU/process checkpoint and resume is deferred. |
| Exact execution remains the reference backend | Any accelerated model must be qualified against held-out firmware-driven behavior; the experimental controller model has not met all broad repeatability/performance criteria. |
| Cloud and multiplayer are future directions | Stable interfaces can be retained, but no production cloud authority or player-code sandbox is claimed. |
