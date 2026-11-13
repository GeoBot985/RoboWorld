param([string]$AppPath = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$evidence = Join-Path $root 'artifacts/evidence/T05'
$dotnet = Join-Path $root '.toolchain/dotnet/dotnet.exe'
$godot = Get-ChildItem (Join-Path $root '.toolchain/godot') -Filter 'Godot_v4.7.2-stable_mono_win64_console.exe' -Recurse | Select-Object -First 1 -ExpandProperty FullName
$buildLog = Join-Path $evidence 'event-queue-build.log'
$log = Join-Path $evidence 'event-queue-bounds.log'
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = $godot
    if (-not (Test-Path $dotnet)) { throw 'Pinned .NET SDK is missing.' }
    $appBaseArguments = @('--headless', '--path', (Join-Path $root 'godot'))
    $workingDirectory = $root
} else {
    $AppPath = [System.IO.Path]::GetFullPath($AppPath)
    $appBaseArguments = @('--headless')
    $workingDirectory = Split-Path -Parent $AppPath
}
if (-not (Test-Path -LiteralPath $AppPath)) { throw "Application under test is missing: $AppPath" }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
if ($AppPath -eq $godot) {
    $env:DOTNET_ROOT = Join-Path $root '.toolchain/dotnet'
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    & $dotnet build (Join-Path $root 'godot/RoboWorld.Godot.csproj') --configuration Debug --nologo *> $buildLog
    if ($LASTEXITCODE -ne 0) { Get-Content $buildLog; throw 'Godot bounded queue build failed.' }
}
$queueArguments = $appBaseArguments + @('--', '--event-queue-smoke')
& $AppPath @queueArguments 2>&1 | Set-Content -Encoding utf8 $log
if ($LASTEXITCODE -ne 0 -or (Get-Content -Raw $log) -notmatch 'GODOT_EVENT_QUEUE_BOUNDED capacity=2 production_capacity=512 backpressure=PASS fifo=PASS dropped=0') {
    throw "Godot bounded event queue smoke failed. See $log"
}
Get-Content $log | Select-String 'GODOT_EVENT_QUEUE_BOUNDED'

$serviceLog = Join-Path $evidence 'loopback-listener-smoke.log'
$serviceError = Join-Path $evidence 'loopback-listener-smoke.stderr.log'
$appProcess = $null
$existingRenodeIds = @(Get-Process -Name 'renode' -ErrorAction SilentlyContinue | ForEach-Object Id)
try {
    $appProcess = Start-Process -FilePath $AppPath -ArgumentList $appBaseArguments -WorkingDirectory $workingDirectory -PassThru -NoNewWindow -RedirectStandardOutput $serviceLog -RedirectStandardError $serviceError
    $deadline = [DateTime]::UtcNow.AddSeconds(45)
    $eventPort = 0
    $uartPort = 0
    $monitorPort = 0
    $renodeUartPort = 0
    $clockPort = 0
    while ([DateTime]::UtcNow -lt $deadline -and ($eventPort -eq 0 -or $uartPort -eq 0 -or $monitorPort -eq 0 -or $renodeUartPort -eq 0 -or $clockPort -eq 0)) {
        if ($appProcess.HasExited) { throw "Godot exited before service startup. See $serviceError" }
        $output = [string](Get-Content -LiteralPath $serviceLog -Raw -ErrorAction SilentlyContinue)
        $endpoint = [regex]::Match($output, 'events_port=(\d+) uart=tcp://127\.0\.0\.1:(\d+)')
        if ($endpoint.Success) { $eventPort = [int]$endpoint.Groups[1].Value; $uartPort = [int]$endpoint.Groups[2].Value }
        $renodeEndpoints = [regex]::Match($output, 'RENODE_PRIVATE_ENDPOINTS monitor_port=(\d+) uart_port=(\d+) clock_port=(\d+)')
        if ($renodeEndpoints.Success) {
            $monitorPort = [int]$renodeEndpoints.Groups[1].Value
            $renodeUartPort = [int]$renodeEndpoints.Groups[2].Value
            $clockPort = [int]$renodeEndpoints.Groups[3].Value
        }
        $renodeInfo = Get-CimInstance Win32_Process -Filter "Name='renode.exe'" | Where-Object { $_.ProcessId -notin $existingRenodeIds } | Select-Object -First 1
        if ($renodeInfo) {
            if ($monitorPort -eq 0 -and $renodeInfo.CommandLine -match '--port\s+(\d+)') { $monitorPort = [int]$Matches[1] }
            if (($renodeUartPort -eq 0 -or $clockPort -eq 0) -and $renodeInfo.CommandLine -match '@([^\s"]+\.resc)') {
                $scriptPath = $Matches[1].Replace('/', '\')
                if (Test-Path -LiteralPath $scriptPath) {
                    $renodeScript = Get-Content -LiteralPath $scriptPath -Raw
                    $uartMatch = [regex]::Match($renodeScript, 'CreateServerSocketTerminal\s+(\d+)')
                    $clockMatch = [regex]::Match($renodeScript, 'CreateExternalControlServer\s+"roboworld_clock"\s+(\d+)')
                    if ($uartMatch.Success) { $renodeUartPort = [int]$uartMatch.Groups[1].Value }
                    if ($clockMatch.Success) { $clockPort = [int]$clockMatch.Groups[1].Value }
                }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    if ($eventPort -eq 0 -or $uartPort -eq 0 -or $monitorPort -eq 0 -or $renodeUartPort -eq 0 -or $clockPort -eq 0) { throw 'Timed out waiting for Robot Service and Renode private endpoints.' }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $output = [string](Get-Content -LiteralPath $serviceLog -Raw -ErrorAction SilentlyContinue)
        if ($output.Contains('GODOT_EVENT_STREAM_HANDSHAKE_COMPLETE')) { break }
        if ($appProcess.HasExited) { throw "Godot exited before the internal service handshake. See $serviceError" }
        Start-Sleep -Milliseconds 100
    }
    if (-not $output.Contains('GODOT_EVENT_STREAM_HANDSHAKE_COMPLETE')) { throw 'Godot and Robot Service did not complete their private event handshake.' }

    $listeners = @()
    $listenerDeadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.LocalPort -in @($eventPort, $uartPort, $monitorPort, $renodeUartPort) })
        $clockConnections = @(Get-NetTCPConnection -ErrorAction SilentlyContinue | Where-Object LocalPort -eq $clockPort)
        if ($listeners.Count -ge 4 -and $clockConnections.Count -gt 0) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $listenerDeadline)
    $uartListener = @($listeners | Where-Object LocalPort -eq $uartPort)
    $monitorListener = @($listeners | Where-Object LocalPort -eq $monitorPort)
    $eventListener = @($listeners | Where-Object LocalPort -eq $eventPort)
    $renodeUartListener = @($listeners | Where-Object LocalPort -eq $renodeUartPort)
    if ($eventListener.Count -ne 1 -or $uartListener.Count -ne 1 -or $monitorListener.Count -ne 1) { throw 'Expected exactly one listener for the application event, raw UART and privileged Renode monitor endpoints.' }
    if ($renodeUartListener.Count -ne 1 -or $clockConnections.Count -eq 0) {
        $listenerEvidence = ($listeners | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', '
        throw "Unexpected private listener counts: RenodeUART=$($renodeUartListener.Count)@$renodeUartPort ClockConnections=$($clockConnections.Count)@$clockPort; observed: $listenerEvidence"
    }
    $nonLoopbackListeners = @($listeners + $clockConnections | Where-Object { $_.LocalAddress -ne '127.0.0.1' })
    if ($nonLoopbackListeners.Count -gt 0) {
        @(
            'A-014 security negative test: FAIL'
            "Application under test: $AppPath"
            "Application event endpoint: $($eventListener[0].LocalAddress):$eventPort"
            "Robot Service UART endpoint: $($uartListener[0].LocalAddress):$uartPort"
            "Renode monitor endpoint: $($monitorListener[0].LocalAddress):$monitorPort"
            "Renode firmware UART endpoint: $($renodeUartListener[0].LocalAddress):$renodeUartPort"
            "Renode external-control endpoint: $($clockConnections[0].LocalAddress):$clockPort"
            'One or more application or Renode service listeners is not loopback-bound.'
            'Queue resource test: PASS bounded capacity applies backpressure and preserves event order without dropping.'
        ) | Set-Content -Encoding utf8 (Join-Path $evidence 'a014-security-summary.txt')
        Get-Content (Join-Path $evidence 'a014-security-summary.txt')
        throw 'A-014 fails because one or more RoboWorld/Renode endpoints is bound to a non-loopback interface.'
    }

    $lanAddress = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' } | Select-Object -First 1 -ExpandProperty IPAddress
    if (-not $lanAddress) { throw 'No usable non-loopback IPv4 address is available for the negative exposure test.' }
    foreach ($port in @($eventPort, $uartPort, $monitorPort, $renodeUartPort, $clockPort)) {
        $probe = [System.Net.Sockets.TcpClient]::new()
        try {
            $probe.Connect($lanAddress, $port)
            throw "Endpoint $port accepted a connection on non-loopback address $lanAddress."
        } catch [System.Net.Sockets.SocketException] { }
        finally { $probe.Dispose() }
    }
    @(
        "Application under test: $AppPath"
        "Loopback-only listeners: PASS Event=127.0.0.1:$eventPort ServiceUART=127.0.0.1:$uartPort RenodeMonitor=127.0.0.1:$monitorPort RenodeUART=127.0.0.1:$renodeUartPort RenodeClock=127.0.0.1:$clockPort"
        "Non-loopback connection attempts rejected: PASS address=$lanAddress endpoints=$eventPort,$uartPort,$monitorPort,$renodeUartPort,$clockPort"
        'Godot/Robot Service HELLO session handshake completed before event traffic: PASS'
    ) | Set-Content -Encoding utf8 (Join-Path $evidence 'a014-security-summary.txt')
    Get-Content (Join-Path $evidence 'a014-security-summary.txt')
} finally {
    if ($appProcess -and -not $appProcess.HasExited) { try { $appProcess.Kill($true); $appProcess.WaitForExit() } catch { } }
}
