param(
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory,
    [Parameter(Mandatory = $true)][string]$EvidenceTask,
    [int]$NoEchoAfterSample = 8,
    [string]$ObstacleSequence = '-1.5,-2.0,-2.5,-3.0',
    [string]$SensorInterval = '1.0'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
if ($NoEchoAfterSample -lt 1) { throw 'NoEchoAfterSample must be positive.' }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$trace = Join-Path $evidence 'controller-characterization-noecho.jsonl'
$stdout = Join-Path $evidence 'godot-noecho.log'
$stderr = Join-Path $evidence 'godot-noecho.stderr.log'
$controllerLog = Join-Path $evidence 'controller-noecho.log'
$godotProcess = $null
try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--actuation-loaded-smoke', '--sensor-e2e',
        "--sensor-obstacle-z-sequence-m=$ObstacleSequence", "--sensor-no-echo-after-sample=$NoEchoAfterSample",
        "--evidence-task=$EvidenceTask", '--renderer-slow', '--require-clock-verification', "--characterization-trace=$trace")
    $godotProcess = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $port = 0
    while ([DateTime]::UtcNow -lt $deadline -and $port -eq 0) {
        if ($godotProcess.HasExited) { throw "Godot exited before Robot Service became ready. See $stderr" }
        if (Test-Path -LiteralPath $stdout) {
            $readyText = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            $match = [regex]::Match($readyText, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
            if ($match.Success) { $port = [int]$match.Groups[1].Value }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($port -eq 0) { throw 'Timed out waiting for the exact Renode firmware UART.' }
    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $port.ToString(), '--timeout', '8', '--motor', '50', '50', '--sensor-count', '5', '--sensor-interval', $SensorInterval)
    $controllerProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerLog -RedirectStandardError (Join-Path $evidence 'controller-noecho.stderr.log')
    if ($controllerProcess.ExitCode -ne 0) { throw "Exact firmware UART query failed. See $controllerLog" }
    if (-not $godotProcess.WaitForExit(90000)) { throw 'Godot did not finish the bounded no-echo evidence window.' }
    if ($godotProcess.ExitCode -ne 0) { throw "Godot no-echo run exited $($godotProcess.ExitCode). See $stderr" }
    & $python (Join-Path $root 'tools/evaluate-sensor-noecho-trace.py') $trace $controllerLog --output (Join-Path $evidence 'noecho-evaluation.json')
    if ($LASTEXITCODE -ne 0) { throw 'Measured no-echo/stale-reading evaluation failed.' }
    $evaluation = Get-Content -LiteralPath (Join-Path $evidence 'noecho-evaluation.json') -Raw | ConvertFrom-Json
    @(
        "Exact Renode firmware image SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash)"
        "Real Godot raycast changed from in-range to no-echo after sample index ${NoEchoAfterSample}: PASS ($($evaluation.no_echo_samples) no-echo samples)"
        "Sub-tick PA0 pulse edges retained for all $($evaluation.in_range_samples) in-range ray samples: PASS"
        "Post-no-echo SENSOR? replies causally bracketed: $($evaluation.post_no_echo_replies)"
        'STM32F407 retains the previous valid distance and valid=1 when subsequent raycasts produce no echo: PASS'
        'Characterization is exact-firmware evidence only; deterministic fast profile remains UNQUALIFIED.'
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'noecho-summary.txt')
    Get-Content -LiteralPath (Join-Path $evidence 'noecho-summary.txt')
}
finally {
    if ($godotProcess -and -not $godotProcess.HasExited) { Stop-Process -Id $godotProcess.Id -Force -ErrorAction SilentlyContinue; $godotProcess.WaitForExit() }
}
