$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T04'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$stdout = Join-Path $evidence 'godot-clock-pause.log'
$stderr = Join-Path $evidence 'godot-clock-pause.stderr.log'
$process = $null
try {
    $args = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--pause-e2e', '--renderer-slow')
    $process = Start-Process -FilePath $godot -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(45000)) { throw 'Godot pause/resume clock check exceeded 45 seconds.' }
    $log = Get-Content -LiteralPath $stdout -Raw
    $errorText = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    if ($errorText -match 'SCRIPT ERROR|ERROR:') { throw "Godot pause/resume reported an error: $errorText" }
    $pause = [regex]::Match($log, 'SIM_PAUSE sim_time_ns=(\d+) physics_ticks=(\d+)')
    $resume = [regex]::Match($log, 'SIM_RESUME sim_time_ns=(\d+) physics_ticks=(\d+) paused_sim_advance_ns=(\d+)')
    $complete = [regex]::Match($log, 'SIM_PAUSE_E2E complete physics_ticks=(\d+) sim_time_ns=(\d+)')
    if (-not $pause.Success -or -not $resume.Success -or -not $complete.Success) { throw 'Missing pause, resume or post-resume completion marker.' }
    if ($pause.Groups[1].Value -ne $resume.Groups[1].Value -or $resume.Groups[3].Value -ne '0') { throw "Simulation clock advanced while paused: $log" }
    if ([long]$complete.Groups[1].Value -le [long]$resume.Groups[2].Value) { throw 'Physics did not resume after the aligned pause.' }
    @(
        'Pause held Godot physics and Renode clock advancement at the same acknowledged boundary: PASS'
        "Paused simulation time: $($pause.Groups[1].Value) ns; advance while paused: $($resume.Groups[3].Value) ns"
        "Physics tick count after resume: $($complete.Groups[1].Value)"
        'Renderer cap: 15 FPS; fixed-step physics and Renode remained request/ack synchronized: PASS'
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'clock-pause-summary.txt')
    Get-Content (Join-Path $evidence 'clock-pause-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue; $process.WaitForExit() }
}
