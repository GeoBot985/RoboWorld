$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $exe) { throw 'Godot 4.7.2 .NET editor is missing from .toolchain/godot.' }
$env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
$env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
$env:PATH = "$($env:DOTNET_ROOT);$env:PATH"
& $exe --path (Join-Path $root 'godot')
exit $LASTEXITCODE
