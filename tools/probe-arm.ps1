$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gcc = Get-ChildItem (Join-Path $root '.toolchain/arm') -Filter arm-none-eabi-gcc.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
$gdb = Get-ChildItem (Join-Path $root '.toolchain/arm') -Filter arm-none-eabi-gdb.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $gcc -or -not $gdb) { throw 'Arm GNU Toolchain compiler/debugger missing.' }
Push-Location $root
try {
    & $gcc --version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $gdb --version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $gcc -mcpu=cortex-m4 -mthumb -ffreestanding -Wall -Wextra -Werror -c firmware/t00_compile_smoke.c -o artifacts/evidence/T00/t00_compile_smoke.o
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} finally {
    Pop-Location
}
