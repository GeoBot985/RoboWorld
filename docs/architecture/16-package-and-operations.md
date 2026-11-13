# 16 — Desktop packaging, operations and lifecycle

## 16.1 User-facing invariant
One icon, one install, one uninstall. On launch show 3D scene immediately and manage any Renode/Robot Service child processes. Normal users do not run service commands, configure port numbers, or open terminal windows.

## 16.2 Expected packaged shape (illustrative)
```text
RoboWorld/
  RoboWorld.exe                 # Godot 4 .NET exported desktop app
  RoboWorld.pck / native assets
  runtime/robot-service/...    # pinned .NET runtime/app
  runtime/renode/...           # redistributable pinned Renode and dependencies
  samples/firmware/...         # bundled reference ELF + sources/license
  samples/projects/...         # room and living rover
  licenses/THIRD_PARTY_NOTICES.txt
  manifests/toolchain.lock.json
```
User writeable state lives in `%LOCALAPPDATA%/RoboWorld/` (Windows) or platform equivalent, never installation dir. Validate absolute and relative paths; reject traversals. Developer toolchain can be separately optional for firmware compilation, but users can run bundled sample binaries without installing VS Code/GCC; for one-click **Build Firmware** later bundle or provision a pinned toolchain with explicit license/size considerations.

## 16.3 Launcher supervision state machine
`STARTING -> READY -> RUNNING <-> PAUSED -> STOPPING -> STOPPED` with `DEGRADED` and `FAILED` transitions. The UI viewport should survive child worker crash. Restart may reset MCU, with explicit warning and preserved design state; never falsely claim exact runtime continuation.

Startup sequence:
1. Acquire single-instance application lock and config; load last project or starter scene.
2. Show viewport and world; resolve versioned project/asset graph.
3. Allocate secure loopback ports and ephemeral session token; start Robot Service with argument arrays and isolated work directory.
4. Health handshake; worker starts MCU emulator with pinned firmware and platform script; expose UART endpoints and metadata.
5. Load initial state or start idle; diagnostics show lifecycle statuses.
6. External Python can connect; user runs code separately.

Shutdown sequence:
1. Stop accepting new commands; warn if unsaved project.
2. Pause/stop physics at safe boundary and optionally save project.
3. Ask Robot Service to stop/release emulator, tear down ports, flush bounded logs.
4. Force kill only after bounded timeout and record reason.
5. Close window/process without orphan workers.

## 16.4 Observability and errors
- Correlation IDs for session/world/robot/MCU; structured JSONL logs rotated by max size/count, default privacy-safe.
- UI should report precise error and recovery route: firmware missing, emulator crash, unsupported device, endpoint conflict, invalid project, Renode version mismatch, save failure.
- Do not swallow background exceptions. Capture crash dumps optionally with consent and clear location.
- `Diagnostics > Export support bundle` should include logs, versions, project manifest redacted as needed, test reports — never secret tokens by default.

## 16.5 Upgrades
- Preserve user's projects and custom catalogue in data folder.
- Never mutate a persisted project in place without versioned migration and backup.
- First-run migration report and ability to reopen prior valid project.
- Check compatibility of Renode binary/snapshot; on upgrade warn that runtime snapshots may need old engine to load.

## 16.6 CI release gate
1. Export Godot .NET Windows build.
2. Package pinned Robot Service, Renode, config and licenses.
3. Test install on clean Windows 11 VM with non-admin user where practical.
4. Launch visible room; load firmware; connect host Python; drive rover; close app; verify no orphan processes and bounded network listeners.
5. Run multi-launch, missing-asset and failed-emulator tests.
