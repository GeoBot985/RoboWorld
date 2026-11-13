param(
    [Parameter(Mandatory = $true)]
    [string]$ReplayDirectory,
    [long]$EpochSimTimeNs = 10000000,
    [ValidateSet('project','exact','fast','auto')]
    [string]$ControllerMode = 'project'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$ReplayDirectory = [IO.Path]::GetFullPath((Join-Path $root $ReplayDirectory))
$godotProject = Join-Path $root 'godot'
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$buildLog = Join-Path $ReplayDirectory 'godot-replay-build.log'
if ($EpochSimTimeNs -lt 0) { throw 'The replay epoch must be a non-negative simulation timestamp.' }

& $dotnet build (Join-Path $godotProject 'RoboWorld.Godot.csproj') -c Debug --no-restore *> $buildLog
if ($LASTEXITCODE -ne 0) { Get-Content $buildLog -Tail 40; throw "Godot replay build failed with exit $LASTEXITCODE." }
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
$exactEvents = Import-Csv -LiteralPath (Join-Path $ReplayDirectory 'exact-actuation.csv')
$modelEvents = Import-Csv -LiteralPath (Join-Path $ReplayDirectory 'model-actuation.csv')
if ($exactEvents.Count -lt 2 -or $modelEvents.Count -lt 2) { throw 'Exact and model actuator traces must both contain at least two states.' }
$lastActuatorEventNs = [Math]::Max([long]$exactEvents[-1].sim_time_ns, [long]$modelEvents[-1].sim_time_ns)
$replayEndSimTimeNs = [long]($lastActuatorEventNs + 2500000000)
foreach ($mode in @('exact', 'model')) {
    $inputPath = Join-Path $ReplayDirectory "$mode-actuation.csv"
    $trajectoryPath = Join-Path $ReplayDirectory "godot-$mode-trajectory.csv"
    $logPath = Join-Path $ReplayDirectory "godot-$mode.log"
    if (-not (Test-Path -LiteralPath $inputPath)) { throw "Missing $mode actuator replay input: $inputPath" }
    $godotArguments = @('--headless','--path',$godotProject,'--',"--actuation-replay=$inputPath",
        "--actuation-replay-output=$trajectoryPath","--actuation-replay-epoch-ns=$EpochSimTimeNs",
        "--actuation-replay-end-sim-time-ns=$replayEndSimTimeNs")
    if ($ControllerMode -ne 'project') { $godotArguments += "--controller-mode=$ControllerMode" }
    & $godot @godotArguments *> $logPath
    if ($LASTEXITCODE -ne 0) { Get-Content $logPath -Tail 40; throw "Godot $mode replay failed with exit $LASTEXITCODE." }
    $log = Get-Content -LiteralPath $logPath -Raw
    if (($log -notmatch 'ACTUATION_REPLAY complete') -or ($log -notmatch 'worker=disabled') -or ($log -match 'ROBOT_SERVICE_ENDPOINT READY'))
    { throw "Godot $mode replay did not complete in offline mode." }
    if (-not (Test-Path -LiteralPath $trajectoryPath)) { throw "Godot $mode replay did not create a trajectory." }
    $samples = Import-Csv -LiteralPath $trajectoryPath
    if ($samples.Count -lt 20) { throw "Godot $mode replay produced only $($samples.Count) trajectory samples." }
    Write-Host "GODOT_ACTUATION_REPLAY_PASS mode=$mode samples=$($samples.Count) output=$trajectoryPath"
}
Get-Content $buildLog -Tail 6
