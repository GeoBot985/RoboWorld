$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T07'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/multi_controller_isolation.py'
$stdout = Join-Path $evidence 'multi-controller-godot-stdout.log'
$stderr = Join-Path $evidence 'multi-controller-godot-stderr.log'
$controllerOutput = Join-Path $evidence 'multi-controller-probe.log'
$controllerError = Join-Path $evidence 'multi-controller-probe-stderr.log'
$process = $null
$previousDotnetRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
    & $dotnet build (Join-Path $root 'src/RoboWorld.RobotService/RoboWorld.RobotService.csproj') --configuration Release --no-restore --nologo *> (Join-Path $evidence 'multi-controller-service-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Robot Service Release build failed.' }
    & $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --no-restore --nologo *> (Join-Path $evidence 'multi-controller-godot-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Godot Debug build failed.' }
} finally { $env:DOTNET_ROOT = $previousDotnetRoot }

try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--robot-id=rover-multi-01', '--multi-controller-isolation-smoke')
    $process = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $ports = @{}
    while ([DateTime]::UtcNow -lt $deadline -and $ports.Count -lt 2) {
        if ($process.HasExited) { throw 'Godot exited before both controller endpoints were ready.' }
        if (Test-Path -LiteralPath $stdout) {
            $text = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            foreach ($match in [regex]::Matches($text, 'UART_ENDPOINT controller_id=([^\s]+).*?uart=tcp://127\.0\.0\.1:(\d+)')) { $ports[$match.Groups[1].Value] = [int]$match.Groups[2].Value }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($ports.Count -ne 2 -or -not $ports.ContainsKey('mcu-drive') -or -not $ports.ContainsKey('mcu-aux')) { throw "Did not receive two named UART endpoints: $($ports.Keys -join ', ')" }
    $pythonArgs = @($controller, '--host', '127.0.0.1', '--primary-port', $ports['mcu-drive'].ToString(), '--secondary-port', $ports['mcu-aux'].ToString())
    $probe = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerOutput -RedirectStandardError $controllerError
    if ($probe.ExitCode -ne 0) { throw 'Two-controller UART isolation probe failed.' }
    if (-not $process.WaitForExit(120000)) { throw 'Godot did not complete its multi-controller smoke within the 120-second regression-test allowance.' }
    $godotLog = Get-Content -LiteralPath $stdout -Raw
    if ($godotLog -notmatch 'TEST_MCU_HALTED controller_id=mcu-aux component_id=mcu-board-aux' -or $godotLog -notmatch 'MULTI_CONTROLLER_ISOLATION_SMOKE complete controllers=2 clock_ticks=960 shared_clock=PASS') { throw 'App-level multi-controller fault/clock assertions were missing.' }
    if ($godotLog -notmatch 'DIAGNOSTIC_ROW id=mcu-board-aux status=FAILED' -or $godotLog -match 'DIAGNOSTIC_ROW id=motor-left status=DEGRADED detail=PWM latched after MCU halt') { throw 'The auxiliary MCU fault was not attributed to its own component or incorrectly degraded the primary motor.' }
    if ($godotLog -notmatch 'ACTUATION_TRACE .*actuator=motor-right duty=0\.500' -or $godotLog -notmatch 'DIAGNOSTIC_ROW id=motor-right status=DEGRADED detail=PWM latched after its MCU halt') { throw 'The auxiliary firmware did not actuate or isolate its graph-connected right motor.' }
    if ($godotLog -match 'GODOT_TICK_STREAM_FAILED|CLOCK_ERROR|MONITOR_SAMPLE_INVALID|MONITOR_READ_FAILED|RENODE_EXITED|SCRIPT ERROR|ERROR:') { throw 'Runtime reported a controller, clock, monitor or engine failure outside the injected secondary fault.' }
    @(
        'App-level two-machine Renode startup with one exact shared simulation clock: PASS'
        'Separate drive and auxiliary firmware ELFs, UART endpoints and PING/PONG: PASS'
        'Auxiliary firmware channel 1 follows its graph-wired PA6 -> H-bridge B -> right motor route: PASS'
        'Halting the auxiliary MCU leaves the primary drive MCU responsive: PASS'
        "Drive ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash)"
        "Auxiliary ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-arm/build/firmware.elf')).Hash)"
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'multi-controller-isolation-summary.txt')
    Get-Content (Join-Path $evidence 'multi-controller-isolation-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill($true); $process.WaitForExit() } catch { } }
}
