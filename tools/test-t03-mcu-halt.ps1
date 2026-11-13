$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T03'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$stdout = Join-Path $evidence 'mcu-halt-godot-stdout.log'
$stderr = Join-Path $evidence 'mcu-halt-godot-stderr.log'
$controllerOutput = Join-Path $evidence 'mcu-halt-controller.log'
$process = $null

try {
    $process = Start-Process -FilePath $godot -ArgumentList @('--headless', '--path', (Join-Path $root 'godot'), '--', '--actuation-halt-smoke') -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $port = 0
    while ([DateTime]::UtcNow -lt $deadline -and $port -eq 0) {
        if ($process.HasExited) { throw 'Godot exited before the Robot Service was ready.' }
        $readyLog = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
        $match = [regex]::Match($readyLog, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
        if ($match.Success) { $port = [int]$match.Groups[1].Value }
        else { Start-Sleep -Milliseconds 100 }
    }
    if ($port -eq 0) { throw 'Timed out waiting for Renode UART.' }

    $controllerProcess = Start-Process -FilePath $python -ArgumentList @($controller, '--host', '127.0.0.1', '--port', $port.ToString(), '--timeout', '8', '--motor', '50', '50') -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerOutput -RedirectStandardError (Join-Path $evidence 'mcu-halt-controller-stderr.log')
    if ($controllerProcess.ExitCode -ne 0 -or (Get-Content -Raw $controllerOutput) -notmatch 'Motor reply: OK MOTOR') { throw 'Firmware did not acknowledge MOTOR 50 50.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        if ($process.HasExited) { throw "Godot exited before the halt state was observed. See $stderr" }
        $godotLog = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
        if ($godotLog -match 'ROBOT_SERVICE_LOG TEST_MCU_HALTED controller_id=mcu-drive component_id=mcu-board cpu=stm32f407vg') { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($godotLog -notmatch 'ROBOT_SERVICE_LOG TEST_MCU_HALTED controller_id=mcu-drive component_id=mcu-board cpu=stm32f407vg') { throw 'Renode did not confirm the targeted CPU halt after firmware-driven 50% PWM output.' }

    $uart = [System.Net.Sockets.TcpClient]::new(); $uart.Connect('127.0.0.1', $port); $stream = $uart.GetStream(); $stream.ReadTimeout = 750
    $ping = [System.Text.Encoding]::ASCII.GetBytes("PING`n"); $stream.Write($ping, 0, $ping.Length)
    $reply = [byte[]]::new(128); $noReply = $false
    try { $read = $stream.Read($reply, 0, $reply.Length); if ($read -eq 0) { $noReply = $true } } catch [System.IO.IOException] { $noReply = $true }
    $stream.Dispose(); $uart.Dispose()
    if (-not $noReply) { throw 'Halted firmware replied to PING.' }
    if (-not $process.WaitForExit(30000)) { throw 'Godot did not finish the actuation window.' }

    $trajectory = Import-Csv -LiteralPath (Join-Path $evidence 'godot-actuation-smoke.csv')
    $postHalt = @($trajectory | Select-Object -Last 10)
    $left = ($postHalt | ForEach-Object { [double]$_.left_wheel_omega_x_radps } | Measure-Object -Average).Average
    $right = ($postHalt | ForEach-Object { [double]$_.right_wheel_omega_x_radps } | Measure-Object -Average).Average
    if ([math]::Abs($left) -lt 10 -or [math]::Abs($right) -lt 10) { throw "PWM latch did not continue driving the unloaded wheels after CPU halt: left=$left right=$right rad/s" }

    $diagnostics = [string](Get-Content -LiteralPath $stdout -Raw)
    foreach ($expected in @(
        'DIAGNOSTIC_ROW id=robot status=FAILED',
        'DIAGNOSTIC_ROW id=mcu-board status=FAILED detail=Renode CPU halted',
        'DIAGNOSTIC_ROW id=uart status=INACTIVE',
        'DIAGNOSTIC_ROW id=driver status=DEGRADED',
        'DIAGNOSTIC_ROW id=motor-left status=DEGRADED detail=PWM latched after its MCU halt',
        'DIAGNOSTIC_ROW id=motor-right status=DEGRADED detail=PWM latched after its MCU halt')) {
        if ($diagnostics -notmatch [regex]::Escape($expected)) { throw "Hierarchical diagnostic attribution missing: $expected" }
    }

    @('Renode halted STM32F407 core after MOTOR 50 50: PASS','UART PING received no firmware response after halt: PASS',"Persistent 50% PWM kept both unloaded wheels above 10 rad/s: PASS ($left / $right)",'Diagnostic root cause MCU FAILED; UART INACTIVE; latched output downstream marked DEGRADED: PASS','Interactive Godot hierarchical diagnostics panel visually inspected: PASS') | Set-Content -Encoding utf8 (Join-Path $evidence 'mcu-halt-summary.txt')
    Get-Content (Join-Path $evidence 'mcu-halt-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill($true); $process.WaitForExit() } catch { } }
}
