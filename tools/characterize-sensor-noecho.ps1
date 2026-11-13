param([int[]]$Seeds = @(6101,6102,6103,6104), [string]$OutputDirectory, [string]$SensorInterval = '3.0')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/evidence/C01/sensor-noecho-campaign-' + (Get-Date -Format 'yyyyMMddTHHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
if ($Seeds.Count -lt 4) { throw 'Use at least four independent seeds so complete sequences can be split into training and held-out groups.' }
$pool = @('-1.5','-2.0','-2.5','-3.0')
$rows = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $Seeds.Count; $index++) {
    $seed = $Seeds[$index]
    $split = if ($index -ge ($Seeds.Count - 2)) { 'heldout' } else { 'training' }
    $rng = [Random]::new($seed)
    $positions = [Collections.Generic.List[string]]::new()
    foreach ($position in $pool) { $positions.Add($position) }
    for ($positionIndex = $positions.Count - 1; $positionIndex -gt 0; $positionIndex--) {
        $swapIndex = $rng.Next($positionIndex + 1)
        $temporary = $positions[$positionIndex]
        $positions[$positionIndex] = $positions[$swapIndex]
        $positions[$swapIndex] = $temporary
    }
    $sequenceId = "seed-$seed-$split"
    $sequenceDirectory = Join-Path $OutputDirectory $sequenceId
    $evidenceTask = "C01-sensor-noecho-$seed"
    $noEchoAfterSample = 1 + ($index % 4)
    Write-Host "C01_SENSOR_NOECHO_START sequence=$sequenceId threshold=$noEchoAfterSample positions_m=$($positions -join ',')"
    & (Join-Path $PSScriptRoot 'test-t04-noecho.ps1') -EvidenceDirectory $sequenceDirectory -EvidenceTask $evidenceTask `
        -NoEchoAfterSample $noEchoAfterSample -ObstacleSequence ($positions -join ',') -SensorInterval $SensorInterval
    $trace = Join-Path $sequenceDirectory 'controller-characterization-noecho.jsonl'
    $evaluationPath = Join-Path $sequenceDirectory 'noecho-evaluation.json'
    $summaryPath = Join-Path $sequenceDirectory 'noecho-summary.txt'
    $trajectory = Join-Path $root "artifacts/evidence/$evidenceTask/godot-actuation-loaded.csv"
    foreach ($path in @($trace,$evaluationPath,$summaryPath,$trajectory)) { if (-not (Test-Path -LiteralPath $path)) { throw "$sequenceId did not produce $path" } }
    $evaluation = Get-Content -LiteralPath $evaluationPath -Raw | ConvertFrom-Json
    if ($evaluation.status -ne 'MEASURED_EXACT_FIRMWARE_NO_ECHO_BEHAVIOR_NOT_MODEL_QUALIFICATION' -or $evaluation.post_no_echo_replies -lt 1) { throw "$sequenceId failed the post-no-echo held-out measurement gate." }
    $rows.Add([ordered]@{
        sequence_id = $sequenceId
        split = $split
        seed = $seed
        no_echo_after_sample_index = $noEchoAfterSample
        obstacle_center_z_m = @($positions)
        sensor_query_interval_seconds = [double]::Parse($SensorInterval, [Globalization.CultureInfo]::InvariantCulture)
        firmware_sha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash
        trace = "$sequenceId/controller-characterization-noecho.jsonl"
        trace_sha256 = (Get-FileHash -Algorithm SHA256 $trace).Hash
        evaluation = "$sequenceId/noecho-evaluation.json"
        evaluation_sha256 = (Get-FileHash -Algorithm SHA256 $evaluationPath).Hash
        godot_trajectory = "$evidenceTask/godot-actuation-loaded.csv"
        godot_trajectory_sha256 = (Get-FileHash -Algorithm SHA256 $trajectory).Hash
        no_echo_samples = $evaluation.no_echo_samples
        in_range_samples = $evaluation.in_range_samples
        post_no_echo_replies = $evaluation.post_no_echo_replies
        post_no_echo_output = $evaluation.post_no_echo_firmware_behavior
        evidence_task = $evidenceTask
    })
    $rows | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'sequence-manifest.json')
    Write-Host "C01_SENSOR_NOECHO_PASS sequence=$sequenceId in_range=$($evaluation.in_range_samples) no_echo=$($evaluation.no_echo_samples) post_no_echo_replies=$($evaluation.post_no_echo_replies)"
}
$manifestPath = Join-Path $OutputDirectory 'sequence-manifest.json'
$rows | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $manifestPath
$firmwareHash = (Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash
$heldout = @($rows | Where-Object split -eq 'heldout')
$training = @($rows | Where-Object split -eq 'training')
$report = @(
    '# C01 no-echo / stale-reading campaign',
    '',
    '**Status:** measured exact-firmware reference behavior; not model qualification.',
    '',
    "- Firmware SHA-256: ``$firmwareHash``",
    "- Complete sequences: $($rows.Count) ($($training.Count) training, $($heldout.Count) held out by sequence)",
    "- Held-out post-no-echo replies checked: $(($heldout | Measure-Object -Property post_no_echo_replies -Sum).Sum)",
    '- Stimulus: real Godot 3D physics raycast, moving the obstacle outside the sensor cone after a recorded number of samples; negative sample means no echo pulse is injected.',
    '- Measurement: timestamped distance samples, PA0 echo edges, Renode advancement boundaries and captured UART requests/replies; post-no-echo response values are compared to the last completed echo.',
    '- Observed behavior: the current STM32 firmware retains the last valid distance and `valid=1` after subsequent no-echo samples. It does not clear validity or implement a stale-sample timeout.',
    '- This expands the measured input/history envelope only. It does not add no-echo behavior to the model; the profile remains `UNQUALIFIED` until an induced stateful rule passes held-out validation and all other acceptance gates.',
    '- Limits: partial-high timeout, noisy/overlapping pulse handling, reset/recovery, loaded trajectory equivalence, exact UART byte timing, and other firmware states remain uncovered.',
    '',
    "The immutable per-sequence evidence and hashes are in ``sequence-manifest.json``."
) -join "`n"
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'campaign-report.md'), $report + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "C01_SENSOR_NOECHO_CAMPAIGN_COMPLETE sequences=$($rows.Count) heldout=$($heldout.Count) path=$OutputDirectory"
