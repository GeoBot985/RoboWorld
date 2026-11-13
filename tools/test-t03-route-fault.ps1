$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T03'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$stdout = Join-Path $evidence 'route-fault-godot-stdout.log'
$stderr = Join-Path $evidence 'route-fault-godot-stderr.log'
$controllerOutput = Join-Path $evidence 'route-fault-controller.log'
$process = $null

try {
    $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', '--actuation-fault-smoke')
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
    $pythonArgs = @($controller, '--host', '127.0.0.1', '--port', $port.ToString(), '--timeout', '8', '--motor', '50', '50')
    $controllerProcess = Start-Process -FilePath $python -ArgumentList $pythonArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerOutput -RedirectStandardError (Join-Path $evidence 'route-fault-controller-stderr.log')
    if ($controllerProcess.ExitCode -ne 0) { throw 'Firmware motor command failed before the injected route fault.' }
    $controllerLog = Get-Content -LiteralPath $controllerOutput -Raw
    if ($controllerLog -notmatch 'Motor reply: OK MOTOR') { throw "Firmware did not acknowledge the motor command: $controllerLog" }

    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $godotLog = ''
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Godot exited before reporting the injected output fault. See $stderr" }
        if (Test-Path -LiteralPath $stdout) { $godotLog = Get-Content -LiteralPath $stdout -Raw }
        if ($godotLog -match 'ACTUATION_FAULT reason=PWM_GPIO_AF_MISMATCH_PA6_PA7 signed_duty=0\.000') { break }
        Start-Sleep -Milliseconds 100
    }
    if ($godotLog -notmatch 'ACTUATION_FAULT reason=PWM_GPIO_AF_MISMATCH_PA6_PA7 signed_duty=0\.000') { throw 'GPIO route fault was not delivered as a diagnostic that halts motor actuation.' }
    $lastMotion = $godotLog.LastIndexOf('ACTUATION_TRACE')
    $fault = $godotLog.IndexOf('ACTUATION_FAULT')
    if ($lastMotion -lt 0 -or $lastMotion -gt $fault) { throw 'Actuation fault appeared before a valid firmware output reached Godot.' }

    @(
        'Python -> UART -> STM32 MOTOR 50 50: PASS'
        'Renode GPIOA PA6 alternate-function route fault injected after valid output: PASS'
        'Robot Service reported PWM_GPIO_AF_MISMATCH_PA6_PA7 diagnostic: PASS'
        'Godot reset both actuator duties to zero on diagnostic: PASS'
        "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash)"
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'route-fault-summary.txt')
    Get-Content (Join-Path $evidence 'route-fault-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill($true); $process.WaitForExit() } catch { }
    }
}
