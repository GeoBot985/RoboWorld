param(
    [ValidateRange(1, 1440)][int]$DurationMinutes = 240,
    [ValidateRange(5, 3600)][int]$SampleSeconds = 60,
    [ValidateRange(1, 3600)][int]$PingIntervalSeconds = 30
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $root 'artifacts/release/RoboWorld'
$app = Join-Path $packageRoot 'RoboWorld.exe'
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $packageRoot 'samples/controller/main.py'
$evidence = Join-Path $root 'artifacts/evidence/T06'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + "-$DurationMinutes"
$appLog = Join-Path $evidence "soak-$runId-app.log"
$appError = Join-Path $evidence "soak-$runId-app.stderr.log"
$controllerLog = Join-Path $evidence "soak-$runId-controller.log"
$controllerError = Join-Path $evidence "soak-$runId-controller.stderr.log"
$csvPath = Join-Path $evidence "soak-$runId-process-metrics.csv"
$summaryPath = Join-Path $evidence "soak-$runId-summary.txt"
foreach ($path in @($app, $python, $controller)) { if (-not (Test-Path -LiteralPath $path)) { throw "Missing T06 soak input: $path" } }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$appProcess = $null
$controllerProcess = $null
$workerProcessId = 0
$renodeProcessId = 0
$failure = $null
$runWatch = $null
$samples = [System.Collections.Generic.List[object]]::new()
$pingsAtStart = 0
try {
    $appProcess = Start-Process -FilePath $app -ArgumentList @() -WorkingDirectory $packageRoot -PassThru -WindowStyle Minimized -RedirectStandardOutput $appLog -RedirectStandardError $appError
    $startupDeadline = [DateTime]::UtcNow.AddSeconds(90)
    $uartPort = 0
    while ([DateTime]::UtcNow -lt $startupDeadline -and $uartPort -eq 0) {
        if ($appProcess.HasExited) { throw "Packaged app exited during soak startup. See $appError" }
        $appOutput = [string](Get-Content -LiteralPath $appLog -Raw -ErrorAction SilentlyContinue)
        $uartMatch = [regex]::Match($appOutput, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
        if ($uartMatch.Success) { $uartPort = [int]$uartMatch.Groups[1].Value }
        else { Start-Sleep -Milliseconds 250 }
    }
    if ($uartPort -eq 0) { throw 'Packaged app did not expose the local firmware UART within 90 seconds.' }

    $controllerArguments = @($controller, '--host', '127.0.0.1', '--port', $uartPort.ToString(), '--timeout', '8', '--ping-duration-seconds', ($DurationMinutes * 60).ToString(), '--ping-interval-seconds', $PingIntervalSeconds.ToString())
    $controllerProcess = Start-Process -FilePath $python -ArgumentList $controllerArguments -WorkingDirectory $packageRoot -PassThru -NoNewWindow -RedirectStandardOutput $controllerLog -RedirectStandardError $controllerError
    $deadline = [DateTime]::UtcNow.AddMinutes($DurationMinutes)
    $runWatch = [Diagnostics.Stopwatch]::StartNew()
    $sampleWatch = [Diagnostics.Stopwatch]::StartNew()
    $previousCpu = @{}
    $previousSampleTime = [DateTime]::UtcNow
    $cpuCount = [Math]::Max([Environment]::ProcessorCount, 1)

    while ([DateTime]::UtcNow -lt $deadline) {
        if ($appProcess.HasExited) { throw "Packaged app exited during the soak (exit $($appProcess.ExitCode))." }
        if ($controllerProcess.HasExited -and $controllerProcess.ExitCode -ne 0) { throw "Sustained firmware PING session failed (exit $($controllerProcess.ExitCode)). See $controllerError" }
        $processTree = @(Get-CimInstance Win32_Process)
        $workerInfo = $processTree | Where-Object { $_.ParentProcessId -eq $appProcess.Id -and $_.Name -like 'RoboWorld.RobotService*' } | Select-Object -First 1
        $renodeInfo = if ($workerInfo) { $processTree | Where-Object { $_.ParentProcessId -eq $workerInfo.ProcessId -and $_.Name -ieq 'renode.exe' } | Select-Object -First 1 } else { $null }
        $godotProcess = Get-Process -Id $appProcess.Id -ErrorAction SilentlyContinue
        $worker = if ($workerInfo) { Get-Process -Id $workerInfo.ProcessId -ErrorAction SilentlyContinue } else { $null }
        $renodeProc = if ($renodeInfo) { Get-Process -Id $renodeInfo.ProcessId -ErrorAction SilentlyContinue } else { $null }
        if (-not $godotProcess -or -not $worker -or -not $renodeProc) { throw 'The exported app, Robot Service, and Renode were not all alive during soak sampling.' }
        $workerProcessId = $worker.Id
        $renodeProcessId = $renodeProc.Id

        if ($sampleWatch.Elapsed.TotalSeconds -ge $SampleSeconds -or $samples.Count -eq 0) {
            $now = [DateTime]::UtcNow
            $processCpu = 0.0
            foreach ($process in @($godotProcess, $worker, $renodeProc)) {
                $currentCpu = $process.TotalProcessorTime.TotalSeconds
                if ($previousCpu.ContainsKey($process.Id)) { $processCpu += $currentCpu - $previousCpu[$process.Id] }
                $previousCpu[$process.Id] = $currentCpu
            }
            $elapsedSeconds = [Math]::Max(($now - $previousSampleTime).TotalSeconds, 0.001)
            $cpuPercent = 100 * $processCpu / $elapsedSeconds / $cpuCount
            $record = [pscustomobject]@{
                utc = $now.ToString('o')
                elapsed_seconds = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2}', $runWatch.Elapsed.TotalSeconds)
                app_working_set_mb = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2}', $godotProcess.WorkingSet64 / 1MB)
                service_working_set_mb = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2}', $worker.WorkingSet64 / 1MB)
                renode_working_set_mb = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2}', $renodeProc.WorkingSet64 / 1MB)
                measured_process_cpu_percent = [string]::Format([Globalization.CultureInfo]::InvariantCulture, '{0:F2}', $cpuPercent)
                active_firmware_pings = [regex]::Matches([string](Get-Content -LiteralPath $controllerLog -Raw -ErrorAction SilentlyContinue), 'SOAK_PING index=').Count
            }
            $samples.Add($record)
            if ($samples.Count -eq 1) { $record | Export-Csv -LiteralPath $csvPath -NoTypeInformation }
            else { $record | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Append }
            $previousSampleTime = $now
            $sampleWatch.Restart()
        }
        Start-Sleep -Seconds 1
    }
    if (-not $controllerProcess.WaitForExit(15000)) { throw 'Sustained firmware PING controller did not finish after its requested soak duration.' }
    if ($controllerProcess.ExitCode -ne 0) { throw "Sustained firmware PING controller failed. See $controllerError" }
    $pingsAtStart = [regex]::Matches([string](Get-Content -LiteralPath $controllerLog -Raw), 'SOAK_PING index=').Count
    if ($pingsAtStart -lt [Math]::Floor(($DurationMinutes * 60) / $PingIntervalSeconds * 0.9)) { throw "Too few firmware PING responses during soak: $pingsAtStart" }
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($controllerProcess -and -not $controllerProcess.HasExited) { try { [void]$controllerProcess.CloseMainWindow(); if (-not $controllerProcess.WaitForExit(10000)) { $controllerProcess.Kill(); $controllerProcess.WaitForExit() } } catch { } }
    if ($appProcess -and -not $appProcess.HasExited) {
        try { [void]$appProcess.CloseMainWindow(); if (-not $appProcess.WaitForExit(30000)) { $failure = 'App did not shut down gracefully after the soak.'; $appProcess.Kill($true); $appProcess.WaitForExit() } }
        catch { if (-not $failure) { $failure = $_.Exception.Message } }
    }
}

$pingsAtStart = [regex]::Matches([string](Get-Content -LiteralPath $controllerLog -Raw -ErrorAction SilentlyContinue), 'SOAK_PING index=').Count
$cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
do {
    $orphanRenode = @(Get-Process -Name renode -ErrorAction SilentlyContinue | Where-Object Id -eq $renodeProcessId)
    $orphanService = @(Get-Process -Name RoboWorld.RobotService -ErrorAction SilentlyContinue | Where-Object Id -eq $workerProcessId)
    if ($orphanRenode.Count -eq 0 -and $orphanService.Count -eq 0) { break }
    Start-Sleep -Milliseconds 250
} while ([DateTime]::UtcNow -lt $cleanupDeadline)
$actualMinutes = if ($runWatch) { [Math]::Round($runWatch.Elapsed.TotalMinutes, 2) } else { 0 }
$summary = @(
    "T06 package soak: $(if ($failure) { 'FAIL' } else { 'PASS' })"
    "Duration requested: $DurationMinutes minutes; sampled: $actualMinutes minutes"
    "Firmware PING responses: $pingsAtStart"
    "Process metrics CSV: $([IO.Path]::GetFileName($csvPath))"
    "App stdout/stderr: $([IO.Path]::GetFileName($appLog)), $([IO.Path]::GetFileName($appError))"
    "Controller stdout/stderr: $([IO.Path]::GetFileName($controllerLog)), $([IO.Path]::GetFileName($controllerError))"
    "Robot Service process released: $($orphanService.Count -eq 0)"
    "Renode process released: $($orphanRenode.Count -eq 0)"
    "Failure: $(if ($failure) { $failure } else { 'none' })"
)
$summary | Set-Content -LiteralPath $summaryPath -Encoding utf8
$summary
if ($failure) { throw $failure }
