param(
    [int]$UartPort = 45123,
    [int]$MonitorPort = 45124,
    [int]$GdbPort = 3333,
    [string]$ElfPath = (Join-Path $PSScriptRoot 'build/firmware.elf'),
    [switch]$PrepareOnly,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$renode = Get-ChildItem (Join-Path $root '.toolchain/renode') -Filter renode.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
if (-not $renode) { throw 'Pinned Renode executable not found.' }
if ($Build) { & (Join-Path $PSScriptRoot 'build.ps1'); if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE } }
$elf = [System.IO.Path]::GetFullPath($ElfPath)
if (-not (Test-Path -LiteralPath $elf)) { throw "Firmware ELF missing: $elf" }
$stock = Join-Path (Split-Path -Parent $renode) 'scripts/single-node/stm32f4_discovery.resc'
if (-not (Test-Path $stock)) { throw "Renode STM32F4 Discovery platform script missing: $stock" }
$firmwarePath = $elf.Replace('\', '/')
$stockPath = $stock.Replace('\', '/')
$resc = Join-Path $PSScriptRoot 'build/roboworld-stm32f4.resc'
@(
    ":name: RoboWorld STM32F4 firmware UART proof"
    "`$bin?=@$firmwarePath"
    "include @$stockPath"
    "emulation CreateServerSocketTerminal $UartPort `"roboworld_uart`" false"
    'connector Connect sysbus.uart4 roboworld_uart'
    "machine StartGdbServer $GdbPort"
    'start'
) | Set-Content -Encoding ascii -LiteralPath $resc
Write-Host "Renode binary: $renode"
Write-Host "Firmware ELF: $elf"
Write-Host "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $elf).Hash)"
Write-Host "UART TCP port: $UartPort; monitor TCP port: $MonitorPort; GDB TCP port: $GdbPort"
if ($PrepareOnly) { Write-Host "Prepared Renode script: $resc"; return }
Push-Location (Split-Path -Parent $renode)
try {
    Write-Host "Command: `"$renode`" --disable-xwt --plain --port $MonitorPort --execute `"i @$($resc.Replace('\', '/'))`""
    & $renode --disable-xwt --plain --port $MonitorPort --execute "i @$($resc.Replace('\', '/'))"
    exit $LASTEXITCODE
} finally {
    Pop-Location
}
