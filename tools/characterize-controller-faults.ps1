param(
    [int[]]$Seeds = @(6401, 6402, 6403, 6404),
    [string]$OutputDirectory = 'artifacts/evidence/C01/controller-fault-campaign-20261109T',
    [long]$FaultAtSimTimeNs = 1500000000
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$OutputDirectory = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if ($Seeds.Count -lt 4 -or $Seeds.Count % 2 -ne 0 -or $FaultAtSimTimeNs -le 0) {
    throw 'Fault campaigns require an even seed count of at least four and a positive simulation-time boundary.'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
function Stop-ProbeProcessTree([System.Diagnostics.Process]$Probe) {
    if ($Probe -and -not $Probe.HasExited) { $Probe.Kill($true); $Probe.WaitForExit() }
}
function Get-StableSha256([string]$Path) {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try { return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path -ErrorAction Stop).Hash }
        catch { if ($attempt -eq 29) { throw }; Start-Sleep -Milliseconds 100 }
    }
}
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$manifest = @()
$sequenceIndex = 0
$motorLevels = @(-100, -75, -50, -25, 0, 25, 50, 75, 100)
foreach ($faultType in @('route-fault', 'mcu-halt')) {
    foreach ($seed in $Seeds) {
        $split = if ($sequenceIndex -lt ($Seeds.Count / 2)) { 'training' } else { 'heldout' }
        $id = "seed-$seed-$split-$faultType"
        $sequenceIndex++
        $sequenceDirectory = Join-Path $OutputDirectory $id
        New-Item -ItemType Directory -Force -Path $sequenceDirectory | Out-Null
        $trace = Join-Path $sequenceDirectory 'controller-fault-trace.jsonl'
        $stdout = Join-Path $sequenceDirectory 'godot.log'
        $stderr = Join-Path $sequenceDirectory 'godot.stderr.log'
        $controllerOutput = Join-Path $sequenceDirectory 'controller.log'
        $controllerError = Join-Path $sequenceDirectory 'controller.stderr.log'
        $sequenceStarted = [DateTime]::UtcNow
        $process = $null
        try {
            $smokeFlag = if ($faultType -eq 'route-fault') { '--actuation-fault-smoke' } else { '--actuation-halt-smoke' }
            $faultFlag = if ($faultType -eq 'route-fault') { "--test-route-fault-at-sim-time-ns=$FaultAtSimTimeNs" } else { "--test-mcu-halt-at-sim-time-ns=$FaultAtSimTimeNs" }
            $arguments = @('--headless', '--path', (Join-Path $root 'godot'), '--', $smokeFlag, $faultFlag,
                "--characterization-trace=$trace", '--evidence-task=C01')
            if ($faultType -eq 'mcu-halt') {
                $arguments += '--require-clock-verification'
                $arguments += '--test-actuation-smoke-duration-seconds=10'
                $arguments += '--test-actuation-smoke-clock-ns=2500000000'
            }
            $process = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
            $deadline = [DateTime]::UtcNow.AddSeconds(45)
            $port = 0
            while ([DateTime]::UtcNow -lt $deadline -and $port -eq 0) {
                if ($process.HasExited) { throw "${id}: Godot exited before Robot Service became ready; inspect $stderr" }
                $rawLog = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
                $match = [regex]::Match($rawLog, 'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
                if ($match.Success) { $port = [int]$match.Groups[1].Value } else { Start-Sleep -Milliseconds 100 }
            }
            if ($port -eq 0) { throw "${id}: timed out waiting for the exact Renode UART endpoint." }
            $rng = [Random]::new($seed)
            $commands = @()
            for ($commandIndex = 0; $commandIndex -lt 4; $commandIndex++) {
                $left = $motorLevels[$rng.Next($motorLevels.Length)]
                $right = $motorLevels[$rng.Next($motorLevels.Length)]
                $commands += "$left,$right"
            }
            $commands += '50,50'
            $controllerArguments = @($controller, '--host', '127.0.0.1', '--port', $port.ToString(), '--timeout', '8', '--motor-sequence-delay', '0.1')
            $controllerArguments += @($commands | ForEach-Object { "--motor-sequence=$($_)" })
            $motor = Start-Process -FilePath $python -ArgumentList $controllerArguments `
                -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerOutput -RedirectStandardError $controllerError
            if ($motor.ExitCode -ne 0 -or (Get-Content -LiteralPath $controllerOutput -Raw) -notmatch 'Motor 50,50 reply: OK MOTOR') {
                throw "${id}: exact firmware did not acknowledge the final MOTOR 50 50 command."
            }
            $faultMarker = if ($faultType -eq 'route-fault') { 'ACTUATION_FAULT reason=PWM_GPIO_AF_MISMATCH_PA6_PA7' } else { 'TEST_MCU_HALTED .*sim_time_ns=' }
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            do {
                if ($process.HasExited -and $faultType -eq 'route-fault') { throw "${id}: Godot exited before the route fault was observed." }
                $rawLog = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
                if ($rawLog -match $faultMarker) { break }
                Start-Sleep -Milliseconds 100
            } while ([DateTime]::UtcNow -lt $deadline)
            if ($rawLog -notmatch $faultMarker) { throw "${id}: expected injected/observed fault was not logged." }
            $pingNoReply = $null
            if ($faultType -eq 'mcu-halt') {
                $client = [System.Net.Sockets.TcpClient]::new()
                try {
                    $client.Connect('127.0.0.1', $port)
                    $stream = $client.GetStream(); $stream.ReadTimeout = 750
                    $request = [Text.Encoding]::ASCII.GetBytes("PING`n")
                    $stream.Write($request, 0, $request.Length)
                    $buffer = [byte[]]::new(128)
                    try { $read = $stream.Read($buffer, 0, $buffer.Length); $pingNoReply = $read -eq 0 }
                    catch [System.IO.IOException] { $pingNoReply = $true }
                } finally { $client.Dispose() }
                if (-not $pingNoReply) { throw "${id}: halted firmware replied to PING." }
            }
            if ($faultType -eq 'route-fault') {
                if ($rawLog -notmatch 'ROBOT_SERVICE_ERROR .*PWM_GPIO_AF_MISMATCH_PA6_PA7' -or $rawLog -notmatch 'ACTUATION_FAULT .*signed_duty=0\.000') {
                    throw "${id}: route-fault diagnostic did not reset Godot actuation."
                }
                if (-not $process.WaitForExit(1000)) {
                    Stop-ProbeProcessTree $process
                }
            } elseif (-not $process.WaitForExit(15000)) {
                Stop-ProbeProcessTree $process
            }
            $rawLog = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
            $trajectory = $null
            $trajectoryHash = $null
            if ($faultType -eq 'mcu-halt') {
                $sharedTrajectory = Join-Path $root 'artifacts/evidence/C01/godot-actuation-smoke.csv'
                $trajectoryExists = Test-Path -LiteralPath $sharedTrajectory
                $trajectoryFresh = $false
                if ($trajectoryExists) {
                    $trajectoryFile = Get-Item -LiteralPath $sharedTrajectory
                    $trajectoryFresh = $trajectoryFile.LastWriteTimeUtc -ge $sequenceStarted
                }
                if (($rawLog -notmatch 'ACTUATION_SMOKE complete') -or (-not $trajectoryExists) -or (-not $trajectoryFresh)) {
                    throw "${id}: Godot did not produce a fresh post-halt wheel-response trajectory."
                }
                $trajectory = Join-Path $id 'godot-actuation-smoke.csv'
                $sequenceTrajectory = Join-Path $sequenceDirectory 'godot-actuation-smoke.csv'
                Copy-Item -LiteralPath $sharedTrajectory -Destination $sequenceTrajectory -Force
                $trajectoryHash = Get-StableSha256 $sequenceTrajectory
            }
            $traceHash = Get-StableSha256 $trace
            $manifest += [pscustomobject]@{
                sequence_id = $id; split = $split; seed = $seed; fault_type = $faultType
                motor_commands = $commands
                requested_fault_sim_time_ns = $FaultAtSimTimeNs
                firmware_sha256 = Get-StableSha256 (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')
                trace = (Join-Path $id 'controller-fault-trace.jsonl'); trace_sha256 = $traceHash
                godot_log = (Join-Path $id 'godot.log'); godot_log_sha256 = Get-StableSha256 $stdout
                controller_log = (Join-Path $id 'controller.log'); controller_log_sha256 = Get-StableSha256 $controllerOutput
                godot_trajectory = $trajectory; godot_trajectory_sha256 = $trajectoryHash
                post_halt_ping_no_reply = $pingNoReply
            }
            Write-Host "FAULT_SEQUENCE_PASS id=$id trace_sha256=$traceHash"
        } finally {
            Stop-ProbeProcessTree $process
        }
    }
    $sequenceIndex = 0
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'sequence-manifest.json')
Write-Host "FAULT_CAMPAIGN_COMPLETE count=$($manifest.Count) path=$OutputDirectory"
