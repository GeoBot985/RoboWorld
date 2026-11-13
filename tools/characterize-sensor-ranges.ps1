param(
    [int[]]$Seeds = @(5301,5302,5303,5304,5305,5306),
    [string]$OutputDirectory,
    [string[]]$ObstacleCenterZSequence = @('-1.5','-2.0','-2.5','-3.0'),
    [string]$SensorIntervalSeconds = '1.0',
    [switch]$PreserveObstacleOrder
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root ('artifacts/evidence/C01/sensor-range-campaign-' + (Get-Date -Format 'yyyyMMddTHHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$runner = Join-Path $PSScriptRoot 'test-t04-e2e.ps1'
$basePositions = @($ObstacleCenterZSequence)
if ($basePositions.Count -ne 4 -or @($basePositions | Select-Object -Unique).Count -ne 4) {
    throw 'Provide exactly four unique obstacle center Z positions for the fixed four-query sensor sequence.'
}
foreach ($position in $basePositions) {
    $parsedPosition = 0.0
    if (-not [double]::TryParse($position, [Globalization.NumberStyles]::Float, [Globalization.CultureInfo]::InvariantCulture, [ref]$parsedPosition) -or [double]::IsNaN($parsedPosition) -or [double]::IsInfinity($parsedPosition)) {
        throw "Obstacle center Z must be a finite SI-meter value: '$position'."
    }
}
$rows = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $Seeds.Count; $index++) {
    $seed = $Seeds[$index]
    $split = if ($index -ge ($Seeds.Count - 2)) { 'heldout' } else { 'training' }
    $rng = [Random]::new($seed)
    $positions = [Collections.Generic.List[string]]::new()
    foreach ($position in $basePositions) { $positions.Add($position) }
    if (-not $PreserveObstacleOrder) {
        for ($positionIndex = $positions.Count - 1; $positionIndex -gt 0; $positionIndex--) {
            $swapIndex = $rng.Next($positionIndex + 1)
            $temporary = $positions[$positionIndex]
            $positions[$positionIndex] = $positions[$swapIndex]
            $positions[$swapIndex] = $temporary
        }
    }
    $sequence = $positions -join ','
    $sequenceId = "seed-$seed-$split"
    $sequenceDirectory = Join-Path $OutputDirectory $sequenceId
    $evidenceTask = "C01-sensor-$([IO.Path]::GetFileName($OutputDirectory))-$seed"
    Write-Host "C01_SENSOR_SEQUENCE_START id=$sequenceId positions_m=$sequence"
    & $runner -EvidenceDirectory $sequenceDirectory -EvidenceTask $evidenceTask -SensorObstacleZSequence $sequence -SensorInterval $SensorIntervalSeconds
    $trace = Join-Path $sequenceDirectory 'controller-characterization-t04.jsonl'
    $evaluationPath = Join-Path $sequenceDirectory 'sensor-reference-evaluation.json'
    $summaryPath = Join-Path $sequenceDirectory 'e2e-summary.txt'
    $trajectory = Join-Path $root (Join-Path (Join-Path 'artifacts/evidence' $evidenceTask) 'godot-actuation-loaded.csv')
    foreach ($path in @($trace,$evaluationPath,$summaryPath,$trajectory)) { if (-not (Test-Path -LiteralPath $path)) { throw "$sequenceId did not produce required evidence $path" } }
    $evaluation = Get-Content -LiteralPath $evaluationPath -Raw | ConvertFrom-Json
    $summary = Get-Content -LiteralPath $summaryPath -Raw
    if ($evaluation.causally_matched_valid_replies -lt 4 -or $summary -notmatch 'Valid varied firmware sensor readings: 4 unique=[3-9]') { throw "$sequenceId failed its causal or diverse-range result checks." }
    $rows.Add([ordered]@{
        sequence_id = $sequenceId
        split = $split
        seed = $seed
        obstacle_center_z_m = @($positions)
        sensor_campaign_range_template_center_z_m = @($basePositions)
        sensor_interval_seconds = [double]::Parse($SensorIntervalSeconds, [Globalization.CultureInfo]::InvariantCulture)
        obstacle_order_preserved = [bool]$PreserveObstacleOrder
        firmware_sha256 = (Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash
        trace = "$sequenceId/controller-characterization-t04.jsonl"
        trace_sha256 = (Get-FileHash -Algorithm SHA256 $trace).Hash
        sensor_evaluation = "$sequenceId/sensor-reference-evaluation.json"
        sensor_evaluation_sha256 = (Get-FileHash -Algorithm SHA256 $evaluationPath).Hash
        godot_trajectory = "$evidenceTask/godot-actuation-loaded.csv"
        godot_trajectory_sha256 = (Get-FileHash -Algorithm SHA256 $trajectory).Hash
        evaluation_status = $evaluation.status
        causal_valid_replies = $evaluation.causally_matched_valid_replies
        reply_error_p95_mm = $evaluation.absolute_error_mm.p95
        reply_error_max_mm = $evaluation.absolute_error_mm.max
        godot_evidence_task = $evidenceTask
    })
    $rows | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'sensor-sequence-manifest.json')
    Write-Host "C01_SENSOR_SEQUENCE_PASS id=$sequenceId trace_sha256=$((Get-FileHash -Algorithm SHA256 $trace).Hash)"
}
Write-Host "C01_SENSOR_CAMPAIGN_COMPLETE sequences=$($rows.Count) heldout=$(@($rows | Where-Object split -eq 'heldout').Count) path=$OutputDirectory"
