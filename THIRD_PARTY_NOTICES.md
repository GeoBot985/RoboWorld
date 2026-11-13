# Third-party notices and project license status

## RoboWorld project

No project license is currently included. Public visibility does not grant permission to redistribute, modify, or use the RoboWorld source. A project license has not been selected; this notice does not select one.

## Dependencies and tools

This source repository references external tools and packages but does not bundle their executable binaries or NuGet/Python package payloads. Each tool remains subject to its own terms and notices.

| Dependency | Use in RoboWorld | License / attribution |
|---|---|---|
| Godot Engine .NET 4.7.2 | Desktop editor, rendering, and physics adapter | MIT; Godot requests that redistributed engine copies include its copyright/license notice. [Official license page](https://godotengine.org/license/) |
| Renode 1.17.0 | Executes the STM32F407 firmware and exposes modeled peripherals | The included source patches are derived from Renode source, licensed under MIT by Antmicro; its notice and terms are in [the included Renode license](THIRD_PARTY_LICENSES/renode-MIT.txt). No Renode binary is bundled. [Renode project and license](https://github.com/renode/renode) |
| .NET SDK 10.0.403 | Build and test runtime | .NET source/binaries are MIT-licensed; the SDK and Windows components have separate bundled notices. [Microsoft .NET overview](https://learn.microsoft.com/dotnet/core/introduction) |
| Google.Protobuf 3.36.2 | Runtime for generated Protobuf messages | BSD-3-Clause. [NuGet package metadata](https://www.nuget.org/packages/Google.Protobuf/3.36.2) |
| Grpc.Tools 2.85.0 | Build-time Protobuf code generation | Apache-2.0. [NuGet package metadata](https://www.nuget.org/packages/Grpc.Tools/2.85.0) |
| Arm GNU Toolchain 15.2.rel1 | Builds sample Cortex-M firmware | Not included. The toolchain contains components with their own GPL/LGPL/runtime-exception and other notices; consult the downloaded package's license directory before redistribution. |
| Python 3.11.9 | Host controller and schema validation | PSF license; Python is not bundled. [Python license](https://docs.python.org/3/license.html) |
| Python validator packages | `attrs`, `jsonschema`, `jsonschema-specifications`, `referencing`, `rpds-py`, `typing_extensions` | Exact versions are in `python/requirements.txt`. Install from PyPI; consult each package's metadata/license before redistribution. No package payloads are included here. |

The visible screenshot in `docs/images/robot-view.jpg` is a direct capture of the RoboWorld application. The warehouse and component visuals are generated from RoboWorld's project data and Godot primitives; this repository snapshot contains no downloaded third-party image assets.

This inventory covers the source snapshot and its declared development dependencies. It is not a release SBOM and does not clear redistribution of a compiled application or toolchain bundle.
