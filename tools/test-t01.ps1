$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$firmware = Join-Path $root 'samples/firmware/stm32-drive'
$renodeExe = Get-ChildItem (Join-Path $root '.toolchain/renode') -Filter renode.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
$logPath = Join-Path $root 'artifacts/evidence/T01/integration.log'
$uartPort = 45123
$monitorPort = 45124
$gdbPort = 3333
$scriptPath = Join-Path $firmware 'build/roboworld-stm32f4.resc'
$meta = Join-Path $firmware 'build/build-metadata.txt'
$buildId = [regex]::Match((Get-Content (Join-Path $firmware 'build/build_id.h') -Raw), '"([a-f0-9]{8})"').Groups[1].Value
$elf = Join-Path $firmware 'build/firmware.elf'
$renodeProcess = $null
$log = [System.Collections.Generic.List[string]]::new()

function Wait-TcpPort([int]$Port, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $Port); return }
        catch { Start-Sleep -Milliseconds 200 }
        finally { $client.Dispose() }
    }
    throw "Timed out waiting for localhost:$Port"
}

function Start-RenodeSession {
    $renodeArgs = '--disable-xwt --plain --port {0} --execute "i @{1}"' -f $monitorPort, $scriptPath.Replace('\', '/')
    $log.Add("Renode command: `"$renodeExe`" $renodeArgs")
    $log.Add("Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $elf).Hash)")
    $stdout = Join-Path $root 'artifacts/evidence/T01/renode-session.log'
    $stderr = Join-Path $root 'artifacts/evidence/T01/renode-session-error.log'
    $script:renodeProcess = Start-Process -FilePath $renodeExe -ArgumentList $renodeArgs -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Wait-TcpPort $monitorPort
    Start-Sleep -Milliseconds 400
    if ($script:renodeProcess.HasExited) { throw "Renode exited during startup; inspect $stdout and $stderr" }
}

function Stop-RenodeSession {
    if (-not $script:renodeProcess -or $script:renodeProcess.HasExited) { return }
    $client = [System.Net.Sockets.TcpClient]::new()
    $client.Connect('127.0.0.1', $monitorPort)
    $stream = $client.GetStream()
    $stream.ReadTimeout = 500
    $buffer = New-Object byte[] 256
    try { [void]$stream.Read($buffer, 0, $buffer.Length) } catch [System.IO.IOException] { }
    $bytes = [System.Text.Encoding]::ASCII.GetBytes("quit`n")
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
    if (-not $script:renodeProcess.WaitForExit(10000)) { throw 'Renode did not exit after monitor quit.' }
    $client.Dispose()
    $script:renodeProcess = $null
}

try {
    & (Join-Path $firmware 'build.ps1') *> (Join-Path $root 'artifacts/evidence/T01/firmware-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed.' }
    & (Join-Path $firmware 'run-renode.ps1') -UartPort $uartPort -MonitorPort $monitorPort -GdbPort $gdbPort -PrepareOnly *> $null
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare Renode session.' }

    try {
        & (Join-Path $firmware 'run-renode.ps1') -ElfPath (Join-Path $firmware 'build/does-not-exist.elf') -PrepareOnly 2>&1 | Out-String | Add-Content $logPath
        throw 'Missing ELF was not rejected.'
    } catch {
        if ($_.Exception.Message -notmatch 'Firmware ELF missing') { throw }
        $log.Add('Missing ELF validation: PASS')
    }

    Start-RenodeSession
    & (Join-Path $root '.venv/Scripts/python.exe') (Join-Path $root 'samples/controller/main.py') --port $uartPort 2>&1 | Tee-Object -FilePath (Join-Path $root 'artifacts/evidence/T01/controller-ping.log')
    if ($LASTEXITCODE -ne 0) { throw 'First firmware UART ping failed.' }
    $log.Add('First Renode boot banner + UART PING/PONG: PASS')

    $python = Join-Path $root '.venv/Scripts/python.exe'
    $testPath = Join-Path $root 'samples/controller/test_uart.py'
    $testArgs = '"{0}" --port {1} --build-id {2}' -f $testPath, $uartPort, $buildId
    $testProc = Start-Process -FilePath $python -ArgumentList $testArgs -PassThru -Wait -NoNewWindow -RedirectStandardOutput (Join-Path $root 'artifacts/evidence/T01/uart-tests-output.log') -RedirectStandardError (Join-Path $root 'artifacts/evidence/T01/uart-tests.log')
    if ($testProc.ExitCode -ne 0) { Get-Content (Join-Path $root 'artifacts/evidence/T01/uart-tests.log'); Get-Content (Join-Path $root 'artifacts/evidence/T01/uart-tests-output.log'); throw 'Firmware UART protocol tests failed.' }
    $log.Add('UART protocol tests (incomplete line, invalid command, overlong line, reconnect): PASS')

    & (Join-Path $root 'tools/probe-gdb.ps1') -GdbPort $gdbPort -UartPort $uartPort *> (Join-Path $root 'artifacts/evidence/T01/gdb-test-run.log')
    if ($LASTEXITCODE -ne 0) { throw 'GDB parser breakpoint proof failed.' }
    $log.Add('GDB parser breakpoint on same ELF: PASS')

    Stop-RenodeSession
    $log.Add('Renode clean monitor shutdown: PASS')

    Start-RenodeSession
    & (Join-Path $root '.venv/Scripts/python.exe') (Join-Path $root 'samples/controller/main.py') --port $uartPort 2>&1 | Tee-Object -FilePath (Join-Path $root 'artifacts/evidence/T01/controller-restart.log')
    if ($LASTEXITCODE -ne 0) { throw 'UART ping after Renode restart failed.' }
    $log.Add('Renode process restart + firmware UART ping: PASS')
    Stop-RenodeSession
    $log.Add("Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 $elf).Hash)")
    $log.Add("Firmware build metadata: $((Get-Content $meta -Raw).Trim() -replace "`r?`n", '; ')")
    $log.Add('Renode version: 1.17.0')
    $log | Set-Content -Encoding utf8 $logPath
    Get-Content $logPath
} finally {
    if ($renodeProcess -and -not $renodeProcess.HasExited) {
        try { Stop-RenodeSession } catch { Stop-Process -Id $renodeProcess.Id -Force -ErrorAction SilentlyContinue }
    }
}
