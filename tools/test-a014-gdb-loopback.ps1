$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$renodeRoot = Join-Path $root '.toolchain/renode/renode_1.17.0-portable'
$renode = Join-Path $renodeRoot 'renode.exe'
$firmware = Join-Path $root 'samples/firmware/stm32-drive/build/firmware.elf'
$stock = Join-Path $renodeRoot 'scripts/single-node/stm32f4_discovery.resc'
$evidence = Join-Path $root 'artifacts/evidence/T05'
$resc = Join-Path $evidence 'a014-gdb-probe.resc'
$stdout = Join-Path $evidence 'a014-gdb-probe.log'
$stderr = Join-Path $evidence 'a014-gdb-probe.stderr.log'
foreach ($path in @($renode, $firmware, $stock)) { if (-not (Test-Path -LiteralPath $path)) { throw "Missing GDB probe input: $path" } }
New-Item -ItemType Directory -Force -Path $evidence | Out-Null

$portPicker = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$portPicker.Start()
$monitorPort = ([System.Net.IPEndPoint]$portPicker.LocalEndpoint).Port
$portPicker.Stop()
$firmwarePath = $firmware.Replace('\', '/')
$stockPath = $stock.Replace('\', '/')
$rescContents = @(
    ':name: RoboWorld loopback GDB security probe'
    ('$bin?=@' + $firmwarePath)
    "include @$stockPath"
    'machine StartGdbServer 0'
    'start'
) -join "`n"
[System.IO.File]::WriteAllText($resc, $rescContents, [System.Text.Encoding]::ASCII)

$process = $null
try {
    $arguments = '--disable-xwt --plain --port {0} --execute "i @{1}"' -f $monitorPort, $resc.Replace('\', '/')
    $process = Start-Process -FilePath $renode -ArgumentList $arguments -WorkingDirectory $renodeRoot -PassThru -NoNewWindow -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $listeners = @()
    $gdbPort = 0
    do {
        if ($process.HasExited) { throw "Renode exited during the GDB security probe. See $stderr" }
        $listeners = @(Get-NetTCPConnection -State Listen -OwningProcess $process.Id -ErrorAction SilentlyContinue)
        $gdbListener = @($listeners | Where-Object LocalPort -ne $monitorPort)
        if ($listeners.Count -ge 2 -and $gdbListener.Count -eq 1) { $gdbPort = [int]$gdbListener[0].LocalPort; break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($gdbPort -eq 0) { throw "Renode did not expose exactly one GDB listener alongside its monitor. See $stdout" }
    $nonLoopbackListeners = @($listeners | Where-Object LocalAddress -ne '127.0.0.1')
    if ($nonLoopbackListeners.Count -gt 0) {
        throw "Renode GDB/monitor listeners are not loopback-only: $(($listeners | ForEach-Object { "$($_.LocalAddress):$($_.LocalPort)" }) -join ', ')"
    }

    $gdb = [System.Net.Sockets.TcpClient]::new()
    try { $gdb.Connect('127.0.0.1', $gdbPort) }
    finally { $gdb.Dispose() }

    $lanAddress = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred' } | Select-Object -First 1 -ExpandProperty IPAddress
    if (-not $lanAddress) { throw 'No usable non-loopback IPv4 address is available for the GDB exposure test.' }
    foreach ($port in @($monitorPort, $gdbPort)) {
        $probe = [System.Net.Sockets.TcpClient]::new()
        try {
            $probe.Connect($lanAddress, $port)
            throw "Renode endpoint $port accepted a non-loopback connection on $lanAddress."
        } catch [System.Net.Sockets.SocketException] { }
        finally { $probe.Dispose() }
    }

    @(
        "Renode GDB listener: PASS 127.0.0.1:$gdbPort; local attach accepted"
        "Renode monitor listener: PASS 127.0.0.1:$monitorPort"
        "Non-loopback GDB and monitor connections rejected: PASS address=$lanAddress"
        "Renode version: $(& $renode --version | Select-Object -First 1)"
    ) | Set-Content -LiteralPath (Join-Path $evidence 'a014-gdb-summary.txt') -Encoding utf8
    Get-Content (Join-Path $evidence 'a014-gdb-summary.txt')
} finally {
    if ($process -and -not $process.HasExited) { try { $process.Kill($true); $process.WaitForExit() } catch { } }
}
