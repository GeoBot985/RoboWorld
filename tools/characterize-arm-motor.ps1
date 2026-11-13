[CmdletBinding()]
param(
    [int[]]$Seeds = @(5601,5602,5603,5604,5605,5606),
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ($Seeds.Count -lt 4) { throw 'Arm motor characterization requires at least four complete sequences so whole-sequence training and held-out splits are meaningful.' }
if (@($Seeds | Select-Object -Unique).Count -ne $Seeds.Count) { throw 'Arm motor characterization seeds must be unique.' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/evidence/C01/arm-motor-' + (Get-Date -Format 'yyyyMMddTHHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$firmware = Join-Path $root 'samples/firmware/stm32-arm/build/firmware.elf'
if (-not (Test-Path -LiteralPath $godot) -or -not (Test-Path -LiteralPath $firmware)) { throw 'Pinned Godot runtime or arm firmware ELF is missing.' }
$firmwareHash = (Get-FileHash -LiteralPath $firmware -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $Seeds.Count; $index++) {
    $seed = $Seeds[$index]
    $split = if ($index -ge $Seeds.Count - 2) { 'heldout' } else { 'training' }
    $id = "seed-$seed-$split"
    $rng = [Random]::new($seed)
    $positive = @(25,50,75)[$rng.Next(3)]
    $reverse = -@(25,50,75)[$rng.Next(3)]
    $rightValues = @(-75,-25,0,25,75)
    $commands = @("$positive,$($rightValues[$rng.Next($rightValues.Length)])", "$reverse,$($rightValues[$rng.Next($rightValues.Length)])", "75,$($rightValues[$rng.Next($rightValues.Length)])")
    $trace = Join-Path $OutputDirectory "$id.jsonl"
    $stdout = Join-Path $OutputDirectory "$id-godot.log"
    $stderr = Join-Path $OutputDirectory "$id-godot-stderr.log"
    $controllerLog = Join-Path $OutputDirectory "$id-controller.log"
    $controllerError = Join-Path $OutputDirectory "$id-controller-stderr.log"
    $arguments = @('--headless','--path',(Join-Path $root 'godot'),'--','--robot-id=arm-01','--arm-actuation-smoke',"--characterization-trace=$trace")
    $app = Start-Process -FilePath $godot -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    try {
        $port = 0
        $deadline = [DateTime]::UtcNow.AddSeconds(45)
        while (-not $port -and [DateTime]::UtcNow -lt $deadline) {
            if ($app.HasExited) { throw "${id}: Godot exited before the arm UART endpoint was ready." }
            if (Test-Path -LiteralPath $stdout) {
                $log = [string](Get-Content -LiteralPath $stdout -Raw -ErrorAction SilentlyContinue)
                $match = [regex]::Match($log,'UART_ENDPOINT controller_id=arm-controller .*uart=tcp://127\.0\.0\.1:(\d+)')
                if ($match.Success) { $port = [int]$match.Groups[1].Value }
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $port) { throw "${id}: arm UART endpoint timeout." }
        $pythonArguments = @($controller,'--port',"$port",'--timeout','8',"--motor-sequence=$($commands[0])","--motor-sequence=$($commands[1])","--motor-sequence=$($commands[2])",'--motor-sequence-delay','0.35','--motor-sequence-final-hold-seconds','0.25')
        $client = Start-Process -FilePath $python -ArgumentList $pythonArguments -PassThru -Wait -NoNewWindow -RedirectStandardOutput $controllerLog -RedirectStandardError $controllerError
        if ($client.ExitCode -ne 0) { throw "${id}: Python UART sequence failed; inspect $controllerError." }
        if (-not $app.WaitForExit(45000)) { throw "${id}: firmware-driven arm smoke did not complete." }
        if ($app.ExitCode -ne 0) { throw "${id}: Godot arm smoke exited with code $($app.ExitCode)." }
        $godotLog = Get-Content -LiteralPath $stdout -Raw
        $godotError = Get-Content -LiteralPath $stderr -Raw -ErrorAction SilentlyContinue
        $clientText = Get-Content -LiteralPath $controllerLog -Raw
        if ($godotError -match 'SCRIPT ERROR|ERROR:') { throw "${id}: Godot reported an engine/script error." }
        if ($clientText -notmatch '(?m)^UART reply: PONG [0-9a-f]{8}\r?$' -or ([regex]::Matches($clientText,'Motor -?\d+,-?\d+ reply: OK MOTOR')).Count -ne 3 -or $clientText -notmatch 'Motor sequence stop reply: OK MOTOR') { throw "${id}: expected UART PING, three MOTOR responses and terminal stop were not observed." }
        if ($godotLog -notmatch 'ARM_ACTUATION_SMOKE complete actuator=arm-motor peak_duty=0\.750 angle_rad=([0-9.]+)') { throw "${id}: expected firmware-driven arm smoke completion was missing." }
        $angle = [double]::Parse($Matches[1],[Globalization.CultureInfo]::InvariantCulture)
        $manifest.Add([pscustomobject]@{
            sequence_id=$id; split=$split; seed=$seed; commands=$commands; final_stop='0,0'
            robot_id='arm-01'; controller_id='arm-controller'; firmware_sha256=$firmwareHash
            trace="$id.jsonl"; trace_sha256=(Get-FileHash -LiteralPath $trace -Algorithm SHA256).Hash.ToLowerInvariant()
            godot_stdout="$id-godot.log"; godot_stdout_sha256=(Get-FileHash -LiteralPath $stdout -Algorithm SHA256).Hash.ToLowerInvariant()
            controller_log="$id-controller.log"; controller_log_sha256=(Get-FileHash -LiteralPath $controllerLog -Algorithm SHA256).Hash.ToLowerInvariant()
            measured_shoulder_angle_rad=$angle
        })
        Write-Output "ARM_CHARACTERIZATION_SEQUENCE_PASS id=$id angle_rad=$($angle.ToString('F4',[Globalization.CultureInfo]::InvariantCulture))"
    } finally {
        if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force; $app.WaitForExit() }
    }
}
$manifestPath = Join-Path $OutputDirectory 'sequence-manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "ARM_MOTOR_CHARACTERIZATION_COMPLETE count=$($manifest.Count) path=$OutputDirectory manifest_sha256=$((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant())"
