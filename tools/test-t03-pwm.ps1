param([int[]]$MotorPair = @(25, 75), [switch]$NoCommand)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$firmware = Join-Path $root 'samples/firmware/stm32-drive'
$renodeExe = Get-ChildItem (Join-Path $root '.toolchain/renode') -Filter renode.exe -Recurse | Select-Object -First 1 -ExpandProperty FullName
$renodeRoot = Split-Path -Parent $renodeExe
$resc = Join-Path $firmware 'build/t03-pwm.resc'
$monitorPort = 45224
$uartPort = 45223
$scriptPath = Join-Path $firmware 'build/firmware.elf'
$stock = Join-Path $renodeRoot 'scripts/single-node/stm32f4_discovery.resc'
$stdout = Join-Path $root 'artifacts/evidence/T03/renode-pwm.stdout.log'
$stderr = Join-Path $root 'artifacts/evidence/T03/renode-pwm.stderr.log'
$script:renodeProcess = $null

function Wait-Port([int]$Port) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $deadline) {
        $client = [System.Net.Sockets.TcpClient]::new()
        try { $client.Connect('127.0.0.1', $Port); return }
        catch { Start-Sleep -Milliseconds 150 }
        finally { $client.Dispose() }
    }
    throw "Timed out waiting for TCP port $Port"
}

function Read-Available($Stream, [int]$Milliseconds = 350) {
    $result = [System.Text.StringBuilder]::new()
    $buffer = New-Object byte[] 1024
    $deadline = [DateTime]::UtcNow.AddMilliseconds($Milliseconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($Stream.DataAvailable) {
            $count = $Stream.Read($buffer, 0, $buffer.Length)
            if ($count -gt 0) { [void]$result.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $count)) }
        } else { Start-Sleep -Milliseconds 25 }
    }
    $result.ToString()
}

function Send-MonitorCommand($Stream, [string]$Command) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Command + "`n")
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
    Read-Available $Stream 700
}

try {
    & (Join-Path $firmware 'build.ps1') *> (Join-Path $root 'artifacts/evidence/T03/firmware-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Firmware build failed.' }
    @(
        ':name: RoboWorld STM32 TIM3 PWM test'
        ('$bin?=@' + $scriptPath.Replace('\', '/'))
        ('include @' + $stock.Replace('\', '/'))
        'machine LoadPlatformDescriptionFromString "leftPwm: PWMTester @ gpioPortA 6"'
        'machine LoadPlatformDescriptionFromString "gpioPortA: { 6 -> leftPwm@0 }"'
        'machine LoadPlatformDescriptionFromString "rightPwm: PWMTester @ gpioPortA 7"'
        'machine LoadPlatformDescriptionFromString "gpioPortA: { 7 -> rightPwm@0 }"'
        "emulation CreateServerSocketTerminal $uartPort `"roboworld_t03_uart`" false"
        'connector Connect sysbus.uart4 roboworld_t03_uart'
        'start'
    ) | Set-Content -Encoding ascii -LiteralPath $resc

    $renodeArgs = '--disable-xwt --plain --port {0} --execute "i @{1}"' -f $monitorPort, $resc.Replace('\', '/')
    $script:renodeProcess = Start-Process -FilePath $renodeExe -ArgumentList $renodeArgs -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    Wait-Port $monitorPort
    Start-Sleep -Milliseconds 500

    $monitor = [System.Net.Sockets.TcpClient]::new()
    $monitor.Connect('127.0.0.1', $monitorPort)
    $monitorStream = $monitor.GetStream()
    $monitorStream.ReadTimeout = 1000
    [void](Read-Available $monitorStream 200)

    $uart = [System.Net.Sockets.TcpClient]::new()
    $uartDeadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([DateTime]::UtcNow -lt $uartDeadline) {
        try { $uart.Connect('127.0.0.1', $uartPort); break }
        catch { $uart.Dispose(); $uart = [System.Net.Sockets.TcpClient]::new(); Start-Sleep -Milliseconds 150 }
    }
    if (-not $uart.Connected) { throw "Timed out connecting to UART TCP port $uartPort" }
    $uartStream = $uart.GetStream()
    $uartStream.ReadTimeout = 4000
    $buffer = New-Object byte[] 2048
    $boot = [System.Text.StringBuilder]::new()
    $bootDeadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $bootDeadline -and $boot.ToString() -notmatch 'BOOT [0-9a-f]{8}') {
        if ($uartStream.DataAvailable) {
            $n = $uartStream.Read($buffer, 0, $buffer.Length)
            if ($n -gt 0) { [void]$boot.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $n)) }
        } else { Start-Sleep -Milliseconds 20 }
    }
    if ($boot.ToString() -notmatch 'BOOT [0-9a-f]{8}') { throw "No firmware boot banner: $($boot.ToString())" }

    if ($NoCommand) {
        Start-Sleep -Milliseconds 500
        $idlePins = Send-MonitorCommand $monitorStream 'sysbus ReadDoubleWord 0x40020010'
        $idleMatches = [regex]::Matches($idlePins, '(?i)0x([0-9a-f]+)')
        if ($idleMatches.Count -eq 0) { throw "Idle PWM pins could not be sampled: [$idlePins]" }
        $idleState = [Convert]::ToUInt32($idleMatches[$idleMatches.Count - 1].Groups[1].Value, 16)
        if (($idleState -band 0xC0) -ne 0) { throw "PWM pins were unexpectedly active before a command: 0x$($idleState.ToString('X8'))." }
        $idleRecord = "No MOTOR command -> PWM PA6/PA7 low (IDR=0x$($idleState.ToString('X8'))): PASS"
        $idleRecord | Add-Content -Encoding utf8 (Join-Path $root 'artifacts/evidence/T03/pwm-output-summary.txt')
        $idleRecord
        return
    }

    $invalid = [System.Text.Encoding]::ASCII.GetBytes("MOTOR 101 0`n")
    $uartStream.Write($invalid, 0, $invalid.Length)
    $uartStream.Flush()
    $invalidReply = [System.Text.StringBuilder]::new()
    $invalidDeadline = [DateTime]::UtcNow.AddSeconds(3)
    while ([DateTime]::UtcNow -lt $invalidDeadline -and $invalidReply.ToString() -notmatch 'ERR RANGE') {
        if ($uartStream.DataAvailable) { $n = $uartStream.Read($buffer, 0, $buffer.Length); if ($n -gt 0) { [void]$invalidReply.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $n)) } }
        else { Start-Sleep -Milliseconds 20 }
    }
    if ($invalidReply.ToString() -notmatch 'ERR RANGE') { throw "Out-of-range command was not rejected: $($invalidReply.ToString())" }

    $summary = [System.Collections.Generic.List[string]]::new()
    $vector = $MotorPair
        $hostArrivalNs = [long]([System.Diagnostics.Stopwatch]::GetTimestamp() / [double][System.Diagnostics.Stopwatch]::Frequency * 1000000000d)
        $command = "MOTOR $($vector[0]) $($vector[1])`n"
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($command)
        $uartStream.Write($bytes, 0, $bytes.Length)
        $uartStream.Flush()
        $reply = [System.Text.StringBuilder]::new()
        $replyDeadline = [DateTime]::UtcNow.AddSeconds(3)
        while ([DateTime]::UtcNow -lt $replyDeadline -and $reply.ToString() -notmatch 'OK MOTOR|ERR') {
            if ($uartStream.DataAvailable) {
                $n = $uartStream.Read($buffer, 0, $buffer.Length)
                if ($n -gt 0) { [void]$reply.Append([System.Text.Encoding]::ASCII.GetString($buffer, 0, $n)) }
            } else { Start-Sleep -Milliseconds 20 }
        }
        if ($reply.ToString() -notmatch 'OK MOTOR') { throw "Firmware command failed: $($reply.ToString())" }
        $hostAckNs = [long]([System.Diagnostics.Stopwatch]::GetTimestamp() / [double][System.Diagnostics.Stopwatch]::Frequency * 1000000000d)
        Start-Sleep -Milliseconds 750
        $directionOutput = Send-MonitorCommand $monitorStream 'sysbus ReadDoubleWord 0x40020C14'
        $directionMatches = [regex]::Matches($directionOutput, '(?i)0x([0-9a-f]+)')
        if ($directionMatches.Count -eq 0) { throw "Direction GPIO output could not be read: [$directionOutput]" }
        $directionBits = [Convert]::ToUInt32($directionMatches[$directionMatches.Count - 1].Groups[1].Value, 16)
        $leftForward = ($directionBits -band 0x4) -ne 0
        $rightForward = ($directionBits -band 0x8) -ne 0
        if (($leftForward -ne ($vector[0] -ge 0)) -or ($rightForward -ne ($vector[1] -ge 0))) {
            throw "Direction GPIO mismatch for [$($vector -join ',')]: ODR=0x$($directionBits.ToString('X8'))."
        }
        $leftOutput = Send-MonitorCommand $monitorStream 'sysbus.gpioPortA.leftPwm HighPercentage'
        $rightOutput = Send-MonitorCommand $monitorStream 'sysbus.gpioPortA.rightPwm HighPercentage'
        $renodeTime = Send-MonitorCommand $monitorStream 'emulation GetTimeSourceInfo'
        $leftMatch = [regex]::Match($leftOutput, '(?m)(\d+(?:\.\d+)?)\s*$')
        $rightMatch = [regex]::Match($rightOutput, '(?m)(\d+(?:\.\d+)?)\s*$')
        if (-not $leftMatch.Success -or -not $rightMatch.Success) {
            if ($leftOutput -notmatch 'NaN' -or $rightOutput -notmatch 'NaN') { throw "PWM measurement query failed. L=[$leftOutput] R=[$rightOutput]" }
            $idrOutput = Send-MonitorCommand $monitorStream 'sysbus ReadDoubleWord 0x40020010'
            $idrMatches = [regex]::Matches($idrOutput, '(?i)0x([0-9a-f]+)')
            if ($idrMatches.Count -eq 0) { throw "PWM is static at a rail and IDR could not be read: [$idrOutput]" }
            $pinState = [Convert]::ToUInt32($idrMatches[$idrMatches.Count - 1].Groups[1].Value, 16)
            $leftDuty = if (($pinState -band 0xC0) -eq 0xC0) { 100.0 } else { 0.0 }
            $rightDuty = if (($pinState -band 0x80) -ne 0) { 100.0 } else { 0.0 }
            Add-Content -Encoding utf8 -LiteralPath (Join-Path $root 'artifacts/evidence/T03/pwm-probe-raw.log') -Value "IDR=$idrOutput; parsed=$pinState"
        } else {
            $leftDuty = [double]::Parse($leftMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
            $rightDuty = [double]::Parse($rightMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture)
        }
        $cleanLeftOutput = ($leftOutput -replace '[\r\n]+', ' ' -replace '\s{2,}', ' ').Trim()
        $cleanRightOutput = ($rightOutput -replace '[\r\n]+', ' ' -replace '\s{2,}', ' ').Trim()
        Add-Content -Encoding utf8 -LiteralPath (Join-Path $root 'artifacts/evidence/T03/pwm-probe-raw.log') -Value "MOTOR $($vector -join ' ')`nL=$cleanLeftOutput`nR=$cleanRightOutput"
        $traceFile = Join-Path $root 'artifacts/evidence/T03/actuation-probe-trace.csv'
        if (-not (Test-Path -LiteralPath $traceFile)) { 'host_uart_write_monotonic_ns,host_firmware_ack_monotonic_ns,pwm_left_percent,pwm_right_percent,direction_gpio_odr,renode_time_source_info' | Set-Content -Encoding utf8 $traceFile }
        $traceLine = '"{0}","{1}",{2},{3},"0x{4:X}","{5}"' -f $hostArrivalNs, $hostAckNs, $leftDuty.ToString('F3', [Globalization.CultureInfo]::InvariantCulture), $rightDuty.ToString('F3', [Globalization.CultureInfo]::InvariantCulture), $directionBits, ($renodeTime -replace '[\r\n"]', ' ')
        Add-Content -Encoding utf8 -LiteralPath $traceFile -Value $traceLine
        $expectedLeft = [math]::Abs($vector[0])
        $expectedRight = [math]::Abs($vector[1])
        if ([math]::Abs($leftDuty - $expectedLeft) -gt 2 -or [math]::Abs($rightDuty - $expectedRight) -gt 2) {
            throw "PWM mismatch for [$($vector -join ',')]: measured [$leftDuty,$rightDuty] expected [$expectedLeft,$expectedRight]."
        }
        $summary.Add("MOTOR $($vector -join ' ') -> direction GPIO 0x$($directionBits.ToString('X')) and PWM duty $leftDuty% / $rightDuty%: PASS")
    $summary.Add("Firmware Build ID: $((Get-Content (Join-Path $firmware 'build/build_id.h') -Raw).Trim())")
    $summary.Add("Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 $scriptPath).Hash)")
    $summary | Add-Content -Encoding utf8 (Join-Path $root 'artifacts/evidence/T03/pwm-output-summary.txt')
    $summary
} finally {
    if ($monitor) { $monitor.Dispose() }
    if ($uart) { $uart.Dispose() }
    if ($script:renodeProcess -and -not $script:renodeProcess.HasExited) {
        try {
            $client = [System.Net.Sockets.TcpClient]::new()
            $client.Connect('127.0.0.1', $monitorPort)
            $stream = $client.GetStream()
            [void](Read-Available $stream 100)
            $quit = [System.Text.Encoding]::ASCII.GetBytes("quit`n")
            $stream.Write($quit, 0, $quit.Length)
            $stream.Flush()
            if (-not $script:renodeProcess.WaitForExit(5000)) { Stop-Process -Id $script:renodeProcess.Id -Force }
            $client.Dispose()
        } catch { Stop-Process -Id $script:renodeProcess.Id -Force -ErrorAction SilentlyContinue }
    }
}
