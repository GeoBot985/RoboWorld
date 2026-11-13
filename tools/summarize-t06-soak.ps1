param([Parameter(Mandatory = $true)][string]$SummaryPath)
$ErrorActionPreference = 'Stop'
$summaryFile = [IO.Path]::GetFullPath($SummaryPath)
$directory = Split-Path -Parent $summaryFile
if (-not (Test-Path -LiteralPath $summaryFile)) { throw "Soak summary does not exist: $summaryFile" }
$lines = Get-Content -LiteralPath $summaryFile
$csvName = ($lines | Where-Object { $_ -match '^Process metrics CSV: ' } | Select-Object -First 1) -replace '^Process metrics CSV: ', ''
$controllerName = ($lines | Where-Object { $_ -match '^Controller stdout/stderr: ' } | Select-Object -First 1) -replace '^Controller stdout/stderr: ', '' -replace ', .*$', ''
$csvPath = Join-Path $directory $csvName
$controllerPath = Join-Path $directory $controllerName
foreach ($path in @($csvPath, $controllerPath)) { if (-not (Test-Path -LiteralPath $path)) { throw "Soak evidence file is missing: $path" } }
$metrics = @(Import-Csv -LiteralPath $csvPath)
if ($metrics.Count -lt 2) { throw 'At least two process samples are required for soak summary statistics.' }
function Get-Quantile([double[]]$Values, [double]$Quantile) {
    $sorted = @($Values | Sort-Object)
    if ($sorted.Count -eq 0) { return [double]::NaN }
    return [double]$sorted[[Math]::Max(0, [Math]::Ceiling($Quantile * $sorted.Count) - 1)]
}
$invariant = [Globalization.CultureInfo]::InvariantCulture
$cpu = @($metrics | ForEach-Object { [double]::Parse($_.measured_process_cpu_percent, $invariant) })
$appRam = @($metrics | ForEach-Object { [double]::Parse($_.app_working_set_mb, $invariant) })
$serviceRam = @($metrics | ForEach-Object { [double]::Parse($_.service_working_set_mb, $invariant) })
$renodeRam = @($metrics | ForEach-Object { [double]::Parse($_.renode_working_set_mb, $invariant) })
$elapsedSeconds = @($metrics | ForEach-Object { [double]::Parse($_.elapsed_seconds, $invariant) })
$meanElapsed = ($elapsedSeconds | Measure-Object -Average).Average
$meanRenodeRam = ($renodeRam | Measure-Object -Average).Average
$slopeNumerator = 0.0
$slopeDenominator = 0.0
for ($index = 0; $index -lt $metrics.Count; $index++) {
    $deltaSeconds = $elapsedSeconds[$index] - $meanElapsed
    $slopeNumerator += $deltaSeconds * ($renodeRam[$index] - $meanRenodeRam)
    $slopeDenominator += $deltaSeconds * $deltaSeconds
}
$renodeWorkingSetSlopePerHour = if ($slopeDenominator -gt 0) { 3600 * $slopeNumerator / $slopeDenominator } else { [double]::NaN }
$rtts = @([regex]::Matches((Get-Content -LiteralPath $controllerPath -Raw), 'SOAK_PING index=\d+ rtt_ns=(\d+)') | ForEach-Object { [double]::Parse($_.Groups[1].Value, $invariant) / 1000000 })
if ($rtts.Count -eq 0) { throw 'No firmware PING RTT measurements were found.' }
$first = [DateTimeOffset]::Parse($metrics[0].utc)
$last = [DateTimeOffset]::Parse($metrics[-1].utc)
$requested = [regex]::Match(($lines -join "`n"), 'Duration requested: (\d+) minutes')
$requestedMinutes = if ($requested.Success) { [int]$requested.Groups[1].Value } else { 0 }
$sampledMinutes = ($last - $first).TotalMinutes
$soakResult = if ($requestedMinutes -ge 240 -and $sampledMinutes -ge 239 -and $rtts.Count -ge [Math]::Floor($requestedMinutes * 60 / 30 * 0.9)) { 'PASS' } else { 'PARTIAL' }
$runName = [IO.Path]::GetFileNameWithoutExtension($summaryFile) -replace '-summary$', ''
$outputPath = Join-Path $directory "$runName-performance-summary.txt"
$fmt = { param([double]$Value) [string]::Format($invariant, '{0:F2}', $Value) }
$report = @(
    "T06 soak performance summary: $soakResult"
    "Process samples: $($metrics.Count); sampled span: $(& $fmt $sampledMinutes) minutes"
    "Firmware PING replies: $($rtts.Count); response RTT p50/p95/p99: $(& $fmt (Get-Quantile $rtts 0.50)) / $(& $fmt (Get-Quantile $rtts 0.95)) / $(& $fmt (Get-Quantile $rtts 0.99)) ms"
    "Three-process CPU (app + Robot Service + Renode), normalized by logical processors, mean/max: $(& $fmt (($cpu | Measure-Object -Average).Average))% / $(& $fmt (($cpu | Measure-Object -Maximum).Maximum))%"
    "App working set MB, mean/max: $(& $fmt (($appRam | Measure-Object -Average).Average)) / $(& $fmt (($appRam | Measure-Object -Maximum).Maximum))"
    "Robot Service working set MB, mean/max: $(& $fmt (($serviceRam | Measure-Object -Average).Average)) / $(& $fmt (($serviceRam | Measure-Object -Maximum).Maximum))"
    "Renode working set MB, mean/max: $(& $fmt (($renodeRam | Measure-Object -Average).Average)) / $(& $fmt (($renodeRam | Measure-Object -Maximum).Maximum))"
    "Renode working-set linear trend: $(& $fmt $renodeWorkingSetSlopePerHour) MB/hour (descriptive process working set; not a managed-heap leak test)"
    'UART response RTT includes firmware/emulator/bridge latency; it is not an isolated IPC overhead measurement.'
    "Raw inputs: $csvName; $controllerName"
)
$report | Set-Content -LiteralPath $outputPath -Encoding utf8
$report
