$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$renode = Get-ChildItem (Join-Path $root '.toolchain/renode') -Filter renode.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $renode) { throw 'Renode portable runtime is missing from .toolchain/renode.' }
$script = Join-Path (Split-Path -Parent $renode) 'scripts/single-node/stm32f4_discovery.resc'
if (-not (Test-Path $script)) { throw "Renode STM32F4 Discovery script missing: $script" }
& $renode --disable-xwt --plain --port -1 --execute q $script
exit $LASTEXITCODE
