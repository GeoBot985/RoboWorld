[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateSet('headless','idle','actuated','sensor','two-mcu')] [string]$Scenario,
    [int]$DurationSeconds = 60,
    [ValidateRange(0,3600)] [int]$SimulationDurationSeconds = 0,
    [ValidateSet(0,15,30,60,120,144)] [int]$RenderFps = 60,
    [int]$Width = 1152,
    [int]$Height = 648,
    [ValidateSet('project','exact','fast','auto')] [string]$ControllerMode = 'project',
    [switch]$BuildPackage,
    [switch]$DisableVsync,
    [switch]$DisablePerTickTraceFlush,
    [switch]$CaptureCharacterizationTrace,
    [switch]$CapturePresentationTrace,
    [switch]$ElevatePresentationCapture,
    [switch]$CaptureWindowPresentation,
    [switch]$MaintainWindowForeground,
    [string]$WindowPresentationProbePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/tools/window-presentation-probe.exe'),
    [string]$PresentMonPath = (Join-Path $env:TEMP 'PresentMon-2.6.0-x64.exe'),
    [string[]]$MotorCommandPattern = @(),
    [string]$PackageRoot
)

$ErrorActionPreference = 'Stop'
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
[System.Threading.Thread]::CurrentThread.CurrentUICulture = [System.Globalization.CultureInfo]::InvariantCulture
if ($null -eq ('BenchmarkOutputSink' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

public sealed class BenchmarkOutputSink : IDisposable
{
    private readonly StreamWriter writer;
    private readonly bool enqueueLines;
    private readonly object gate = new object();
    public ConcurrentQueue<string> Queue { get; } = new ConcurrentQueue<string>();
    public DataReceivedEventHandler Handler => new DataReceivedEventHandler(Receive);
    public BenchmarkOutputSink(string path, bool enqueue)
    {
        writer = new StreamWriter(path, false, new UTF8Encoding(false), 65536);
        writer.AutoFlush = true;
        enqueueLines = enqueue;
    }
    private void Receive(object sender, DataReceivedEventArgs args)
    {
        if (args.Data == null) return;
        if (enqueueLines) Queue.Enqueue(args.Data);
        lock (gate) writer.WriteLine(args.Data);
    }
    public void Dispose() { lock (gate) writer.Dispose(); }
}
'@
}
function Test-CsvHasDataRows([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    $reader = [IO.StreamReader]::new($Path)
    try {
        $header = $reader.ReadLine()
        $firstRow = $reader.ReadLine()
        return (-not [string]::IsNullOrWhiteSpace($header) -and -not [string]::IsNullOrWhiteSpace($firstRow))
    } finally { $reader.Dispose() }
}
if ($null -eq ('RoboWorldWindowActivation' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RoboWorldWindowActivation
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    public static bool IsForeground(IntPtr window) => window != IntPtr.Zero && GetForegroundWindow() == window;
    public static uint ForegroundProcessId()
    {
        var foreground = GetForegroundWindow();
        uint processId = 0;
        if (foreground != IntPtr.Zero) GetWindowThreadProcessId(foreground, out processId);
        return processId;
    }
    public static bool Activate(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        uint foregroundPid, targetPid;
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out foregroundPid);
        var targetThread = GetWindowThreadProcessId(window, out targetPid);
        var attached = foregroundThread != 0 && targetThread != 0 && foregroundThread != targetThread && AttachThreadInput(foregroundThread, targetThread, true);
        ShowWindow(window, 9);
        BringWindowToTop(window);
        var activated = SetForegroundWindow(window);
        var topmost = SetWindowPos(window, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        if (attached) AttachThreadInput(foregroundThread, targetThread, false);
        return activated && topmost && GetForegroundWindow() == window;
    }
}
'@
}
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root '.venv/Scripts/python.exe'
$controllerScript = Join-Path $root 'samples/controller/perf_controller.py'
$evidenceBase = Join-Path $root 'artifacts/evidence/PERF'
if ($DurationSeconds -le 0 -or $Width -lt 640 -or $Height -lt 480) { throw 'Invalid benchmark duration or resolution.' }
if (($CaptureCharacterizationTrace -or $MotorCommandPattern.Count -gt 0) -and ($Scenario -ne 'two-mcu' -or $ControllerMode -ne 'exact' -or $SimulationDurationSeconds -lt 12)) {
    throw 'Controller characterization capture/patterns require an Exact two-MCU run with at least a 12-second simulation horizon.'
}
if ($ElevatePresentationCapture -and -not $CapturePresentationTrace) { throw 'Elevated PresentMon requires -CapturePresentationTrace.' }
if ($CapturePresentationTrace -and $CaptureWindowPresentation) { throw 'Choose one independent presentation capture method per benchmark run.' }
if ($MaintainWindowForeground -and -not $CaptureWindowPresentation) { throw '-MaintainWindowForeground requires -CaptureWindowPresentation.' }
foreach ($command in $MotorCommandPattern) {
    if ($command -notmatch '^MOTOR (-?\d{1,3}) (-?\d{1,3})$' -or [Math]::Abs([int]$Matches[1]) -gt 100 -or [Math]::Abs([int]$Matches[2]) -gt 100) {
        throw "Invalid firmware-range MOTOR command pattern: $command"
    }
}
if ($BuildPackage) { & (Join-Path $root 'tools/package-windows.ps1') }
if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Join-Path $root 'artifacts/release/RoboWorld' }
$PackageRoot = [IO.Path]::GetFullPath($PackageRoot)
$projectSettings = Get-Content -LiteralPath (Join-Path $root 'godot/project.godot') -Raw
$projectVsyncMatch = [regex]::Match($projectSettings, '(?m)^window/vsync/vsync_mode=(\d+)\s*$')
if (-not $projectVsyncMatch.Success) { throw 'Could not determine the project V-Sync mode for benchmark metadata.' }
$projectVsyncMode = [int]$projectVsyncMatch.Groups[1].Value
$effectiveVsyncMode = if ($DisableVsync) { 0 } else { $projectVsyncMode }
$vsyncModeNames = @{ 0='disabled'; 1='enabled'; 2='adaptive'; 3='mailbox' }
$app = Join-Path $PackageRoot 'RoboWorld.exe'
if (-not (Test-Path -LiteralPath $app) -or -not (Test-Path -LiteralPath $python) -or -not (Test-Path -LiteralPath $controllerScript)) { throw 'A current exported package and the workspace Python runtime/controller are required.' }
if ($CapturePresentationTrace -and -not (Test-Path -LiteralPath $PresentMonPath)) { throw "PresentMon executable not found: $PresentMonPath" }
if ($CaptureWindowPresentation -and -not (Test-Path -LiteralPath $WindowPresentationProbePath)) { throw "Window presentation probe not found: $WindowPresentationProbePath. Build it with tools/build-window-presentation-probe.ps1." }

$runId = '{0}-{1}-wall{2}-sim{3}' -f (Get-Date -Format 'yyyyMMddTHHmmss'), $Scenario, $DurationSeconds, $SimulationDurationSeconds
$runDir = Join-Path $evidenceBase $runId
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$stdout = Join-Path $runDir 'godot-stdout.log'
$stderr = Join-Path $runDir 'godot-stderr.log'
$controllerLog = Join-Path $runDir 'controller-output.log'
$controllerError = Join-Path $runDir 'controller-stderr.log'
$processCsv = Join-Path $runDir 'process-samples.csv'
$threadCsv = Join-Path $runDir 'process-thread-samples.csv'
$controllerCsv = Join-Path $runDir 'controller-commands.csv'
$characterizationTracePath = if ($CaptureCharacterizationTrace) { Join-Path $runDir 'controller-characterization.jsonl' } else { $null }
$timingCsv = Join-Path $runDir 'phase-timing.csv'
$presentMonCsv = Join-Path $runDir 'presentmon.csv'
$presentMonStdout = Join-Path $runDir 'presentmon.stdout.log'
$presentMonStderr = Join-Path $runDir 'presentmon.stderr.log'
$presentMonOutput = Join-Path $runDir 'presentmon.output.log'
$windowPresentationCsv = Join-Path $runDir 'window-presentation.csv'
$foregroundMaintenanceCsv = Join-Path $runDir 'window-foreground-maintenance.csv'
$windowPresentationStdout = Join-Path $runDir 'window-presentation.stdout.log'
$windowPresentationStderr = Join-Path $runDir 'window-presentation.stderr.log'
$windowPresentationProcess = $null
$windowPresentationOutputState = $null
$windowPresentationErrorState = $null
$presentMonProcess = $null
$presentMonOutputState = $null
$presentMonErrorState = $null

$robotId = if ($Scenario -eq 'two-mcu') { 'rover-multi-01' } else { 'rover-01' }
$sensorObstacleZSequenceM = @(-1.5,-2.0,-2.5,-2.75)
$appArguments = [System.Collections.Generic.List[string]]::new()
if ($Scenario -eq 'headless') { $appArguments.Add('--headless') } else { $appArguments.Add('--windowed') }
if ($DisableVsync) { $appArguments.Add('--disable-vsync') }
$appArguments.Add('--')
$appArguments.Add("--robot-id=$robotId")
$appArguments.Add("--perf-scenario=$Scenario")
$appArguments.Add("--perf-duration-seconds=$DurationSeconds")
if ($SimulationDurationSeconds -gt 0) { $appArguments.Add("--perf-sim-duration-seconds=$SimulationDurationSeconds") }
if ($Scenario -in @('actuated','sensor','two-mcu') -and $SimulationDurationSeconds -gt 0) {
    $appArguments.Add("--perf-flush-ticks-each-record=$(-not $DisablePerTickTraceFlush)")
}
$appArguments.Add("--perf-output-dir=$runDir")
$appArguments.Add("--perf-render-fps=$RenderFps")
$appArguments.Add("--perf-width=$Width")
$appArguments.Add("--perf-height=$Height")
if ($ControllerMode -ne 'project') { $appArguments.Add("--controller-mode=$ControllerMode") }
if ($characterizationTracePath) { $appArguments.Add("--characterization-trace=$characterizationTracePath") }
if ($Scenario -eq 'sensor') { $appArguments.Add("--sensor-obstacle-z-sequence-m=$($sensorObstacleZSequenceM -join ',')") }

$processInfo = [Diagnostics.ProcessStartInfo]::new($app)
$processInfo.WorkingDirectory = $PackageRoot
$processInfo.UseShellExecute = $false
$processInfo.CreateNoWindow = $false
$processInfo.RedirectStandardOutput = $true
$processInfo.RedirectStandardError = $true
foreach ($argument in $appArguments) { [void]$processInfo.ArgumentList.Add($argument) }
$application = [Diagnostics.Process]::new()
$application.StartInfo = $processInfo
$outputState = [BenchmarkOutputSink]::new($stdout,$true)
$errorState = [BenchmarkOutputSink]::new($stderr,$false)
$application.add_OutputDataReceived($outputState.Handler)
$application.add_ErrorDataReceived($errorState.Handler)
if (-not $application.Start()) { throw 'Could not start the exported RoboWorld application.' }
$application.BeginOutputReadLine(); $application.BeginErrorReadLine()
$appPid = $application.Id
if ($CapturePresentationTrace -and $ElevatePresentationCapture) {
    $presentMonSession = 'RoboWorld-{0}-{1}' -f $appPid, (Get-Date -Format 'HHmmss')
    $presentMonCaptureSeconds = if ($SimulationDurationSeconds -gt 0) { [Math]::Max(30, $SimulationDurationSeconds + 30) } else { $DurationSeconds + 30 }
    $presentMonLauncher = Join-Path $runDir 'presentmon-elevated-launcher.ps1'
    $presentMonLauncherContent = @'
$ErrorActionPreference = 'Stop'
$exe = '__PRESENTMON_EXE__'
$arguments = @('--process_id', '__PRESENTMON_PID__', '--output_file', '__PRESENTMON_CSV__', '--v1_metrics', '--qpc_time', '--no_track_gpu', '--no_track_input', '--set_circular_buffer_size', '8192', '--session_name', '__PRESENTMON_SESSION__', '--stop_existing_session', '--timed', '__PRESENTMON_CAPTURE_SECONDS__', '--terminate_after_timed')
& $exe @arguments *> '__PRESENTMON_OUTPUT__'
exit $LASTEXITCODE
'@
    $presentMonLauncherContent = $presentMonLauncherContent.Replace('__PRESENTMON_EXE__', [IO.Path]::GetFullPath($PresentMonPath)).Replace('__PRESENTMON_CSV__', $presentMonCsv).Replace('__PRESENTMON_SESSION__', $presentMonSession).Replace('__PRESENTMON_OUTPUT__', $presentMonOutput).Replace('__PRESENTMON_CAPTURE_SECONDS__', [string]$presentMonCaptureSeconds).Replace('__PRESENTMON_PID__', [string]$appPid)
    Set-Content -LiteralPath $presentMonLauncher -Value $presentMonLauncherContent -Encoding utf8
    $launcherArgumentLine = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $presentMonLauncher
    $presentMonProcess = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList $launcherArgumentLine -Verb RunAs -WindowStyle Hidden -PassThru
}
$presentMonStartMonoTicks = if ($presentMonProcess) { [Diagnostics.Stopwatch]::GetTimestamp() } else { $null }
$startUtc = [DateTime]::UtcNow
$appStartMonoTicks = [Diagnostics.Stopwatch]::GetTimestamp()
$timer = [Diagnostics.Stopwatch]::StartNew()
$controllerProcess = $null
$windowActivationSucceeded = $null
$windowTopmostEnabled = $null
if ($CaptureWindowPresentation) {
    $windowReady = [Diagnostics.Stopwatch]::StartNew()
    do {
        $application.Refresh()
        $windowHandle = $application.MainWindowHandle
        if ($windowHandle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 50 }
    } while ($windowHandle -eq [IntPtr]::Zero -and -not $application.HasExited -and $windowReady.Elapsed.TotalSeconds -lt 20)
    if ($windowHandle -ne [IntPtr]::Zero) { $windowActivationSucceeded = [RoboWorldWindowActivation]::Activate($windowHandle); $windowTopmostEnabled = $windowActivationSucceeded }
    $windowPresentationInfo = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($WindowPresentationProbePath))
    $windowPresentationInfo.UseShellExecute = $false
    $windowPresentationInfo.CreateNoWindow = $true
    $windowPresentationInfo.RedirectStandardOutput = $true
    $windowPresentationInfo.RedirectStandardError = $true
    [void]$windowPresentationInfo.ArgumentList.Add([string]$appPid)
    [void]$windowPresentationInfo.ArgumentList.Add($windowPresentationCsv)
    $windowPresentationProcess = [Diagnostics.Process]::new()
    $windowPresentationProcess.StartInfo = $windowPresentationInfo
    $windowPresentationOutputState = [BenchmarkOutputSink]::new($windowPresentationStdout,$false)
    $windowPresentationErrorState = [BenchmarkOutputSink]::new($windowPresentationStderr,$false)
    $windowPresentationProcess.add_OutputDataReceived($windowPresentationOutputState.Handler)
    $windowPresentationProcess.add_ErrorDataReceived($windowPresentationErrorState.Handler)
    if (-not $windowPresentationProcess.Start()) { throw 'Could not start the desktop window presentation probe.' }
    $windowPresentationProcess.BeginOutputReadLine(); $windowPresentationProcess.BeginErrorReadLine()
}
if ($CapturePresentationTrace -and -not $ElevatePresentationCapture) {
    $presentMonArguments = @('--process_id', [string]$appPid, '--output_file', $presentMonCsv, '--v1_metrics', '--qpc_time', '--session_name', "RoboWorld-$appPid", '--stop_existing_session', '--terminate_on_proc_exit')
    $presentMonInfo = [Diagnostics.ProcessStartInfo]::new([IO.Path]::GetFullPath($PresentMonPath))
    $presentMonInfo.UseShellExecute = $false
    $presentMonInfo.CreateNoWindow = $true
    $presentMonInfo.RedirectStandardOutput = $true
    $presentMonInfo.RedirectStandardError = $true
    foreach ($argument in $presentMonArguments) { [void]$presentMonInfo.ArgumentList.Add($argument) }
    $presentMonProcess = [Diagnostics.Process]::new()
    $presentMonProcess.StartInfo = $presentMonInfo
    $presentMonOutputState = [BenchmarkOutputSink]::new($presentMonStdout,$false)
    $presentMonErrorState = [BenchmarkOutputSink]::new($presentMonStderr,$false)
    $presentMonProcess.add_OutputDataReceived($presentMonOutputState.Handler)
    $presentMonProcess.add_ErrorDataReceived($presentMonErrorState.Handler)
    if (-not $presentMonProcess.Start()) { throw 'Could not start PresentMon for the exported application PID.' }
    $presentMonProcess.BeginOutputReadLine(); $presentMonProcess.BeginErrorReadLine()
}
$controllerOutputState = $null
$controllerErrorState = $null
$lastSampleSeconds = -1
$lastCpuByPid = @{}
$processRows = [Collections.Generic.List[object]]::new()
$threadRows = [Collections.Generic.List[object]]::new()
$foregroundMaintenanceRows = [Collections.Generic.List[object]]::new()
$foregroundLossStartTicks = $null
$foregroundLossInitialPid = $null
$foregroundLossAttempts = 0
$foregroundMaintenanceNextTicks = [long]0
$phaseStart = $null
$phaseStartSimNs = $null
$clockEpochSimNs = $null
$phaseEnd = $null
$endpointPorts = @{}
$processHasExitedAt = $null
$watchdogTimeoutSeconds = $DurationSeconds + $(if ($CaptureCharacterizationTrace) { 960 } else { 120 })
$logicalProcessors = [Environment]::ProcessorCount
$machine = Get-CimInstance Win32_ComputerSystem
$processor = Get-CimInstance Win32_Processor | Select-Object -First 1
$os = Get-CimInstance Win32_OperatingSystem
$powerPlan = (& powercfg.exe /getactivescheme 2>$null | Out-String).Trim()
$firmwarePaths = @('samples/firmware/stm32-drive/build/firmware.elf','samples/firmware/stm32-arm/build/firmware.elf')
$expectedFirmwareBuildIds = @{
    'mcu-drive' = 'samples/firmware/stm32-drive/build/build-metadata.txt'
    'mcu-aux' = 'samples/firmware/stm32-arm/build/build-metadata.txt'
}
$firmwareHashes = @{}
foreach ($path in $firmwarePaths) { $full = Join-Path $PackageRoot $path; if (Test-Path -LiteralPath $full) { $firmwareHashes[$path] = (Get-FileHash -Algorithm SHA256 -LiteralPath $full).Hash } }

try {
    $lastSampleAt = [Diagnostics.Stopwatch]::StartNew()
    while (-not $application.HasExited) {
        if ($MaintainWindowForeground -and $windowHandle -ne [IntPtr]::Zero -and [Diagnostics.Stopwatch]::GetTimestamp() -ge $foregroundMaintenanceNextTicks) {
            $nowTicks = [Diagnostics.Stopwatch]::GetTimestamp()
            $foregroundMaintenanceNextTicks = $nowTicks + [long]([Diagnostics.Stopwatch]::Frequency / 10)
            if (-not [RoboWorldWindowActivation]::IsForeground($windowHandle)) {
                $foreground = [RoboWorldWindowActivation]::ForegroundProcessId()
                if ($null -eq $foregroundLossStartTicks) { $foregroundLossStartTicks = $nowTicks; $foregroundLossInitialPid = $foreground; $foregroundLossAttempts = 0 }
                $foregroundLossAttempts++
                if ([RoboWorldWindowActivation]::Activate($windowHandle)) {
                    $recoveryTicks = [Diagnostics.Stopwatch]::GetTimestamp()
                    $foregroundMaintenanceRows.Add([pscustomobject]@{ loss_start_mono_ticks=$foregroundLossStartTicks; recovery_mono_ticks=$recoveryTicks; loss_duration_ns=[long](([decimal]($recoveryTicks - $foregroundLossStartTicks) * 1000000000) / [decimal][Diagnostics.Stopwatch]::Frequency); initial_foreground_pid=$foregroundLossInitialPid; activation_attempts=$foregroundLossAttempts; recovered=$true })
                    $foregroundLossStartTicks = $null; $foregroundLossInitialPid = $null; $foregroundLossAttempts = 0
                }
            } elseif ($null -ne $foregroundLossStartTicks) {
                $recoveryTicks = [Diagnostics.Stopwatch]::GetTimestamp()
                $foregroundMaintenanceRows.Add([pscustomobject]@{ loss_start_mono_ticks=$foregroundLossStartTicks; recovery_mono_ticks=$recoveryTicks; loss_duration_ns=[long](([decimal]($recoveryTicks - $foregroundLossStartTicks) * 1000000000) / [decimal][Diagnostics.Stopwatch]::Frequency); initial_foreground_pid=$foregroundLossInitialPid; activation_attempts=$foregroundLossAttempts; recovered=$true })
                $foregroundLossStartTicks = $null; $foregroundLossInitialPid = $null; $foregroundLossAttempts = 0
            }
        }
        $newLines = [Collections.Generic.List[string]]::new()
        $line = $null
        while ($outputState.Queue.TryDequeue([ref]$line)) { $newLines.Add($line); $line = $null }
        foreach ($line in $newLines) {
            if ($line -match 'UART_ENDPOINT controller_id=([^\s]+).*?uart=tcp://127\.0\.0\.1:(\d+)') { $endpointPorts[$Matches[1]] = [int]$Matches[2] }
            if ($line -match '^PERF_PHASE_BEGIN .*?sim_time_ns=(\d+).*?mono_ticks=(\d+)') { $phaseStartSimNs = [long]$Matches[1]; $phaseStart = [long]$Matches[2] }
            if ($line -match '^GODOT_CLOCK_EPOCH sim_time_ns=(\d+)') { $clockEpochSimNs = [long]$Matches[1] }
            if ($line -match '^PERF_PHASE_END .*?mono_ticks=(\d+).*?wall_elapsed_ns=(\d+).*?committed_sim_ns=(\d+).*?rtf=([0-9.]+)') {
                $phaseEnd = [pscustomobject]@{ MonoTicks=[long]$Matches[1]; WallElapsedNs=[long]$Matches[2]; CommittedSimNs=[long]$Matches[3]; Rtf=[double]$Matches[4] }
            }
        }

        if ($null -ne $phaseStart -and $null -eq $controllerProcess -and $Scenario -in @('actuated','sensor','two-mcu')) {
            $requiredIds = if ($Scenario -eq 'two-mcu') { @('mcu-drive','mcu-aux') } else { @('drive-controller') }
            if ($Scenario -ne 'two-mcu') { $requiredIds = @('mcu-drive') }
            if (($requiredIds | Where-Object { -not $endpointPorts.ContainsKey($_) }).Count -eq 0) {
                $controllerArgs = [System.Collections.Generic.List[string]]::new()
                foreach ($id in $requiredIds) {
                    $controllerArgs.Add('--uart'); $controllerArgs.Add("$id=$($endpointPorts[$id])")
                    if ($ControllerMode -eq 'exact') {
                        $metadataPath = Join-Path $PackageRoot $expectedFirmwareBuildIds[$id]
                        if (-not (Test-Path -LiteralPath $metadataPath)) { throw "Firmware build metadata is missing for ${id}: $metadataPath" }
                        $metadata = Get-Content -LiteralPath $metadataPath -Raw
                        $buildIdMatch = [regex]::Match($metadata, '(?m)^Build ID: ([0-9a-f]{8})\s*$')
                        if (-not $buildIdMatch.Success) { throw "Firmware build metadata has no valid Build ID for $id." }
                        $controllerArgs.Add('--expected-boot'); $controllerArgs.Add("$id=$($buildIdMatch.Groups[1].Value.ToLowerInvariant())")
                    }
                }
                if ($SimulationDurationSeconds -gt 0) {
                    $controllerArgs.Add('--sim-duration-seconds'); $controllerArgs.Add([string]$SimulationDurationSeconds)
                    $controllerArgs.Add('--simulation-ticks-csv'); $controllerArgs.Add((Join-Path $runDir 'godot-ticks.csv'))
                    $controllerArgs.Add('--sim-phase-start-ns'); $controllerArgs.Add([string]$phaseStartSimNs)
                    # The project epoch is recorded separately. Schedule from the first committed phase tick so
                    # Exact/Fast workloads remain identical after sharing the controller startup epoch.
                    if ($null -eq $clockEpochSimNs) { throw 'The authoritative Godot clock epoch was not observed before controller startup.' }
                    $controllerArgs.Add('--sim-command-epoch-ns'); $controllerArgs.Add([string]($phaseStartSimNs + 5000000000L))
                } else {
                    $controllerArgs.Add('--run-seconds'); $controllerArgs.Add([string][Math]::Max(1,$DurationSeconds - 2))
                    $controllerArgs.Add('--cycle-seconds'); $controllerArgs.Add('3')
                }
                if ($Scenario -eq 'sensor') { $controllerArgs.Add('--sensor-query-every'); $controllerArgs.Add('3') }
                foreach ($command in $MotorCommandPattern) { $controllerArgs.Add('--motor-command'); $controllerArgs.Add($command) }
                if ($CaptureCharacterizationTrace) { $controllerArgs.Add('--response-timeout-seconds'); $controllerArgs.Add('45') }
                $controllerArgs.Add('--output'); $controllerArgs.Add($controllerCsv)
                $controllerInfo = [Diagnostics.ProcessStartInfo]::new($python)
                $controllerInfo.WorkingDirectory = $PackageRoot
                $controllerInfo.UseShellExecute = $false
                $controllerInfo.CreateNoWindow = $true
                $controllerInfo.RedirectStandardOutput = $true
                $controllerInfo.RedirectStandardError = $true
                foreach ($argument in @($controllerScript) + $controllerArgs.ToArray()) { [void]$controllerInfo.ArgumentList.Add($argument) }
                $controllerProcess = [Diagnostics.Process]::new()
                $controllerProcess.StartInfo = $controllerInfo
                $controllerOutputState = [BenchmarkOutputSink]::new($controllerLog,$false)
                $controllerErrorState = [BenchmarkOutputSink]::new($controllerError,$false)
                $controllerProcess.add_OutputDataReceived($controllerOutputState.Handler)
                $controllerProcess.add_ErrorDataReceived($controllerErrorState.Handler)
                if (-not $controllerProcess.Start()) { throw 'Could not start the firmware performance controller.' }
                $controllerProcess.BeginOutputReadLine(); $controllerProcess.BeginErrorReadLine()
            }
        }

        if ($lastSampleAt.Elapsed.TotalSeconds -ge 1) {
            $lastSampleAt.Restart()
            $all = Get-CimInstance Win32_Process
            $descendants = [Collections.Generic.HashSet[int]]::new()
            [void]$descendants.Add($appPid)
            $changed = $true
            while ($changed) {
                $changed = $false
                foreach ($procRow in $all) {
                    if ($descendants.Contains([int]$procRow.ParentProcessId) -and $descendants.Add([int]$procRow.ProcessId)) { $changed = $true }
                }
            }
            if ($controllerProcess -and -not $controllerProcess.HasExited) { [void]$descendants.Add($controllerProcess.Id) }
            $sampleMonoNs = [long](([decimal][Diagnostics.Stopwatch]::GetTimestamp() * [decimal]1000000000) / [decimal][Diagnostics.Stopwatch]::Frequency)
            foreach ($processId in $descendants) {
                try {
                    $proc = [Diagnostics.Process]::GetProcessById($processId); $proc.Refresh()
                    $cpu = $proc.TotalProcessorTime.TotalSeconds
                    $cpuPct = $null
                    if ($lastCpuByPid.ContainsKey($processId)) {
                        $previous = $lastCpuByPid[$processId]
                        $cpuPct = 100.0 * ($cpu - $previous.CpuSeconds) / (($sampleMonoNs - $previous.MonoNs) / 1e9) / $logicalProcessors
                    }
                    $lastCpuByPid[$processId] = @{ CpuSeconds=$cpu; MonoNs=$sampleMonoNs }
                    $processRows.Add([pscustomobject]@{ sample_mono_ns=$sampleMonoNs; pid=$processId; parent_pid=($all | Where-Object ProcessId -eq $processId | Select-Object -First 1 -ExpandProperty ParentProcessId); process_name=$proc.ProcessName; cpu_total_s=$cpu; cpu_percent_host=$cpuPct; working_set_bytes=$proc.WorkingSet64; private_bytes=$proc.PrivateMemorySize64; thread_count=$proc.Threads.Count })
                    foreach ($thread in @($proc.Threads | Sort-Object { $_.TotalProcessorTime.Ticks } -Descending | Select-Object -First 8)) {
                        $threadRows.Add([pscustomobject]@{ sample_mono_ns=$sampleMonoNs; pid=$processId; process_name=$proc.ProcessName; thread_id=$thread.Id; thread_cpu_total_s=$thread.TotalProcessorTime.TotalSeconds; state=[string]$thread.ThreadState })
                    }
                } catch { }
            }
        }
        if ($timer.Elapsed.TotalSeconds -gt $watchdogTimeoutSeconds) { throw "Performance application exceeded the $watchdogTimeoutSeconds s startup/run watchdog." }
        Start-Sleep -Milliseconds 50
    }
    $application.WaitForExit()
    $application.WaitForExit()
    if ($presentMonProcess -and -not $presentMonProcess.HasExited) {
        $elapsedCaptureSeconds = [Diagnostics.Stopwatch]::GetElapsedTime($presentMonStartMonoTicks).TotalSeconds
        $remainingCaptureMs = [int][Math]::Max(0, ($presentMonCaptureSeconds - $elapsedCaptureSeconds + 10) * 1000)
        $presentMonProcess.WaitForExit($remainingCaptureMs) | Out-Null
    }
    if ($windowPresentationProcess -and -not $windowPresentationProcess.HasExited) { $windowPresentationProcess.WaitForExit(10000) | Out-Null }
    # Drain the redirected streams through process exit, then recover the
    # authoritative phase markers from the flushed raw log. DataReceived can
    # deliver its final callback just after HasExited becomes true.
    $stdoutText = Get-Content -Raw -LiteralPath $stdout
    if ($null -eq $phaseStart -and $stdoutText -match '(?m)^PERF_PHASE_BEGIN .*?sim_time_ns=(\d+).*?mono_ticks=(\d+)') { $phaseStartSimNs = [long]$Matches[1]; $phaseStart = [long]$Matches[2] }
    if ($stdoutText -match '(?m)^PERF_PHASE_END .*?mono_ticks=(\d+).*?wall_elapsed_ns=(\d+).*?committed_sim_ns=(\d+).*?rtf=([0-9.]+)') {
        $phaseEnd = [pscustomobject]@{ MonoTicks=[long]$Matches[1]; WallElapsedNs=[long]$Matches[2]; CommittedSimNs=[long]$Matches[3]; Rtf=[double]$Matches[4] }
    }
    if ($application.ExitCode -ne 0) { throw "Exported application exited with code $($application.ExitCode). See $stdout and $stderr" }
    if ($null -eq $phaseStart -or $null -eq $phaseEnd) { throw 'Performance phase markers were incomplete; no acceptance result can be calculated.' }
    if ($controllerProcess) {
        $controllerProcess.WaitForExit()
        if ($controllerProcess.ExitCode -ne 0) { throw "Firmware performance controller exited with code $($controllerProcess.ExitCode). See $controllerError" }
    }
    if ($Scenario -in @('actuated','sensor','two-mcu')) {
        $requiredIds = if ($Scenario -eq 'two-mcu') { @('mcu-drive','mcu-aux') } else { @('mcu-drive') }
        foreach ($controllerId in $requiredIds) {
            if (-not $endpointPorts.ContainsKey($controllerId)) { throw "Required firmware UART endpoint '$controllerId' was not discovered." }
        }
        if ($null -eq $controllerProcess) { throw 'The firmware performance controller did not start.' }
        $motorRows = @(Import-Csv -LiteralPath $controllerCsv | Where-Object event -eq 'motor')
        if ($motorRows.Count -lt 2) { throw 'The performance controller did not record enough successful firmware motor commands.' }
    }
} finally {
    if ($null -ne $foregroundLossStartTicks) {
        $endTicks = [Diagnostics.Stopwatch]::GetTimestamp()
        $foregroundMaintenanceRows.Add([pscustomobject]@{ loss_start_mono_ticks=$foregroundLossStartTicks; recovery_mono_ticks=$endTicks; loss_duration_ns=[long](([decimal]($endTicks - $foregroundLossStartTicks) * 1000000000) / [decimal][Diagnostics.Stopwatch]::Frequency); initial_foreground_pid=$foregroundLossInitialPid; activation_attempts=$foregroundLossAttempts; recovered=$false })
    }
    if ($application -and -not $application.HasExited) { try { $application.Kill($true); $application.WaitForExit() } catch { } }
    if ($controllerProcess -and -not $controllerProcess.HasExited) { try { $controllerProcess.Kill($true); $controllerProcess.WaitForExit() } catch { } }
    if ($presentMonProcess -and -not $presentMonProcess.HasExited) { try { $presentMonProcess.Kill($true); $presentMonProcess.WaitForExit() } catch { } }
    if ($windowPresentationProcess -and -not $windowPresentationProcess.HasExited) { try { $windowPresentationProcess.Kill($true); $windowPresentationProcess.WaitForExit() } catch { } }
    if ($outputState) { $outputState.Dispose() }
    if ($errorState) { $errorState.Dispose() }
    if ($controllerOutputState) { $controllerOutputState.Dispose() }
    if ($controllerErrorState) { $controllerErrorState.Dispose() }
    if ($presentMonOutputState) { $presentMonOutputState.Dispose() }
    if ($presentMonErrorState) { $presentMonErrorState.Dispose() }
    if ($windowPresentationOutputState) { $windowPresentationOutputState.Dispose() }
    if ($windowPresentationErrorState) { $windowPresentationErrorState.Dispose() }
    $processRows | Export-Csv -LiteralPath $processCsv -NoTypeInformation -Encoding utf8
    $threadRows | Export-Csv -LiteralPath $threadCsv -NoTypeInformation -Encoding utf8
    if ($MaintainWindowForeground) { $foregroundMaintenanceRows | Export-Csv -LiteralPath $foregroundMaintenanceCsv -NoTypeInformation -Encoding utf8 }
    if ($null -ne $phaseStart -and $null -ne $phaseEnd) {
        @([pscustomobject]@{ phase='active';start_mono_ticks=$phaseStart;end_mono_ticks=$phaseEnd.MonoTicks;stopwatch_frequency=[Diagnostics.Stopwatch]::Frequency;wall_elapsed_ns=$phaseEnd.WallElapsedNs;committed_sim_ns=$phaseEnd.CommittedSimNs;rtf=$phaseEnd.Rtf;startup_wall_s=([Diagnostics.Stopwatch]::GetElapsedTime($appStartMonoTicks,$phaseStart).TotalSeconds) }) | Export-Csv -LiteralPath $timingCsv -NoTypeInformation -Encoding utf8
    }
    $physicsConfigMatch = [regex]::Match($stdoutText, '(?m)^PHYSICS_CLOCK_CONFIGURATION physics_ticks_setting_hz=(\d+) time_scale=([0-9.]+) physics_jitter_fix=(-?\d+) target_server_step_hz=(\d+) authoritative_clock_hz=(\d+)')
    $workingTreeDirty = -not [string]::IsNullOrWhiteSpace((& git -C $root status --porcelain))
    $managedAssemblyPath = Join-Path $PackageRoot 'data_RoboWorld.Godot_windows_x86_64/RoboWorld.Godot.dll'
    $measurementStatus = if ($null -eq $phaseEnd) { 'INCOMPLETE_NO_ACTIVE_MEASUREMENT' } elseif ($application.ExitCode -eq 0) { 'MEASURED' } else { 'APPLICATION_FAILED' }
    $metadata = [ordered]@{
        run_id=$runId; scenario=$Scenario; duration_seconds=$DurationSeconds; watchdog_timeout_seconds=$watchdogTimeoutSeconds; simulation_duration_seconds=$SimulationDurationSeconds; render_fps_cap=$RenderFps; vsync_enabled=($effectiveVsyncMode -ne 0); project_vsync_mode=$projectVsyncMode; effective_vsync_mode=$effectiveVsyncMode; effective_vsync_mode_name=$vsyncModeNames[$effectiveVsyncMode]; vsync_disabled_cli_override=[bool]$DisableVsync; controller_mode=$ControllerMode; motor_command_pattern=$MotorCommandPattern; characterization_trace=$(if ($characterizationTracePath) { 'controller-characterization.jsonl' } else { $null }); characterization_trace_sha256=$(if ($characterizationTracePath -and (Test-Path -LiteralPath $characterizationTracePath)) { (Get-FileHash -LiteralPath $characterizationTracePath -Algorithm SHA256).Hash }); requested_physics_hz=$(if ($physicsConfigMatch.Success) { [int]$physicsConfigMatch.Groups[5].Value } else { $null }); godot_physics_ticks_setting_hz=$(if ($physicsConfigMatch.Success) { [int]$physicsConfigMatch.Groups[1].Value } else { $null }); godot_time_scale=$(if ($physicsConfigMatch.Success) { [double]::Parse($physicsConfigMatch.Groups[2].Value,[Globalization.CultureInfo]::InvariantCulture) } else { $null }); godot_physics_jitter_fix=$(if ($physicsConfigMatch.Success) { [int]$physicsConfigMatch.Groups[3].Value } else { $null }); target_server_step_hz=$(if ($physicsConfigMatch.Success) { [int]$physicsConfigMatch.Groups[4].Value } else { $null }); sensor_obstacle_z_sequence_m=$(if ($Scenario -eq 'sensor') { $sensorObstacleZSequenceM } else { $null }); resolution="${Width}x${Height}"; headless=($Scenario -eq 'headless'); robot_id=$robotId
        started_utc=$startUtc.ToString('o'); startup_wall_s=$(if ($null -ne $phaseStart) { [Diagnostics.Stopwatch]::GetElapsedTime($appStartMonoTicks,$phaseStart).TotalSeconds } else { $null }); git_sha=(& git -C $root rev-parse HEAD).Trim(); source_tree_dirty=$workingTreeDirty; main_cs_sha256=(Get-FileHash -LiteralPath (Join-Path $root 'godot/Main.cs') -Algorithm SHA256).Hash; toolchain_lock_sha256=(Get-FileHash -LiteralPath (Join-Path $root 'toolchain.lock.json') -Algorithm SHA256).Hash
        package_root=$PackageRoot; executable_sha256=(Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash; managed_assembly_sha256=$(if (Test-Path -LiteralPath $managedAssemblyPath) { (Get-FileHash -LiteralPath $managedAssemblyPath -Algorithm SHA256).Hash } else { $null }); package_manifest_sha256=(Get-FileHash -LiteralPath (Join-Path $PackageRoot 'SHA256SUMS.csv') -Algorithm SHA256).Hash; firmware_sha256=$firmwareHashes
        os_caption=$os.Caption; os_version=$os.Version; cpu_count=$logicalProcessors; cpu_name=$processor.Name; machine_model=$machine.Model; memory_bytes=$machine.TotalPhysicalMemory; power_plan=$powerPlan
        godot_version='4.7.2-stable'; robot_service_runtime='.NET 10 self-contained'; renode_version='1.17.0 patched'; stopwatch_frequency=[Diagnostics.Stopwatch]::Frequency
        endpoint_ports=$endpointPorts; clock_epoch_sim_ns=$clockEpochSimNs; tick_trace_flush_each_record=(-not $DisablePerTickTraceFlush); presentation_capture_requested=[bool]$CapturePresentationTrace; presentation_capture_elevated=[bool]$ElevatePresentationCapture; presentation_capture_status=$(if (-not $CapturePresentationTrace) { 'NOT_REQUESTED' } elseif ((Test-CsvHasDataRows $presentMonCsv) -and $presentMonProcess -and $presentMonProcess.HasExited -and $presentMonProcess.ExitCode -eq 0) { 'CAPTURE_COMPLETE' } else { 'CAPTURE_FAILED' }); presentation_capture_csv=$(if ($CapturePresentationTrace -and (Test-CsvHasDataRows $presentMonCsv)) { 'presentmon.csv' } else { $null }); presentmon_executable=$(if ($CapturePresentationTrace) { [IO.Path]::GetFullPath($PresentMonPath) } else { $null }); presentmon_exit_code=$(if ($presentMonProcess -and $presentMonProcess.HasExited) { $presentMonProcess.ExitCode } else { $null }); window_presentation_capture_requested=[bool]$CaptureWindowPresentation; window_presentation_capture_status=$(if (-not $CaptureWindowPresentation) { 'NOT_REQUESTED' } elseif ((Test-CsvHasDataRows $windowPresentationCsv) -and $windowPresentationProcess -and $windowPresentationProcess.HasExited -and $windowPresentationProcess.ExitCode -eq 0) { 'CAPTURE_COMPLETE' } else { 'CAPTURE_FAILED' }); window_presentation_capture_csv=$(if ($CaptureWindowPresentation -and (Test-CsvHasDataRows $windowPresentationCsv)) { 'window-presentation.csv' } else { $null }); window_presentation_probe_sha256=$(if ($CaptureWindowPresentation) { (Get-FileHash -LiteralPath $WindowPresentationProbePath -Algorithm SHA256).Hash } else { $null }); window_presentation_probe_exit_code=$(if ($windowPresentationProcess -and $windowPresentationProcess.HasExited) { $windowPresentationProcess.ExitCode } else { $null }); window_activation_succeeded=$windowActivationSucceeded; window_topmost_enabled=$windowTopmostEnabled; foreground_maintenance_enabled=[bool]$MaintainWindowForeground; foreground_maintenance_period_ms=$(if ($MaintainWindowForeground) { 100 } else { $null }); foreground_maintenance_event_count=$(if ($MaintainWindowForeground) { $foregroundMaintenanceRows.Count } else { 0 }); foreground_maintenance_csv=$(if ($MaintainWindowForeground) { 'window-foreground-maintenance.csv' } else { $null }); application_exit_code=$application.ExitCode; measurement_status=$measurementStatus; performance_phase=$phaseEnd; output_directory=$runDir
    }
    $metadata | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $runDir 'run.meta.json') -Encoding utf8
}

Write-Output "PERFORMANCE_RUN_COMPLETE path=$runDir scenario=$Scenario rtf=$($phaseEnd.Rtf) sim_ns=$($phaseEnd.CommittedSimNs) wall_ns=$($phaseEnd.WallElapsedNs)"
