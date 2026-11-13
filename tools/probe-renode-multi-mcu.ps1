$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$renodeRoot = Join-Path $root '.toolchain/renode/renode_1.17.0-portable'
$renode = Join-Path $renodeRoot 'renode.exe'
$firmware = Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf'
$stock = Join-Path $renodeRoot 'scripts/single-node/stm32f4_discovery.resc'
$evidence = Join-Path $root 'artifacts/evidence/T07'
$resc = Join-Path $evidence 'multi-mcu-renode-probe.resc'
$stdout = Join-Path $evidence 'multi-mcu-renode-probe.log'
$stderr = Join-Path $evidence 'multi-mcu-renode-probe.stderr.log'
$summaryPath = Join-Path $evidence 'multi-mcu-renode-probe-summary.txt'
foreach ($path in @($renode, $firmware, $stock)) { if (-not (Test-Path -LiteralPath $path)) { throw "Missing multi-MCU Renode probe input: $path" } }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

function Get-LoopbackPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    $listener.Stop()
    return $port
}

function Read-RenodePrompt([System.Net.Sockets.NetworkStream]$Stream) {
    $text = [System.Text.StringBuilder]::new()
    $buffer = [byte[]]::new(1)
    while ($true) {
        $count = $Stream.Read($buffer, 0, 1)
        if ($count -eq 0) { throw 'Renode monitor closed before returning its prompt.' }
        [void]$text.Append([char]$buffer[0])
        if ($text.Length -ge 2 -and $text[$text.Length - 2] -eq ')' -and $text[$text.Length - 1] -eq ' ') { return $text.ToString() }
    }
}

function Invoke-RenodeCommand([System.Net.Sockets.NetworkStream]$Stream, [string]$Command) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($Command + [Environment]::NewLine)
    $Stream.Write($bytes, 0, $bytes.Length)
    $Stream.Flush()
    return Read-RenodePrompt $Stream
}

function Read-FirmwareReply([System.IO.StreamReader]$Reader, [string]$ExpectedPrefix, [int]$TimeoutMs) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.ElapsedMilliseconds -lt $TimeoutMs) {
        try {
            $line = $Reader.ReadLine()
            if ($null -eq $line) { return $null }
            if ($line.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) { return $line }
        } catch [System.IO.IOException] { return $null }
    }
    return $null
}

function Read-ExternalControlBytes([System.Net.Sockets.NetworkStream]$Stream, [int]$Length) {
    $buffer = [byte[]]::new($Length)
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($buffer, $offset, $Length - $offset)
        if ($read -eq 0) { throw 'Renode external-control connection closed unexpectedly.' }
        $offset += $read
    }
    return ,$buffer
}

function Invoke-ExternalControlRequest([System.Net.Sockets.NetworkStream]$Stream, [UInt16]$Command, [byte[]]$Payload) {
    $script:clockRequestId = [UInt16](32768 + ($script:clockRequestCounter % 32768))
    $script:clockRequestCounter++
    $message = [byte[]]::new($Payload.Length + 3)
    [BitConverter]::GetBytes($Command).CopyTo($message, 0)
    $message[2] = 0
    if ($Payload.Length -gt 0) { $Payload.CopyTo($message, 3) }
    $header = [byte[]]::new(6)
    [BitConverter]::GetBytes($script:clockRequestId).CopyTo($header, 0)
    [BitConverter]::GetBytes([UInt32]$message.Length).CopyTo($header, 2)
    $Stream.Write($header, 0, $header.Length)
    $Stream.Write($message, 0, $message.Length)
    $Stream.Flush()
    $responseHeader = Read-ExternalControlBytes $Stream 6
    $responseId = [BitConverter]::ToUInt16($responseHeader, 0)
    $responseLength = [BitConverter]::ToUInt32($responseHeader, 2)
    if ($responseId -ne $script:clockRequestId -or $responseLength -lt 3 -or $responseLength -gt 1024) { throw 'Malformed Renode external-control response header.' }
    $response = Read-ExternalControlBytes $Stream ([int]$responseLength)
    if ([BitConverter]::ToUInt16($response, 0) -ne $Command -or $response[2] -ne 2) { throw "Renode external-control command $Command failed." }
    if ($response.Length -gt 3) { return ,$response[3..($response.Length - 1)] }
    return ,([byte[]]@())
}

function Get-ExternalControlTimeNs([System.Net.Sockets.NetworkStream]$Stream) {
    $result = Invoke-ExternalControlRequest $Stream 2 ([byte[]]::new(8))
    return [BitConverter]::ToUInt64($result, 0)
}

function Advance-ExternalControlTime([System.Net.Sockets.NetworkStream]$Stream, [UInt64]$DeltaNs) {
    [void](Invoke-ExternalControlRequest $Stream 1 ([BitConverter]::GetBytes($DeltaNs)))
}

$monitorPort = Get-LoopbackPort
$uartPrimaryPort = Get-LoopbackPort
$uartSecondaryPort = Get-LoopbackPort
$clockPort = Get-LoopbackPort
$firmwareForward = $firmware.Replace('\', '/')
$stockForward = $stock.Replace('\', '/')
$rescLines = @(
    ':name: RoboWorld two-instance STM32 isolation probe'
    ('$name="mcu_primary"')
    ('$bin?=@' + $firmwareForward)
    ('include @' + $stockForward)
    ('$name="mcu_secondary"')
    ('$bin=@' + $firmwareForward)
    ('include @' + $stockForward)
    'mach set "mcu_primary"'
    ("emulation CreateServerSocketTerminal $uartPrimaryPort " + '"uart_primary" false')
    'connector Connect sysbus.uart4 uart_primary'
    'mach set "mcu_secondary"'
    ("emulation CreateServerSocketTerminal $uartSecondaryPort " + '"uart_secondary" false')
    'connector Connect sysbus.uart4 uart_secondary'
    ("emulation CreateExternalControlServer " + '"roboworld_clock" ' + $clockPort)
)
[IO.File]::WriteAllText($resc, [string]::Join([Environment]::NewLine, $rescLines) + [Environment]::NewLine, [Text.Encoding]::ASCII)

$process = $null
$monitorClient = $null
$primaryClient = $null
$secondaryClient = $null
$failure = $null
$lines = [System.Collections.Generic.List[string]]::new()
try {
    $arguments = '--disable-xwt --plain --port {0} --execute "i @{1}"' -f $monitorPort, $resc.Replace('\', '/')
    $process = Start-Process -FilePath $renode -ArgumentList $arguments -WorkingDirectory $renodeRoot -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $listeners = @()
    do {
        if ($process.HasExited) { throw "Renode exited during the multi-MCU probe. See $stderr" }
        $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue)
        if ($listeners.Count -ge 4) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    $expectedPorts = @($monitorPort, $uartPrimaryPort, $uartSecondaryPort, $clockPort)
    $bound = @($listeners | Where-Object { $_.LocalAddress -eq '127.0.0.1' -and $_.LocalPort -in $expectedPorts })
    if ($bound.Count -ne 4) { throw "Renode multi-MCU listeners were not loopback-only or complete: $(($listeners | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', ')" }

    $monitorClient = [System.Net.Sockets.TcpClient]::new()
    $monitorClient.Connect('127.0.0.1', $monitorPort)
    $monitorStream = $monitorClient.GetStream()
    $monitorStream.ReadTimeout = 5000
    [void](Read-RenodePrompt $monitorStream)

    $clockClient = [System.Net.Sockets.TcpClient]::new()
    $clockClient.Connect('127.0.0.1', $clockPort)
    $clockStream = $clockClient.GetStream()
    $clockStream.ReadTimeout = 5000
    $script:clockRequestCounter = 0
    $version = Invoke-ExternalControlRequest $clockStream 0 ([byte[]]::new(4))
    if ($version.Length -gt 0 -and [BitConverter]::ToUInt32($version, 0) -ne 0) { throw 'Unsupported Renode external-control protocol version.' }

    $primaryClient = [System.Net.Sockets.TcpClient]::new()
    $primaryClient.Connect('127.0.0.1', $uartPrimaryPort)
    $primaryClient.ReceiveTimeout = 5000
    $primaryReader = [System.IO.StreamReader]::new($primaryClient.GetStream(), [Text.Encoding]::ASCII, $false, 1024, $true)
    $primaryWriter = [System.IO.StreamWriter]::new($primaryClient.GetStream(), [Text.Encoding]::ASCII, 1024, $true)
    $primaryWriter.AutoFlush = $true

    $secondaryClient = [System.Net.Sockets.TcpClient]::new()
    $secondaryClient.Connect('127.0.0.1', $uartSecondaryPort)
    $secondaryClient.ReceiveTimeout = 5000
    $secondaryReader = [System.IO.StreamReader]::new($secondaryClient.GetStream(), [Text.Encoding]::ASCII, $false, 1024, $true)
    $secondaryWriter = [System.IO.StreamWriter]::new($secondaryClient.GetStream(), [Text.Encoding]::ASCII, 1024, $true)
    $secondaryWriter.AutoFlush = $true

    $timeBefore = Get-ExternalControlTimeNs $clockStream
    Advance-ExternalControlTime $clockStream 10000000
    if ((Get-ExternalControlTimeNs $clockStream) -ne ($timeBefore + 10000000)) { throw 'Shared multi-MCU simulation clock did not advance by the requested exact interval.' }

    $instances = @(@{ Name = 'primary'; Writer = $primaryWriter; Reader = $primaryReader }, @{ Name = 'secondary'; Writer = $secondaryWriter; Reader = $secondaryReader })
    foreach ($instance in $instances) { $instance.Writer.WriteLine('PING') }
    Advance-ExternalControlTime $clockStream 10000000
    foreach ($instance in $instances) {
        $reply = Read-FirmwareReply $instance.Reader 'PONG ' 8000
        if (-not $reply) { throw "$($instance.Name) MCU did not return PONG before fault injection." }
        $lines.Add("$($instance.Name) MCU initial PING/PONG: PASS ($reply)")
    }

    [void](Invoke-RenodeCommand $monitorStream 'mach set "mcu_secondary"')
    [void](Invoke-RenodeCommand $monitorStream 'cpu IsHalted true')
    $secondaryWriter.WriteLine('PING')
    $primaryWriter.WriteLine('PING')
    Advance-ExternalControlTime $clockStream 10000000
    $primaryReply = Read-FirmwareReply $primaryReader 'PONG ' 8000
    if (-not $primaryReply) { throw 'Primary MCU stopped responding after the secondary MCU was halted.' }
    $secondaryReply = Read-FirmwareReply $secondaryReader 'PONG ' 1500
    if ($secondaryReply) { throw "Secondary MCU returned a response after its CPU was halted: $secondaryReply" }
    if ($process.HasExited) { throw 'Renode exited when one MCU halted.' }
    $lines.Add("Secondary MCU fault isolation: PASS (halted secondary; primary replied $primaryReply)")
    $lines.Add('Four Renode endpoints (monitor, two UARTs, shared simulation clock) bind to 127.0.0.1: PASS')
    $lines.Add('One external-clock RunFor advanced both MCU instances by the exact requested nanoseconds: PASS')
    $lines.Add("Firmware ELF SHA-256: $((Get-FileHash -Algorithm SHA256 -LiteralPath $firmware).Hash)")
    $lines.Add('Renode version: 1.17.0')
} catch {
    $failure = $_.Exception.Message
} finally {
    foreach ($client in @($primaryClient, $secondaryClient)) { if ($client) { $client.Dispose() } }
    if ($monitorClient) {
        try { $stream = $monitorClient.GetStream(); [void](Invoke-RenodeCommand $stream 'quit') } catch { }
        $monitorClient.Dispose()
    }
    if ($clockClient) { $clockClient.Dispose() }
    if ($process -and -not $process.HasExited) {
        if (-not $process.WaitForExit(10000)) { try { $process.Kill($true); $process.WaitForExit() } catch { } }
    }
}

if ($failure) { $lines.Add("Multi-MCU Renode probe: FAIL ($failure)") }
else { $lines.Insert(0, 'Multi-MCU Renode isolation probe: PASS') }
$lines | Set-Content -LiteralPath $summaryPath -Encoding utf8
$lines
if ($failure) { throw $failure }
