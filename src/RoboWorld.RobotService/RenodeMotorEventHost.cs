using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Google.Protobuf;
using RoboWorld.Contracts.V1;

namespace RoboWorld.RobotService;

public sealed record RenodeControllerOptions(string Id, string ComponentInstanceId, string FirmwarePath, string Channel1ActuatorId, string Channel2ActuatorId);

/// <summary>Runs the reference MCU in Renode and forwards validated timer/GPIO output samples over the v1 protobuf event stream.</summary>
public sealed class RenodeMotorEventHost : IAsyncDisposable
{
    private const int ProtocolMajor = 1;
    private const int ProtocolMinor = 0;
    private const int MaxFrameBytes = 64 * 1024;
    private const uint Tim3Base = 0x40000400;
    private const uint GpioABase = 0x40020000;
    private const uint GpioDBase = 0x40020C00;
    private sealed record StagedUartIngress(byte[] Bytes, long ArrivalBoundaryLowerBoundNs, long ArrivalHostTicks);
    private sealed class ControllerRuntime(RenodeControllerOptions options, string machineName, int renodeUartPort)
    {
        public RenodeControllerOptions Options { get; } = options;
        public string MachineName { get; } = machineName;
        public int RenodeUartPort { get; } = renodeUartPort;
        public int ServiceUartPort { get; set; }
        public TcpListener? Listener { get; set; }
        public TcpClient? UpstreamClient { get; set; }
        public TcpClient? Client { get; set; }
        public NetworkStream? UpstreamStream { get; set; }
        public Task<TcpClient>? PendingClient { get; set; }
        public double? Channel1Duty { get; set; }
        public double? Channel2Duty { get; set; }
        public int FastOutputSamplingTicksRemaining { get; set; }
        public Channel<StagedUartIngress> UartIngress { get; } = Channel.CreateBounded<StagedUartIngress>(
            new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    }
    private readonly string _root;
    private readonly int? _routeFaultAfterMs;
    private readonly int? _cpuHaltAfterMs;
    private readonly long? _routeFaultAtSimTimeNs;
    private readonly long? _cpuHaltAtSimTimeNs;
    private readonly string? _firmwareOverride;
    private readonly string _channel1ActuatorId;
    private readonly string _channel2ActuatorId;
    private readonly string? _haltControllerId;
    private readonly IReadOnlyList<RenodeControllerOptions>? _controllerOverrides;
    private readonly string? _performanceTraceDirectory;
    private readonly string _performanceScenario;
    private readonly string? _characterizationTracePath;
    private ControllerCharacterizationTrace? _characterizationTrace;
    private readonly ConcurrentDictionary<ulong, TickIngress> _tickIngress = new();
    private readonly ConcurrentDictionary<ulong, long> _tickReceiptHostTicks = new();
    private RobotServicePerformanceTrace? _performanceTrace;
    private int _simulationQueueDepth;
    private int _simulationQueueHighWatermark;
    private readonly List<ControllerRuntime> _controllers = [];
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly CancellationTokenSource _stop = new();
    private Process? _renode;
    private TcpClient? _monitor;
    private NetworkStream? _monitorStream;
    private TcpListener? _eventListener;
    private TcpClient? _eventClient;
    private NetworkStream? _eventStream;
    private RenodeExternalControlClient? _renodeClock;
    private long _clockEpochNs;
    private long _coordinatedTimeNs;
    private long _renodeAdvancedTimeNs;
    private readonly Channel<Envelope> _simulationTicks = Channel.CreateBounded<Envelope>(new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true });
    private Task<TcpClient>? _pendingEventClient;
    private string? _rescPath;
    private string? _lastFault;
    private double? _leftDuty;
    private double? _rightDuty;
    private long _sequence;
    private bool _routeFaultInjected;
    private bool _cpuHaltInjected;
    private Envelope? _pendingDistanceSample;
    private long _echoReleaseNs;
    private bool _echoHigh;
    private ulong _lastTickIndex;
    private long _lastClockVerifyNs;
    private long _lastClockVerifyHostTicks;

    private sealed record PeripheralSample(long SimTimeNs, uint Cr1, uint Ccmr1, uint Ccer, uint Arr, uint Ccr1, uint Ccr2, uint GpioAModer, uint GpioAAfrl, uint GpioDModer, uint GpioDOdr);
    private sealed class TickIngress(long frameReadNs, long protobufParseNs, long queuedAtTicks, int queueDepth, int queueHighWatermark)
    {
        public long FrameReadNs { get; } = frameReadNs;
        public long ProtobufParseNs { get; } = protobufParseNs;
        public long QueuedAtTicks { get; } = queuedAtTicks;
        public int QueueDepth { get; } = queueDepth;
        public int QueueHighWatermark { get; } = queueHighWatermark;
        public long ChannelWriteBlockedNs { get; set; }
    }

    public RenodeMotorEventHost(string root, int? routeFaultAfterMs = null, int? cpuHaltAfterMs = null, string? firmwarePath = null,
        string channel1ActuatorId = "motor-left", string channel2ActuatorId = "motor-right", string? haltControllerId = null,
        IReadOnlyList<RenodeControllerOptions>? controllerOverrides = null, string? performanceTraceDirectory = null, string performanceScenario = "unspecified",
        string? characterizationTracePath = null, long? routeFaultAtSimTimeNs = null, long? cpuHaltAtSimTimeNs = null)
    {
        if (routeFaultAfterMs is not null && routeFaultAtSimTimeNs is not null
            || cpuHaltAfterMs is not null && cpuHaltAtSimTimeNs is not null)
            throw new ArgumentException("Choose either host-delay or simulation-boundary fault injection for each fault type.");
        if (routeFaultAtSimTimeNs is <= 0 || cpuHaltAtSimTimeNs is <= 0)
            throw new ArgumentOutOfRangeException(nameof(routeFaultAtSimTimeNs), "Simulation-time fault boundaries must be positive.");
        _root = Path.GetFullPath(root);
        _routeFaultAfterMs = routeFaultAfterMs;
        _cpuHaltAfterMs = cpuHaltAfterMs;
        _routeFaultAtSimTimeNs = routeFaultAtSimTimeNs;
        _cpuHaltAtSimTimeNs = cpuHaltAtSimTimeNs;
        _firmwareOverride = firmwarePath is null ? null : Path.GetFullPath(firmwarePath);
        _channel1ActuatorId = channel1ActuatorId;
        _channel2ActuatorId = channel2ActuatorId;
        _haltControllerId = haltControllerId;
        _controllerOverrides = controllerOverrides;
        _performanceTraceDirectory = performanceTraceDirectory;
        _performanceScenario = performanceScenario;
        _characterizationTracePath = characterizationTracePath is null ? null : Path.GetFullPath(characterizationTracePath);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        if (_performanceTraceDirectory is not null) _performanceTrace = new RobotServicePerformanceTrace(_performanceTraceDirectory, _performanceScenario);
        if (_characterizationTracePath is not null) _characterizationTrace = new ControllerCharacterizationTrace(_characterizationTracePath);
        var firmwareDir = Path.Combine(_root, "samples", "firmware", "stm32-drive");
        var firmware = _firmwareOverride ?? Path.Combine(firmwareDir, "build", "firmware.elf");
        var configuredControllers = _controllerOverrides ?? [new RenodeControllerOptions("mcu-drive", "mcu-board", firmware, _channel1ActuatorId, _channel2ActuatorId)];
        if (configuredControllers.Count == 0) throw new InvalidDataException("At least one firmware controller assignment is required.");
        var duplicateController = configuredControllers.GroupBy(item => item.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicateController is not null) throw new InvalidDataException($"Duplicate runtime controller id {duplicateController.Key}.");
        foreach (var assignment in configuredControllers)
            if (!File.Exists(assignment.FirmwarePath)) throw new FileNotFoundException($"Build firmware for controller {assignment.Id} before starting the Renode motor host.", assignment.FirmwarePath);

        var renodeExe = Directory.EnumerateFiles(Path.Combine(_root, ".toolchain", "renode"), "renode.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new FileNotFoundException("Pinned Renode executable was not found.");
        var renodeRoot = Path.GetDirectoryName(renodeExe)!;
        var stockScript = Path.Combine(renodeRoot, "scripts", "single-node", "stm32f4_discovery.resc");
        if (!File.Exists(stockScript)) throw new FileNotFoundException("Pinned STM32F4 Discovery platform script was not found.", stockScript);

        for (var index = 0; index < configuredControllers.Count; index++)
            _controllers.Add(new ControllerRuntime(configuredControllers[index], $"mcu_{index + 1}", ReservePort()));
        var monitorPort = ReservePort();
        var clockPort = ReservePort();
        _eventListener = new TcpListener(IPAddress.Loopback, 0);
        _eventListener.Start(1);
        var eventPort = ((IPEndPoint)_eventListener.LocalEndpoint).Port;
        _pendingEventClient = _eventListener.AcceptTcpClientAsync(token).AsTask();
        var sessionDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RoboWorld", "sessions", _sessionId);
        Directory.CreateDirectory(sessionDirectory);
        _rescPath = Path.Combine(sessionDirectory, "runtime.resc");
        WriteRenodeScript(_rescPath, _controllers, stockScript, clockPort);

        var start = new ProcessStartInfo(renodeExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = renodeRoot
        };
        start.ArgumentList.Add("--disable-xwt");
        start.ArgumentList.Add("--plain");
        start.ArgumentList.Add("--port");
        start.ArgumentList.Add(monitorPort.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--execute");
        start.ArgumentList.Add($"i @{_rescPath.Replace('\\', '/')}");
        _renode = new Process { StartInfo = start, EnableRaisingEvents = true };
        _renode.OutputDataReceived += (_, eventArgs) => { if (!string.IsNullOrWhiteSpace(eventArgs.Data)) Console.WriteLine($"RENODE {eventArgs.Data}"); };
        _renode.ErrorDataReceived += (_, eventArgs) => { if (!string.IsNullOrWhiteSpace(eventArgs.Data)) Console.Error.WriteLine($"RENODE {eventArgs.Data}"); };
        _renode.Exited += (_, _) => Console.Error.WriteLine("RENODE_EXITED");
        if (!_renode.Start()) throw new InvalidOperationException("Could not start Renode.");
        _renode.BeginOutputReadLine();
        _renode.BeginErrorReadLine();

        try
        {
            await WaitForPortAsync(monitorPort, TimeSpan.FromSeconds(20), token);
            await WaitForPortAsync(clockPort, TimeSpan.FromSeconds(20), token);
            _monitor = new TcpClient { NoDelay = true };
            await _monitor.ConnectAsync(IPAddress.Loopback, monitorPort, token);
            _monitorStream = _monitor.GetStream();
            _monitorStream.ReadTimeout = 3000;
            await ReadMonitorPromptAsync(_monitorStream, token); // consume the startup banner and prompt
            await ReadMonitorPromptAsync(_monitorStream, token); // wait for the runtime script to finish loading every configured MCU
            _renodeClock = await RenodeExternalControlClient.ConnectAsync(clockPort, token);
            Console.WriteLine($"RENODE_CLOCK_READY sim_time_ns={await _renodeClock.GetTimeNsAsync(token)}");

            foreach (var controller in _controllers)
            {
                await WaitForPortAsync(controller.RenodeUartPort, TimeSpan.FromSeconds(20), token);
                controller.UpstreamClient = new TcpClient { NoDelay = true };
                await controller.UpstreamClient.ConnectAsync(IPAddress.Loopback, controller.RenodeUartPort, token);
                controller.UpstreamStream = controller.UpstreamClient.GetStream();
                controller.Listener = new TcpListener(IPAddress.Loopback, 0);
                controller.Listener.Start(1);
                controller.ServiceUartPort = ((IPEndPoint)controller.Listener.LocalEndpoint).Port;
                controller.PendingClient = controller.Listener.AcceptTcpClientAsync(token).AsTask();
            }
            // Reset after every Renode UART terminal is attached so the first BOOT line is captured.
            // The stock scripts share reset state across machines, so reload each controller's assigned
            // ELF after its reset while that machine is selected. This preserves per-controller identity.
            foreach (var controller in _controllers)
            {
                await SelectMachineAsync(controller, token);
                await ExecuteMonitorCommandAsync("machine Reset", token);
                var firmwareForMachine = controller.Options.FirmwarePath.Replace('\\', '/');
                await ExecuteMonitorCommandAsync("sysbus LoadELF @" + firmwareForMachine, token);
            }
            await _renodeClock.RunForAsync(RobotServiceSimulationClock.ControllerStartupEpochNs, token); // Start virtual peripherals and allow each firmware to emit its boot line.
            _clockEpochNs = await _renodeClock.GetTimeNsAsync(token);
            if (_clockEpochNs != RobotServiceSimulationClock.ControllerStartupEpochNs)
                throw new InvalidDataException($"Renode startup clock epoch {_clockEpochNs} ns differs from the shared controller epoch {RobotServiceSimulationClock.ControllerStartupEpochNs} ns.");
            _coordinatedTimeNs = _clockEpochNs;
            _renodeAdvancedTimeNs = _clockEpochNs;
            _characterizationTrace?.RecordRenodeTimeBoundary(_renodeAdvancedTimeNs);
            _lastClockVerifyNs = _clockEpochNs;
            _lastClockVerifyHostTicks = Stopwatch.GetTimestamp();
            Console.WriteLine($"RENODE_CONTROLLER_STARTUP_EPOCH sim_time_ns={_clockEpochNs}");

            Console.WriteLine($"RENODE_PRIVATE_ENDPOINTS monitor_port={monitorPort} uart_ports={string.Join(',', _controllers.Select(item => item.RenodeUartPort))} clock_port={clockPort}");
            var firstController = _controllers[0];
            var firstFirmwareHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(firstController.Options.FirmwarePath, token))).ToLowerInvariant();
            Console.WriteLine($"READY RoboWorld RobotService health=healthy mode=renode-motor events_port={eventPort} uart=tcp://127.0.0.1:{firstController.ServiceUartPort} firmware_sha256={firstFirmwareHash}");
            foreach (var controller in _controllers)
            {
                Console.WriteLine($"UART_ENDPOINT controller_id={controller.Options.Id} component_id={controller.Options.ComponentInstanceId} uart=tcp://127.0.0.1:{controller.ServiceUartPort} firmware_sha256={Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(controller.Options.FirmwarePath, token))).ToLowerInvariant()}");
                _ = ForwardUartWhenControllerConnectsAsync(controller, token);
            }
            var hostStarted = Stopwatch.GetTimestamp();
            while (_eventStream is null)
            {
                await AttachEventClientIfReadyAsync(token);
                if (_eventStream is null) await Task.Delay(10, token);
            }
            while (!token.IsCancellationRequested)
            {
                await AttachEventClientIfReadyAsync(token);
                var request = await _simulationTicks.Reader.ReadAsync(token);
                var perfTickStarted = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                long perfRunForNs = 0;
                long perfMonitorQueryNs = 0;
                long perfEchoInjectionNs = 0;
                long perfPeripheralEventNs = 0;
                long perfClockVerifyNs = 0;
                long perfAckWriteNs = 0;
                long perfAckSerializationNs = 0;
                long perfAckTransportNs = 0;
                long perfFrameReadNs = 0;
                long perfProtobufParseNs = 0;
                long perfChannelWriteBlockedNs = 0;
                long perfQueueWaitNs = 0;
                var perfQueueDepth = 0;
                var perfQueueHighWatermark = 0;
                if (_performanceTrace is not null)
                {
                    Interlocked.Decrement(ref _simulationQueueDepth);
                    perfQueueDepth = Volatile.Read(ref _simulationQueueDepth);
                    perfQueueHighWatermark = Volatile.Read(ref _simulationQueueHighWatermark);
                    if (request.PayloadCase == Envelope.PayloadOneofCase.Tick && _tickIngress.TryRemove(request.Tick.TickIndex, out var ingress))
                    {
                        perfFrameReadNs = ingress.FrameReadNs;
                        perfProtobufParseNs = ingress.ProtobufParseNs;
                        perfChannelWriteBlockedNs = ingress.ChannelWriteBlockedNs;
                        perfQueueWaitNs = Stopwatch.GetElapsedTime(ingress.QueuedAtTicks).Ticks * 100L;
                        perfQueueDepth = ingress.QueueDepth;
                        perfQueueHighWatermark = ingress.QueueHighWatermark;
                    }
                }
                if (request.PayloadCase == Envelope.PayloadOneofCase.HardwareEvent)
                {
                    var sensor = request.HardwareEvent;
                    if (sensor.Kind != HardwareEvent.Types.Kind.SensorSample || sensor.EndpointId != "distance.echo" || sensor.Unit != "meters" || !double.IsFinite(sensor.Scalar) || sensor.Scalar < -1 || sensor.Scalar > 4 || sensor.EffectiveSimTimeNs <= _coordinatedTimeNs)
                        throw new InvalidDataException("Godot submitted an invalid distance sensor sample.");
                    _characterizationTrace?.RecordDistanceSample(sensor.EffectiveSimTimeNs, sensor.Scalar);
                    _pendingDistanceSample = request;
                    continue;
                }
                var tick = request.Tick;
                if (request.PayloadCase != Envelope.PayloadOneofCase.Tick || tick.TickIndex == 0 || tick.TickIndex != _lastTickIndex + 1 || tick.DeltaNs <= 0 || tick.StartNs < 0)
                    throw new InvalidDataException("Godot submitted an invalid simulation tick.");
                if (_renodeClock is null) throw new InvalidOperationException("Renode external clock is not connected.");
                if (tick.StartNs != _coordinatedTimeNs) throw new InvalidDataException($"Godot tick starts at {tick.StartNs} ns but Renode is at {_coordinatedTimeNs} ns.");
                if (!_tickReceiptHostTicks.TryRemove(tick.TickIndex, out var tickReceiptHostTicks))
                    throw new InvalidDataException($"Tick {tick.TickIndex} has no host-ingress boundary timestamp.");
                await ForwardUartAtTickStartAsync(tick, tickReceiptHostTicks, token);
                await SelectMachineAsync(_controllers[0], token);
                if (!_routeFaultInjected && _routeFaultAtSimTimeNs is long routeFaultSimTimeNs
                    && tick.StartNs >= routeFaultSimTimeNs && (_leftDuty.GetValueOrDefault() != 0 || _rightDuty.GetValueOrDefault() != 0))
                {
                    await ExecuteMonitorCommandAsync("sysbus WriteDoubleWord 0x40020000 0x00008000", token);
                    _routeFaultInjected = true;
                    _characterizationTrace?.RecordFault(_controllers[0].Options.Id, tick.StartNs, "PWM_GPIO_AF_MISMATCH_PA6_PA7", "injected");
                    Console.WriteLine($"TEST_ROUTE_FAULT_INJECTED controller_id={_controllers[0].Options.Id} sim_time_ns={tick.StartNs} pin=PA6 mode=input");
                }
                else if (_routeFaultAfterMs is int delayMs && !_routeFaultInjected && Stopwatch.GetElapsedTime(hostStarted).TotalMilliseconds >= delayMs)
                {
                    await ExecuteMonitorCommandAsync("sysbus WriteDoubleWord 0x40020000 0x00008000", token);
                    _routeFaultInjected = true;
                    Console.WriteLine("TEST_ROUTE_FAULT_INJECTED pin=PA6 mode=input");
                }
                if (!_cpuHaltInjected && _cpuHaltAtSimTimeNs is long cpuHaltSimTimeNs
                    && tick.StartNs >= cpuHaltSimTimeNs && (_leftDuty.GetValueOrDefault() != 0 || _rightDuty.GetValueOrDefault() != 0))
                {
                    var targetController = _haltControllerId is null
                        ? _controllers[0]
                        : _controllers.SingleOrDefault(controller => controller.Options.Id == _haltControllerId)
                            ?? throw new InvalidDataException($"Fault injection references unknown controller {_haltControllerId}.");
                    await SelectMachineAsync(targetController, token);
                    await ExecuteMonitorCommandAsync("cpu IsHalted true", token);
                    _cpuHaltInjected = true;
                    _characterizationTrace?.RecordFault(targetController.Options.Id, tick.StartNs, "MCU_HALTED", "injected");
                    Console.WriteLine($"TEST_MCU_HALTED controller_id={targetController.Options.Id} component_id={targetController.Options.ComponentInstanceId} sim_time_ns={tick.StartNs} cpu=stm32f407vg");
                }
                else if (_cpuHaltAfterMs is int haltDelay && !_cpuHaltInjected && Stopwatch.GetElapsedTime(hostStarted).TotalMilliseconds >= haltDelay)
                {
                    var targetController = _haltControllerId is null
                        ? _controllers[0]
                        : _controllers.SingleOrDefault(controller => controller.Options.Id == _haltControllerId)
                            ?? throw new InvalidDataException($"Fault injection references unknown controller {_haltControllerId}.");
                    await SelectMachineAsync(targetController, token);
                    await ExecuteMonitorCommandAsync("cpu IsHalted true", token);
                    _cpuHaltInjected = true;
                    Console.WriteLine($"TEST_MCU_HALTED controller_id={targetController.Options.Id} component_id={targetController.Options.ComponentInstanceId} cpu=stm32f407vg");
                }
                var tickEndNs = checked(tick.StartNs + tick.DeltaNs);
                if (_pendingDistanceSample is not null)
                {
                    if (_pendingDistanceSample.HardwareEvent.EffectiveSimTimeNs != tickEndNs) throw new InvalidDataException("Distance sample timestamp does not match a completed Godot physics boundary.");
                    var meters = _pendingDistanceSample.HardwareEvent.Scalar;
                    if (meters >= 0)
                    {
                        var pulseNs = (long)Math.Round(2 * meters / 343d * 1_000_000_000d, MidpointRounding.AwayFromZero);
                        _echoReleaseNs = checked(tickEndNs + pulseNs);
                        perfRunForNs += await RunForMeasuredAsync(tick.DeltaNs, token);
                        await SelectMachineAsync(_controllers[0], token);
                        var echoStartTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                        await ExecuteMonitorCommandAsync("gpioPortA.sensorEcho Press", token);
                        _characterizationTrace?.RecordDistanceEchoEdge(_renodeAdvancedTimeNs, high: true);
                        if (_performanceTrace is not null) perfEchoInjectionNs += Stopwatch.GetElapsedTime(echoStartTicks).Ticks * 100L;
                        _echoHigh = true;
                        Console.WriteLine($"DISTANCE_ECHO_RISE sample_ns={tickEndNs} meters={meters:F3} pulse_ns={pulseNs}");
                    }
                    else perfRunForNs += await RunForMeasuredAsync(tick.DeltaNs, token);
                    _pendingDistanceSample = null;
                }
                else if (_echoHigh && _echoReleaseNs <= tickEndNs)
                {
                    var elapsed = _echoReleaseNs - tick.StartNs;
                    if (elapsed > 0) perfRunForNs += await RunForMeasuredAsync(elapsed, token);
                    await SelectMachineAsync(_controllers[0], token);
                    var echoStartTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                    await ExecuteMonitorCommandAsync("gpioPortA.sensorEcho Release", token);
                    _characterizationTrace?.RecordDistanceEchoEdge(_renodeAdvancedTimeNs, high: false);
                    if (_performanceTrace is not null) perfEchoInjectionNs += Stopwatch.GetElapsedTime(echoStartTicks).Ticks * 100L;
                    _echoHigh = false;
                    var remaining = tick.DeltaNs - Math.Max(0, elapsed);
                    if (remaining > 0) perfRunForNs += await RunForMeasuredAsync(remaining, token);
                    Console.WriteLine($"DISTANCE_ECHO_FALL sim_time_ns={_echoReleaseNs}");
                }
                else perfRunForNs += await RunForMeasuredAsync(tick.DeltaNs, token);
                _coordinatedTimeNs = checked(tick.StartNs + tick.DeltaNs);
                if (_renodeAdvancedTimeNs != _coordinatedTimeNs)
                    throw new InvalidDataException($"Renode segment frontier {_renodeAdvancedTimeNs} ns does not match completed Godot tick {_coordinatedTimeNs} ns.");
                _lastTickIndex = tick.TickIndex;
                // Characterization needs physics-tick timing for output response validation. Normal runtime keeps
                // the lower-cost 50 ms peripheral sample cadence.
                if (_characterizationTrace is not null || _controllers.Any(item => item.FastOutputSamplingTicksRemaining > 0) || tick.TickIndex % 6 == 0)
                {
                    foreach (var controller in _controllers)
                    {
                        await SelectMachineAsync(controller, token);
                        var monitorStartTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                        var sample = await ReadPeripheralSampleAsync(token);
                        if (_performanceTrace is not null) perfMonitorQueryNs += Stopwatch.GetElapsedTime(monitorStartTicks).Ticks * 100L;
                        if (sample is null)
                        {
                            if (controller.FastOutputSamplingTicksRemaining > 0) controller.FastOutputSamplingTicksRemaining--;
                            continue;
                        }
                        var eventStartTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                        if (!ValidateOutputRoute(sample, out var fault))
                        {
                            if (controller.FastOutputSamplingTicksRemaining > 0) controller.FastOutputSamplingTicksRemaining--;
                            if (controller == _controllers[0] && !string.Equals(fault, _lastFault, StringComparison.Ordinal))
                            {
                                _lastFault = fault;
                                _characterizationTrace?.RecordFault(controller.Options.Id, sample.SimTimeNs, fault, "observed");
                                Console.Error.WriteLine($"ACTUATOR_ROUTE_FAILED controller={controller.Options.Id} {fault}");
                                await SendDiagnosticAsync(fault, token);
                            }
                            continue;
                        }
                        var left = GetSignedDuty(sample.Ccr1, sample.Arr, (sample.GpioDOdr & (1u << 2)) != 0);
                        var right = GetSignedDuty(sample.Ccr2, sample.Arr, (sample.GpioDOdr & (1u << 3)) != 0);
                        var leftForward = (sample.GpioDOdr & (1u << 2)) != 0;
                        var rightForward = (sample.GpioDOdr & (1u << 3)) != 0;
                        _characterizationTrace?.RecordPwm(controller.Options.Id, sample.SimTimeNs, left, right,
                            leftForward, rightForward,
                            Math.Clamp(sample.Ccr1 / ((double)sample.Arr + 1d), 0, 1),
                            Math.Clamp(sample.Ccr2 / ((double)sample.Arr + 1d), 0, 1));
                        if (controller.Channel1Duty != left)
                        {
                            controller.Channel1Duty = left;
                            if (controller.Options.Channel1ActuatorId.Length > 0)
                                await SendPwmEventAsync(controller.Options.Channel1ActuatorId, "tim3.ch1.pa6", left, sample.SimTimeNs, token);
                        }
                        if (controller.Channel2Duty != right)
                        {
                            controller.Channel2Duty = right;
                            if (controller.Options.Channel2ActuatorId.Length > 0)
                                await SendPwmEventAsync(controller.Options.Channel2ActuatorId, "tim3.ch2.pa7", right, sample.SimTimeNs, token);
                        }
                        if (controller == _controllers[0]) { _leftDuty = left; _rightDuty = right; _lastFault = null; }
                        Console.WriteLine($"PERIPHERAL_OUTPUT controller_id={controller.Options.Id} sim_time_ns={sample.SimTimeNs} channel1={left:F3} channel2={right:F3}");
                        if (_performanceTrace is not null) perfPeripheralEventNs += Stopwatch.GetElapsedTime(eventStartTicks).Ticks * 100L;
                        if (controller.FastOutputSamplingTicksRemaining > 0) controller.FastOutputSamplingTicksRemaining--;
                    }
                }
                if (tick.TickIndex % 120 == 0)
                {
                    var clockVerifyStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                    var measuredTime = await _renodeClock.GetTimeNsAsync(token);
                    if (measuredTime != _coordinatedTimeNs) throw new InvalidDataException($"Renode advanced to {measuredTime} ns, expected {_coordinatedTimeNs} ns.");
                    var verifyHostTicks = Stopwatch.GetTimestamp();
                    var intervalHostSeconds = (verifyHostTicks - _lastClockVerifyHostTicks) / (double)Stopwatch.Frequency;
                    var intervalSimSeconds = (measuredTime - _lastClockVerifyNs) / 1_000_000_000d;
                    Console.WriteLine(FormattableString.Invariant($"CLOCK_VERIFIED tick={tick.TickIndex} sim_time_ns={measuredTime} measured_rtf={(intervalHostSeconds <= 0 ? 0 : intervalSimSeconds / intervalHostSeconds):F3}"));
                    _lastClockVerifyNs = measuredTime;
                    _lastClockVerifyHostTicks = verifyHostTicks;
                    if (_performanceTrace is not null) perfClockVerifyNs = Stopwatch.GetElapsedTime(clockVerifyStart).Ticks * 100L;
                }
                var ackWriteStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                var ackEnvelope = new Envelope
                {
                    ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolMinor, SessionId = _sessionId,
                    WorldId = "roboworld-local", RobotId = "rover-01", Sequence = tick.TickIndex,
                    SimTimeNs = _coordinatedTimeNs, Tick = new SimulationTick { TickIndex = tick.TickIndex, StartNs = tick.StartNs, DeltaNs = tick.DeltaNs, RealTimeFactor = tick.RealTimeFactor }
                };
                if (_performanceTrace is null) await WriteEnvelopeAsync(_eventStream!, ackEnvelope, token);
                else
                {
                    (perfAckSerializationNs, perfAckTransportNs) = await WriteEnvelopeMeasuredAsync(_eventStream!, ackEnvelope, token);
                }
                if (_performanceTrace is not null) perfAckWriteNs = Stopwatch.GetElapsedTime(ackWriteStart).Ticks * 100L;
                if (_performanceTrace is not null)
                {
                    var processingElapsedNs = Stopwatch.GetElapsedTime(perfTickStarted).Ticks * 100L;
                    long managedHeapBytes = 0;
                    long managedHeapCommittedBytes = 0;
                    long managedHeapFragmentedBytes = 0;
                    var gen0Collections = 0;
                    var gen1Collections = 0;
                    var gen2Collections = 0;
                    if (tick.TickIndex % 120 == 0)
                    {
                        var gcInfo = GC.GetGCMemoryInfo();
                        managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
                        managedHeapCommittedBytes = gcInfo.TotalCommittedBytes;
                        managedHeapFragmentedBytes = gcInfo.FragmentedBytes;
                        gen0Collections = GC.CollectionCount(0);
                        gen1Collections = GC.CollectionCount(1);
                        gen2Collections = GC.CollectionCount(2);
                    }
                    _performanceTrace.Record(new WorkerTickPerformanceSample(
                        tick.TickIndex, _coordinatedTimeNs, Stopwatch.GetTimestamp(), tick.DeltaNs,
                        perfFrameReadNs, perfProtobufParseNs, perfChannelWriteBlockedNs, perfQueueWaitNs, perfQueueDepth, perfQueueHighWatermark,
                        _controllers.Count, Math.Max(0, processingElapsedNs - perfAckWriteNs - perfRunForNs - perfMonitorQueryNs - perfEchoInjectionNs - perfPeripheralEventNs - perfClockVerifyNs), perfRunForNs,
                        perfMonitorQueryNs, perfEchoInjectionNs, perfPeripheralEventNs, perfClockVerifyNs,
                        perfAckSerializationNs, perfAckTransportNs, _coordinatedTimeNs,
                        managedHeapBytes, managedHeapCommittedBytes, managedHeapFragmentedBytes,
                        gen0Collections, gen1Collections, gen2Collections), _performanceScenario);
                }
                if (tick.TickIndex % 120 == 0) Console.WriteLine($"CLOCK_TICK tick={tick.TickIndex} sim_time_ns={_coordinatedTimeNs} delta_ns={tick.DeltaNs}");
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            await StopRuntimeAsync();
            _performanceTrace?.Dispose();
            _performanceTrace = null;
        }
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForPortAsync(int port, TimeSpan timeout, CancellationToken token)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            token.ThrowIfCancellationRequested();
            using var probe = new TcpClient();
            try { await probe.ConnectAsync(IPAddress.Loopback, port, token); return; }
            catch (SocketException) { await Task.Delay(100, token); }
        }
        throw new TimeoutException($"Renode monitor did not listen on 127.0.0.1:{port}.");
    }

    private static void WriteRenodeScript(string path, IReadOnlyList<ControllerRuntime> controllers, string stock, int clockPort)
    {
        var lines = new List<string>
        {
            ":name: RoboWorld live multi-controller STM32F407 motor output host"
        };
        for (var index = 0; index < controllers.Count; index++)
        {
            var controller = controllers[index];
            if (controller.Options.FirmwarePath.Contains('\n') || controller.Options.FirmwarePath.Contains('\r') || controller.Options.FirmwarePath.Contains('"'))
                throw new InvalidDataException($"Firmware path for controller {controller.Options.Id} cannot be represented in a Renode script.");
            lines.Add($"$name=\"{controller.MachineName}\"");
            lines.Add((index == 0 ? "$bin?=@" : "$bin=@") + controller.Options.FirmwarePath.Replace('\\', '/'));
            lines.Add("include @" + stock.Replace('\\', '/'));
            lines.Add($"mach set \"{controller.MachineName}\"");
            // Each included stock script mutates the shared `$bin variable. Explicitly load the
            // assigned image while its machine is selected; this keeps MCU identity stable across
            // multiple included platform scripts.
            lines.Add("sysbus LoadELF @" + controller.Options.FirmwarePath.Replace('\\', '/'));
            lines.Add("machine LoadPlatformDescriptionFromString \"leftPwm: PWMTester @ gpioPortA 6\"");
            lines.Add("machine LoadPlatformDescriptionFromString \"gpioPortA: { 6 -> leftPwm@0 }\"");
            lines.Add("machine LoadPlatformDescriptionFromString \"rightPwm: PWMTester @ gpioPortA 7\"");
            lines.Add("machine LoadPlatformDescriptionFromString \"gpioPortA: { 7 -> rightPwm@0 }\"");
            if (index == 0) lines.Add("machine LoadPlatformDescriptionFromString \"sensorEcho: Miscellaneous.Button @ gpioPortA { -> gpioPortA@0 }\"");
            lines.Add($"emulation CreateServerSocketTerminal {controller.RenodeUartPort} \"roboworld_runtime_uart_{index + 1}\" false");
            lines.Add($"connector Connect sysbus.uart4 roboworld_runtime_uart_{index + 1}");
            lines.Add("sysbus.cpu PerformanceInMips 16");
        }
        lines.Add($"emulation CreateExternalControlServer \"roboworld_clock\" {clockPort}");
        File.WriteAllLines(path, lines, Encoding.ASCII);
    }

    private async Task SelectMachineAsync(ControllerRuntime controller, CancellationToken token)
    {
        if (_controllers.Count > 1) await ExecuteMonitorCommandAsync($"mach set \"{controller.MachineName}\"", token);
    }

    private async Task AttachEventClientIfReadyAsync(CancellationToken token)
    {
        if (_eventStream is null && _pendingEventClient is not null && _pendingEventClient.IsCompleted) Console.WriteLine($"EVENT_ACCEPT_TASK status={_pendingEventClient.Status}");
        if (_eventStream is not null || _pendingEventClient is null || !_pendingEventClient.IsCompletedSuccessfully) return;
        _eventClient = await _pendingEventClient;
        Console.WriteLine("GODOT_EVENT_CLIENT_ACCEPTED");
        _eventClient.NoDelay = true;
        _eventStream = _eventClient.GetStream();
        await WriteEnvelopeAsync(_eventStream, new Envelope
        {
            ProtocolMajor = ProtocolMajor,
            ProtocolMinor = ProtocolMinor,
            SessionId = _sessionId,
            WorldId = "roboworld-local",
            RobotId = "rover-01",
            Hello = new Hello { ClientKind = "robotservice", InstanceVersion = "T03-renode-host", Capabilities = { "pwm_output", "gpio_direction" } }
            ,SimTimeNs = _clockEpochNs
        }, token);
        Console.WriteLine("SERVICE_HELLO_SENT");
        var clientHello = await ReadEnvelopeAsync(_eventStream, token);
        if (clientHello.ProtocolMajor != ProtocolMajor || clientHello.SessionId != _sessionId || clientHello.PayloadCase != Envelope.PayloadOneofCase.Hello || clientHello.Hello.ClientKind != "godot")
            throw new InvalidDataException("Godot event-stream handshake was incompatible.");
        Console.WriteLine("EVENT_STREAM_CONNECTED protocol=1.0");
        _ = ReadSimulationTicksAsync(_eventStream, token);
    }

    private async Task ReadSimulationTicksAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                Envelope envelope;
                long frameReadNs = 0;
                long protobufParseNs = 0;
                if (_performanceTrace is null) envelope = await ReadEnvelopeAsync(stream, token);
                else
                {
                    (envelope, frameReadNs, protobufParseNs) = await ReadEnvelopeMeasuredAsync(stream, token);
                }
                Console.WriteLine($"GODOT_STREAM_FRAME payload={envelope.PayloadCase} tick={envelope.Tick?.TickIndex}");
                if (envelope.ProtocolMajor != ProtocolMajor || envelope.SessionId != _sessionId || envelope.PayloadCase is not (Envelope.PayloadOneofCase.Tick or Envelope.PayloadOneofCase.HardwareEvent))
                    throw new InvalidDataException("Godot event stream sent an invalid tick or sensor envelope.");
                if (envelope.PayloadCase == Envelope.PayloadOneofCase.Tick)
                    _tickReceiptHostTicks[envelope.Tick!.TickIndex] = Stopwatch.GetTimestamp();
                if (_performanceTrace is null) await _simulationTicks.Writer.WriteAsync(envelope, token);
                else
                {
                    var queueDepth = Interlocked.Increment(ref _simulationQueueDepth);
                    var highWater = Volatile.Read(ref _simulationQueueHighWatermark);
                    while (queueDepth > highWater)
                    {
                        var previous = Interlocked.CompareExchange(ref _simulationQueueHighWatermark, queueDepth, highWater);
                        if (previous == highWater) break;
                        highWater = previous;
                    }
                    TickIngress? ingress = null;
                    if (envelope.PayloadCase == Envelope.PayloadOneofCase.Tick)
                    {
                        ingress = new TickIngress(frameReadNs, protobufParseNs, Stopwatch.GetTimestamp(), queueDepth, Volatile.Read(ref _simulationQueueHighWatermark));
                        _tickIngress[envelope.Tick!.TickIndex] = ingress;
                    }
                    if (!_simulationTicks.Writer.TryWrite(envelope))
                    {
                        var writeStartTicks = Stopwatch.GetTimestamp();
                        await _simulationTicks.Writer.WriteAsync(envelope, token);
                        if (ingress is not null) ingress.ChannelWriteBlockedNs = Stopwatch.GetElapsedTime(writeStartTicks).Ticks * 100L;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Console.Error.WriteLine($"GODOT_TICK_STREAM_FAILED {exception.Message}"); _stop.Cancel(); }
    }

    private async Task ForwardUartWhenControllerConnectsAsync(ControllerRuntime controller, CancellationToken token)
    {
        try
        {
            if (controller.PendingClient is null || controller.UpstreamStream is null) return;
            controller.Client = await controller.PendingClient.WaitAsync(token);
            controller.Client.NoDelay = true;
            var downstream = controller.Client.GetStream();
            Console.WriteLine($"UART_PROXY_CONNECTED controller_id={controller.Options.Id} boot_stream=live");
            Console.WriteLine("UART_PROXY_BOUNDED buffer_bytes=1024 overflow_policy=tcp-backpressure dropped_bytes=0 uart_model=Renode-terminal-no-baud-serialization");
            var upstreamToPython = CopyBoundedAsync(controller, controller.UpstreamStream, downstream, token, "mcu_to_controller", 0);
            var pythonToUpstream = CopyBoundedAsync(controller, downstream, controller.UpstreamStream, token, "controller_to_mcu", 0);
            await Task.WhenAny(upstreamToPython, pythonToUpstream);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Console.Error.WriteLine($"UART_PROXY_FAILED {exception.Message}"); }
    }

    private async Task CopyBoundedAsync(ControllerRuntime controller, NetworkStream source, NetworkStream destination, CancellationToken token, string direction, int initialBytes)
    {
        var buffer = new byte[1024];
        long transferred = initialBytes;
        var line = new List<byte>(128);
        while (!token.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, token);
            if (read == 0) return;
            var arrivalTicks = Stopwatch.GetTimestamp();
            var arrivalBoundaryNs = Interlocked.Read(ref _renodeAdvancedTimeNs);
            if (direction == "controller_to_mcu")
            {
                await controller.UartIngress.Writer.WriteAsync(
                    new StagedUartIngress(buffer.AsSpan(0, read).ToArray(), arrivalBoundaryNs, arrivalTicks), token);
                Console.WriteLine($"UART_PROXY_INGRESS controller_id={controller.Options.Id} mono_ticks={arrivalTicks} frontier_ns={arrivalBoundaryNs} bytes={read} mode=exact queued_for_tick_boundary=true");
            }
            else
            {
                _characterizationTrace?.Record(controller.Options.Id, direction, arrivalBoundaryNs,
                    arrivalTicks, buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
            }
            transferred += read;
            if (direction != "controller_to_mcu")
                Console.WriteLine($"UART_PROXY_TRANSFER direction={direction} bytes={read} total={transferred}");
            if (direction == "mcu_to_controller")
            {
                for (var index = 0; index < read; index++)
                {
                    if (buffer[index] == (byte)'\n')
                    {
                        var text = Encoding.ASCII.GetString(line.ToArray()).TrimEnd('\r', '\n');
                        if (Regex.IsMatch(text, "^BOOT [0-9a-f]{8}$", RegexOptions.CultureInvariant))
                            Console.WriteLine($"UART_BOOT_OBSERVED {text}");
                        var sensor = Regex.Match(text, @"^DIST mm=(\d+) valid=([01])$", RegexOptions.CultureInvariant);
                        if (sensor.Success) Console.WriteLine($"UART_SENSOR_TELEMETRY mm={sensor.Groups[1].Value} valid={sensor.Groups[2].Value}");
                        line.Clear();
                    }
                    else if (line.Count < 128) line.Add(buffer[index]);
                    else line.Clear();
                }
            }
        }
    }

    private async Task ForwardUartAtTickStartAsync(SimulationTick tick, long tickReceiptHostTicks, CancellationToken token)
    {
        foreach (var controller in _controllers)
        {
            if (controller.UpstreamStream is null) throw new InvalidOperationException("Controller UART is not connected to Renode.");
            var reader = controller.UartIngress.Reader;
            while (reader.TryPeek(out var pending) && pending.ArrivalHostTicks <= tickReceiptHostTicks)
            {
                if (!reader.TryRead(out pending)) continue;
                _characterizationTrace?.Record(controller.Options.Id, "controller_to_mcu", pending.ArrivalBoundaryLowerBoundNs,
                    pending.ArrivalHostTicks, pending.Bytes, tick.StartNs);
                await controller.UpstreamStream.WriteAsync(pending.Bytes, token);
                // Firmware can update PWM/GPIO as soon as this UART command is parsed. Sample each
                // following physics boundary for a bounded window so output transitions are not
                // quantized to the idle 50 ms diagnostics polling interval.
                controller.FastOutputSamplingTicksRemaining = Math.Max(controller.FastOutputSamplingTicksRemaining, 6);
                Console.WriteLine($"UART_PROXY_TRANSFER direction=controller_to_mcu bytes={pending.Bytes.Length} total=scheduled sim_time_ns={tick.StartNs}");
            }
        }
    }

    private async Task<long> RunForMeasuredAsync(long deltaNs, CancellationToken token)
    {
        if (deltaNs <= 0) throw new ArgumentOutOfRangeException(nameof(deltaNs));
        if (_renodeClock is null) throw new InvalidOperationException("Renode external clock is not connected.");
        var startTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        await _renodeClock.RunForAsync(deltaNs, token);
        _renodeAdvancedTimeNs = checked(_renodeAdvancedTimeNs + deltaNs);
        _characterizationTrace?.RecordRenodeTimeBoundary(_renodeAdvancedTimeNs);
        return _performanceTrace is null ? 0 : Stopwatch.GetElapsedTime(startTicks).Ticks * 100L;
    }

    private async Task<PeripheralSample?> ReadPeripheralSampleAsync(CancellationToken token)
    {
        const string command = "python \"print('%d,%d,%d,%d,%d,%d,%d,%d,%d,%d,%d' % (self.Machine.LocalTimeSource.ElapsedVirtualTime.Ticks, self.Machine.SystemBus.ReadDoubleWord(0x40000400), self.Machine.SystemBus.ReadDoubleWord(0x40000418), self.Machine.SystemBus.ReadDoubleWord(0x40000420), self.Machine.SystemBus.ReadDoubleWord(0x4000042C), self.Machine.SystemBus.ReadDoubleWord(0x40000434), self.Machine.SystemBus.ReadDoubleWord(0x40000438), self.Machine.SystemBus.ReadDoubleWord(0x40020000), self.Machine.SystemBus.ReadDoubleWord(0x40020020), self.Machine.SystemBus.ReadDoubleWord(0x40020C00), self.Machine.SystemBus.ReadDoubleWord(0x40020C14)))\"";
        string response;
        try { response = await ExecuteMonitorCommandAsync(command, token); }
        catch (IOException exception) { Console.Error.WriteLine($"MONITOR_READ_FAILED {exception.Message}"); return null; }
        var match = Regex.Match(response, @"(?m)^\s*(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\s*$");
        if (!match.Success)
        {
            if (!response.Contains("sensorEcho", StringComparison.Ordinal) && !response.Contains("machine Reset", StringComparison.Ordinal))
                Console.Error.WriteLine($"MONITOR_SAMPLE_INVALID {response.Replace('\r', ' ').Replace('\n', ' ')}");
            return null;
        }
        uint Read(int index) => uint.Parse(match.Groups[index].Value, CultureInfo.InvariantCulture);
        var simTimeNs = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return new PeripheralSample(simTimeNs, Read(2), Read(3), Read(4), Read(5), Read(6), Read(7), Read(8), Read(9), Read(10), Read(11));
    }

    private async Task<string> ExecuteMonitorCommandAsync(string command, CancellationToken token)
    {
        if (_monitorStream is null) throw new IOException("Renode monitor is not connected.");
        var bytes = Encoding.ASCII.GetBytes(command + "\n");
        await _monitorStream.WriteAsync(bytes, token);
        await _monitorStream.FlushAsync(token);
        return await ReadMonitorPromptAsync(_monitorStream, token);
    }

    private static async Task<string> ReadMonitorPromptAsync(NetworkStream stream, CancellationToken token)
    {
        var response = new StringBuilder();
        var buffer = new byte[1];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0) throw new IOException("Renode monitor closed the connection.");
            response.Append((char)buffer[0]);
            if (response.Length >= 2 && response[^2] == ')' && response[^1] == ' ') return response.ToString();
        }
    }

    private static bool ValidateOutputRoute(PeripheralSample sample, out string fault)
    {
        if ((sample.Cr1 & 1) == 0) { fault = "TIM3_COUNTER_STOPPED"; return false; }
        if ((sample.Ccer & 0x11) != 0x11) { fault = "TIM3_OUTPUT_CHANNEL_DISABLED"; return false; }
        if ((sample.Ccmr1 & 0x70) != 0x60 || ((sample.Ccmr1 >> 8) & 0x70) != 0x60) { fault = "TIM3_CHANNEL_NOT_PWM_MODE_1"; return false; }
        if (((sample.GpioAModer >> 12) & 0xF) != 0xA || ((sample.GpioAAfrl >> 24) & 0xFF) != 0x22) { fault = "PWM_GPIO_AF_MISMATCH_PA6_PA7"; return false; }
        if (((sample.GpioDModer >> 4) & 0xF) != 0x5) { fault = "MOTOR_DIRECTION_GPIO_NOT_OUTPUT"; return false; }
        if (sample.Arr == 0) { fault = "TIM3_AUTO_RELOAD_INVALID"; return false; }
        fault = string.Empty;
        return true;
    }

    private static double GetSignedDuty(uint compare, uint autoReload, bool forward)
    {
        var duty = Math.Clamp(compare / ((double)autoReload + 1d), 0, 1);
        return duty == 0 ? 0 : forward ? duty : -duty;
    }

    private async Task SendPwmEventAsync(string componentId, string endpointId, double signedDuty, long simTimeNs, CancellationToken token)
    {
        if (_eventStream is null) return;
        var envelope = new Envelope
        {
            ProtocolMajor = ProtocolMajor,
            ProtocolMinor = ProtocolMinor,
            SessionId = _sessionId,
            WorldId = "roboworld-local",
            RobotId = "rover-01",
            ComponentId = componentId,
            SimTimeNs = simTimeNs,
            Sequence = (ulong)Interlocked.Increment(ref _sequence),
            HardwareEvent = new HardwareEvent
            {
                Kind = HardwareEvent.Types.Kind.PwmOutput,
                EndpointId = endpointId,
                Scalar = signedDuty,
                Unit = "fraction_signed",
                EffectiveSimTimeNs = simTimeNs,
                FidelityNote = "TIM3 CCR/ARR sampled through Renode system bus; GPIO AF2 route verified; duty waveform capture validated separately."
            }
        };
        try { await WriteEnvelopeAsync(_eventStream, envelope, token); }
        catch (IOException exception) { Console.Error.WriteLine($"EVENT_STREAM_WRITE_FAILED {exception.Message}"); await CloseEventClientAsync(); }
    }

    private async Task SendDiagnosticAsync(string reason, CancellationToken token)
    {
        if (_eventStream is null) return;
        var envelope = new Envelope
        {
            ProtocolMajor = ProtocolMajor,
            ProtocolMinor = ProtocolMinor,
            SessionId = _sessionId,
            WorldId = "roboworld-local",
            RobotId = "rover-01",
            Sequence = (ulong)Interlocked.Increment(ref _sequence),
            Diagnostic = new ComponentDiagnostic
            {
                Status = ComponentDiagnostic.Types.Status.Failed,
                ComponentPath = "rover-01/mcu-board/driver",
                ReasonCode = reason,
                Reason = "Renode output route is unsupported or disconnected; no motor event was emitted.",
                RootFaultId = _sessionId
            }
        };
        await WriteEnvelopeAsync(_eventStream, envelope, token);
    }

    private static async Task WriteEnvelopeAsync(NetworkStream stream, Envelope envelope, CancellationToken token)
    {
        var payload = envelope.ToByteArray();
        if (payload.Length == 0 || payload.Length > MaxFrameBytes) throw new InvalidDataException($"Invalid protobuf frame length {payload.Length}.");
        var frame = new byte[payload.Length + 4];
        frame[0] = (byte)(payload.Length >> 24);
        frame[1] = (byte)(payload.Length >> 16);
        frame[2] = (byte)(payload.Length >> 8);
        frame[3] = (byte)payload.Length;
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
    }

    private static async Task<(long SerializationNs, long TransportNs)> WriteEnvelopeMeasuredAsync(NetworkStream stream, Envelope envelope, CancellationToken token)
    {
        var serializationStart = Stopwatch.GetTimestamp();
        var payload = envelope.ToByteArray();
        if (payload.Length == 0 || payload.Length > MaxFrameBytes) throw new InvalidDataException($"Invalid protobuf frame length {payload.Length}.");
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        var serializationNs = Stopwatch.GetElapsedTime(serializationStart).Ticks * 100L;
        var transportStart = Stopwatch.GetTimestamp();
        await stream.WriteAsync(frame, token);
        await stream.FlushAsync(token);
        var transportNs = Stopwatch.GetElapsedTime(transportStart).Ticks * 100L;
        return (serializationNs, transportNs);
    }

    private static async Task<Envelope> ReadEnvelopeAsync(NetworkStream stream, CancellationToken token)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException($"Godot protobuf frame length {length} is outside the allowed range.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token);
        return Envelope.Parser.ParseFrom(payload);
    }

    private static async Task<(Envelope Envelope, long FrameReadNs, long ProtobufParseNs)> ReadEnvelopeMeasuredAsync(NetworkStream stream, CancellationToken token)
    {
        var frameReadStart = Stopwatch.GetTimestamp();
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > MaxFrameBytes) throw new InvalidDataException($"Godot protobuf frame length {length} is outside the allowed range.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token);
        var frameReadNs = Stopwatch.GetElapsedTime(frameReadStart).Ticks * 100L;
        var parseStart = Stopwatch.GetTimestamp();
        var envelope = Envelope.Parser.ParseFrom(payload);
        var protobufParseNs = Stopwatch.GetElapsedTime(parseStart).Ticks * 100L;
        return (envelope, frameReadNs, protobufParseNs);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException("Godot event stream closed.");
            offset += read;
        }
    }

    private async Task CloseEventClientAsync()
    {
        if (_eventStream is not null) await _eventStream.DisposeAsync();
        _eventStream = null;
        _eventClient?.Dispose();
        _eventClient = null;
        if (_eventListener is not null)
        {
            _pendingEventClient = _eventListener.AcceptTcpClientAsync(_stop.Token).AsTask();
        }
    }

    private async Task StopRuntimeAsync()
    {
        _stop.Cancel();
        if (_eventStream is not null) await _eventStream.DisposeAsync();
        _eventClient?.Dispose();
        _eventListener?.Stop();
        foreach (var controller in _controllers)
        {
            controller.Listener?.Stop();
            controller.Client?.Dispose();
            controller.UpstreamClient?.Dispose();
        }
        if (_monitorStream is not null) await _monitorStream.DisposeAsync();
        _monitor?.Dispose();
        if (_renode is not null)
        {
            try { if (!_renode.HasExited) { _renode.Kill(entireProcessTree: true); await _renode.WaitForExitAsync(); } }
            catch (InvalidOperationException) { }
            finally { _renode.Dispose(); _renode = null; }
        }
        if (_rescPath is not null)
        {
            try
            {
                var sessionDirectory = Path.GetDirectoryName(_rescPath);
                File.Delete(_rescPath);
                if (sessionDirectory is not null && Directory.Exists(sessionDirectory)) Directory.Delete(sessionDirectory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await StopRuntimeAsync();
        _characterizationTrace?.Dispose();
        _stop.Dispose();
    }
}
