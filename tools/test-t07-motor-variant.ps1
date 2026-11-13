$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T07'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$stdout = Join-Path $evidence 'motor-variant-godot.log'
$stderr = Join-Path $evidence 'motor-variant-godot.stderr.log'
$pythonOutput = Join-Path $evidence 'motor-variant-controller.log'
$godotProcess = Start-Process -FilePath $godot -ArgumentList @('--headless','--path',(Join-Path $root 'godot'),'--','--robot-id=rover-slow-01','--actuation-quick-smoke') -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    $endpointPort = 0
    while ([DateTime]::UtcNow -lt $deadline -and $endpointPort -eq 0) {
        if ($godotProcess.HasExited) { throw 'Selected motor variant exited before Robot Service became ready.' }
        if (Test-Path -LiteralPath $stdout) {
            $log = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            $match = [regex]::Match($log, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
            if ($match.Success) { $endpointPort = [int]$match.Groups[1].Value }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($endpointPort -eq 0) { throw 'Selected motor variant did not expose a UART endpoint.' }
    $pythonProcess = Start-Process -FilePath $python -ArgumentList @($controller,'--host','127.0.0.1','--port',$endpointPort.ToString(),'--timeout','8','--motor','50','50','--motor-duration','5') -PassThru -Wait -NoNewWindow -RedirectStandardOutput $pythonOutput -RedirectStandardError (Join-Path $evidence 'motor-variant-controller.stderr.log')
    if ($pythonProcess.ExitCode -ne 0) { Get-Content $pythonOutput; throw 'Firmware did not accept the equal 50% motor command.' }
    if (-not $godotProcess.WaitForExit(25000)) { throw 'Selected motor variant did not complete its physics smoke.' }
} finally {
    if (-not $godotProcess.HasExited) { Stop-Process -Id $godotProcess.Id -Force }
}

$log = Get-Content $stdout -Raw
if ($log -notmatch 'ROBOT_BUILT id=rover-slow-01' -or $log -notmatch 'ACTUATION_SMOKE complete') { throw 'Selected variant did not build or complete the actuation trace.' }
$rows = @(Import-Csv (Join-Path $evidence 'motor-variant-motion.csv') | Where-Object { [math]::Abs([double]$_.left_signed_duty - 0.5) -lt 0.0001 -and [math]::Abs([double]$_.right_signed_duty - 0.5) -lt 0.0001 })
if ($rows.Count -lt 8) { throw "Only $($rows.Count) fixed-step samples were captured at 50% duty." }
$settled = @($rows | Select-Object -Last 12)
$left = ($settled | ForEach-Object { [double]$_.left_wheel_omega_x_radps } | Measure-Object -Average).Average
$right = ($settled | ForEach-Object { [double]$_.right_wheel_omega_x_radps } | Measure-Object -Average).Average
if ($left -lt 9 -or $right -lt 3.5 -or ($left - $right) -lt 3) { throw "Versioned motor profiles did not produce distinct measured movement: left=$left right=$right rad/s." }
"PASS: genuine STM32 PWM 50/50 drove versioned motors at distinct measured speeds: left=$([math]::Round($left,3)) rad/s, right=$([math]::Round($right,3)) rad/s." | Set-Content -Encoding utf8 (Join-Path $evidence 'motor-variant-summary.txt')
Get-Content (Join-Path $evidence 'motor-variant-summary.txt')
