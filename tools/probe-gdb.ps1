param(
    [int]$GdbPort = 3333,
    [int]$UartPort = 45123
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$gdb = Get-ChildItem (Join-Path $root '.toolchain/arm') -Filter arm-none-eabi-gdb.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
$python = Join-Path $root '.venv/Scripts/python.exe'
$elf = Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf'
if (-not (Test-Path $elf)) { throw 'Firmware ELF missing; build it first.' }
if (-not $gdb) { throw 'Pinned ARM GDB not found.' }
$log = Join-Path $root 'artifacts/evidence/T01/gdb-breakpoint.log'
$err = Join-Path $root 'artifacts/evidence/T01/gdb-breakpoint-error.log'
$gdbArgs = '-q -batch -ex "target remote 127.0.0.1:{0}" -ex "break handle_command" -ex "continue" -ex "finish" -ex "detach" "{1}"' -f $GdbPort, $elf
"GDB: $gdb $gdbArgs" | Set-Content -LiteralPath (Join-Path $root 'artifacts/evidence/T01/gdb-command.txt')
$proc = Start-Process -FilePath $gdb -ArgumentList $gdbArgs -PassThru -NoNewWindow -RedirectStandardOutput $log -RedirectStandardError $err
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($proc.HasExited) { throw "GDB exited before setting its breakpoint: $((Get-Content $err -Raw) + (Get-Content $log -Raw))" }
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Breakpoint 1 at' -Quiet)) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not ((Test-Path $log) -and (Select-String -Path $log -Pattern 'Breakpoint 1 at' -Quiet))) { throw 'GDB did not become ready with a parser breakpoint.' }
    & $python (Join-Path $root 'samples/controller/send_ping.py') --port $UartPort
    if ($LASTEXITCODE -ne 0) { throw 'UART client failed during GDB breakpoint proof.' }
    if (-not $proc.WaitForExit(12000)) { Stop-Process -Id $proc.Id -Force; throw 'GDB did not hit/finish the parser breakpoint before timeout.' }
    $output = (Get-Content $log -Raw) + (Get-Content $err -Raw)
    if ($output -notmatch 'Breakpoint 1, handle_command') { throw "GDB did not stop in handle_command; see $log" }
    if ($output -notmatch '\[Inferior 1 \(Remote target\) detached\]') { throw "GDB did not detach cleanly; see $log" }
    @("ELF: $elf", "ELF SHA-256: $((Get-FileHash -Algorithm SHA256 $elf).Hash)", 'GDB parser breakpoint and detach: PASS') | Set-Content -LiteralPath (Join-Path $root 'artifacts/evidence/T01/gdb-proof.txt')
} finally {
    if (-not $proc.HasExited) { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
}
