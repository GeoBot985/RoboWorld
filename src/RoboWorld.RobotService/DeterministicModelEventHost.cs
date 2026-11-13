using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Google.Protobuf;
using RoboWorld.Contracts.V1;
using RoboWorld.Domain;

namespace RoboWorld.RobotService;

public sealed record DeterministicModelRuntimeEndpoints(int EventPort, IReadOnlyDictionary<string, int> UartPorts);

/// <summary>Runs validated controller models on the same authoritative tick and hardware-event stream as exact mode.</summary>
public sealed class DeterministicModelEventHost : IAsyncDisposable
{
    private const int ProtocolMajor = 1;
    private const int ProtocolMinor = 0;
    private const int MaxFrameBytes = 64 * 1024;
    private sealed record PendingEchoFall(string ControllerId, string EndpointId, long SimTimeNs);
    private sealed record StagedUartIngress(byte[] Bytes, long ArrivalHostTicks);
    private sealed record TimestampedSimulationEvent(Envelope Envelope, long ReadMonoTicks, long ReadParseNs,
        long ChannelWriteBlockedNs, int QueueDepthAfterRead);
    private sealed record ScheduledModelInput(string ControllerId, long SimTimeNs, int Priority,
        string EndpointId, ControllerSignalKind Kind, double Scalar, byte[]? Bytes, string? Unit = null);

    private sealed class ModelControllerRuntime(RenodeControllerOptions options, ValidatedModelAssignment assignment)
    {
        public RenodeControllerOptions Options { get; } = options;
        public ValidatedModelAssignment Assignment { get; } = assignment;
        public string? EchoInputEndpointId => Assignment.Model.EchoPulse?.InputEndpointId;
        public TcpListener Listener { get; set; } = null!;
        public TcpClient? Client { get; set; }
        public NetworkStream? Stream { get; set; }
        public Task? UartTask { get; set; }
        public Channel<StagedUartIngress> UartInput { get; } = Channel.CreateBounded<StagedUartIngress>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
        });
        public Channel<byte[]> UartOutput { get; } = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
        });
        public ulong InputSequence { get; set; }
        public ulong DutySequence { get; set; }
        public double LeftDuty { get; set; }
        public double RightDuty { get; set; }
        public bool LeftForward { get; set; } = true;
        public bool RightForward { get; set; } = true;
        public double LastLeftSignedDuty { get; set; }
        public double LastRightSignedDuty { get; set; }
    }

    private readonly DeterministicModelBackend _backend;
    private readonly string _robotId;
    private readonly Dictionary<string, ModelControllerRuntime> _controllers = new(StringComparer.Ordinal);
    private readonly List<HardwareEvent> _pendingSensorEvents = [];
    private readonly List<PendingEchoFall> _pendingEchoFalls = [];
    private readonly Channel<TimestampedSimulationEvent> _simulationEvents = Channel.CreateBounded<TimestampedSimulationEvent>(new BoundedChannelOptions(2)
    {
        FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = true
    });
    private readonly TaskCompletionSource<DeterministicModelRuntimeEndpoints> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private TcpListener? _eventListener;
    private TcpClient? _eventClient;
    private NetworkStream? _eventStream;
    private Task<TcpClient>? _pendingEventClient;
    private ulong _lastTickIndex;
    private long _coordinatedTimeNs;
    private ulong _eventSequence;
    private readonly long? _testMcuHaltAtSimTimeNs;
    private readonly string _performanceScenario;
    private readonly DeterministicModelPerformanceTrace? _performanceTrace;
    private readonly long _initialSimTimeNs;
    private bool _testMcuHaltInjected;
    private bool _disposed;

    public Task<DeterministicModelRuntimeEndpoints> Ready => _ready.Task;

    public DeterministicModelEventHost(string robotId, IReadOnlyList<RenodeControllerOptions> controllerOptions,
        IReadOnlyList<ValidatedModelAssignment> assignments)
        : this(robotId, controllerOptions, assignments, new DeterministicModelBackend(assignments), null)
    {
    }

    public DeterministicModelEventHost(string robotId, IReadOnlyList<RenodeControllerOptions> controllerOptions,
        IReadOnlyList<ValidatedModelAssignment> assignments, DeterministicModelBackend backend, long? testMcuHaltAtSimTimeNs = null,
        string? performanceDirectory = null, string performanceScenario = "unspecified", long initialSimTimeNs = 0)
    {
        if (string.IsNullOrWhiteSpace(robotId)) throw new ArgumentException("Robot ID is required.", nameof(robotId));
        ArgumentNullException.ThrowIfNull(controllerOptions);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(backend);
        if (backend.Kind != ControllerBackendKind.DeterministicModel)
            throw new InvalidDataException("Deterministic model event host requires the validated model backend.");
        if (controllerOptions.Count == 0 || assignments.Count != controllerOptions.Count)
            throw new InvalidDataException("Every runtime controller must have exactly one validated model assignment.");
        if (controllerOptions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != controllerOptions.Count)
            throw new InvalidDataException("Deterministic runtime controller IDs must be unique.");
        _robotId = robotId;
        if (testMcuHaltAtSimTimeNs is <= 0) throw new ArgumentOutOfRangeException(nameof(testMcuHaltAtSimTimeNs));
        _testMcuHaltAtSimTimeNs = testMcuHaltAtSimTimeNs;
        if (initialSimTimeNs < 0) throw new ArgumentOutOfRangeException(nameof(initialSimTimeNs));
        _initialSimTimeNs = initialSimTimeNs;
        _performanceScenario = performanceScenario;
        if (performanceDirectory is not null) _performanceTrace = new DeterministicModelPerformanceTrace(performanceDirectory, performanceScenario);
        if (!backend.ControllerInstanceIds.Order(StringComparer.Ordinal).SequenceEqual(
                assignments.Select(item => item.ControllerInstanceId).Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Deterministic backend assignments do not match the host controller configuration.");
        _backend = backend;
        foreach (var options in controllerOptions)
        {
            var assignment = assignments.SingleOrDefault(item => StringComparer.Ordinal.Equals(item.ControllerInstanceId, options.Id))
                ?? throw new InvalidDataException($"Controller {options.Id} has no validated model assignment.");
            _controllers.Add(options.Id, new ModelControllerRuntime(options, assignment));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        await _backend.ResetAsync(_initialSimTimeNs, token);
        _coordinatedTimeNs = _initialSimTimeNs;
        Console.WriteLine($"DETERMINISTIC_MODEL_CLOCK_EPOCH sim_time_ns={_initialSimTimeNs}");
        var uartPorts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var controller in _controllers.Values)
        {
            controller.Listener = new TcpListener(IPAddress.Loopback, 0);
            controller.Listener.Start(1);
            controller.UartTask = RunUartConnectionAsync(controller, token);
            uartPorts.Add(controller.Options.Id, ((IPEndPoint)controller.Listener.LocalEndpoint).Port);
        }
        _eventListener = new TcpListener(IPAddress.Loopback, 0);
        _eventListener.Start(1);
        var eventPort = ((IPEndPoint)_eventListener.LocalEndpoint).Port;
        _pendingEventClient = _eventListener.AcceptTcpClientAsync(token).AsTask();
        _ready.TrySetResult(new DeterministicModelRuntimeEndpoints(eventPort, uartPorts));
        var first = _controllers.Values.First();
        var firmwareHash = first.Assignment.Profile.Identity.FirmwareSha256.ToLowerInvariant();
        Console.WriteLine($"READY RoboWorld RobotService health=healthy mode=deterministic-model events_port={eventPort} uart=tcp://127.0.0.1:{uartPorts[first.Options.Id]} firmware_sha256={firmwareHash}");
        foreach (var controller in _controllers.Values)
            Console.WriteLine($"UART_ENDPOINT controller_id={controller.Options.Id} component_id={controller.Options.ComponentInstanceId} uart=tcp://127.0.0.1:{uartPorts[controller.Options.Id]} firmware_sha256={controller.Assignment.Profile.Identity.FirmwareSha256.ToLowerInvariant()}");

        try
        {
            _eventClient = await _pendingEventClient.WaitAsync(token);
            _eventClient.NoDelay = true;
            _eventStream = _eventClient.GetStream();
            await WriteEnvelopeAsync(_eventStream, new Envelope
            {
                ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolMinor, SessionId = _sessionId,
                WorldId = "roboworld-local", RobotId = _robotId, SimTimeNs = _initialSimTimeNs,
                Hello = new Hello { ClientKind = "robotservice", InstanceVersion = "Goal03-deterministic", Capabilities = { "deterministic_model", "pwm_output", "gpio_direction" } }
            }, token);
            var clientHello = await ReadEnvelopeAsync(_eventStream, token);
            if (clientHello.ProtocolMajor != ProtocolMajor || clientHello.SessionId != _sessionId
                || clientHello.PayloadCase != Envelope.PayloadOneofCase.Hello || clientHello.Hello.ClientKind != "godot")
                throw new InvalidDataException("Godot deterministic-model event-stream handshake was incompatible.");
            Console.WriteLine("EVENT_STREAM_CONNECTED protocol=1.0 backend=deterministic-model");
            _ = ReadSimulationEventsAsync(_eventStream, token);
            await ProcessSimulationEventsAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _stop.Cancel();
            _simulationEvents.Writer.TryComplete();
            foreach (var controller in _controllers.Values)
            {
                controller.UartInput.Writer.TryComplete();
                controller.UartOutput.Writer.TryComplete();
                controller.Listener.Stop();
                controller.Client?.Dispose();
            }
            _eventListener.Stop();
            _eventClient?.Dispose();
            try
            {
                await Task.WhenAll(_controllers.Values.Select(item => item.UartTask).OfType<Task>());
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    private async Task RunUartConnectionAsync(ModelControllerRuntime controller, CancellationToken token)
    {
        try
        {
            controller.Client = await controller.Listener.AcceptTcpClientAsync(token);
            controller.Client.NoDelay = true;
            controller.Stream = controller.Client.GetStream();
            if (controller.Assignment.Model.InitialUartOutputBytes is { Length: > 0 } bootBytes)
            {
                await controller.Stream.WriteAsync(bootBytes, token);
                Console.WriteLine($"UART_PROXY_CONNECTED controller_id={controller.Options.Id} boot_stream=measured-model bytes={bootBytes.Length}");
            }
            else Console.WriteLine($"UART_PROXY_CONNECTED controller_id={controller.Options.Id} boot_stream=none");
            var inputTask = ReadUartInputsAsync(controller, token);
            var outputTask = WriteUartOutputsAsync(controller, token);
            await Task.WhenAny(inputTask, outputTask);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { Console.Error.WriteLine($"MODEL_UART_PROXY_FAILED controller={controller.Options.Id} {exception.Message}"); }
    }

    private static async Task ReadUartInputsAsync(ModelControllerRuntime controller, CancellationToken token)
    {
        var stream = controller.Stream ?? throw new InvalidOperationException("Model UART stream is not connected.");
        var buffer = new byte[1024];
        long transferred = 0;
        while (!token.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) return;
            var arrivalHostTicks = Stopwatch.GetTimestamp();
            await controller.UartInput.Writer.WriteAsync(new StagedUartIngress(
                buffer.AsSpan(0, read).ToArray(), arrivalHostTicks), token);
            transferred += read;
            Console.WriteLine($"UART_PROXY_TRANSFER direction=controller_to_mcu controller_id={controller.Options.Id} bytes={read} total={transferred}");
            Console.WriteLine($"UART_PROXY_INGRESS controller_id={controller.Options.Id} mono_ticks={arrivalHostTicks} bytes={read} mode=deterministic queued_for_tick_boundary=true");
        }
    }

    private static async Task WriteUartOutputsAsync(ModelControllerRuntime controller, CancellationToken token)
    {
        var stream = controller.Stream ?? throw new InvalidOperationException("Model UART stream is not connected.");
        await foreach (var bytes in controller.UartOutput.Reader.ReadAllAsync(token))
        {
            await stream.WriteAsync(bytes, token);
            Console.WriteLine($"UART_PROXY_TRANSFER direction=mcu_to_controller controller_id={controller.Options.Id} bytes={bytes.Length} model=deterministic");
        }
    }

    private async Task ReadSimulationEventsAsync(NetworkStream stream, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var readStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                var envelope = await ReadEnvelopeAsync(stream, token);
                // Keep a shared monotonic ingress cut-off even when diagnostics are disabled.
                var readTicks = Stopwatch.GetTimestamp();
                if (envelope.ProtocolMajor != ProtocolMajor || envelope.SessionId != _sessionId
                    || envelope.PayloadCase is not (Envelope.PayloadOneofCase.Tick or Envelope.PayloadOneofCase.HardwareEvent))
                    throw new InvalidDataException("Godot sent an invalid deterministic-model event envelope.");
                var blockedStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                var queueDepth = _simulationEvents.Reader.CanCount ? _simulationEvents.Reader.Count : -1;
                while (true)
                {
                    if (!await _simulationEvents.Writer.WaitToWriteAsync(token))
                        throw new ChannelClosedException("Deterministic-model simulation-event channel closed unexpectedly.");
                    var timestampedEvent = new TimestampedSimulationEvent(
                        envelope, readTicks,
                        _performanceTrace is null ? 0 : Stopwatch.GetElapsedTime(readStart, readTicks).Ticks * 100L,
                        _performanceTrace is null ? 0 : Stopwatch.GetElapsedTime(blockedStart).Ticks * 100L,
                        queueDepth);
                    if (_simulationEvents.Writer.TryWrite(timestampedEvent)) break;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"MODEL_EVENT_STREAM_FAILED {exception.Message}");
            _stop.Cancel();
        }
    }

    private async Task ProcessSimulationEventsAsync(CancellationToken token)
    {
        await foreach (var timestamped in _simulationEvents.Reader.ReadAllAsync(token))
        {
            var envelope = timestamped.Envelope;
            if (envelope.PayloadCase == Envelope.PayloadOneofCase.HardwareEvent)
            {
                var sensor = envelope.HardwareEvent;
                if (sensor.Kind != HardwareEvent.Types.Kind.SensorSample || sensor.EndpointId != "distance.echo"
                    || sensor.Unit != "meters" || !double.IsFinite(sensor.Scalar) || sensor.Scalar < -1 || sensor.Scalar > 4
                    || sensor.EffectiveSimTimeNs <= _coordinatedTimeNs)
                    throw new InvalidDataException("Godot submitted an invalid deterministic-model distance sensor sample.");
                if (_pendingSensorEvents.Count >= 64) throw new InvalidDataException("Deterministic-model sensor event queue is full.");
                _pendingSensorEvents.Add(sensor.Clone());
                continue;
            }

            var tick = envelope.Tick;
            if (tick.TickIndex == 0 || tick.TickIndex != _lastTickIndex + 1 || tick.DeltaNs <= 0 || tick.StartNs != _coordinatedTimeNs)
                throw new InvalidDataException($"Godot submitted an invalid or out-of-order deterministic-model tick {_lastTickIndex}->{tick.TickIndex} at {tick.StartNs} ns; expected {_coordinatedTimeNs} ns.");
            var endNs = checked(tick.StartNs + tick.DeltaNs);
            var processingStartTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
            var scheduled = new List<ScheduledModelInput>();
            foreach (var controller in _controllers.Values)
            {
                var uartReader = controller.UartInput.Reader;
                while (uartReader.TryPeek(out var pendingUart) && pendingUart.ArrivalHostTicks <= timestamped.ReadMonoTicks)
                {
                    if (!uartReader.TryRead(out pendingUart)) continue;
                    var bytes = pendingUart.Bytes;
                    Console.WriteLine($"UART_MODEL_INPUT_BOUNDARY controller_id={controller.Options.Id} sim_time_ns={tick.StartNs} arrival_mono_ticks={pendingUart.ArrivalHostTicks} tick_read_mono_ticks={timestamped.ReadMonoTicks} bytes={bytes.Length}");
                    scheduled.Add(new ScheduledModelInput(controller.Options.Id, tick.StartNs, 1,
                        controller.Assignment.Model.UartInputEndpointId ?? throw new InvalidDataException("Controller profile has no UART input endpoint."),
                        ControllerSignalKind.UartRxByte, 0, bytes, "bytes"));
                }
            }
            foreach (var echoFall in _pendingEchoFalls.Where(item => item.SimTimeNs <= endNs).ToArray())
            {
                if (echoFall.SimTimeNs < tick.StartNs) throw new InvalidDataException("Deterministic echo pulse became overdue before the next physics boundary.");
                scheduled.Add(new ScheduledModelInput(echoFall.ControllerId, echoFall.SimTimeNs, 0,
                    echoFall.EndpointId, ControllerSignalKind.GpioLevel, 0, null, "level"));
                _pendingEchoFalls.Remove(echoFall);
            }
            if (!_testMcuHaltInjected && _testMcuHaltAtSimTimeNs is long requestedHaltNs && tick.StartNs >= requestedHaltNs)
            {
                var target = _controllers.Values.FirstOrDefault(controller => controller.Assignment.Model.FaultRules?.Any(rule =>
                    rule.Behavior == ControllerModelFaultBehavior.HaltAndLatchOutputs && rule.FaultCode == "MCU_HALTED") == true)
                    ?? throw new InvalidDataException("Test MCU-halt injection requires a model with a measured MCU_HALTED transition.");
                var endpoint = target.Assignment.Model.FaultRules!.Single(rule => rule.FaultCode == "MCU_HALTED").InputEndpointId;
                scheduled.Add(new ScheduledModelInput(target.Options.Id, tick.StartNs, 3, endpoint,
                    ControllerSignalKind.Fault, 0, null, "MCU_HALTED"));
                _testMcuHaltInjected = true;
                Console.WriteLine($"TEST_MODEL_MCU_HALTED controller_id={target.Options.Id} sim_time_ns={tick.StartNs}");
            }
            foreach (var sensor in _pendingSensorEvents.ToArray())
            {
                if (sensor.EffectiveSimTimeNs != endNs)
                    throw new InvalidDataException("Distance sensor sample timestamp does not match the completed physics boundary.");
                var targets = _controllers.Values.Where(item => item.EchoInputEndpointId is not null).ToArray();
                if (targets.Length == 0) throw new InvalidDataException("No controller profile is wired to the configured distance sensor.");
                if (sensor.Scalar < 0)
                {
                    if (targets.Any(target => target.Assignment.Model.EchoPulse?.NoPulseBehavior != ControllerEchoNoPulseBehavior.RetainLastValidReading))
                    {
                        const string reason = "No-echo sensor behavior is outside the characterized deterministic-model envelope; restart in exact firmware mode.";
                        await SendDiagnosticAsync(reason, token);
                        throw new InvalidDataException(reason);
                    }
                    Console.WriteLine($"SENSOR_NO_ECHO backend=deterministic sim_time_ns={endNs} controllers={targets.Length} behavior=retain_last_valid_reading");
                    _pendingSensorEvents.Remove(sensor);
                    continue;
                }
                var pulseWidthNs = (long)Math.Round(2 * sensor.Scalar / 343d * 1_000_000_000d, MidpointRounding.AwayFromZero);
                if (pulseWidthNs <= 0) throw new InvalidDataException("Distance sample produced a pulse outside the validated positive-width echo behavior.");
                foreach (var target in targets)
                {
                    var endpoint = target.EchoInputEndpointId!;
                    scheduled.Add(new ScheduledModelInput(target.Options.Id, endNs, 2, endpoint, ControllerSignalKind.GpioLevel, 1, null));
                    _pendingEchoFalls.Add(new PendingEchoFall(target.Options.Id, endpoint, checked(endNs + pulseWidthNs)));
                }
                _pendingSensorEvents.Remove(sensor);
            }
            foreach (var input in scheduled.OrderBy(item => item.SimTimeNs).ThenBy(item => item.Priority)
                         .ThenBy(item => item.ControllerId, StringComparer.Ordinal))
            {
                var controller = _controllers[input.ControllerId];
                _backend.ScheduleInput(new ControllerInputEvent(input.ControllerId, input.SimTimeNs,
                    checked(++controller.InputSequence), input.EndpointId, input.Kind, input.Scalar, input.Bytes,
                    input.Unit ?? (input.Kind == ControllerSignalKind.UartRxByte ? "bytes" : "level")));
            }

            var eventBoundaries = scheduled.Select(item => item.SimTimeNs)
                .Where(time => time > _backend.CurrentSimTimeNs && time <= endNs)
                .Distinct().Order().ToArray();
            var modelPipelineStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
            foreach (var boundaryNs in eventBoundaries)
                await AdvanceAndRouteAsync(boundaryNs, token);
            await AdvanceAndRouteAsync(endNs, token);
            var modelPipelineNs = _performanceTrace is null ? 0 : Stopwatch.GetElapsedTime(modelPipelineStart).Ticks * 100L;
            _coordinatedTimeNs = endNs;
            _lastTickIndex = tick.TickIndex;
            var ackWriteStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
            await WriteEnvelopeAsync(_eventStream!, new Envelope
            {
                ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolMinor, SessionId = _sessionId,
                WorldId = "roboworld-local", RobotId = _robotId, Sequence = tick.TickIndex,
                SimTimeNs = endNs,
                Tick = new SimulationTick { TickIndex = tick.TickIndex, StartNs = tick.StartNs, DeltaNs = tick.DeltaNs, RealTimeFactor = tick.RealTimeFactor }
            }, token);
            if (_performanceTrace is not null)
            {
                _performanceTrace.Record(new DeterministicModelTickPerformanceSample(
                    tick.TickIndex, tick.StartNs, timestamped.ReadMonoTicks, processingStartTicks,
                    timestamped.ReadParseNs,
                    Stopwatch.GetElapsedTime(timestamped.ReadMonoTicks, processingStartTicks).Ticks * 100L,
                    modelPipelineNs, Stopwatch.GetElapsedTime(ackWriteStart).Ticks * 100L,
                    timestamped.QueueDepthAfterRead, timestamped.ChannelWriteBlockedNs, _controllers.Count),
                    _performanceScenario);
            }
            if (tick.TickIndex % 120 == 0) Console.WriteLine($"CLOCK_TICK backend=deterministic tick={tick.TickIndex} sim_time_ns={endNs} delta_ns={tick.DeltaNs}");
        }
    }

    private async Task AdvanceAndRouteAsync(long simTimeNs, CancellationToken token)
    {
        var result = await _backend.AdvanceToAsync(simTimeNs, token);
        if (!result.IsSupported)
        {
            await SendDiagnosticAsync(result.Diagnostics.FirstOrDefault(item => item.IsFault)?.Message ?? "Controller input is outside the validated model.", token);
            throw new InvalidDataException("Deterministic controller rejected an input outside its validated model.");
        }
        await RouteOutputsAsync(result.Outputs, token);
        foreach (var diagnostic in result.Diagnostics.Where(item => item.IsFault))
            await SendControllerFaultDiagnosticAsync(diagnostic, token);
    }

    private async Task RouteOutputsAsync(IReadOnlyList<ControllerOutputEvent> outputs, CancellationToken token)
    {
        var changedControllers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs)
        {
            if (!_controllers.TryGetValue(output.ControllerInstanceId, out var controller))
                throw new InvalidDataException("Deterministic backend emitted an output for an unassigned controller.");
            switch (output.Kind)
            {
                case ControllerSignalKind.UartTxByte:
                    if (output.Bytes is not { Length: > 0 }) throw new InvalidDataException("Deterministic backend emitted an empty UART response.");
                    await controller.UartOutput.Writer.WriteAsync(output.Bytes, token);
                    break;
                case ControllerSignalKind.PwmCapture when output.EndpointId == "tim3.ch1.duty_fraction":
                    if (!double.IsFinite(output.Scalar) || output.Scalar is < 0 or > 1) throw new InvalidDataException("Model left PWM duty is outside [0,1].");
                    controller.LeftDuty = output.Scalar;
                    changedControllers.Add(output.ControllerInstanceId);
                    break;
                case ControllerSignalKind.PwmCapture when output.EndpointId == "tim3.ch2.duty_fraction":
                    if (!double.IsFinite(output.Scalar) || output.Scalar is < 0 or > 1) throw new InvalidDataException("Model right PWM duty is outside [0,1].");
                    controller.RightDuty = output.Scalar;
                    changedControllers.Add(output.ControllerInstanceId);
                    break;
                case ControllerSignalKind.GpioLevel when output.EndpointId == "gpio.pd2.direction":
                    controller.LeftForward = output.Scalar == 1;
                    changedControllers.Add(output.ControllerInstanceId);
                    break;
                case ControllerSignalKind.GpioLevel when output.EndpointId == "gpio.pd3.direction":
                    controller.RightForward = output.Scalar == 1;
                    changedControllers.Add(output.ControllerInstanceId);
                    break;
                default:
                    throw new InvalidDataException($"Model output {output.Kind} at {output.EndpointId} has no graph-derived runtime route.");
            }
        }
        foreach (var id in changedControllers.Order(StringComparer.Ordinal))
        {
            var controller = _controllers[id];
            var left = controller.LeftDuty == 0 ? 0 : controller.LeftForward ? controller.LeftDuty : -controller.LeftDuty;
            var right = controller.RightDuty == 0 ? 0 : controller.RightForward ? controller.RightDuty : -controller.RightDuty;
            if (left != controller.LastLeftSignedDuty)
            {
                if (string.IsNullOrWhiteSpace(controller.Options.Channel1ActuatorId))
                    throw new InvalidDataException($"Model output for {id} has no validated channel-one actuator route.");
                await SendPwmEventAsync(controller, "tim3.ch1.pa6", controller.Options.Channel1ActuatorId, left, token);
                controller.LastLeftSignedDuty = left;
            }
            if (right != controller.LastRightSignedDuty)
            {
                if (string.IsNullOrWhiteSpace(controller.Options.Channel2ActuatorId))
                    throw new InvalidDataException($"Model output for {id} has no validated channel-two actuator route.");
                await SendPwmEventAsync(controller, "tim3.ch2.pa7", controller.Options.Channel2ActuatorId, right, token);
                controller.LastRightSignedDuty = right;
            }
        }
    }

    private async Task SendPwmEventAsync(ModelControllerRuntime controller, string endpoint, string actuatorId, double duty, CancellationToken token)
    {
        await WriteEnvelopeAsync(_eventStream!, new Envelope
        {
            ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolMinor, SessionId = _sessionId,
            WorldId = "roboworld-local", RobotId = _robotId, ComponentId = actuatorId,
            SimTimeNs = _backend.CurrentSimTimeNs,
            Sequence = checked(++_eventSequence),
            HardwareEvent = new HardwareEvent
            {
                Kind = HardwareEvent.Types.Kind.PwmOutput, EndpointId = endpoint, Scalar = duty, Unit = "fraction_signed",
                EffectiveSimTimeNs = _backend.CurrentSimTimeNs,
                FidelityNote = "Validated deterministic controller model output routed through the current robot electrical graph."
            }
        }, token);
        Console.WriteLine($"PERIPHERAL_OUTPUT backend=deterministic controller_id={controller.Options.Id} sim_time_ns={_backend.CurrentSimTimeNs} endpoint={endpoint} duty={duty.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    private async Task SendDiagnosticAsync(string reason, CancellationToken token)
    {
        await WriteEnvelopeAsync(_eventStream!, new Envelope
        {
            ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolMinor, SessionId = _sessionId,
            WorldId = "roboworld-local", RobotId = _robotId, Sequence = checked(++_eventSequence),
            Diagnostic = new ComponentDiagnostic
            {
                Status = ComponentDiagnostic.Types.Status.Failed, ComponentPath = $"{_robotId}/controller",
                ReasonCode = "E_CONTROLLER_MODEL_UNSUPPORTED", Reason = reason, RootFaultId = _sessionId
            }
        }, token);
    }

    private async Task SendControllerFaultDiagnosticAsync(ControllerDiagnostic diagnostic, CancellationToken token)
    {
        if (diagnostic.ControllerInstanceId is not { } controllerId || !_controllers.TryGetValue(controllerId, out var controller))
            throw new InvalidDataException("Measured controller fault diagnostic did not identify its source controller.");
        await WriteEnvelopeAsync(_eventStream!, new Envelope
        {
            ProtocolMajor = ProtocolMajor, ProtocolMinor = ProtocolMinor, SessionId = _sessionId,
            WorldId = "roboworld-local", RobotId = _robotId, Sequence = checked(++_eventSequence),
            SimTimeNs = diagnostic.SimTimeNs,
            Diagnostic = new ComponentDiagnostic
            {
                Status = ComponentDiagnostic.Types.Status.Failed, ComponentPath = $"{_robotId}/{controller.Options.ComponentInstanceId}",
                ReasonCode = "E_CONTROLLER_HALTED", Reason = diagnostic.Message, RootFaultId = controllerId
            }
        }, token);
    }

    private static async Task<Envelope> ReadEnvelopeAsync(NetworkStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > MaxFrameBytes) throw new InvalidDataException($"Invalid protobuf frame length {length}.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, token);
        return Envelope.Parser.ParseFrom(bytes);
    }

    private static async Task WriteEnvelopeAsync(NetworkStream stream, Envelope envelope, CancellationToken token)
    {
        var payload = envelope.ToByteArray();
        if (payload.Length is 0 or > MaxFrameBytes) throw new InvalidDataException($"Invalid protobuf frame length {payload.Length}.");
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _eventListener?.Stop();
        _eventClient?.Dispose();
        foreach (var controller in _controllers.Values)
        {
            controller.Listener?.Stop();
            controller.Client?.Dispose();
            controller.UartInput.Writer.TryComplete();
            controller.UartOutput.Writer.TryComplete();
        }
        _simulationEvents.Writer.TryComplete();
        _performanceTrace?.Dispose();
        await _backend.DisposeAsync();
        _stop.Dispose();
    }
}
