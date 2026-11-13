$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T02'
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not (Test-Path $dotnet)) { throw 'Pinned workspace .NET SDK is missing.' }
if (-not $godot) { throw 'Pinned Godot .NET console executable is missing.' }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$($env:DOTNET_ROOT);$env:PATH"

Push-Location $root
try {
    & $dotnet build RoboWorld.slnx --configuration Release --nologo *> (Join-Path $evidence 'dotnet-build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'dotnet-build.log') -Tail 50; exit $LASTEXITCODE }
    & $dotnet build godot/RoboWorld.Godot.csproj --configuration Debug --nologo *> (Join-Path $evidence 'godot-debug-build.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'godot-debug-build.log') -Tail 50; exit $LASTEXITCODE }

    & $godot --path (Join-Path $root 'godot') -- --benchmark-fps=15 --benchmark-worker-restart *> (Join-Path $evidence 'benchmark-15.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'benchmark-15.log') -Tail 60; exit $LASTEXITCODE }
    & $godot --path (Join-Path $root 'godot') -- --benchmark-fps=60 *> (Join-Path $evidence 'benchmark-60.log')
    if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'benchmark-60.log') -Tail 60; exit $LASTEXITCODE }

    $log15 = Get-Content (Join-Path $evidence 'benchmark-15.log') -Raw
    $log60 = Get-Content (Join-Path $evidence 'benchmark-60.log') -Raw
    if ($log15 -notmatch 'worker_failure_seen=True worker_restarted=True worker_state=READY') { throw 'The 15 FPS run did not demonstrate worker failure detection and recovery.' }
    if ($log15 -notmatch 'measured_physics_hz=1(19|20)[,.]' -or $log60 -notmatch 'measured_physics_hz=1(19|20)[,.]') { throw 'A run did not maintain the target 120 Hz fixed physics tick.' }
    if ($log15 -notmatch 'render_fps=1[45]' -or $log60 -notmatch 'render_fps=5[89]|render_fps=60') { throw 'The requested render FPS caps were not observed.' }

    $trajectory15 = Import-Csv (Join-Path $evidence 'trajectory-fps15.csv')
    $trajectory60 = Import-Csv (Join-Path $evidence 'trajectory-fps60.csv')
    if ($trajectory15.Count -lt 1200 -or $trajectory15.Count -ne $trajectory60.Count) { throw 'Ten-second physics trajectories have missing or unequal fixed-step samples.' }
    $maxDelta = 0.0
    for ($i = 0; $i -lt $trajectory15.Count; $i++) {
        foreach ($axis in @('x_m','y_m','z_m')) {
            $delta = [Math]::Abs([double]$trajectory15[$i].$axis - [double]$trajectory60[$i].$axis)
            if ($delta -gt $maxDelta) { $maxDelta = $delta }
        }
    }
    if ($maxDelta -gt 0.002) { throw "FPS trajectory divergence $maxDelta m exceeds 0.002 m." }

    $orphans = Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*RoboWorld.RobotService.dll*--demo-worker*' }
    if ($orphans) { throw "Robot Service child remains after Godot exit: $($orphans.ProcessId -join ', ')" }
    @(
        "PASS: Godot 4.7.2 physics maintained target 120 Hz at 15 and 60 FPS render caps over ten simulated seconds.",
        "PASS: fixed-step trajectory samples=$($trajectory15.Count); maximum position delta=$('{0:F6}' -f $maxDelta) m.",
        "PASS: 15 FPS lifecycle probe detected child crash, surfaced FAILED state, restarted service and reached READY.",
        'PASS: Godot shutdown left no Robot Service child process.'
    ) | Set-Content (Join-Path $evidence 'test-summary.txt')
    Get-Content (Join-Path $evidence 'test-summary.txt')
}
finally { Pop-Location }
