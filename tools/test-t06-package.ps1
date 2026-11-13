$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T06'
$packageRoot = Join-Path $root 'artifacts/release/RoboWorld'
$app = Join-Path $packageRoot 'RoboWorld.exe'
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $packageRoot 'samples/controller/main.py'
if (-not (Test-Path $app) -or -not (Test-Path $python) -or -not (Test-Path $controller)) { throw 'Build the Windows package and provision the workspace Python controller first.' }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$savePath = Join-Path $evidence 'exported-starter.roboworld'
$saveLog = Join-Path $evidence 'exported-design-save.log'
$openLog = Join-Path $evidence 'exported-design-open.log'
& $app --headless -- --design-smoke "--save-project=$savePath" 2>&1 | Set-Content -Encoding utf8 $saveLog
if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw $saveLog) -notmatch 'PROJECT_SAVED id=roboworld-starter') { throw 'Exported application could not save the design package.' }
& $app --headless -- "--project=$savePath" --design-smoke 2>&1 | Set-Content -Encoding utf8 $openLog
if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw $openLog) -notmatch 'PROJECT_LOADED id=roboworld-starter .*firmware_sha256=5d5973a2e119167ef3c2ef18e8929e8ad9735ad65a63b639179256547575b6e9 robot=rover-01 world=room-20x20 components=10') { throw 'Exported application failed to reopen the saved project.' }

$restartLog = Join-Path $evidence 'exported-worker-restart.log'
$restartError = Join-Path $evidence 'exported-worker-restart.stderr.log'
$restartProcess = Start-Process -FilePath $app -ArgumentList @('--headless', '--', '--benchmark-fps=15', '--benchmark-worker-restart') -PassThru -NoNewWindow -RedirectStandardOutput $restartLog -RedirectStandardError $restartError
if (-not $restartProcess.WaitForExit(45000)) { try { $restartProcess.Kill($true) } catch { }; throw 'Exported worker restart benchmark did not finish within 45 seconds.' }
$restartOutput = Get-Content -Raw $restartLog
if ($restartProcess.ExitCode -ne 0 -or $restartOutput -notmatch 'BENCHMARK complete .*worker_failure_seen=True worker_restarted=True worker_state=READY') { throw "Exported viewer did not survive worker failure and restart: $restartOutput" }

$stdout = Join-Path $evidence 'exported-sensor-e2e.log'
$stderr = Join-Path $evidence 'exported-sensor-e2e.stderr.log'
$pythonOutput = Join-Path $evidence 'exported-python-e2e.log'
$pythonError = Join-Path $evidence 'exported-python-e2e.stderr.log'
$godotProcess = $null
$pythonProcess = $null
$beforeRenode = @(Get-Process -Name 'renode' -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
try {
    # The 15 FPS package can run below 0.06 RTF on loaded CI/workstations; hold it long enough to verify tick 120.
    $args = @('--headless', '--', '--actuation-loaded-smoke', '--sensor-e2e', '--renderer-slow', '--test-actuation-smoke-duration-seconds=30')
    $godotProcess = Start-Process -FilePath $app -ArgumentList $args -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $uartPort = 0
    $eventPort = 0
    while ([DateTime]::UtcNow -lt $deadline -and $uartPort -eq 0) {
        if ($godotProcess.HasExited) { throw "Exported app exited before Robot Service was ready. See $stderr" }
        $log = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
        $uartMatch = [regex]::Match($log, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
        $eventMatch = [regex]::Match($log, 'ROBOT_SERVICE_ENDPOINT .*events_port=(\d+)')
        if ($uartMatch.Success -and $eventMatch.Success) { $uartPort = [int]$uartMatch.Groups[1].Value; $eventPort = [int]$eventMatch.Groups[1].Value }
        else { Start-Sleep -Milliseconds 100 }
    }
    if ($uartPort -eq 0) { throw 'Timed out waiting for the exported app to start Renode and publish loopback endpoints.' }
    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $uartPort.ToString(), '--timeout', '8', '--uart-overflow-probe', '--motor', '50', '50', '--motor-duration', '1.5', '--sensor-count', '4', '--sensor-interval', '0.3')
    $pythonProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $pythonOutput -RedirectStandardError $pythonError
    if ($pythonProcess.ExitCode -ne 0) { throw "External Python controller failed: $(Get-Content -Raw $pythonOutput)" }
    $pythonLog = Get-Content -Raw $pythonOutput
    if ($pythonLog -notmatch 'UART reply: PONG [0-9a-f]{8}' -or $pythonLog -notmatch 'UART overflow reply: ERR LINE' -or $pythonLog -notmatch 'Motor reply: OK MOTOR' -or ([regex]::Matches($pythonLog, 'Sensor reply \d+: DIST mm=\d+ valid=1')).Count -lt 3) { throw "Exported firmware UART/sensor chain failed: $pythonLog" }
    if (-not $godotProcess.WaitForExit(45000)) { throw 'Exported app did not finish its loaded actuation/sensor run.' }
    $godotLog = Get-Content -Raw $stdout
    if ($godotLog -notmatch 'GODOT_RAY .*hit=[1-9]' -or $godotLog -notmatch 'ACTUATION_SMOKE complete' -or $godotLog -notmatch 'UART_SENSOR_TELEMETRY mm=\d+ valid=1') { throw 'Exported app did not record physics actuation, obstacle raycast and firmware sensor telemetry.' }
    if ($godotLog -notmatch 'ROBOT_SERVICE_LOG CLOCK_VERIFIED' -or $godotLog -notmatch 'UART_PROXY_BOUNDED buffer_bytes=1024') { throw 'Exported app omitted synchronized clock or bounded UART evidence.' }

    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $orphanRenode = @(Get-Process -Name 'renode' -ErrorAction SilentlyContinue | Where-Object { $_.Id -notin $beforeRenode })
        $orphanService = @(Get-Process -Name 'RoboWorld.RobotService' -ErrorAction SilentlyContinue)
        if ($orphanRenode.Count -eq 0 -and $orphanService.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $cleanupDeadline)
    if ($orphanRenode.Count -gt 0 -or $orphanService.Count -gt 0) { throw 'Exported app left a Robot Service or Renode process running.' }
    foreach ($port in @($uartPort, $eventPort)) {
        $probe = [System.Net.Sockets.TcpClient]::new()
        try { $probe.Connect('127.0.0.1', $port); throw "Loopback port $port is still accepting connections after app exit." }
        catch [System.Net.Sockets.SocketException] { }
        finally { $probe.Dispose() }
    }

    @(
        'Godot Windows release export starts from its installation folder: PASS'
        'Exported app saves and reopens the starter design with firmware hash intact: PASS'
        'Exported viewer survives simulated worker exit and returns to READY: PASS'
        'Bundled Robot Service/Renode started without a manually launched worker: PASS'
        'External Python PING, overflow, motor, and firmware distance sensor flow: PASS'
        'Godot physics obstacle raycast and loaded rover actuation: PASS'
        'Clock verification and bounded UART telemetry emitted: PASS'
        'Robot Service/Renode processes and both ephemeral ports released on app shutdown: PASS'
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'package-e2e-summary.txt')
    Get-Content (Join-Path $evidence 'package-e2e-summary.txt')
} finally {
    if ($pythonProcess -and -not $pythonProcess.HasExited) { try { $pythonProcess.Kill(); $pythonProcess.WaitForExit() } catch { } }
    if ($godotProcess -and -not $godotProcess.HasExited) { try { $godotProcess.Kill($true); $godotProcess.WaitForExit() } catch { } }
}
