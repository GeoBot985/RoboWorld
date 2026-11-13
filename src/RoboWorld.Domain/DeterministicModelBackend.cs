namespace RoboWorld.Domain;

using System.Globalization;
using System.Text;

public sealed record ValidatedModelAssignment(string ControllerInstanceId, DeterministicControllerProfile Profile,
    ControllerProfileIdentity CurrentIdentity, ControllerModelDocument Model);

/// <summary>Session backend for independent, already-qualified deterministic models sharing one sim-time frontier.</summary>
public sealed class DeterministicModelBackend : IControllerBackend
{
    private sealed record PendingInput(ControllerInputEvent Input, long Order);
    private sealed class UartLineState
    {
        public List<byte> Bytes { get; } = [];
        public bool DiscardUntilLineFeed { get; set; }
    }
    private sealed class EchoPulseState
    {
        public long? RisingEdgeTimeNs { get; set; }
        public long? LastDistanceMm { get; set; }
    }
    private sealed class UartRateWindow
    {
        private const long BucketWidthNs = 1_000_000;
        private const int BucketCount = 1001;
        private readonly long[] _bucketIndices = Enumerable.Repeat(-1L, BucketCount).ToArray();
        private readonly int[] _bucketBytes = new int[BucketCount];
        private long _firstIncludedBucket;
        private long _totalBytes;

        public bool TryAdd(long simTimeNs, int byteCount, int maximumBytesPerSecond)
        {
            var currentBucket = simTimeNs / BucketWidthNs;
            var cutoff = simTimeNs - 1_000_000_000L;
            // Include the cutoff bucket in full. This may conservatively count up to 1 ms of expired bytes,
            // but it cannot undercount bytes whose exact nanosecond timestamp is still inside the window.
            var firstIncludedBucket = cutoff < 0 ? 0 : cutoff / BucketWidthNs;
            var expiredBucketCount = firstIncludedBucket - _firstIncludedBucket;
            if (expiredBucketCount < 0) throw new InvalidDataException("UART rate events must be scheduled in timestamp order.");
            var expiredBytes = 0L;
            if (expiredBucketCount >= BucketCount)
            {
                expiredBytes = _totalBytes;
            }
            else
            {
                for (var bucket = _firstIncludedBucket; bucket < firstIncludedBucket; bucket++)
                {
                    var expiredSlot = (int)(bucket % BucketCount);
                    if (_bucketIndices[expiredSlot] == bucket) expiredBytes += _bucketBytes[expiredSlot];
                }
            }
            var bytesInWindow = _totalBytes - expiredBytes;
            if (byteCount > maximumBytesPerSecond - bytesInWindow) return false;

            if (expiredBucketCount >= BucketCount)
            {
                Array.Fill(_bucketIndices, -1L);
                Array.Clear(_bucketBytes);
            }
            else
            {
                for (var bucket = _firstIncludedBucket; bucket < firstIncludedBucket; bucket++)
                {
                    var expiredSlot = (int)(bucket % BucketCount);
                    if (_bucketIndices[expiredSlot] == bucket)
                    {
                        _bucketIndices[expiredSlot] = -1;
                        _bucketBytes[expiredSlot] = 0;
                    }
                }
            }
            _firstIncludedBucket = firstIncludedBucket;
            _totalBytes = bytesInWindow;
            var slot = (int)(currentBucket % BucketCount);
            if (_bucketIndices[slot] != currentBucket)
            {
                if (_bucketIndices[slot] >= firstIncludedBucket) throw new InvalidDataException("UART rate window exceeded its bounded bucket span.");
                _bucketIndices[slot] = currentBucket;
                _bucketBytes[slot] = 0;
            }
            _bucketBytes[slot] += byteCount;
            _totalBytes += byteCount;
            return true;
        }
    }
    private readonly Dictionary<string, ValidatedModelAssignment> _assignments;
    private readonly PriorityQueue<PendingInput, (long Time, long Order)> _pending = new();
    private readonly Dictionary<string, ulong> _lastSequence = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastScheduledTimeNs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UartRateWindow> _uartByteWindows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, UartLineState> _uartLineStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EchoPulseState> _echoPulseStates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unsupportedControllers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _haltedControllers = new(StringComparer.Ordinal);
    private readonly int _maxEventsPerAdvance;
    private readonly int _maxPendingEvents;
    private long _order;
    private bool _disabledUntilReset;
    private bool _disposed;

    public ControllerBackendKind Kind => ControllerBackendKind.DeterministicModel;
    public IReadOnlyList<string> ControllerInstanceIds { get; }
    public long CurrentSimTimeNs { get; private set; }

    public DeterministicModelBackend(IEnumerable<ValidatedModelAssignment> assignments, int maxEventsPerAdvance = 256, int maxPendingEvents = 8192)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        if (maxEventsPerAdvance is <= 0 or > 65536) throw new ArgumentOutOfRangeException(nameof(maxEventsPerAdvance));
        if (maxPendingEvents is <= 0 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(maxPendingEvents));
        _maxEventsPerAdvance = maxEventsPerAdvance;
        _maxPendingEvents = maxPendingEvents;
        _assignments = new Dictionary<string, ValidatedModelAssignment>(StringComparer.Ordinal);
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.ControllerInstanceId)) throw new InvalidDataException("Controller assignment has no instance ID.");
            var identity = ControllerProfileValidator.Validate(assignment.Profile.Identity, assignment.CurrentIdentity);
            if (!identity.IsValid) throw new InvalidDataException("Controller profile identity mismatch: " + string.Join(",", identity.MismatchReasons));
            if (assignment.Profile.SchemaVersion != 1 || string.IsNullOrWhiteSpace(assignment.Profile.ProfileId)
                || !IsSha256(assignment.Profile.ModelDocumentSha256) || !IsSha256(assignment.Profile.TraceCorpusSha256)
                || !IsSha256(assignment.Profile.HeldOutSequencesSha256) || assignment.Profile.HeldOutSequenceCount <= 0
                || assignment.Profile.SupportedBehaviors.Count == 0)
                throw new InvalidDataException("Controller profile metadata is incomplete or unsupported.");
            ValidateEnvelope(assignment.Profile.Envelope);
            if (!ControllerProfileHashes.Matches(assignment.Profile.ModelDocumentSha256, ControllerProfileHashes.Compute(assignment.Model)))
                throw new InvalidDataException("Controller model document hash does not match its qualified profile.");
            ControllerModelEvaluator.ValidateDocument(assignment.Model, assignment.Model.MaximumRules);
            if (!ControllerProfileHashes.Matches(assignment.Profile.Identity.InputEnvelopeSha256, ControllerProfileHashes.Compute(assignment.Profile.Envelope)))
                throw new InvalidDataException("Controller input envelope hash does not match its qualified profile identity.");
            if (assignment.Profile.FidelityLabel != "VALIDATED") throw new InvalidDataException("Only profiles explicitly labeled VALIDATED may execute.");
            if (assignment.Model.UartCommands is not null
                && (!assignment.Profile.Envelope.SupportedSignals.Contains(ControllerSignalKind.UartRxByte)
                    || !assignment.Profile.Envelope.SupportedEndpoints.Contains(assignment.Model.UartInputEndpointId!, StringComparer.Ordinal)
                    || assignment.Model.EchoPulse is null && assignment.Profile.Envelope.SupportedSignals
                        .Where(signal => signal != ControllerSignalKind.Fault).Distinct().Count() != 1
                    || assignment.Model.EchoPulse is not null && (!assignment.Profile.Envelope.SupportedSignals.Contains(ControllerSignalKind.GpioLevel)
                        || !assignment.Profile.Envelope.SupportedEndpoints.Contains(assignment.Model.EchoPulse.InputEndpointId, StringComparer.Ordinal))))
                throw new InvalidDataException("UART model and profile input envelope do not describe the same raw UART input.");
            var faultRules = assignment.Model.FaultRules ?? [];
            if (faultRules.Count != 0 && (!assignment.Profile.Envelope.SupportedSignals.Contains(ControllerSignalKind.Fault)
                    || faultRules.Any(rule => !assignment.Profile.Envelope.SupportedEndpoints.Contains(rule.InputEndpointId, StringComparer.Ordinal)))
                || faultRules.Count == 0 && assignment.Profile.Envelope.SupportedSignals.Contains(ControllerSignalKind.Fault))
                throw new InvalidDataException("Controller fault rules and profile input envelope do not describe the same measured fault inputs.");
            var frozenModel = assignment.Model with
            {
                Rules = assignment.Model.Rules.ToArray(),
                EchoPulse = assignment.Model.EchoPulse is { } echoPulse
                    ? echoPulse with { InitialInvalidResponseBytes = echoPulse.InitialInvalidResponseBytes?.ToArray() }
                    : null,
                UartCommands = assignment.Model.UartCommands?.Select(command => command with
                {
                    OperandBindings = command.OperandBindings.ToArray(),
                    ResponseBytes = command.ResponseBytes?.ToArray()
                }).ToArray(),
                ExactUartLines = assignment.Model.ExactUartLines?.Select(rule => rule with
                {
                    RequestBytes = rule.RequestBytes.ToArray(),
                    ResponseBytes = rule.ResponseBytes.ToArray()
                }).ToArray(),
                FaultRules = assignment.Model.FaultRules?.ToArray(),
                InitialUartOutputBytes = assignment.Model.InitialUartOutputBytes?.ToArray()
            };
            if (!_assignments.TryAdd(assignment.ControllerInstanceId, assignment with { Model = frozenModel }))
                throw new InvalidDataException("Duplicate controller model assignment.");
        }
        if (_assignments.Count == 0) throw new InvalidDataException("At least one validated controller model assignment is required.");
        ControllerInstanceIds = _assignments.Keys.Order(StringComparer.Ordinal).ToArray();
    }

    public ValueTask ResetAsync(long simTimeNs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (simTimeNs < 0) throw new ArgumentOutOfRangeException(nameof(simTimeNs));
        if (_assignments.Values.Any(assignment => simTimeNs > assignment.Profile.Envelope.MaximumSimTimeNs))
            throw new InvalidDataException("Reset time exceeds a controller profile's validated time envelope.");
        _pending.Clear();
        _lastSequence.Clear();
        _lastScheduledTimeNs.Clear();
        _uartByteWindows.Clear();
        _uartLineStates.Clear();
        _echoPulseStates.Clear();
        _unsupportedControllers.Clear();
        _haltedControllers.Clear();
        _disabledUntilReset = false;
        CurrentSimTimeNs = simTimeNs;
        _order = 0;
        return ValueTask.CompletedTask;
    }

    public void ScheduleInput(ControllerInputEvent input)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(input);
        if (_disabledUntilReset) throw new InvalidDataException("Controller model session is disabled after unsupported behavior; reset or restart is required.");
        if (!_assignments.TryGetValue(input.ControllerInstanceId, out var assignment)) throw new InvalidDataException("Input targets an unassigned controller.");
        if (_unsupportedControllers.Contains(input.ControllerInstanceId)) throw new InvalidDataException("Controller model is disabled after an unsupported input; reset or restart is required.");
        if (input.SimTimeNs < CurrentSimTimeNs || input.SimTimeNs < 0) throw new InvalidDataException("Input is stale or has negative simulation time.");
        var envelope = assignment.Profile.Envelope;
        if (input.SimTimeNs < envelope.MinimumSimTimeNs || input.SimTimeNs > envelope.MaximumSimTimeNs)
            throw new InvalidDataException("Input timestamp is outside the validated profile time envelope.");
        if (_lastSequence.TryGetValue(input.ControllerInstanceId, out var previous) && input.Sequence <= previous) throw new InvalidDataException("Controller input sequence is not increasing.");
        if (_lastScheduledTimeNs.TryGetValue(input.ControllerInstanceId, out var previousTime) && input.SimTimeNs < previousTime) throw new InvalidDataException("Controller input timestamps are not increasing with sequence.");
        if (_pending.Count >= _maxPendingEvents) throw new InvalidDataException("Controller input queue is full; input was rejected without dropping queued events.");
        if (!envelope.SupportedEndpoints.Contains(input.EndpointId, StringComparer.Ordinal)
            || !envelope.SupportedSignals.Contains(input.Kind)) throw new InvalidDataException("Input is outside the validated profile envelope.");
        if (input.Kind == ControllerSignalKind.UartRxByte && assignment.Model.UartCommands is not null)
        {
            if (!StringComparer.Ordinal.Equals(input.EndpointId, assignment.Model.UartInputEndpointId))
                throw new InvalidDataException("Raw UART input targets a different endpoint than the model parser.");
        }
        else if (input.Kind == ControllerSignalKind.Fault)
        {
            if (input.Bytes is not null || input.Scalar != 0 || string.IsNullOrWhiteSpace(input.Unit)
                || assignment.Model.FaultRules?.Any(rule => StringComparer.Ordinal.Equals(rule.InputEndpointId, input.EndpointId)
                    && StringComparer.Ordinal.Equals(rule.FaultCode, input.Unit)) != true)
                throw new InvalidDataException("Controller fault input is outside the measured fault-transition rules.");
        }
        else if (input.Kind == ControllerSignalKind.GpioLevel && assignment.Model.EchoPulse is { } echoPulse)
        {
            if (!StringComparer.Ordinal.Equals(input.EndpointId, echoPulse.InputEndpointId) || input.Scalar is not (0 or 1))
                throw new InvalidDataException("GPIO input does not match the characterized echo pulse endpoint or logic levels.");
        }
        else
        {
            _ = ControllerModelEvaluator.Evaluate(assignment.Model, input, envelope.MaximumEventsPerAdvance);
        }
        if (input.Kind == ControllerSignalKind.UartRxByte)
        {
            if (input.Bytes is null || input.Bytes.Length == 0) throw new InvalidDataException("UART input must contain at least one byte.");
            var bytes = input.Bytes.ToArray();
            var window = _uartByteWindows.GetValueOrDefault(input.ControllerInstanceId) ?? new UartRateWindow();
            if (!window.TryAdd(input.SimTimeNs, bytes.Length, envelope.MaximumUartBytesPerSecond))
                throw new InvalidDataException("UART input exceeds the validated bytes-per-second envelope.");
            _uartByteWindows[input.ControllerInstanceId] = window;
            input = input with { Bytes = bytes };
        }
        else if (input.Bytes is not null)
        {
            throw new InvalidDataException("Byte payloads are supported only for UART input events.");
        }
        _lastSequence[input.ControllerInstanceId] = input.Sequence;
        _lastScheduledTimeNs[input.ControllerInstanceId] = input.SimTimeNs;
        var item = new PendingInput(input, _order);
        _pending.Enqueue(item, (input.SimTimeNs, _order++));
    }

    public ValueTask<ControllerAdvanceResult> AdvanceToAsync(long simTimeNs, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        if (_disabledUntilReset) throw new InvalidDataException("Controller model session is disabled after unsupported behavior; reset or restart is required.");
        if (simTimeNs < CurrentSimTimeNs) throw new InvalidDataException("Controller session cannot move simulation time backwards.");
        if (_assignments.Values.Any(assignment => simTimeNs > assignment.Profile.Envelope.MaximumSimTimeNs))
            throw new InvalidDataException("Advance target exceeds a controller profile's validated time envelope.");
        var outputs = new List<ControllerOutputEvent>();
        var diagnostics = new List<ControllerDiagnostic>();
        var isSupported = true;
        var dueItems = _pending.UnorderedItems.Where(item => item.Priority.Time <= simTimeNs).ToArray();
        var dueCount = dueItems.Length;
        if (dueCount > _maxEventsPerAdvance) throw new InvalidDataException("Controller event budget exceeded; no inputs were silently dropped.");
        foreach (var controllerBatch in dueItems.GroupBy(item => item.Element.Input.ControllerInstanceId, StringComparer.Ordinal))
            if (controllerBatch.Count() > _assignments[controllerBatch.Key].Profile.Envelope.MaximumEventsPerAdvance)
                throw new InvalidDataException($"Controller {controllerBatch.Key} exceeded its validated per-advance event budget; no inputs were silently dropped.");
        while (_pending.TryPeek(out var pending, out var priority) && priority.Time <= simTimeNs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var assignment = _assignments[pending.Input.ControllerInstanceId];
            var controllerId = pending.Input.ControllerInstanceId;
            if (_haltedControllers.Contains(controllerId))
            {
                // A halted MCU retains its last peripheral outputs and cannot answer further UART input.
                // The owning device/runtime layer keeps the last routed actuator state until reset.
            }
            else if (_unsupportedControllers.Contains(controllerId))
            {
                diagnostics.Add(new ControllerDiagnostic(simTimeNs, "controller_model_disabled_after_unsupported_input",
                    $"Controller {controllerId} received more input after leaving its characterized model domain; reset or restart is required.", true));
                isSupported = false;
            }
            else if (pending.Input.Kind == ControllerSignalKind.UartRxByte && assignment.Model.UartCommands is not null)
            {
                if (!ProcessUartBytes(assignment, pending.Input, simTimeNs, outputs, diagnostics)) isSupported = false;
            }
            else if (pending.Input.Kind == ControllerSignalKind.GpioLevel && assignment.Model.EchoPulse is not null)
            {
                if (!ProcessEchoPulse(assignment, pending.Input, diagnostics)) isSupported = false;
            }
            else if (pending.Input.Kind == ControllerSignalKind.Fault)
            {
                var faultRule = assignment.Model.FaultRules!.Single(rule =>
                    StringComparer.Ordinal.Equals(rule.InputEndpointId, pending.Input.EndpointId)
                    && StringComparer.Ordinal.Equals(rule.FaultCode, pending.Input.Unit));
                if (faultRule.Behavior == ControllerModelFaultBehavior.HaltAndLatchOutputs)
                {
                    _haltedControllers.Add(controllerId);
                    diagnostics.Add(new ControllerDiagnostic(pending.Input.SimTimeNs, "controller_mcu_halted_latched_outputs",
                        $"Measured MCU halt '{faultRule.FaultCode}' leaves the last peripheral outputs latched and suppresses later firmware responses.", true,
                        controllerId));
                }
            }
            else
            {
                outputs.AddRange(ControllerModelEvaluator.Evaluate(assignment.Model, pending.Input,
                    assignment.Profile.Envelope.MaximumEventsPerAdvance));
            }
            _pending.Dequeue();
            var controllerOutputCount = outputs.Count(output => StringComparer.Ordinal.Equals(output.ControllerInstanceId, controllerId));
            var controllerOutputExceeded = controllerOutputCount > assignment.Profile.Envelope.MaximumEventsPerAdvance;
            var sessionOutputExceeded = outputs.Count > _maxEventsPerAdvance;
            if (controllerOutputExceeded || sessionOutputExceeded)
            {
                _unsupportedControllers.Add(controllerId);
                diagnostics.Add(new ControllerDiagnostic(pending.Input.SimTimeNs, "controller_model_output_budget_exceeded",
                    controllerOutputExceeded
                        ? $"Controller {controllerId} produced {controllerOutputCount} outputs in this advance; its characterized output budget is {assignment.Profile.Envelope.MaximumEventsPerAdvance}."
                        : $"Controller model session produced {outputs.Count} outputs in this advance; its runtime output budget is {_maxEventsPerAdvance}.", true));
                isSupported = false;
                outputs.Clear();
            }
            if (!isSupported)
            {
                _disabledUntilReset = true;
                while (_pending.TryPeek(out _, out var remainingPriority) && remainingPriority.Time <= simTimeNs) _pending.Dequeue();
                break;
            }
        }
        if (!isSupported) outputs.Clear();
        CurrentSimTimeNs = simTimeNs;
        return ValueTask.FromResult(new ControllerAdvanceResult(simTimeNs, outputs, diagnostics, isSupported));
    }

    private bool ProcessEchoPulse(ValidatedModelAssignment assignment, ControllerInputEvent input,
        ICollection<ControllerDiagnostic> diagnostics)
    {
        var controllerId = input.ControllerInstanceId;
        var rule = assignment.Model.EchoPulse!;
        var state = _echoPulseStates.GetValueOrDefault(controllerId) ?? new EchoPulseState();
        _echoPulseStates[controllerId] = state;
        if (input.Scalar == 1)
        {
            if (state.RisingEdgeTimeNs is not null)
            {
                diagnostics.Add(new ControllerDiagnostic(input.SimTimeNs, "echo_pulse_overlapping_rise",
                    "A second echo rising edge arrived before the prior pulse fell.", true));
                _unsupportedControllers.Add(controllerId);
                return false;
            }
            state.RisingEdgeTimeNs = input.SimTimeNs;
            return true;
        }
        if (state.RisingEdgeTimeNs is not { } rise || input.SimTimeNs <= rise)
        {
            diagnostics.Add(new ControllerDiagnostic(input.SimTimeNs, "echo_pulse_unpaired_fall",
                "An echo falling edge has no earlier rising edge.", true));
            _unsupportedControllers.Add(controllerId);
            return false;
        }
        var width = input.SimTimeNs - rise;
        state.RisingEdgeTimeNs = null;
        try
        {
            state.LastDistanceMm = ControllerModelEvaluator.EvaluateEchoPulse(rule, width);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new ControllerDiagnostic(input.SimTimeNs, "echo_pulse_outside_model_envelope", exception.Message, true));
            _unsupportedControllers.Add(controllerId);
            return false;
        }
        return true;
    }

    private bool ProcessUartBytes(ValidatedModelAssignment assignment, ControllerInputEvent input, long outputTimeNs,
        ICollection<ControllerOutputEvent> outputs, ICollection<ControllerDiagnostic> diagnostics)
    {
        var controllerId = input.ControllerInstanceId;
        var state = _uartLineStates.GetValueOrDefault(controllerId) ?? new UartLineState();
        _uartLineStates[controllerId] = state;
        foreach (var value in input.Bytes!)
        {
            if (state.DiscardUntilLineFeed)
            {
                if (value == (byte)'\n')
                {
                    state.DiscardUntilLineFeed = false;
                    if (assignment.Model.OverlengthUartResponseBytes is not { } responseBytes)
                    {
                        _unsupportedControllers.Add(controllerId);
                        diagnostics.Add(new ControllerDiagnostic(outputTimeNs, "uart_line_exceeds_model_limit",
                            $"Controller {controllerId} exceeded its characterized UART line limit of {assignment.Model.MaximumUartLineBytes} bytes.", true));
                        return false;
                    }
                    outputs.Add(new ControllerOutputEvent(controllerId, outputTimeNs, input.Sequence,
                        assignment.Model.UartCommands!.First().ResponseEndpointId, ControllerSignalKind.UartTxByte,
                        Bytes: responseBytes.ToArray(), Unit: "bytes"));
                    diagnostics.Add(new ControllerDiagnostic(outputTimeNs, "uart_line_rejected_overlength",
                        $"Controller {controllerId} rejected a UART line longer than {assignment.Model.MaximumUartLineBytes} bytes.", false));
                }
                continue;
            }
            if (value == (byte)'\r') continue;
            if (value == (byte)'\n')
            {
                var line = state.Bytes.ToArray();
                state.Bytes.Clear();
                if (line.Length > 0 && line[^1] == (byte)'\r') Array.Resize(ref line, line.Length - 1);
                if (!TryEvaluateUartLine(assignment, input, outputTimeNs, line, outputs, out var reason))
                {
                    _unsupportedControllers.Add(controllerId);
                    diagnostics.Add(new ControllerDiagnostic(outputTimeNs, "unsupported_uart_command",
                        $"Controller {controllerId} left its characterized UART command domain: {reason}", true));
                    return false;
                }
            }
            else if (state.Bytes.Count >= assignment.Model.MaximumUartLineBytes)
            {
                state.Bytes.Clear();
                state.DiscardUntilLineFeed = true;
            }
            else state.Bytes.Add(value);
        }
        return true;
    }

    private bool TryEvaluateUartLine(ValidatedModelAssignment assignment, ControllerInputEvent input, long outputTimeNs,
        byte[] line, ICollection<ControllerOutputEvent> outputs, out string reason)
    {
        var model = assignment.Model;
        var exactLine = model.ExactUartLines?.SingleOrDefault(rule => line.AsSpan().SequenceEqual(rule.RequestBytes));
        if (exactLine is not null)
        {
            outputs.Add(new ControllerOutputEvent(input.ControllerInstanceId, outputTimeNs, input.Sequence,
                model.UartCommands!.First().ResponseEndpointId, ControllerSignalKind.UartTxByte,
                Bytes: exactLine.ResponseBytes.ToArray(), Unit: "bytes"));
            reason = string.Empty;
            return true;
        }
        if (line.Any(value => value > 0x7f))
        {
            reason = "non-ASCII UART command";
            return false;
        }
        var tokens = Encoding.ASCII.GetString(line).Split(' ', StringSplitOptions.None);
        var rule = model.UartCommands!.SingleOrDefault(command => command.CommandToken == tokens[0]
            && command.OperandCount == tokens.Length - 1);
        if (rule is null)
        {
            reason = "command token or operand count was not characterized";
            return false;
        }
        if (rule.IsUnsupported)
        {
            reason = "command was explicitly marked outside the characterized model domain";
            return false;
        }
        if (rule.NumericResponseCommandToken is not null)
        {
            if (!_echoPulseStates.TryGetValue(input.ControllerInstanceId, out var sensorState)
                || sensorState.LastDistanceMm is not { } distanceMm)
            {
                if (model.EchoPulse?.InitialInvalidResponseBytes is not { } initialInvalidResponse)
                {
                    reason = "SENSOR? arrived before a valid measured echo pulse";
                    return false;
                }
                outputs.Add(new ControllerOutputEvent(input.ControllerInstanceId, outputTimeNs, input.Sequence,
                    rule.ResponseEndpointId, ControllerSignalKind.UartTxByte, Bytes: initialInvalidResponse.ToArray(), Unit: "bytes"));
                reason = string.Empty;
                return true;
            }
            var pulse = model.EchoPulse!;
            var response = pulse.ResponsePrefix + distanceMm.ToString(CultureInfo.InvariantCulture) + pulse.ResponseSuffix;
            outputs.Add(new ControllerOutputEvent(input.ControllerInstanceId, outputTimeNs, input.Sequence,
                rule.ResponseEndpointId, ControllerSignalKind.UartTxByte, Bytes: Encoding.ASCII.GetBytes(response), Unit: "bytes"));
            reason = string.Empty;
            return true;
        }
        var operands = new int[rule.OperandCount];
        for (var index = 0; index < operands.Length; index++)
        {
            if (tokens[index + 1].Length == 0 || !int.TryParse(tokens[index + 1], NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture, out operands[index])
                || operands[index] < rule.MinimumOperand || operands[index] > rule.MaximumOperand)
            {
                reason = "operand syntax or range was not characterized";
                return false;
            }
        }
        foreach (var binding in rule.OperandBindings.OrderBy(binding => binding.OperandIndex))
        {
            var parsedInput = new ControllerInputEvent(input.ControllerInstanceId, outputTimeNs, input.Sequence,
                binding.InputEndpointId, ControllerSignalKind.AnalogSample, operands[binding.OperandIndex]);
            IReadOnlyList<ControllerOutputEvent> mapped;
            try
            {
                mapped = ControllerModelEvaluator.Evaluate(model, parsedInput,
                    assignment.Profile.Envelope.MaximumEventsPerAdvance);
            }
            catch (InvalidDataException)
            {
                reason = "parsed operand exceeded its fitted output rule domain";
                return false;
            }
            foreach (var output in mapped) outputs.Add(output with { SimTimeNs = outputTimeNs, Sequence = input.Sequence });
        }
        outputs.Add(new ControllerOutputEvent(input.ControllerInstanceId, outputTimeNs, input.Sequence,
            rule.ResponseEndpointId, ControllerSignalKind.UartTxByte, Bytes: rule.ResponseBytes!.ToArray(), Unit: "bytes"));
        reason = string.Empty;
        return true;
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _pending.Clear();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(DeterministicModelBackend));
    }

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static void ValidateEnvelope(ControllerInputEnvelope envelope)
    {
        if (envelope.MinimumSimTimeNs < 0 || envelope.MaximumSimTimeNs < envelope.MinimumSimTimeNs
            || envelope.MaximumEventsPerAdvance is <= 0 or > 65536
            || envelope.MaximumUartBytesPerSecond is < 0 or > 10_000_000
            || envelope.SupportedEndpoints.Count == 0
            || envelope.SupportedEndpoints.Any(string.IsNullOrWhiteSpace)
            || envelope.SupportedEndpoints.Distinct(StringComparer.Ordinal).Count() != envelope.SupportedEndpoints.Count
            || envelope.SupportedSignals.Count == 0
            || envelope.SupportedSignals.Any(signal => !Enum.IsDefined(signal))
            || envelope.SupportedSignals.Distinct().Count() != envelope.SupportedSignals.Count
            || (envelope.SupportedSignals.Contains(ControllerSignalKind.UartRxByte) && envelope.MaximumUartBytesPerSecond == 0))
            throw new InvalidDataException("Controller profile input envelope is invalid or internally inconsistent.");
    }
}
