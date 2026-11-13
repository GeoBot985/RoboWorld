param(
    [int[]]$Seeds = @(5401,5402,5403,5404,5405,5406),
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Seeds.Count -lt 4 -or @($Seeds | Select-Object -Unique).Count -ne $Seeds.Count) {
    throw 'Provide at least four unique seeds for a whole-sequence training/held-out split.'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ('artifacts/evidence/C01/controller-sensor-mixed-' + (Get-Date -Format 'yyyyMMddTHHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$runner = Join-Path $PSScriptRoot 'test-t04-e2e.ps1'
$python = Join-Path $root '.venv/Scripts/python.exe'
$firmware = Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf'
$motorTemplates = @('50,50','-50,-50','50,-50','-50,50','100,-100','-100,100','25,25','-25,-25')
$obstacleTemplates = @('-1.5','-2.0','-2.5','-3.0')
$manifest = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $Seeds.Count; $index++) {
    $seed = $Seeds[$index]
    $split = if ($index -ge ($Seeds.Count - 2)) { 'heldout' } else { 'training' }
    $rng = [Random]::new($seed)
    $motorSequence = [Collections.Generic.List[string]]::new()
    foreach ($command in $motorTemplates) { $motorSequence.Add($command) }
    for ($commandIndex = $motorSequence.Count - 1; $commandIndex -gt 0; $commandIndex--) {
        $swapIndex = $rng.Next($commandIndex + 1)
        $temporary = $motorSequence[$commandIndex]
        $motorSequence[$commandIndex] = $motorSequence[$swapIndex]
        $motorSequence[$swapIndex] = $temporary
    }
    $motorSequence.Add('50,50')
    $obstacles = [Collections.Generic.List[string]]::new()
    foreach ($position in $obstacleTemplates) { $obstacles.Add($position) }
    $delay = @('0.10','0.12','0.15')[$rng.Next(3)]
    $sensorInterval = @('1.50','2.00','2.50')[$rng.Next(3)]
    $sequenceId = "seed-$seed-$split"
    $sequenceDirectory = Join-Path $OutputDirectory $sequenceId
    $evidenceTask = "C01-controller-sensor-mixed-$seed"
    Write-Host "C01_MIXED_START sequence=$sequenceId motor_commands=$($motorSequence.Count) interval_s=$delay obstacles_m=$($obstacles -join ',')"
    & $runner -EvidenceDirectory $sequenceDirectory -EvidenceTask $evidenceTask `
        -SensorObstacleZSequence ($obstacles -join ',') -SensorInterval $sensorInterval `
        -MotorSequence $motorSequence.ToArray() -MotorSequenceDelay $delay -MotorSequenceFinalHoldSeconds '4.0' -SensorInterleaveEvery 2 -SensorInterleaveStartAfter 4 -ProbeMoveBypass
    if (-not $?) { throw "$sequenceId exact-firmware interaction run failed." }

    $trace = Join-Path $sequenceDirectory 'controller-characterization-t04.jsonl'
    $evaluation = Join-Path $sequenceDirectory 'sensor-reference-evaluation.json'
    $trajectory = Join-Path $root "artifacts/evidence/$evidenceTask/godot-actuation-loaded.csv"
    foreach ($path in @($trace,$evaluation,$trajectory)) {
        if (-not (Test-Path -LiteralPath $path)) { throw "$sequenceId missing evidence file: $path" }
    }
    $validationSummary = Get-Content -LiteralPath (Join-Path $sequenceDirectory 'e2e-summary.txt') -Raw
    if ($validationSummary -notmatch "Exact-firmware motor commands accepted: $($motorSequence.Count)" -or $validationSummary -notmatch 'Trace combines motor UART, PWM/direction observations, range samples and echo edges: True') {
        throw "$sequenceId did not prove mixed motor/sensor execution in one exact-firmware run."
    }
    $sensorReport = Get-Content -LiteralPath $evaluation -Raw | ConvertFrom-Json
    $manifest.Add([ordered]@{
        sequence_id = $sequenceId
        split = $split
        seed = $seed
        motor_sequence = $motorSequence.ToArray()
        motor_sequence_delay_s = [double]::Parse($delay, [Globalization.CultureInfo]::InvariantCulture)
        motor_sequence_final_hold_s = 4.0
        motor_sequence_final_hold_command = '50,50'
        terminal_stop_command = '0,0'
        probe_move_bypass = $true
        sensor_interleave_every_motor_commands = 2
        sensor_interleave_start_after_motor_commands = 4
        obstacle_center_z_m = @($obstacles)
        sensor_interval_s = [double]::Parse($sensorInterval, [Globalization.CultureInfo]::InvariantCulture)
        firmware_sha256 = (Get-FileHash -LiteralPath $firmware -Algorithm SHA256).Hash
        trace = "$sequenceId/controller-characterization-t04.jsonl"
        trace_sha256 = (Get-FileHash -LiteralPath $trace -Algorithm SHA256).Hash
        sensor_evaluation = "$sequenceId/sensor-reference-evaluation.json"
        sensor_evaluation_sha256 = (Get-FileHash -LiteralPath $evaluation -Algorithm SHA256).Hash
        sensor_causally_matched_replies = $sensorReport.causally_matched_valid_replies
        sensor_reply_error_p95_mm = $sensorReport.absolute_error_mm.p95
        godot_evidence_task = $evidenceTask
        godot_trajectory = "$evidenceTask/godot-actuation-loaded.csv"
        godot_trajectory_sha256 = (Get-FileHash -LiteralPath $trajectory -Algorithm SHA256).Hash
    })
    Write-Host "C01_MIXED_SEQUENCE_PASS id=$sequenceId sensor_replies=$($sensorReport.causally_matched_valid_replies) trace_sha256=$((Get-FileHash -LiteralPath $trace -Algorithm SHA256).Hash)"
}

$manifestPath = Join-Path $OutputDirectory 'sequence-manifest.json'
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding utf8
& $python (Join-Path $PSScriptRoot 'validate-mixed-controller-sensor-campaign.py') $OutputDirectory `
    --firmware $firmware --output (Join-Path $OutputDirectory 'mixed-campaign-validation.json')
if ($LASTEXITCODE -ne 0) { throw 'Independent mixed controller/sensor reference validation failed.' }
& $python (Join-Path $PSScriptRoot 'fit-motor-peripheral-response.py') $OutputDirectory `
    --output (Join-Path $OutputDirectory 'mixed-peripheral-fit-UNQUALIFIED.json')
if ($LASTEXITCODE -ne 0) { throw 'Mixed-campaign peripheral fit/held-out report failed.' }
$fit = Get-Content -LiteralPath (Join-Path $OutputDirectory 'mixed-peripheral-fit-UNQUALIFIED.json') -Raw | ConvertFrom-Json
$validation = Get-Content -LiteralPath (Join-Path $OutputDirectory 'mixed-campaign-validation.json') -Raw | ConvertFrom-Json
$heldout = @($validation.sequences | Where-Object split -eq 'heldout')
$heldoutSensorP95 = ($heldout | Measure-Object -Property sensor_reply_error_p95_mm -Maximum).Maximum
$matchedSensorReplies = ($validation.sequences | Measure-Object -Property causally_matched_sensor_replies -Sum).Sum
$echoPulses = ($validation.sequences | Measure-Object -Property echo_pulses -Sum).Sum
$sensorSamplesDuringMotor = ($validation.sequences | Measure-Object -Property overlapping_sensor_samples -Sum).Sum
$report = @(
    '# C01 mixed controller and sensor campaign'
    ''
    "- Exact firmware sequences: $($manifest.Count) ($(@($manifest | Where-Object split -eq 'training').Count) training, $(@($manifest | Where-Object split -eq 'heldout').Count) whole held-out)."
    "- Reference validation: PASS; held-out model qualification: NOT ESTABLISHED."
    "- Mixed motor peripheral fit: $($fit.status); declared duty p95 gate: $($fit.threshold_declared_before_fit_duty_p95_percentage_points) percentage points."
    "- Held-out motor fit: $($fit.heldout_commands) observations; left/right duty p95=$($fit.motor_output_fit.left.heldout_error_percentage_points.p95)/$($fit.motor_output_fit.right.heldout_error_percentage_points.p95) pp; direction mismatches=$($fit.motor_output_fit.left.heldout_direction_mismatches + $fit.motor_output_fit.right.heldout_direction_mismatches); maximum response bracket=$($fit.timing_gate.heldout_command_to_output_latency_max_ns.max) ns."
    "- Mixed sensor evidence: $sensorSamplesDuringMotor distance samples overlap motor commands, $echoPulses exact-width echo pulses, $matchedSensorReplies causally matched valid replies; held-out sensor p95 maximum=$heldoutSensorP95 mm."
    '- Every exact-firmware run captures the observed MOVE 100 100 rejection, interleaves Godot distance raycasts/PA0 echo pulses and MCU SENSOR? UART queries after motor commands 4, 6 and 8, then applies a 4 s terminal 50% forward hold and firmware MOTOR 0 0 stop; a final sensor query follows the hold. UART commands, PWM/direction, sensor input and sensor replies are captured.'
    '- Held-out sequences are split by complete seed before fitting; no samples from a held-out sequence enter training.'
    '- This campaign is characterization evidence only. It does not qualify a deterministic model, loaded closed-loop equivalence, fast runtime performance, or unrestricted firmware behavior.'
    ''
    "Reproduction: `& '.\tools\characterize-controller-sensor-mixed.ps1' -Seeds @(5401,5402,5403,5404,5405,5406) -OutputDirectory '$OutputDirectory'`. Firmware, Renode, Godot, .NET and Windows build pins are recorded in `toolchain.lock.json`; the firmware SHA-256 is $($validation.firmware_sha256)."
    ''
    'Trace, evaluator, Godot trajectory and firmware hashes are retained in `sequence-manifest.json`.'
) -join "`n"
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'campaign-report.md'), $report + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "C01_MIXED_CAMPAIGN_COMPLETE sequences=$($manifest.Count) output=$OutputDirectory"
