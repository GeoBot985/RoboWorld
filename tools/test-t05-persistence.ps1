$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T05'
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$package = Join-Path $evidence 'design-smoke.roboworld'
$saveLog = Join-Path $evidence 'design-save.log'
$openLog = Join-Path $evidence 'design-open.log'
& $godot -headless --path (Join-Path $root 'godot') -- --design-smoke "--save-project=$package" 2>&1 | Set-Content -Encoding utf8 $saveLog
if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw $saveLog) -notmatch 'PROJECT_SAVED id=roboworld-starter') { throw 'Design package save failed.' }
& $godot -headless --path (Join-Path $root 'godot') -- "--project=$package" --design-smoke 2>&1 | Set-Content -Encoding utf8 $openLog
if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw $openLog) -notmatch 'PROJECT_LOADED id=roboworld-starter .*firmware_sha256=5d5973a2e119167ef3c2ef18e8929e8ad9735ad65a63b639179256547575b6e9 robot=rover-01 world=room-20x20 components=10') { throw 'Saved project did not reopen with its design and firmware assignment.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$work = Join-Path $evidence 'negative-package-work'
$fullEvidence = [IO.Path]::GetFullPath($evidence).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fullWork = [IO.Path]::GetFullPath($work)
if (-not $fullWork.StartsWith($fullEvidence, [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved negative-test work directory escaped T05 evidence.' }
if (Test-Path $fullWork) { Remove-Item -LiteralPath $fullWork -Recurse -Force }
[System.IO.Compression.ZipFile]::ExtractToDirectory($package, $work)
$lockPath = Join-Path $work 'project.lock.json'
$lockText = Get-Content -Raw $lockPath
$lockText = $lockText.Replace('"worlds/room-20x20.world.json": "fa782', '"worlds/room-20x20.world.json": "aa782')
if ($lockText -notmatch '"worlds/room-20x20\.world\.json": "aa782') { throw 'Could not mutate the world dependency hash for the negative check.' }
Set-Content -Encoding utf8 -NoNewline -LiteralPath $lockPath -Value $lockText
$invalidHashPackage = Join-Path $evidence 'invalid-hash.roboworld'
if (Test-Path $invalidHashPackage) { Remove-Item -LiteralPath $invalidHashPackage -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($work, $invalidHashPackage)
$hashLog = Join-Path $evidence 'invalid-hash-open.log'
& $godot -headless --path (Join-Path $root 'godot') -- "--project=$invalidHashPackage" --design-smoke 2>&1 | Set-Content -Encoding utf8 $hashLog
if ((Get-Content -Raw $hashLog) -notmatch 'PROJECT_LOAD_FAILED Project dependency hash mismatch: worlds/room-20x20\.world\.json') { throw 'Modified project lock hash was not rejected with a clear dependency error.' }

Remove-Item -LiteralPath $fullWork -Recurse -Force
[System.IO.Compression.ZipFile]::ExtractToDirectory($package, $work)
$robotPath = Join-Path $work 'robots/rover-01.robot.json'
$robotText = (Get-Content -Raw $robotPath).Replace('"catalogue_version": "1.0.0"', '"catalogue_version": "9.9.9"')
Set-Content -Encoding utf8 -NoNewline -LiteralPath $robotPath -Value $robotText
$staleComponentPackage = Join-Path $evidence 'stale-component.roboworld'
if (Test-Path $staleComponentPackage) { Remove-Item -LiteralPath $staleComponentPackage -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($work, $staleComponentPackage)
$componentLog = Join-Path $evidence 'stale-component-open.log'
& $godot -headless --path (Join-Path $root 'godot') -- "--project=$staleComponentPackage" --design-smoke 2>&1 | Set-Content -Encoding utf8 $componentLog
if ((Get-Content -Raw $componentLog) -notmatch 'PROJECT_LOAD_FAILED Component .* requires rw\.chassis\.rect@9\.9\.9, found 1\.0\.0') { throw 'Stale component version was not rejected with an actionable error.' }

$manifestPath = Join-Path $work 'project.json'
$manifest = Get-Content -Raw $manifestPath
$manifest = $manifest.Replace('"schema_version": "1.0"', '"schema_version": "9.0"')
Set-Content -Encoding utf8 -NoNewline -LiteralPath $manifestPath -Value $manifest
$unsupportedPackage = Join-Path $evidence 'unsupported-schema.roboworld'
if (Test-Path $unsupportedPackage) { Remove-Item -LiteralPath $unsupportedPackage -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($work, $unsupportedPackage)
$schemaLog = Join-Path $evidence 'unsupported-schema-open.log'
& $godot -headless --path (Join-Path $root 'godot') -- "--project=$unsupportedPackage" --design-smoke 2>&1 | Set-Content -Encoding utf8 $schemaLog
if ((Get-Content -Raw $schemaLog) -notmatch 'PROJECT_LOAD_FAILED Unsupported project schema version; expected 1\.0\.') { throw 'Unsupported project schema was not rejected clearly.' }
Remove-Item -LiteralPath $work -Recurse -Force
'Design save/reopen, dependency hash rejection, stale component rejection, and schema version rejection: PASS' | Set-Content -Encoding utf8 (Join-Path $evidence 'persistence-summary.txt')
Get-Content (Join-Path $evidence 'persistence-summary.txt')
