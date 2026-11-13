$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/RS01A'
$null = New-Item -ItemType Directory -Path $evidence -Force
$env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
$dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName

& $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --no-restore --nologo *> (Join-Path $evidence 'build.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'build.log'); throw 'RS-01A Godot build failed.' }

& $godot --headless --path (Join-Path $root 'godot') -- --new-empty-robot --authoring-smoke *> (Join-Path $evidence 'authoring-smoke.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'authoring-smoke.log'); throw 'RS-01A authoring smoke failed.' }
$smoke = Get-Content (Join-Path $evidence 'authoring-smoke.log') -Raw
if ($smoke -match '(?m)^ERROR:') { Get-Content (Join-Path $evidence 'authoring-smoke.log'); throw 'RS-01A authoring smoke logged a Godot runtime error.' }
if ($smoke -notmatch 'AUTHORING_DRAFT_SMOKE empty_roundtrip=PASS failed_run_preserves_draft=PASS add_remove_root=PASS partial_roundtrip=PASS valid_draft_roundtrip=PASS starter_launch_validation=PASS new_assembly_launch_validation=PASS package_materialization=PASS runtime_launch_ready=PASS editor_add_remove_save_reopen_run=PASS') {
    throw 'RS-01A authoring evidence is incomplete.'
}

& $godot --headless --path (Join-Path $root 'godot') -- --design-smoke *> (Join-Path $evidence 'existing-project-smoke.log')
if ($LASTEXITCODE -ne 0) { Get-Content (Join-Path $evidence 'existing-project-smoke.log'); throw 'Existing project compatibility smoke failed.' }
if ((Get-Content (Join-Path $evidence 'existing-project-smoke.log') -Raw) -notmatch 'PROJECT_LOADED .*robot=rover-01') {
    throw 'Existing project compatibility evidence is missing the default robot load.'
}

Write-Host 'RS-01A draft lifecycle smoke and existing project load compatibility: PASS'
