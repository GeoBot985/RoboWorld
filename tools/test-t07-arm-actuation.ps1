$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T07'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$firmwareBuild = Join-Path $root 'samples/firmware/stm32-arm/build.ps1'
$stdout = Join-Path $evidence 'arm-actuation-godot-stdout.log'
$stderr = Join-Path $evidence 'arm-actuation-godot-stderr.log'
$controllerOutput = Join-Path $evidence 'arm-actuation-controller.log'
$controllerError = Join-Path $evidence 'arm-actuation-controller-stderr.log'
$process = $null

& $firmwareBuild *> (Join-Path $evidence 'arm-firmware-build.log')
if ($LASTEXITCODE -ne 0) { throw 'Arm firmware build failed; see arm-firmware-build.log.' }
$previousDotnetRoot = $env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
    & $dotnet build (Join-Path $root 'src/RoboWorld.RobotService/RoboWorld.RobotService.csproj') --configuration Release --no-restore --nologo *> (Join-Path $evidence 'arm-service-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Robot Service Release build failed; see arm-service-build.log.' }
    & $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --no-restore --nologo *> (Join-Path $evidence 'arm-godot-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Godot Debug build failed; see arm-godot-build.log.' }
} finally { $env:DOTNET_ROOT = $previousDotnetRoot }

try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--robot-id=arm-01', '--arm-actuation-smoke')
    $process = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(35)
    $port = 0
    while ([DateTime]::UtcNow -lt $deadline -and $port -eq 0) {
        if ($process.HasExited) { throw "Godot exited before the arm firmware UART was ready. See arm-actuation-godot-stderr.log" }
        if (Test-Path -LiteralPath $stdout) {
            $text = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            $match = [regex]::Match($text, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
            if ($match.Success) { $port = [int]$match.Groups[1].Value }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($port -eq 0) { throw 'Timed out waiting for the arm MCU UART endpoint.' }

    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $port.ToString(), '--timeout', '8', '--motor', '50', '0', '--motor-duration', '2.5')
    $controllerProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerOutput -RedirectStandardError $controllerError
    if ($controllerProcess.ExitCode -ne 0) { throw 'Arm firmware UART command sequence failed.' }
    if (-not $process.WaitForExit(30000)) { throw 'Godot did not finish the firmware-driven arm movement check.' }

    $godotLog = Get-Content -LiteralPath $stdout -Raw
    $stderrText = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
    $controllerLog = Get-Content -LiteralPath $controllerOutput -Raw
    if ($controllerLog -notmatch '(?m)^UART boot: BOOT [0-9a-f]{8}\r?$' -or $controllerLog -notmatch '(?m)^UART reply: PONG [0-9a-f]{8}\r?$' -or $controllerLog -notmatch '(?m)^Motor reply: OK MOTOR\r?$' -or $controllerLog -notmatch '(?m)^Motor stop reply: OK MOTOR\r?$') { throw 'Arm firmware UART boot, PING, MOTOR or stop response was missing or out of order.' }
    if ($godotLog -notmatch 'ACTUATION_TRACE .*actuator=arm-motor duty=0\.500') { throw 'Godot did not receive the arm motor PWM output sampled from Renode.' }
    if ($godotLog -notmatch 'ARM_ACTUATION_SMOKE complete actuator=arm-motor peak_duty=0\.500 angle_rad=([0-9.]+)') { throw 'Arm actuation smoke did not report a measured joint angle.' }
    $angle = [double]::Parse($Matches[1], [Globalization.CultureInfo]::InvariantCulture)
    if ($angle -lt 0.03) { throw "Arm moved less than 0.03 rad: $angle rad." }
    if ($stderrText -match 'SCRIPT ERROR|ERROR:') { throw 'Godot reported an engine error; see arm-actuation-godot-stderr.log.' }
    @(
        'External MOTOR 50 0 UART command -> arm STM32 firmware -> Renode TIM3/GPIO output -> Godot joint torque: PASS'
        "Measured shoulder rotation from initial pose: $($angle.ToString('F4',[Globalization.CultureInfo]::InvariantCulture)) rad"
        "Firmware build ID: $((Get-Content (Join-Path $root 'samples/firmware/stm32-arm/build/build_id.h') -Raw).Trim())"
        "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-arm/build/firmware.elf')).Hash)"
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'arm-actuation-summary.txt')
    Get-Content (Join-Path $evidence 'arm-actuation-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill($true); $process.WaitForExit() } catch { } }
}
