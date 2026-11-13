[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$PackageRoot,
    [int]$DurationSeconds = 240,
    [ValidateRange(18,3600)] [int]$SimulationDurationSeconds = 18,
    [ValidateRange(5701,5706)] [int]$StartSeed = 5701
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/evidence/C01/rover-multi-controller-' + (Get-Date -Format 'yyyyMMddTHHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $PackageRoot) { $PackageRoot = Join-Path $root 'artifacts/release/RoboWorld' }
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$runner = Join-Path $PSScriptRoot 'run-performance-benchmark.ps1'
$python = Join-Path $root '.venv/Scripts/python.exe'
$performanceRoot = Join-Path $root 'artifacts/evidence/PERF'
$firmwarePaths = @{
    'mcu-drive' = Join-Path $PackageRoot 'samples/firmware/stm32-drive/build/firmware.elf'
    'mcu-aux' = Join-Path $PackageRoot 'samples/firmware/stm32-arm/build/firmware.elf'
}
foreach ($firmware in $firmwarePaths.Values) { if (-not (Test-Path -LiteralPath $firmware)) { throw "Packaged controller firmware missing: $firmware" } }
$firmwareHashes = @{}
foreach ($id in $firmwarePaths.Keys) { $firmwareHashes[$id] = (Get-FileHash -LiteralPath $firmwarePaths[$id] -Algorithm SHA256).Hash.ToLowerInvariant() }

$sequences = @(
    [pscustomobject]@{ id='seed-5701-training'; split='training'; seed=5701; commands=@('MOTOR 25 25','MOTOR 50 50','MOTOR -25 -25','MOTOR -50 -50') },
    [pscustomobject]@{ id='seed-5702-training'; split='training'; seed=5702; commands=@('MOTOR 75 50','MOTOR 25 0','MOTOR -75 -50','MOTOR -25 0') },
    [pscustomobject]@{ id='seed-5703-training'; split='training'; seed=5703; commands=@('MOTOR -50 -25','MOTOR 75 75','MOTOR -25 25','MOTOR 50 -50') },
    [pscustomobject]@{ id='seed-5704-training'; split='training'; seed=5704; commands=@('MOTOR 50 75','MOTOR -75 -25','MOTOR 25 25','MOTOR -50 -75') },
    [pscustomobject]@{ id='seed-5705-heldout'; split='heldout'; seed=5705; commands=@('MOTOR 10 60','MOTOR -40 -10','MOTOR 60 -40','MOTOR -10 40') },
    [pscustomobject]@{ id='seed-5706-heldout'; split='heldout'; seed=5706; commands=@('MOTOR -60 10','MOTOR 40 -60','MOTOR -10 -40','MOTOR 60 10') }
)
if ($DurationSeconds -lt 120) { throw 'Exact multi-controller benchmark wall allowance must cover startup and the complete simulated horizon.' }

$profiles = @{}
foreach ($controllerId in @('mcu-drive','mcu-aux')) {
    $directory = Join-Path $OutputDirectory $controllerId
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $profiles[$controllerId] = [Collections.Generic.List[object]]::new()
}
$runManifest = [Collections.Generic.List[object]]::new()
# Reconstruct completed earlier sequences from the bound raw traces and exact
# run metadata so an interrupted corpus can resume without replaying evidence.
foreach ($sequence in $sequences | Where-Object { $_.seed -lt $StartSeed }) {
    $traceName = "$($sequence.id)-controller-characterization.jsonl"
    $driveTrace = Join-Path (Join-Path $OutputDirectory 'mcu-drive') $traceName
    $auxTrace = Join-Path (Join-Path $OutputDirectory 'mcu-aux') $traceName
    if (-not (Test-Path -LiteralPath $driveTrace) -or -not (Test-Path -LiteralPath $auxTrace)) { throw "Cannot resume at seed $StartSeed; completed traces are missing for $($sequence.id)." }
    $traceHash = (Get-FileHash -LiteralPath $driveTrace -Algorithm SHA256).Hash.ToLowerInvariant()
    if ((Get-FileHash -LiteralPath $auxTrace -Algorithm SHA256).Hash.ToLowerInvariant() -ne $traceHash) { throw "Controller traces disagree for $($sequence.id)." }
    $sourceMeta = Get-ChildItem -LiteralPath $performanceRoot -Directory | ForEach-Object {
        $metaPath = Join-Path $_.FullName 'run.meta.json'
        if (Test-Path -LiteralPath $metaPath) {
            $candidate = Get-Content -LiteralPath $metaPath -Raw | ConvertFrom-Json
            if ($candidate.scenario -eq 'two-mcu' -and $candidate.controller_mode -eq 'exact' -and $candidate.characterization_trace_sha256 -eq $traceHash) { $candidate }
        }
    } | Select-Object -First 1
    if (-not $sourceMeta -or $sourceMeta.performance_phase.CommittedSimNs -ne ([long]$SimulationDurationSeconds * [long]1000000000)) { throw "Completed Exact run metadata does not bind the trace for $($sequence.id)." }
    foreach ($controllerId in @('mcu-drive','mcu-aux')) {
        $profiles[$controllerId].Add([pscustomobject]@{
            sequence_id=$sequence.id; split=$sequence.split; seed=$sequence.seed
            commands=$sequence.commands; final_stop='MOTOR 0 0'; controller=$controllerId
            firmware_sha256=$firmwareHashes[$controllerId]; trace=$traceName
            trace_sha256=$traceHash; source_run_id=$sourceMeta.run_id; source_run_trace_sha256=$traceHash
        })
    }
    $runManifest.Add([pscustomobject]@{
        sequence_id=$sequence.id; split=$sequence.split; seed=$sequence.seed; run_id=$sourceMeta.run_id
        run_directory=[IO.Path]::GetRelativePath($root,(Join-Path $performanceRoot $sourceMeta.run_id)).Replace('\','/')
        simulation_duration_ns=$sourceMeta.performance_phase.CommittedSimNs; exact_rtf=$sourceMeta.performance_phase.Rtf
        executable_sha256=$sourceMeta.executable_sha256; package_manifest_sha256=$sourceMeta.package_manifest_sha256
        characterization_trace_sha256=$traceHash; controller_commands=$sequence.commands
    })
}
foreach ($sequence in $sequences | Where-Object { $_.seed -ge $StartSeed }) {
    $runnerLog = Join-Path $OutputDirectory "$($sequence.id)-runner.log"
    $runnerArgs = @{
        Scenario='two-mcu'; DurationSeconds=$DurationSeconds; SimulationDurationSeconds=$SimulationDurationSeconds
        RenderFps=0; ControllerMode='exact'; CaptureCharacterizationTrace=$true
        MotorCommandPattern=$sequence.commands; PackageRoot=$PackageRoot
    }
    $attemptStartedUtc = [DateTime]::UtcNow
    try { $runnerOutput = @(& $runner @runnerArgs 2>&1); $runnerExitCode = $LASTEXITCODE }
    catch { $runnerOutput = @($_ | Out-String); $runnerExitCode = 1 }
    $runnerOutput | Set-Content -LiteralPath $runnerLog -Encoding utf8
    if ($runnerExitCode -ne 0) {
        $partialRun = Get-ChildItem -LiteralPath $performanceRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like '*-two-mcu-*' -and $_.LastWriteTimeUtc -ge $attemptStartedUtc.AddSeconds(-5) } |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($partialRun) {
            $partialMetaPath = Join-Path $partialRun.FullName 'run.meta.json'
            $partialMeta = if (Test-Path -LiteralPath $partialMetaPath) { Get-Content -LiteralPath $partialMetaPath -Raw | ConvertFrom-Json } else { $null }
            $partialTicks = Join-Path $partialRun.FullName 'godot-ticks.csv'
            $lastTick = if (Test-Path -LiteralPath $partialTicks) { Get-Content -LiteralPath $partialTicks -Tail 1 } else { $null }
            $attemptsDirectory = Join-Path $OutputDirectory 'rejected-attempts'
            New-Item -ItemType Directory -Force -Path $attemptsDirectory | Out-Null
            [pscustomobject]@{
                status='REJECTED_INCOMPLETE_EXACT_SEQUENCE'; sequence_id=$sequence.id; attempt_started_utc=$attemptStartedUtc.ToString('o')
                run_id=$partialMeta.run_id; run_directory=[IO.Path]::GetRelativePath($root,$partialRun.FullName).Replace('\','/')
                application_exit_code=$partialMeta.application_exit_code; committed_sim_ns=$partialMeta.performance_phase.CommittedSimNs
                partial_characterization_trace_sha256=$partialMeta.characterization_trace_sha256
                last_godot_tick_csv_row=$lastTick; runner_log=[IO.Path]::GetFileName($runnerLog)
                failure=($runnerOutput -join "`n")
            } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $attemptsDirectory "$($sequence.id)-rejected.json") -Encoding utf8
        }
        throw "$($sequence.id): exact two-controller benchmark failed; rejected run and runner output were retained."
    }
    $completeLine = $runnerOutput | Where-Object { $_ -match '^PERFORMANCE_RUN_COMPLETE path=' } | Select-Object -Last 1
    if (-not $completeLine -or $completeLine -notmatch 'path=(.+?) scenario=two-mcu rtf=([0-9.]+) sim_ns=(\d+)') { throw "$($sequence.id): benchmark did not report a complete exact run." }
    $runDirectory = [IO.Path]::GetFullPath($Matches[1])
    $runRtf = [double]::Parse($Matches[2],[Globalization.CultureInfo]::InvariantCulture)
    $simNs = [long]$Matches[3]
    $tracePath = Join-Path $runDirectory 'controller-characterization.jsonl'
    if ($simNs -ne ([long]$SimulationDurationSeconds * [long]1000000000) -or -not (Test-Path -LiteralPath $tracePath)) { throw "$($sequence.id): incomplete simulation horizon or missing raw controller trace." }
    $runMeta = Get-Content -LiteralPath (Join-Path $runDirectory 'run.meta.json') -Raw | ConvertFrom-Json
    if ($runMeta.controller_mode -ne 'exact') { throw "$($sequence.id): runner metadata did not select exact firmware mode." }
    if ($runManifest.Count -gt 0 -and $runMeta.executable_sha256 -ne $runManifest[0].executable_sha256) { throw 'Release executable changed during the characterization campaign.' }
    foreach ($controllerId in @('mcu-drive','mcu-aux')) {
        $traceName = "$($sequence.id)-controller-characterization.jsonl"
        $destination = Join-Path (Join-Path $OutputDirectory $controllerId) $traceName
        Copy-Item -LiteralPath $tracePath -Destination $destination -Force
        $profiles[$controllerId].Add([pscustomobject]@{
            sequence_id=$sequence.id; split=$sequence.split; seed=$sequence.seed
            commands=$sequence.commands; final_stop='MOTOR 0 0'; controller=$controllerId
            firmware_sha256=$firmwareHashes[$controllerId]; trace=$traceName
            trace_sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
            source_run_id=$runMeta.run_id; source_run_trace_sha256=$runMeta.characterization_trace_sha256
        })
    }
    $runManifest.Add([pscustomobject]@{
        sequence_id=$sequence.id; split=$sequence.split; seed=$sequence.seed; run_id=$runMeta.run_id
        run_directory=[IO.Path]::GetRelativePath($root,$runDirectory).Replace('\','/')
        simulation_duration_ns=$simNs; exact_rtf=$runRtf; executable_sha256=$runMeta.executable_sha256
        package_manifest_sha256=$runMeta.package_manifest_sha256
        characterization_trace_sha256=$runMeta.characterization_trace_sha256
        controller_commands=$sequence.commands
    })
    foreach ($controllerId in @('mcu-drive','mcu-aux')) {
        $profiles[$controllerId] | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path (Join-Path $OutputDirectory $controllerId) 'sequence-manifest.json') -Encoding utf8
    }
    $runManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'sequence-manifest.json') -Encoding utf8
    Write-Output "MULTI_CONTROLLER_CHARACTERIZATION_SEQUENCE_PASS id=$($sequence.id) exact_rtf=$($runRtf.ToString('F6',[Globalization.CultureInfo]::InvariantCulture))"
}

foreach ($controllerId in @('mcu-drive','mcu-aux')) {
    $manifestPath = Join-Path (Join-Path $OutputDirectory $controllerId) 'sequence-manifest.json'
    $profiles[$controllerId] | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}
$runManifestPath = Join-Path $OutputDirectory 'sequence-manifest.json'
$runManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $runManifestPath -Encoding utf8
foreach ($controllerId in @('mcu-drive','mcu-aux')) {
    $fitOutput = Join-Path (Join-Path $OutputDirectory $controllerId) 'peripheral-fit-UNQUALIFIED.json'
    & $python (Join-Path $root 'tools/fit-motor-peripheral-response.py') (Join-Path $OutputDirectory $controllerId) --output $fitOutput
    if ($LASTEXITCODE -ne 0) { throw "${controllerId}: raw held-out peripheral fit failed; candidate remains unqualified." }
}
Write-Output "MULTI_CONTROLLER_CHARACTERIZATION_COMPLETE path=$OutputDirectory firmware_drive=$($firmwareHashes['mcu-drive']) firmware_aux=$($firmwareHashes['mcu-aux'])"
