$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T05'
$firmware = Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf'
$renodeExe = Get-ChildItem (Join-Path $root '.toolchain/renode') -Filter 'renode.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$renodeRoot = Split-Path -Parent $renodeExe
$stock = Join-Path $renodeRoot 'scripts/single-node/stm32f4_discovery.resc'
$runId = [guid]::NewGuid().ToString('N')
$resc = Join-Path $env:TEMP "roboworld-t05-$runId.resc"
$save = Join-Path $env:TEMP "roboworld-t05-$runId.save"
$stdout = Join-Path $evidence 'renode-save-load.stdout.log'
$stderr = Join-Path $evidence 'renode-save-load.stderr.log'
$summary = Join-Path $evidence 'renode-save-load-summary.txt'
$renodeVersion = (& $renodeExe --version | Select-Object -First 1).Trim()
$monitorPort = 0
$uartPort = 0
$renodeProcess = $null
$monitor = $null
$uart = $null

function Get-FreePort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}

function Wait-Port([int]$Port, [int]$TimeoutSeconds = 20) {
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try { [void]$client.Connect('127.0.0.1', $Port); return }
        catch { Start-Sleep -Milliseconds 100 }
        finally { $client.Dispose() }
    }
    throw "Timed out waiting for 127.0.0.1:$Port"
}

function Read-MonitorPrompt($Stream) {
    $result = [System.Text.StringBuilder]::new()
    $buffer = New-Object byte[] 1
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $count = $Stream.Read($buffer, 0, 1)
            if ($count -eq 0) { throw 'Renode monitor closed.' }
            [void]$result.Append([char]$buffer[0])
            if ($result.Length -ge 2 -and $result[$result.Length - 2] -eq ')' -and $result[$result.Length - 1] -eq ' ') { return $result.ToString() }
        } catch [System.IO.IOException] {
            $socketError = $_.Exception.InnerException -as [System.Net.Sockets.SocketException]
            if ($socketError -and $socketError.SocketErrorCode -ne [System.Net.Sockets.SocketError]::TimedOut) { throw }
            Start-Sleep -Milliseconds 10
        }
    }
    throw "Timed out waiting for Renode monitor prompt: $($result.ToString())"
}

function Drain-Monitor($Stream, [int]$Milliseconds = 400) {
    $buffer = New-Object byte[] 1024
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Milliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Stream.DataAvailable) { [void]$Stream.Read($buffer, 0, $buffer.Length) }
        else { Start-Sleep -Milliseconds 10 }
    }
}

function Send-MonitorCommand($Stream, [string]$Command) {
    # Renode can emit a second startup prompt after the initial monitor prompt.
    # Clear any queued prompt before issuing a command so the response reader
    # cannot mistake that stale prompt for the command's completion.
    Drain-Monitor $Stream 100
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Command + "`n")
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
    return Read-MonitorPrompt $Stream
}

function Send-UartCommand($Stream, [string]$Command, [string]$Expected) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Command + "`n")
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
    $response = [System.Text.StringBuilder]::new()
    $buffer = New-Object byte[] 512
    $deadline = [DateTime]::UtcNow.AddSeconds(4)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Stream.DataAvailable) {
            $count = $Stream.Read($buffer, 0, $buffer.Length)
            if ($count -gt 0) { [void]$response.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $count)) }
            if ($response.ToString() -match $Expected) { return $response.ToString() }
        } else { Start-Sleep -Milliseconds 10 }
    }
    throw "UART response to '$Command' did not match '$Expected': $($response.ToString())"
}

function Read-UartBoot($Stream) {
    $response = [System.Text.StringBuilder]::new()
    $buffer = New-Object byte[] 256
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Stream.DataAvailable) {
            $count = $Stream.Read($buffer, 0, $buffer.Length)
            if ($count -gt 0) { [void]$response.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $count)) }
            if ($response.ToString() -match 'BOOT [0-9a-f]{8}') { return $response.ToString() }
        } else { Start-Sleep -Milliseconds 10 }
    }
    throw "Firmware boot banner was not observed: $($response.ToString())"
}

function Read-Register($Stream, [string]$Address) {
    $output = Send-MonitorCommand $Stream "sysbus ReadDoubleWord $Address"
    $matches = [regex]::Matches($output, '(?i)0x([0-9a-f]+)')
    if ($matches.Count -eq 0) { throw "Could not parse register $Address from: $output" }
    return [Convert]::ToUInt32($matches[$matches.Count - 1].Groups[1].Value, 16)
}

try {
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    if (-not (Test-Path $firmware)) { throw "Firmware image is missing: $firmware" }
    $monitorPort = Get-FreePort
    $uartPort = Get-FreePort
    @(
        ':name: RoboWorld T05 Renode save/load feasibility probe'
        ('$bin?=@' + $firmware.Replace('\', '/'))
        ('include @' + $stock.Replace('\', '/'))
        "emulation CreateServerSocketTerminal $uartPort `"roboworld_t05_uart`" false"
        'connector Connect sysbus.uart4 roboworld_t05_uart'
        'start'
    ) | Set-Content -Encoding ascii -LiteralPath $resc

    $arguments = '--disable-xwt --plain --port {0} --execute "i @{1}"' -f $monitorPort, $resc.Replace('\', '/')
    $renodeProcess = Start-Process -FilePath $renodeExe -ArgumentList $arguments -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Wait-Port $monitorPort
    Start-Sleep -Milliseconds 250
    if ($renodeProcess.HasExited) { throw "Renode exited during startup. See $stdout and $stderr" }

    $monitor = [System.Net.Sockets.TcpClient]::new()
    $monitor.Connect('127.0.0.1', $monitorPort)
    $monitorStream = $monitor.GetStream()
    $monitorStream.ReadTimeout = 250
    $monitorBanner = Read-MonitorPrompt $monitorStream
    Start-Sleep -Milliseconds 500
    Drain-Monitor $monitorStream

    $uart = [System.Net.Sockets.TcpClient]::new()
    $uartDeadline = [DateTime]::UtcNow.AddSeconds(20)
    while ([DateTime]::UtcNow -lt $uartDeadline) {
        try { $uart.Connect('127.0.0.1', $uartPort); break }
        catch { $uart.Dispose(); $uart = [System.Net.Sockets.TcpClient]::new(); Start-Sleep -Milliseconds 100 }
    }
    if (-not $uart.Connected) { throw "Timed out connecting to firmware UART at 127.0.0.1:$uartPort" }
    $uartStream = $uart.GetStream()
    $uartStream.ReadTimeout = 250
    $boot = Read-UartBoot $uartStream
    $null = Send-UartCommand $uartStream 'MOTOR 20 30' 'OK MOTOR'
    Start-Sleep -Milliseconds 400
    $savedCcr1 = Read-Register $monitorStream '0x40000434'
    $savedCcr2 = Read-Register $monitorStream '0x40000438'
    Send-MonitorCommand $monitorStream "Save @$($save.Replace('\', '/'))" | Out-Null
    if (-not (Test-Path $save) -or (Get-Item $save).Length -lt 1024) { throw 'Renode did not produce a nonempty state file.' }

    $null = Send-UartCommand $uartStream 'MOTOR 80 90' 'OK MOTOR'
    Start-Sleep -Milliseconds 400
    $changedCcr1 = Read-Register $monitorStream '0x40000434'
    $changedCcr2 = Read-Register $monitorStream '0x40000438'
    if ($savedCcr1 -eq $changedCcr1 -or $savedCcr2 -eq $changedCcr2) { throw "Firmware command did not change both timer compare registers: saved=[$savedCcr1,$savedCcr2] changed=[$changedCcr1,$changedCcr2]." }

    $loadOutput = Send-MonitorCommand $monitorStream "Load @$($save.Replace('\', '/'))"
    Send-MonitorCommand $monitorStream 'mach set 0' | Out-Null
    Start-Sleep -Milliseconds 250
    $restoredCcr1 = Read-Register $monitorStream '0x40000434'
    $restoredCcr2 = Read-Register $monitorStream '0x40000438'
    if ($restoredCcr1 -ne $savedCcr1 -or $restoredCcr2 -ne $savedCcr2)
    {
        throw "Renode peripheral state did not return to the saved register values: saved=[$savedCcr1,$savedCcr2] restored=[$restoredCcr1,$restoredCcr2]."
    }

    Send-MonitorCommand $monitorStream "emulation CreateServerSocketTerminal $uartPort `"roboworld_t05_uart_restored`" false" | Out-Null
    Send-MonitorCommand $monitorStream 'mach set 0' | Out-Null
    Send-MonitorCommand $monitorStream 'connector Connect sysbus.uart4 roboworld_t05_uart_restored' | Out-Null
    Send-MonitorCommand $monitorStream 'start' | Out-Null
    $uart.Dispose()
    $uart = [System.Net.Sockets.TcpClient]::new()
    $uartDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $uartDeadline) {
        try { $uart.Connect('127.0.0.1', $uartPort); break }
        catch { $uart.Dispose(); $uart = [System.Net.Sockets.TcpClient]::new(); Start-Sleep -Milliseconds 100 }
    }
    if (-not $uart.Connected) { throw 'Renode did not reopen the saved UART server socket after Load.' }
    $uartStream = $uart.GetStream()
    $uartStream.ReadTimeout = 250
    $null = Send-UartCommand $uartStream 'PING' 'PONG [0-9a-f]{8}'

    @(
        'Renode Save/Load same-version runtime state probe: PASS'
        "Renode version: $renodeVersion"
        "Renode executable: $renodeExe"
        "Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 $firmware).Hash.ToLowerInvariant())"
        "Firmware boot banner: $($boot.Trim())"
        "TIM3 CCR1/CCR2 before save: $savedCcr1 / $savedCcr2"
        "TIM3 CCR1/CCR2 after state mutation: $changedCcr1 / $changedCcr2"
        "TIM3 CCR1/CCR2 after Renode Load: $restoredCcr1 / $restoredCcr2"
        "UART socket manually recreated after Load; firmware PING/PONG resumed on port ${uartPort}: PASS"
        'This probe does not yet restore Godot physics, Robot Service queues, or a cross-process checkpoint manifest.'
    ) | Set-Content -Encoding utf8 $summary
    Get-Content $summary
}
finally {
    if ($uart) { $uart.Dispose() }
    if ($monitor) { $monitor.Dispose() }
    if ($renodeProcess -and -not $renodeProcess.HasExited) { try { $renodeProcess.Kill($true); [void]$renodeProcess.WaitForExit(5000) } catch { } }
    if (Test-Path $resc) { Remove-Item -LiteralPath $resc -Force }
    if (Test-Path $save) { Remove-Item -LiteralPath $save -Force }
}
