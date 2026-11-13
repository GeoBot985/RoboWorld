param([int[]]$Seeds = @(5201,5202,5203,5204,5205,5206), [string]$OutputDirectory, [switch]$IncludeLineBoundaryProbe, [switch]$IncludeUartGrammarProbe)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root ('artifacts/evidence/C01/rover-' + (Get-Date -Format 'yyyyMMddTHHmmss')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$controller = Join-Path $root 'samples/controller/main.py'
$manifest = @()
$pool = @(-100,-75,-50,-25,-10,0,10,25,50,75,100)
$sequenceIndex = 0
foreach ($seed in $Seeds) {
  $split = if ($sequenceIndex -ge ($Seeds.Count - 2)) { 'heldout' } else { 'training' }
  $sequenceIndex++
  $id = "seed-$seed-$split"
  $trace = Join-Path $OutputDirectory "$id.jsonl"
  $stdout = Join-Path $OutputDirectory "$id-godot.log"
  $output = Join-Path $OutputDirectory "$id-controller.log"
  $app = $null
  try {
    $argsApp = @('--headless','--path',(Join-Path $root 'godot'),'--','--actuation-smoke',"--characterization-trace=$trace")
    $app = Start-Process $godot -ArgumentList $argsApp -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError (Join-Path $OutputDirectory "$id-stderr.log")
    $port = 0
    $until = [DateTime]::UtcNow.AddSeconds(45)
    while (-not $port -and [DateTime]::UtcNow -lt $until) {
      if ($app.HasExited) { throw "${id}: Godot exited before UART ready." }
      if (Test-Path $stdout) {
        $rawLog = Get-Content $stdout -Raw
        if ($rawLog) {
          $m = [regex]::Match($rawLog,'ROBOT_SERVICE_ENDPOINT .*uart=tcp://127\.0\.0\.1:(\d+)')
          if ($m.Success) { $port = [int]$m.Groups[1].Value }
        }
      }
      Start-Sleep -Milliseconds 100
    }
    if (-not $port) { throw "${id}: UART endpoint timeout." }
    $rng = [Random]::new($seed)
    $seq = @(); for ($n=0; $n -lt 12; $n++) { $seq += "$($pool[$rng.Next($pool.Length)]),$($pool[$rng.Next($pool.Length)])" }
    $argsPy = @($controller,'--port',"$port",'--timeout','8','--probe-move-bypass','--uart-overflow-probe','--sensor-count','4','--sensor-interval','0.25','--motor-sequence-delay','1.0') + @($seq | ForEach-Object { "--motor-sequence=$_" })
    if ($IncludeLineBoundaryProbe) { $argsPy += '--uart-line-boundary-probe' }
    if ($IncludeUartGrammarProbe) { $argsPy += '--uart-grammar-probe' }
    $probe = Start-Process $python -ArgumentList $argsPy -PassThru -Wait -NoNewWindow -RedirectStandardOutput $output -RedirectStandardError (Join-Path $OutputDirectory "$id-controller-stderr.log")
    if ($probe.ExitCode -ne 0) { throw "${id}: controller stimulus failed." }
    if (-not $app.WaitForExit(60000)) { throw "${id}: Godot window timeout." }
    if (-not (Test-Path $trace)) { throw "${id}: trace missing." }
    $log = Get-Content $output -Raw
    $boundaryCount = ([regex]::Matches($log,'UART_BOUNDARY payload_bytes=\d+ reply=')).Count
    $grammarCount = ([regex]::Matches($log,'UART_GRAMMAR request_hex=')).Count
    if (([regex]::Matches($log,'Motor -?\d+,-?\d+ reply: OK MOTOR')).Count -ne 12 -or $log -notmatch 'UART overflow reply: ERR LINE' -or ([regex]::Matches($log,'Sensor reply \d+: DIST mm=')).Count -ne 4 -or ($IncludeLineBoundaryProbe -and $boundaryCount -ne 2) -or ($IncludeUartGrammarProbe -and $grammarCount -ne 5)) { throw "${id}: response coverage incomplete." }
    $manifest += [pscustomobject]@{sequence_id=$id; split=$split; seed=$seed; commands=$seq; line_boundary_probe=[bool]$IncludeLineBoundaryProbe; uart_grammar_probe=[bool]$IncludeUartGrammarProbe; firmware_sha256=(Get-FileHash (Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf')).Hash; trace=[IO.Path]::GetFileName($trace); trace_sha256=(Get-FileHash $trace).Hash}
    Write-Host "C01_SEQUENCE_PASS id=$id"
  } finally { if ($app -and -not $app.HasExited) { Stop-Process $app.Id -Force; $app.WaitForExit() } }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 (Join-Path $OutputDirectory 'sequence-manifest.json')
Write-Host "C01_CAMPAIGN_COMPLETE count=$($manifest.Count) path=$OutputDirectory"
