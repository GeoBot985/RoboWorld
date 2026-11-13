param(
    [string]$EvidenceDirectory,
    [string]$EvidenceTask = 'T04',
    [string]$SensorObstacleZSequence = '-1.5,-2.0,-2.5,-3.0',
    [string]$SensorInterval = '1.0',
    [string[]]$MotorSequence = @(),
    [string]$MotorSequenceDelay = '1.0',
    [string]$MotorSequenceFinalHoldSeconds = '0',
    [int]$SensorInterleaveEvery = 0,
    [int]$SensorInterleaveStartAfter = 1,
    [string]$ProjectPackage,
    [string]$RobotId,
    [switch]$ProbeMoveBypass,
    [switch]$CameraFollowCheck
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = if ([string]::IsNullOrWhiteSpace($EvidenceDirectory)) { Join-Path $root 'artifacts/evidence/T04' } else { [IO.Path]::GetFullPath($EvidenceDirectory) }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$stdout = Join-Path $evidence 'godot-sensor-e2e.log'
$stderr = Join-Path $evidence 'godot-sensor-e2e.stderr.log'
$pythonOutput = Join-Path $evidence 'python-sensor-e2e.log'
$characterizationTrace = Join-Path $evidence 'controller-characterization-t04.jsonl'
$sensorEvaluationPath = Join-Path $evidence 'sensor-reference-evaluation.json'
$godotProcess = $null
if (-not $godot) { throw 'Pinned Godot .NET executable not found.' }
if (-not (Test-Path -LiteralPath $python)) { throw 'Workspace Python environment is not provisioned.' }
try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--actuation-loaded-smoke', '--sensor-e2e', "--sensor-obstacle-z-sequence-m=$SensorObstacleZSequence", "--evidence-task=$EvidenceTask", '--renderer-slow', '--require-clock-verification', "--characterization-trace=$characterizationTrace")
    if (-not [string]::IsNullOrWhiteSpace($ProjectPackage)) {
        $arguments += "--project=$([IO.Path]::GetFullPath($ProjectPackage))"
        if ([string]::IsNullOrWhiteSpace($RobotId)) { throw 'RobotId is required when a project package is supplied.' }
        $arguments += "--robot-id=$RobotId"
        $arguments += '--controller-mode=exact'
        if ($CameraFollowCheck) { $arguments += '--camera-follow-smoke' }
    }
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
    # The obstacle sequence advances in simulation time, whereas the external
    # controller sleeps in wall time. Allow more observations in the ordinary
    # smoke workload so a slow Exact backend can reach the later ranges; the
    # distinct-range, span and causal checks below remain unchanged.
    $sensorCount = if ($MotorSequence.Count -gt 0 -or $SensorInterleaveEvery -gt 0) { 4 } else { 8 }
    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $endpointPort.ToString(), '--timeout', '8', '--uart-overflow-probe', '--sensor-count', $sensorCount.ToString(), '--sensor-interval', $SensorInterval)
    if ($MotorSequence.Count -gt 0) {
        foreach ($command in $MotorSequence) {
            $match = [regex]::Match($command, '^(-?\d+),(-?\d+)$')
            if (-not $match.Success) { throw "Invalid mixed-campaign MOTOR command '$command'. Expected LEFT,RIGHT integer operands." }
            $left = [int]$match.Groups[1].Value
            $right = [int]$match.Groups[2].Value
            if ($left -lt -100 -or $left -gt 100 -or $right -lt -100 -or $right -gt 100) { throw "Mixed-campaign MOTOR operands must be within -100..100: '$command'." }
            $pythonArgs += "--motor-sequence=$command"
        }
        $pythonArgs += @('--motor-sequence-delay', $MotorSequenceDelay, '--motor-sequence-final-hold-seconds', $MotorSequenceFinalHoldSeconds)
        if ($SensorInterleaveEvery -gt 0) { $pythonArgs += @('--sensor-interleave-every', $SensorInterleaveEvery.ToString(), '--sensor-interleave-start-after', $SensorInterleaveStartAfter.ToString()) }
    } else {
        $pythonArgs += @('--motor', '50', '50', '--motor-duration', '4')
    }
    if ($ProbeMoveBypass) { $pythonArgs += '--probe-move-bypass' }
    $pythonProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $pythonOutput -RedirectStandardError (Join-Path $evidence 'python-sensor-e2e.stderr.log')
    if ($pythonProcess.ExitCode -ne 0) { Get-Content -LiteralPath $pythonOutput; throw 'Python controller UART exchange failed.' }
    if ($CameraFollowCheck) {
        $cameraFollowDeadline = [DateTime]::UtcNow.AddSeconds(5)
        while ([DateTime]::UtcNow -lt $cameraFollowDeadline -and (Get-Content -LiteralPath $stdout -Raw) -notmatch 'CAMERA_FOLLOW_SMOKE status=PASS' -and -not $godotProcess.HasExited) {
            Start-Sleep -Milliseconds 100
        }
        if ((Get-Content -LiteralPath $stdout -Raw) -notmatch 'CAMERA_FOLLOW_SMOKE status=PASS') { throw 'Mounted camera did not demonstrate a moving pose that remains rigidly attached to the robot.' }
    }
    $pythonLog = Get-Content -LiteralPath $pythonOutput -Raw
    $motorRepliesMatched = if ($MotorSequence.Count -gt 0) {
        ([regex]::Matches($pythonLog, 'Motor -?\d+,-?\d+ reply: OK MOTOR')).Count -eq $MotorSequence.Count
    } else { $pythonLog -match 'Motor reply: OK MOTOR' }
    if ($pythonLog -notmatch 'UART reply: PONG [0-9a-f]{8}' -or $pythonLog -notmatch 'UART overflow reply: ERR LINE' -or -not $motorRepliesMatched -or ([regex]::Matches($pythonLog, 'Sensor reply \d+: DIST mm=\d+ valid=1')).Count -lt 3) {
        throw "Expected genuine firmware UART boot/PING, explicit line-overflow rejection, loaded motor command and at least three valid MCU distance readings: $pythonLog"
    }
    if ($ProbeMoveBypass -and $pythonLog -notmatch 'Invalid-command reply: ERR CMD') { throw 'Direct MOVE bypass probe did not return the firmware ERR CMD rejection.' }
    $distanceValues = [regex]::Matches($pythonLog, 'Sensor reply \d+: DIST mm=(\d+) valid=1') | ForEach-Object { [int]$_.Groups[1].Value }
    $meanMm = ($distanceValues | Measure-Object -Average).Average
    if ($meanMm -lt 800 -or $meanMm -gt 2500) { throw "MCU echo distance average $meanMm mm is outside the configured 0.8-2.5m sensor validation range." }
    $distinctFirmwareDistances = @($distanceValues | Sort-Object -Unique)
    if ($distinctFirmwareDistances.Count -lt 3 -or ($distinctFirmwareDistances[-1] - $distinctFirmwareDistances[0]) -lt 800) { throw "Expected at least three varied firmware distances spanning 0.8 m; observed: $($distanceValues -join ',') mm." }
    $uartTimings = [regex]::Matches($pythonLog, 'UART_TIMING observed_rtt_ns=(\d+) expected_8n1_ns=(\d+) payload_bytes=(\d+)')
    if ($uartTimings.Count -lt 3) { throw 'Expected per-query UART observed and theoretical 115200 8N1 timing measurements.' }
    if (-not $godotProcess.WaitForExit(60000)) { throw 'Godot did not finish the loaded sensor smoke window after the required simulation-clock verification.' }
    if (-not (Test-Path -LiteralPath $characterizationTrace)) { throw 'Sensor characterization trace was not produced.' }
    $trace = @(Get-Content -LiteralPath $characterizationTrace | ForEach-Object { $_ | ConvertFrom-Json })
    $samples = @($trace | Where-Object { $_.PSObject.Properties.Name -contains 'DistanceMeters' })
    $distinctRayDistances = @($samples | ForEach-Object { [math]::Round([double]$_.DistanceMeters, 2) } | Sort-Object -Unique)
    if ($distinctRayDistances.Count -lt 3 -or ($distinctRayDistances[-1] - $distinctRayDistances[0]) -lt 0.8) { throw "Expected at least three distinct Godot ray ranges spanning 0.8 m; observed: $($distinctRayDistances -join ',') m." }
    $edges = @($trace | Where-Object { $_.PSObject.Properties.Name -contains 'High' })
    $rises = @($edges | Where-Object High)
    $falls = @($edges | Where-Object { -not $_.High })
    if ($samples.Count -lt 3 -or $rises.Count -ne $samples.Count -or $falls.Count -ne $samples.Count) { throw "Expected one timed echo pulse per captured sensor input; samples=$($samples.Count) rises=$($rises.Count) falls=$($falls.Count)." }
    for ($index = 0; $index -lt $samples.Count; $index++) {
        $expectedPulseNs = [long][math]::Round(2 * [double]$samples[$index].DistanceMeters / 343 * 1000000000, [MidpointRounding]::AwayFromZero)
        if ($rises[$index].SimTimeNs -ne $samples[$index].SimTimeNs -or $falls[$index].SimTimeNs -ne ($rises[$index].SimTimeNs + $expectedPulseNs)) {
            throw "Sensor echo pulse $index did not preserve its sampled simulation time or sub-tick duration."
        }
    }
    & $python (Join-Path $root 'tools/evaluate-sensor-trace.py') $characterizationTrace --output $sensorEvaluationPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Causally aligned firmware sensor evaluation failed.' }
    $sensorEvaluation = Get-Content -LiteralPath $sensorEvaluationPath -Raw | ConvertFrom-Json
    if ($sensorEvaluation.paired_replies -lt 3 -or $sensorEvaluation.causally_matched_valid_replies -lt 3) { throw 'Fewer than three sensor replies were causally matched to completed echo stimuli.' }
    $godotLog = Get-Content -LiteralPath $stdout -Raw
    if ($godotLog -notmatch 'ACTUATION_SMOKE_CLOCK_GATE required_sim_ns=1000000000 mode=enabled' -or $godotLog -notmatch 'ACTUATION_SMOKE complete .*clock_elapsed_ns=(1[0-9]{9}|[2-9][0-9]{9,})') { throw 'Loaded sensor smoke ended before at least one simulated second elapsed for a CLOCK_VERIFIED boundary.' }
    if ($godotLog -notmatch 'GODOT_RAY .*hit=[1-9]' -or $godotLog -notmatch 'GODOT_DISTANCE_SAMPLE .*meters=1\.\d+') { throw 'Godot raycast did not hit the configured obstacle and report its distance.' }
    if ($godotLog -notmatch 'RENDERER_SLOW cap_fps=15') { throw 'The deliberate 15 FPS renderer slowdown was not enabled.' }
    if ($godotLog -notmatch 'UART_PROXY_BOUNDED buffer_bytes=1024 overflow_policy=tcp-backpressure dropped_bytes=0' -or $godotLog -notmatch 'CLOCK_VERIFIED .*measured_rtf=') { throw 'Bounded UART backpressure or measured synchronized clock RTF was not reported.' }
    $trajectoryPath = Join-Path $root (Join-Path (Join-Path 'artifacts/evidence' $EvidenceTask) 'godot-actuation-loaded.csv')
    if ($EvidenceTask -eq 'T04') { $trajectoryPath = Join-Path $evidence 'godot-actuation-loaded.csv' }
    $trajectory = Import-Csv -LiteralPath $trajectoryPath
    if ($trajectory.Count -lt 20) { throw "Expected loaded rover trajectory, found $($trajectory.Count) samples." }
    $leftWheelPeak = ($trajectory | ForEach-Object { [math]::Abs([double]$_.left_wheel_omega_x_radps) } | Measure-Object -Maximum).Maximum
    $rightWheelPeak = ($trajectory | ForEach-Object { [math]::Abs([double]$_.right_wheel_omega_x_radps) } | Measure-Object -Maximum).Maximum
    $nonzeroLeftDuty = @($trajectory | Where-Object { [math]::Abs([double]$_.left_signed_duty) -gt 0.01 }).Count
    $nonzeroRightDuty = @($trajectory | Where-Object { [math]::Abs([double]$_.right_signed_duty) -gt 0.01 }).Count
    if ($leftWheelPeak -lt 0.1 -or $rightWheelPeak -lt 0.1 -or $nonzeroLeftDuty -eq 0 -or $nonzeroRightDuty -eq 0) {
        throw "Graph-routed firmware output did not produce both wheel responses: peak_omega=($leftWheelPeak,$rightWheelPeak) rad/s nonzero_duty_rows=($nonzeroLeftDuty,$nonzeroRightDuty)."
    }
    if ($ProjectPackage) {
        $routedOutputs = [regex]::Matches((Get-Content -LiteralPath $stdout -Raw), 'ACTUATION_TRACE .* actuator=([^ ]+) duty=(?!0\.000)(-?\d+\.\d+)') |
            ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
        if ($routedOutputs.Count -lt 2) { throw "Expected nonzero firmware output events for both graph-routed motors; observed: $($routedOutputs -join ',')." }
    }
    $zStart = [double]$trajectory[0].chassis_z_m
    $zEnd = [double]$trajectory[-1].chassis_z_m
    if ([math]::Abs($zEnd - $zStart) -lt 0.001) { throw "Loaded rover did not translate measurably: z_start=$zStart z_end=$zEnd." }
    @(
        "Exact-firmware motor commands accepted: $(if ($MotorSequence.Count -gt 0) { $MotorSequence.Count } else { 1 })"
        "Seeded mixed motor sequence: $($MotorSequence -join ' -> ')"
        "Final motor hold seconds before firmware zero command: $MotorSequenceFinalHoldSeconds"
        "Sensor UART queries interleaved after motor commands: $SensorInterleaveEvery"
        "Sensor query interleaving starts after motor command: $SensorInterleaveStartAfter"
        "Trace combines motor UART, PWM/direction observations, range samples and echo edges: $($MotorSequence.Count -gt 0 -and $samples.Count -gt 0)"
        'Godot fixed-step raycast sampled the configured obstacle during loaded rover motion: PASS'
        'Distance sample passed through the versioned event stream and timed PA0 echo was captured by MCU firmware: PASS'
        'STM32F407 firmware measured pulse width with TIM2 and returned SENSOR? telemetry over UART: PASS'
        'Overlength UART line returned ERR LINE at the fixed firmware buffer boundary: PASS'
        "UART 8N1 theoretical serialization per sensor exchange: $([math]::Round(([double]$uartTimings[0].Groups[2].Value / 1000000), 3)) ms"
        "Observed first sensor request/reply wall RTT: $([math]::Round(([double]$uartTimings[0].Groups[1].Value / 1000000), 3)) ms (includes runtime scheduling; terminal bridge itself does not serialize at baud)"
        "Valid varied firmware sensor readings: $($distanceValues.Count) unique=$($distinctFirmwareDistances.Count) range_mm=$($distinctFirmwareDistances[0])..$($distinctFirmwareDistances[-1])"
        "Captured Godot sensor samples with exact timed echo edges: $($samples.Count)"
        "Godot ray ranges: unique=$($distinctRayDistances.Count) range_m=$($distinctRayDistances[0])..$($distinctRayDistances[-1])"
        'Sub-tick PA0 pulse widths matched distance-to-echo formula exactly: PASS'
        "Exact-firmware sensor error vs latest causally completed raycast: n=$($sensorEvaluation.absolute_error_mm.n) p95=$([math]::Round($sensorEvaluation.absolute_error_mm.p95, 3)) mm max=$([math]::Round($sensorEvaluation.absolute_error_mm.max, 3)) mm"
        "Sensor characterization trace SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $characterizationTrace).Hash)"
        "Mean firmware distance: $([math]::Round($meanMm, 1)) mm"
        "Loaded rover z displacement: $([math]::Round($zEnd - $zStart, 4)) m"
        "Graph-routed wheel peak angular speeds: left=$([math]::Round($leftWheelPeak, 4)) right=$([math]::Round($rightWheelPeak, 4)) rad/s"
        "Nonzero firmware duty samples: left=$nonzeroLeftDuty right=$nonzeroRightDuty"
        "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash)"
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'e2e-summary.txt')
    Get-Content (Join-Path $evidence 'e2e-summary.txt')
} finally {
    if ($godotProcess -and -not $godotProcess.HasExited) {
        Stop-Process -Id $godotProcess.Id -Force -ErrorAction SilentlyContinue
        $godotProcess.WaitForExit()
    }
}
