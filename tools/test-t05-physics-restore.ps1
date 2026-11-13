$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T05'
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$buildLog = Join-Path $evidence 'physics-restore-build.log'
$smokeLog = Join-Path $evidence 'physics-restore-smoke.log'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
& $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --nologo *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content $buildLog; throw 'Godot physics restore adapter build failed.' }
& $godot --headless --path (Join-Path $root 'godot') -- --checkpoint-physics-smoke 2>&1 | Set-Content -Encoding utf8 $smokeLog
if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw $smokeLog) -notmatch 'PHYSICS_CHECKPOINT_RESTORE bodies=\d+ tick=42 sim_time_ns=350000000 pose_velocity_match=PASS') {
    Get-Content $smokeLog
    throw 'Godot physics snapshot did not restore all component body poses and velocities.'
}
Write-Host 'Godot rigid-body snapshot restore and pose/velocity comparison: PASS'
Get-Content -LiteralPath $smokeLog | Select-String 'PHYSICS_CHECKPOINT_RESTORE'
