param([string]$EvidenceDirectory = 'artifacts/evidence/RS01D')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root $EvidenceDirectory
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$package = Join-Path $evidence 'camera-rover-warehouse.roboworld'
$saveLog = Join-Path $evidence 'camera-package-save.log'
$collisionLog = Join-Path $evidence 'warehouse-collision-smoke.log'
& $godot --headless --path (Join-Path $root 'godot') -- '--robot-id=rover-camera-01' '--controller-mode=exact' '--design-smoke' "--save-project=$package" *> $saveLog
if ($LASTEXITCODE -ne 0) { Get-Content $saveLog; throw 'Could not save the camera-ready warehouse rover package.' }
if ((Get-Content -Raw $saveLog) -notmatch 'ROBOT_CAMERA_READY robot=rover-camera-01 component=camera-front') { throw 'The loaded camera rover did not build a mounted camera viewport.' }
& $godot --headless --path (Join-Path $root 'godot') -- "--project=$package" '--robot-id=rover-camera-01' '--world-collision-smoke' *> $collisionLog
if ($LASTEXITCODE -ne 0) { Get-Content $collisionLog; throw 'Warehouse world collision-ray smoke failed.' }
if ((Get-Content -Raw $collisionLog) -notmatch 'WAREHOUSE_COLLISION_SMOKE status=PASS .*collider=sensor-crate distance_m=3\.(\d+)') { Get-Content $collisionLog; throw 'The warehouse sensor target was not hit at its authored distance.' }
Write-Output 'RS-01D camera package mount and warehouse sensor-ray collision: PASS'
