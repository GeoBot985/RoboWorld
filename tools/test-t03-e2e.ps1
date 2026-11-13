$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T03'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$stdout = Join-Path $evidence 'godot-e2e-stdout.log'
$stderr = Join-Path $evidence 'godot-e2e-stderr.log'
$characterizationTrace = Join-Path $evidence 'controller-characterization-t03.jsonl'
$pythonOutput = Join-Path $evidence 'python-motor-command.log'
$godotProcess = $null

if (-not $godot) { throw 'Pinned Godot .NET executable not found.' }
if (-not (Test-Path -LiteralPath $python)) { throw 'Workspace Python environment is not provisioned.' }

try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--actuation-smoke', '--test-clock-epoch-race', "--characterization-trace=$characterizationTrace")
    $godotProcess = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $endpointPort = 0
    while ([DateTime]::UtcNow -lt $deadline -and $endpointPort -eq 0) {
        if ($godotProcess.HasExited) { throw "Godot exited before RobotService became ready. See $stderr" }
        if (Test-Path -LiteralPath $stdout) {
            $log = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            $match = [regex]::Match($log, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
            if ($match.Success) { $endpointPort = [int]$match.Groups[1].Value }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($endpointPort -eq 0) { throw 'Timed out waiting for Godot to start the Renode backed RobotService.' }

    $motorSequence = @('0,0', '25,25', '50,50', '75,75', '100,100', '-25,-25', '-50,-50', '-75,-75', '-100,-100', '0,0')
    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $endpointPort.ToString(), '--timeout', '8', '--probe-move-bypass', '--motor-sequence-delay', '3.8') + @($motorSequence | ForEach-Object { "--motor-sequence=$_" })
    $pythonProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $pythonOutput -RedirectStandardError (Join-Path $evidence 'python-motor-command.stderr.log')
    if ($pythonProcess.ExitCode -ne 0) { Get-Content -LiteralPath $pythonOutput; throw 'Python controller UART exchange failed.' }
    $pythonLog = Get-Content -LiteralPath $pythonOutput -Raw
    if ($pythonLog -notmatch 'UART reply: PONG [0-9a-f]{8}' -or $pythonLog -notmatch 'Invalid-command reply: ERR CMD' -or ([regex]::Matches($pythonLog, 'Motor -?\d+,-?\d+ reply: OK MOTOR')).Count -ne $motorSequence.Count) { throw "Python did not observe PING/PONG, reject the direct MOVE command, and receive all firmware motor acknowledgements: $pythonLog" }

    if (-not $godotProcess.WaitForExit(45000)) { throw 'Godot did not finish the actuation smoke window.' }
    $godotLog = Get-Content -LiteralPath $stdout -Raw
    if ($godotLog -notmatch 'GODOT_CLOCK_EPOCH_TEST_REORDERED') { throw 'Clock epoch ordering regression scenario did not run.' }
    if ($godotLog -notmatch 'GODOT_CLOCK_TICK .*tick=\d+ start_ns=\d+ delta_ns=\d+ sim_time_ns=') { throw 'Godot did not synchronize ticks after the delayed clock epoch notification.' }
    $clockEpochIndex = $godotLog.IndexOf('GODOT_CLOCK_EPOCH sim_time_ns=', [StringComparison]::Ordinal)
    $firstClockTickIndex = $godotLog.IndexOf('GODOT_CLOCK_TICK tick=', [StringComparison]::Ordinal)
    if ($clockEpochIndex -lt 0 -or $firstClockTickIndex -lt $clockEpochIndex) { throw 'Godot forwarded a synchronized tick before applying the delayed clock epoch.' }
    foreach ($duty in @('0.250', '0.500', '0.750', '1.000', '-0.250', '-0.500', '-0.750', '-1.000')) {
        if ($godotLog -notmatch "ACTUATION_TRACE .*actuator=motor-left duty=$([regex]::Escape($duty))") { throw "Godot did not receive left firmware output duty $duty." }
        if ($godotLog -notmatch "ACTUATION_TRACE .*actuator=motor-right duty=$([regex]::Escape($duty))") { throw "Godot did not receive right firmware output duty $duty." }
    }
    if ($godotLog -notmatch 'ACTUATION_SMOKE complete rows=(\d+)') { throw 'Godot did not complete the fixed-physics actuation smoke.' }

    $trajectory = Import-Csv -LiteralPath (Join-Path $evidence 'godot-actuation-smoke.csv')
    if ($trajectory.Count -lt 20) { throw "Expected at least 20 physics samples, found $($trajectory.Count)." }
    $maxWheelSpeed = ($trajectory | ForEach-Object { [math]::Max([math]::Abs([double]$_.left_wheel_omega_x_radps), [math]::Abs([double]$_.right_wheel_omega_x_radps)) } | Measure-Object -Maximum).Maximum
    if ($maxWheelSpeed -lt 0.5) { throw "Wheel response too small under 50% duty: $maxWheelSpeed rad/s" }

    $responseRows = [System.Collections.Generic.List[string]]::new()
    foreach ($duty in @(0.25, 0.5, 0.75, 1.0, -0.25, -0.5, -0.75, -1.0)) {
        $allAtDuty = @($trajectory | Where-Object { [math]::Abs([double]$_.left_signed_duty - $duty) -lt 0.0001 })
        $firstTick = ($allAtDuty | ForEach-Object { [long]$_.physics_tick } | Measure-Object -Minimum).Minimum
        $group = @($allAtDuty | Where-Object { [long]$_.physics_tick -ge ($firstTick + 24) })
        if ($group.Count -lt 8) { throw "Insufficient Godot samples at duty ${duty}: $($group.Count)." }
        $leftMean = ($group | ForEach-Object { [double]$_.left_wheel_omega_x_radps } | Measure-Object -Average).Average
        $rightMean = ($group | ForEach-Object { [double]$_.right_wheel_omega_x_radps } | Measure-Object -Average).Average
        Write-Host ("Wheel check duty={0:F2} samples={1} left={2} right={3}" -f $duty, $group.Count, $leftMean, $rightMean)
        $expected = $duty * 220 * 2 * [math]::PI / 60
        if ([math]::Abs($leftMean - $expected) -gt 0.6 -or [math]::Abs($rightMean - $expected) -gt 0.6) {
            throw "Unloaded wheel response at duty ${duty} outside ±0.6 rad/s: left=$leftMean right=$rightMean expected=$expected."
        }
        $responseRows.Add([string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2},{1:F3},{2:F3},{3:F3}', $duty, $leftMean, $rightMean, $expected))
    }
    $responseHeader = 'signed_duty,left_mean_radps,right_mean_radps,expected_no_load_radps'
    @($responseHeader) + $responseRows | Set-Content -Encoding utf8 (Join-Path $evidence 'wheel-response.csv')

    @(
        'Python raw UART PING/PONG: PASS'
        'Direct MOVE command rejected by firmware UART grammar: PASS'
        'STM32 firmware motor duty sweep, reverse sequence, and zero command acknowledgements: PASS'
        'Renode TIM3/GPIO output sampled and routed over length-prefixed protobuf: PASS'
        'Godot fixed physics applied versioned DC motor torque to separate unloaded wheel bodies: PASS'
        "Godot wheel response max speed: $([double]$maxWheelSpeed.ToString('F3', [Globalization.CultureInfo]::InvariantCulture)) rad/s"
        'Duty response matches signed no-load targets within ±0.6 rad/s: PASS'
        "Trajectory samples: $($trajectory.Count)"
        "Firmware Build ID: $((Get-Content (Join-Path $root 'samples/firmware/stm32-drive/build/build_id.h') -Raw).Trim())"
        "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash)"
        "Toolchain lock SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'toolchain.lock.json')).Hash)"
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'e2e-summary.txt')
    Get-Content (Join-Path $evidence 'e2e-summary.txt')
} finally {
    if ($godotProcess -and -not $godotProcess.HasExited) {
        Stop-Process -Id $godotProcess.Id -Force -ErrorAction SilentlyContinue
        $godotProcess.WaitForExit()
    }
}
