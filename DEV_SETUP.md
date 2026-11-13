# Developer setup

RoboWorld is developed for Windows 11 x64. The pinned versions below match the verified local toolchain in `toolchain.lock.json`; the repository does not redistribute any of these tools.

## Required tools

- .NET SDK **10.0.403** (the repository's `global.json` disables roll-forward).
- Godot **4.7.2-stable .NET** editor and matching export templates.
- Renode **1.17.0**, including its upstream dependency notices.
- Arm GNU Toolchain **15.2.rel1** for `arm-none-eabi` firmware builds.
- Python **3.11.9** for the external controller and example validation.

Download only from the upstream locations recorded in `toolchain.lock.json`, and verify SHA-256 before use. Place the tools in these repository-local locations because the checked-in PowerShell helpers resolve them there:

```text
.toolchain/dotnet/
.toolchain/godot/
.toolchain/renode/
.toolchain/arm/
```

The workspace-local toolchain and generated firmware are ignored by Git. `tools/build-renode-loopback.ps1` builds the Renode extensions used for local external control; source revision and patch hashes are recorded in the lock file. Generated binaries and local logs are not included.

## Restore, build, and validate

From the repository root:

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install --requirement python/requirements.txt
.\.toolchain\dotnet\dotnet.exe restore RoboWorld.slnx
.\.toolchain\dotnet\dotnet.exe build RoboWorld.slnx --configuration Release
\.\samples\firmware\stm32-drive\build.ps1
.\.venv\Scripts\python.exe tools/validate_examples.py
.\.toolchain\dotnet\dotnet.exe run --project tests/RoboWorld.Domain.Tests/RoboWorld.Domain.Tests.csproj --configuration Release
.\.toolchain\dotnet\dotnet.exe run --project tests/RoboWorld.RobotService.Tests/RoboWorld.RobotService.Tests.csproj --configuration Release
```

The profile identity tests compare the exact local firmware ELF. A newly built ELF may differ byte-for-byte from historical characterization inputs and should invalidate their stored identity.

For the visible editor, run `tools/run-godot.ps1`. For a firmware-driven sample, build the STM32 drive firmware first:

```powershell
.\samples\firmware\stm32-drive\build.ps1
.\tools\test-t03-e2e.ps1
```

Targeted regression scripts cover authoring, sensor/clock behavior, warehouse collision, and other device/assembly paths; see their names under `tools/`. Several scripts write raw output to `artifacts/evidence/` and assume the workspace-local toolchain paths shown above.

## Reproducibility scope

The toolchain lock records tested versions and downloaded artifact hashes. The checked-in code and schemas can be inspected without the local binaries, but the full Godot/Renode integration has only been validated on Windows 11 x64. Linux setup and clean-machine packaging are not yet accepted. Do not treat a successful compile alone as proof of firmware/peripheral fidelity; consult [validation results](docs/validation.md).
