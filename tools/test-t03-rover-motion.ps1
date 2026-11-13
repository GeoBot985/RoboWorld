$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T03'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$stdout = Join-Path $evidence 'rover-motion-godot-stdout.log'
$stderr = Join-Path $evidence 'rover-motion-godot-stderr.log'
$controllerOutput = Join-Path $evidence 'rover-motion-controller.log'
$process = $null

try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--actuation-loaded-smoke')
    $process = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $port = 0
    while ([DateTime]::UtcNow -lt $deadline -and $port -eq 0) {
        if ($process.HasExited) { throw "Godot exited before the Renode service was ready. See $stderr" }
        if (Test-Path -LiteralPath $stdout) {
            $readyLog = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            if ($readyLog.Length -gt 0) {
                $match = [regex]::Match($readyLog, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
                if ($match.Success) { $port = [int]$match.Groups[1].Value }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($port -eq 0) { throw 'Timed out waiting for the Renode service UART.' }

    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $port.ToString(), '--timeout', '8', '--motor', '50', '50', '--motor-duration', '6.0')
    $controllerProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerOutput -RedirectStandardError (Join-Path $evidence 'rover-motion-controller-stderr.log')
    if ($controllerProcess.ExitCode -ne 0) { throw 'Python UART command sequence failed.' }
    $controllerLog = Get-Content -LiteralPath $controllerOutput -Raw
    if ($controllerLog -notmatch '(?m)^UART boot: BOOT [0-9a-f]{8}\r?$' -or $controllerLog -notmatch '(?m)^UART reply: PONG [0-9a-f]{8}\r?$' -or $controllerLog -notmatch '(?m)^Motor reply: OK MOTOR\r?$' -or $controllerLog -notmatch '(?m)^Motor stop reply: OK MOTOR\r?$') { throw 'UART boot, PING, MOTOR or stop response was missing or out of order.' }
    if (-not $process.WaitForExit(30000)) { throw 'Godot did not finish its loaded rover motion window.' }

    $godotLog = Get-Content -LiteralPath $stdout -Raw
    if ($godotLog -notmatch 'ACTUATION_TRACE .*actuator=motor-left duty=0\.500' -or $godotLog -notmatch 'ACTUATION_TRACE .*actuator=motor-right duty=0\.500') { throw 'Godot did not receive both 50% firmware outputs.' }
    $trajectory = Import-Csv -LiteralPath (Join-Path $evidence 'godot-actuation-loaded.csv')
    if ($trajectory.Count -lt 20) { throw "Expected at least 20 loaded physics samples, found $($trajectory.Count)." }
    $zStart = [double]$trajectory[0].chassis_z_m
    $zEnd = [double]$trajectory[-1].chassis_z_m
    $displacement = [math]::Abs($zEnd - $zStart)
    if ($displacement -lt 0.02) { throw "Loaded rover did not move at least 0.02 m at 50% duty: $displacement m" }

    @(
        'External Python MOTOR 50 50 -> firmware ACK -> TIM3/GPIO -> Godot motor torque: PASS'
        "Loaded rover chassis z displacement: $([double]$displacement.ToString('F4', [Globalization.CultureInfo]::InvariantCulture)) m"
        "Loaded physics samples: $($trajectory.Count)"
        "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash)"
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'rover-motion-summary.txt')
    Get-Content (Join-Path $evidence 'rover-motion-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true); $process.WaitForExit() } catch { }
    }
}
