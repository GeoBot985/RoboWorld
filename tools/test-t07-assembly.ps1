$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T07'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $godot) { throw 'Pinned Godot .NET console executable not found.' }

$previousDotnetRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
    & $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --no-restore --nologo *> (Join-Path $evidence 'assembly-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Godot assembly Debug build failed; see artifacts/evidence/T07/assembly-build.log.' }
} finally {
    $env:DOTNET_ROOT = $previousDotnetRoot
}

& $godot --headless --path (Join-Path $root 'godot') -- --assembly-smoke *> (Join-Path $evidence 'assembly-physics-smoke.log')
if ($LASTEXITCODE -ne 0) { throw 'Godot assembly physics smoke failed; see artifacts/evidence/T07/assembly-physics-smoke.log.' }
$log = Get-Content (Join-Path $evidence 'assembly-physics-smoke.log') -Raw
if ($log -notmatch 'ROBOT_BUILT id=rover-01 links=3 joints=2 components=10' -or
    $log -notmatch 'ROBOT_BUILT id=arm-01 links=2 joints=1 components=6' -or
    $log -notmatch 'ASSEMBLY_PHYSICS_SMOKE ticks=120 bodies=6 .*placement=PASS') {
    throw 'Expected two robot builds and the 120-step placement assertion were not present in the Godot log.'
}
if ($log -match 'SCRIPT ERROR|ERROR:') { throw 'Godot reported an engine or script error; see assembly-physics-smoke.log.' }
Write-Output 'PASS: rover and firmware-ready revolute-arm assemblies built from canonical graphs and remained at validated poses for 120 Godot physics steps.'
