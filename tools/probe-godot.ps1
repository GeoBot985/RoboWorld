$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$($env:DOTNET_ROOT);$env:PATH"
$exe = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Godot .NET console editor is missing.' }
Push-Location $root
try {
    & $exe --headless --editor --path godot --quit
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $export = Join-Path $root 'artifacts/evidence/T00/RoboWorldSmoke.exe'
    if (Test-Path $export) { Remove-Item -LiteralPath $export -Force }
    $log = Join-Path $root 'artifacts/evidence/T00/godot-export.log'
    & $exe --headless --path godot --export-release 'Windows Desktop' $export *> $log
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (Select-String -Path $log -Pattern '^ERROR:' -Quiet) { throw 'Godot reported an export error; see artifacts/evidence/T00/godot-export.log.' }
    if (-not (Test-Path $export)) { throw 'Godot export returned success but output executable is missing.' }
} finally {
    Pop-Location
}
