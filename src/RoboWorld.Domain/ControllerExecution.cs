namespace RoboWorld.Domain;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Execution selection is explicit and is fixed for one simulation run.</summary>
public enum ControllerExecutionMode
{
    ExactFirmware,
    ValidatedModel,
    Automatic
}

public enum ControllerBackendKind
{
    Renode,
    DeterministicModel
}

public enum ControllerSignalKind
{
    UartRxByte,
    GpioLevel,
    PwmCapture,
    AnalogSample,
    Reset,
    Fault,
    UartTxByte
}

/// <summary>An ordered, simulation-time-stamped input to one controller instance.</summary>
public sealed record ControllerInputEvent(string ControllerInstanceId, long SimTimeNs, ulong Sequence, string EndpointId, ControllerSignalKind Kind,
    double Scalar = 0, byte[]? Bytes = null, string? Unit = null);

/// <summary>An observable output produced by a controller backend at a defined simulation time.</summary>
public sealed record ControllerOutputEvent(string ControllerInstanceId, long SimTimeNs, ulong Sequence, string EndpointId, ControllerSignalKind Kind,
    double Scalar = 0, byte[]? Bytes = null, string? Unit = null);

public sealed record ControllerDiagnostic(long SimTimeNs, string Code, string Message, bool IsFault, string? ControllerInstanceId = null);

/// <summary>Unsupported input or budget faults withhold outputs for that advance; the caller must stop/restart.</summary>
public sealed record ControllerAdvanceResult(long SimTimeNs, IReadOnlyList<ControllerOutputEvent> Outputs,
    IReadOnlyList<ControllerDiagnostic> Diagnostics, bool IsSupported);

/// <summary>
/// Engine-neutral simulation-session seam for one or more independent controller instances. The session owns one
/// authoritative time frontier; advancing a two-MCU session can never run its shared Renode clock twice. State and
/// I/O remain isolated by ControllerInstanceId. Implementations do not own physical devices.
/// </summary>
public interface IControllerBackend : IAsyncDisposable
{
    ControllerBackendKind Kind { get; }
    IReadOnlyList<string> ControllerInstanceIds { get; }
    long CurrentSimTimeNs { get; }
    ValueTask ResetAsync(long simTimeNs, CancellationToken cancellationToken = default);
    void ScheduleInput(ControllerInputEvent input);
    /// <summary>Advance the shared session once; every controller must reach the same requested boundary.</summary>
    ValueTask<ControllerAdvanceResult> AdvanceToAsync(long simTimeNs, CancellationToken cancellationToken = default);
}

/// <summary>Identity of the complete hardware/firmware contract a characterized profile is valid for.</summary>
public sealed record ControllerProfileIdentity(
    int SchemaVersion,
    string FirmwareSha256,
    string ControllerType,
    string BoardContractId,
    string BoardContractVersion,
    string RobotGraphSha256,
    string ComponentDefinitionsSha256,
    string PeripheralMapSha256,
    string InitialStateSha256,
    string CharacterizationSchemaVersion,
    string GeneratorVersion,
    string ModelSchemaVersion,
    string EvaluatorVersion,
    string InputEnvelopeSha256);

/// <summary>Only inputs inside this declared envelope may use a validated model.</summary>
public sealed record ControllerInputEnvelope(IReadOnlyList<string> SupportedEndpoints,
    IReadOnlyList<ControllerSignalKind> SupportedSignals, long MinimumSimTimeNs, long MaximumSimTimeNs,
    int MaximumEventsPerAdvance, int MaximumUartBytesPerSecond);

/// <summary>Persistable identification and validity metadata; ephemeral backend state is never stored here.</summary>
public sealed record DeterministicControllerProfile(int SchemaVersion, string ProfileId,
    ControllerProfileIdentity Identity, ControllerInputEnvelope Envelope, string ModelDocumentSha256,
    string TraceCorpusSha256, string HeldOutSequencesSha256, int HeldOutSequenceCount,
    IReadOnlyDictionary<string, double> ValidationMetrics, IReadOnlyList<string> SupportedBehaviors,
    IReadOnlyList<string> UnsupportedBehaviors, string FidelityLabel);

public sealed record ControllerProfileValidation(bool IsValid, IReadOnlyList<string> MismatchReasons);

public sealed record ControllerBackendResolution(ControllerBackendKind Kind, IControllerBackend? ModelBackend, string Reason);

public static class ControllerBackendSelector
{
    /// <summary>Preflight backend selection. A model factory must fully verify profile identity and artifact hashes before returning.</summary>
    public static ControllerBackendResolution Resolve(ControllerExecutionMode requestedMode, Func<IControllerBackend> createValidatedModel)
    {
        ArgumentNullException.ThrowIfNull(createValidatedModel);
        if (requestedMode == ControllerExecutionMode.ExactFirmware)
            return new ControllerBackendResolution(ControllerBackendKind.Renode, null, "Exact firmware execution was explicitly selected.");
        if (requestedMode is not (ControllerExecutionMode.ValidatedModel or ControllerExecutionMode.Automatic))
            throw new ArgumentOutOfRangeException(nameof(requestedMode));
        try
        {
            var backend = createValidatedModel() ?? throw new InvalidDataException("No qualified controller model is available.");
            if (backend.Kind != ControllerBackendKind.DeterministicModel)
                throw new InvalidDataException("The validated model factory returned a non-model backend.");
            return new ControllerBackendResolution(ControllerBackendKind.DeterministicModel, backend, "A validated matching controller profile is available.");
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or FileNotFoundException)
        {
            if (requestedMode == ControllerExecutionMode.ValidatedModel)
                throw new InvalidDataException("Explicit fast-model selection failed preflight: " + exception.Message, exception);
            return new ControllerBackendResolution(ControllerBackendKind.Renode, null,
                "No usable validated profile; selected exact firmware before run: " + exception.Message);
        }
    }
}

public static class ControllerProfileHashes
{
    private static readonly JsonSerializerOptions CanonicalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Stable SHA-256 for versioned record artifacts; property declaration and list order are significant.</summary>
    public static string Compute<T>(T artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(artifact, CanonicalJson)));
    }

    public static bool Matches(string expectedSha256, string actualSha256) =>
        expectedSha256.Length == 64 && expectedSha256.All(Uri.IsHexDigit)
        && actualSha256.Length == 64 && actualSha256.All(Uri.IsHexDigit)
        && StringComparer.OrdinalIgnoreCase.Equals(expectedSha256, actualSha256);
}

public enum ControllerModelOperator { Affine, Constant, PassThrough, MagnitudeAffine, SignLevel }
public enum ControllerEchoNoPulseBehavior { Reject, RetainLastValidReading }
public enum ControllerModelFaultBehavior { HaltAndLatchOutputs }

/// <summary>Serializable allow-listed operation; no expressions, code, reflection, or plugin names are accepted.</summary>
public sealed record ControllerModelRule(string InputEndpointId, ControllerSignalKind InputKind,
    string OutputEndpointId, ControllerSignalKind OutputKind, ControllerModelOperator Operator,
    double Gain, double Offset, double MinimumInput, double MaximumInput, string Unit);

/// <summary>A bounded UART line command induced from characterization examples, with typed parsed operands.</summary>
public sealed record ControllerUartOperandBinding(int OperandIndex, string InputEndpointId);
/// <summary>One training-derived exact UART line exception for a known bounded malformed command.</summary>
public sealed record ControllerUartExactLineRule(byte[] RequestBytes, byte[] ResponseBytes);
/// <summary>A measured pulse-width mapping used by a bounded stateful controller model.</summary>
public sealed record ControllerEchoPulseRule(string InputEndpointId, double Gain, double Offset,
    long MinimumPulseWidthNs, long MaximumPulseWidthNs, string ResponseCommandToken, string ResponsePrefix, string ResponseSuffix,
    ControllerEchoNoPulseBehavior NoPulseBehavior = ControllerEchoNoPulseBehavior.Reject, byte[]? InitialInvalidResponseBytes = null);
public sealed record ControllerUartCommandRule(string CommandToken, int OperandCount, int MinimumOperand, int MaximumOperand, string ResponseEndpointId,
    IReadOnlyList<ControllerUartOperandBinding> OperandBindings, byte[]? ResponseBytes, bool IsUnsupported = false,
    string? NumericResponseCommandToken = null);
/// <summary>A measured external controller fault transition. Physical route faults remain device-model inputs.</summary>
public sealed record ControllerModelFaultRule(string InputEndpointId, string FaultCode, ControllerModelFaultBehavior Behavior);

public sealed record ControllerModelDocument(int SchemaVersion, string ModelId, int MaximumRules,
    IReadOnlyList<ControllerModelRule> Rules, int MaximumUartLineBytes = 0,
    IReadOnlyList<ControllerUartCommandRule>? UartCommands = null, string? UartInputEndpointId = null,
    byte[]? OverlengthUartResponseBytes = null, ControllerEchoPulseRule? EchoPulse = null,
    IReadOnlyList<ControllerUartExactLineRule>? ExactUartLines = null, byte[]? InitialUartOutputBytes = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<ControllerModelFaultRule>? FaultRules = null);

/// <summary>Bounded deterministic evaluator for validated declarative controller models.</summary>
public static class ControllerModelEvaluator
{
    /// <summary>Evaluate a measured, bounded echo-pulse width as the firmware-visible integer millimetre value.</summary>
    public static long EvaluateEchoPulse(ControllerEchoPulseRule pulse, long pulseWidthNs)
    {
        ArgumentNullException.ThrowIfNull(pulse);
        if (!double.IsFinite(pulse.Gain) || pulse.Gain <= 0 || !double.IsFinite(pulse.Offset)
            || pulse.MinimumPulseWidthNs <= 0 || pulse.MaximumPulseWidthNs < pulse.MinimumPulseWidthNs
            || pulseWidthNs < pulse.MinimumPulseWidthNs || pulseWidthNs > pulse.MaximumPulseWidthNs)
            throw new InvalidDataException("Echo pulse width or fitted mapping is outside its declared model envelope.");
        var distanceMm = (pulse.Gain * pulseWidthNs) + pulse.Offset;
        if (!double.IsFinite(distanceMm) || distanceMm < 0 || distanceMm > int.MaxValue)
            throw new InvalidDataException("Echo pulse mapping produced a distance outside its supported integer range.");
        return checked((long)Math.Floor(distanceMm));
    }

    public static IReadOnlyList<ControllerOutputEvent> Evaluate(ControllerModelDocument model,
        ControllerInputEvent input, int maximumOperations)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(input);
        if (model.SchemaVersion != 1 || string.IsNullOrWhiteSpace(model.ModelId)) throw new InvalidDataException("Unsupported or unnamed controller model.");
        if (model.Rules is null) throw new InvalidDataException("Controller model has no rule collection.");
        if (maximumOperations is <= 0 or > 4096 || model.MaximumRules is <= 0 or > 4096
            || model.Rules.Count + (model.UartCommands?.Count ?? 0) + (model.EchoPulse is null ? 0 : 1)
                + (model.FaultRules?.Count ?? 0) + (model.ExactUartLines?.Count ?? 0) > Math.Min(maximumOperations, model.MaximumRules))
            throw new InvalidDataException("Controller model exceeds its operation budget.");
        if (model.FaultRules is { } faultRules && (faultRules.Any(rule => rule is null
                || string.IsNullOrWhiteSpace(rule.InputEndpointId) || string.IsNullOrWhiteSpace(rule.FaultCode)
                || !Enum.IsDefined(rule.Behavior))
            || faultRules.Select(rule => (rule.InputEndpointId, rule.FaultCode)).Distinct().Count() != faultRules.Count))
            throw new InvalidDataException("Controller model contains an invalid or duplicate measured fault transition.");
        if (input.SimTimeNs < 0 || !double.IsFinite(input.Scalar)) throw new InvalidDataException("Controller input has invalid time or scalar value.");
        var outputs = new List<ControllerOutputEvent>();
        var matchedRules = 0;
        foreach (var rule in model.Rules)
        {
            if (!StringComparer.Ordinal.Equals(rule.InputEndpointId, input.EndpointId) || rule.InputKind != input.Kind) continue;
            matchedRules++;
            if (!Enum.IsDefined(rule.Operator) || !double.IsFinite(rule.Gain) || !double.IsFinite(rule.Offset)
                || !double.IsFinite(rule.MinimumInput) || !double.IsFinite(rule.MaximumInput) || rule.MinimumInput > rule.MaximumInput
                || string.IsNullOrWhiteSpace(rule.OutputEndpointId) || string.IsNullOrWhiteSpace(rule.Unit))
                throw new InvalidDataException("Controller model contains an invalid or unsupported rule.");
            if (input.Scalar < rule.MinimumInput || input.Scalar > rule.MaximumInput)
                throw new InvalidDataException($"Input {input.EndpointId} is outside the certified model range.");
            double? output = rule.Operator switch
            {
                ControllerModelOperator.Affine => (input.Scalar * rule.Gain) + rule.Offset,
                ControllerModelOperator.Constant => rule.Offset,
                ControllerModelOperator.PassThrough => input.Scalar,
                ControllerModelOperator.MagnitudeAffine => (Math.Abs(input.Scalar) * rule.Gain) + rule.Offset,
                ControllerModelOperator.SignLevel => input.Scalar < 0 ? 0 : 1,
                _ => throw new InvalidDataException("Controller model contains an unsupported operation.")
            };
            if (!double.IsFinite(output.Value)) throw new InvalidDataException("Controller model produced a non-finite output.");
            outputs.Add(new ControllerOutputEvent(input.ControllerInstanceId, input.SimTimeNs, input.Sequence,
                rule.OutputEndpointId, rule.OutputKind, output.Value, Unit: rule.Unit));
        }
        if (matchedRules == 0) throw new InvalidDataException($"Controller model does not support {input.Kind} at {input.EndpointId}.");
        if (outputs.Count > maximumOperations) throw new InvalidDataException("Controller model output fan-out exceeds the operation budget.");
        return outputs;
    }

    public static void ValidateDocument(ControllerModelDocument model, int maximumOperations)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.SchemaVersion != 1 || string.IsNullOrWhiteSpace(model.ModelId)) throw new InvalidDataException("Unsupported or unnamed controller model.");
        if (model.Rules is null) throw new InvalidDataException("Controller model has no rule collection.");
        foreach (var rule in model.Rules)
        {
            if (rule is null || string.IsNullOrWhiteSpace(rule.InputEndpointId) || string.IsNullOrWhiteSpace(rule.OutputEndpointId)
                || !Enum.IsDefined(rule.InputKind) || !Enum.IsDefined(rule.OutputKind) || !Enum.IsDefined(rule.Operator)
                || !double.IsFinite(rule.Gain) || !double.IsFinite(rule.Offset) || !double.IsFinite(rule.MinimumInput)
                || !double.IsFinite(rule.MaximumInput) || rule.MinimumInput > rule.MaximumInput || string.IsNullOrWhiteSpace(rule.Unit))
                throw new InvalidDataException("Controller model contains an invalid or unsupported rule.");
        }
        if (maximumOperations is <= 0 or > 4096 || model.MaximumRules is <= 0 or > 4096
            || model.Rules.Count + (model.UartCommands?.Count ?? 0) + (model.EchoPulse is null ? 0 : 1)
                + (model.FaultRules?.Count ?? 0) + (model.ExactUartLines?.Count ?? 0) > Math.Min(maximumOperations, model.MaximumRules))
            throw new InvalidDataException("Controller model exceeds its operation budget.");
        if (model.UartCommands is null)
        {
            if (model.MaximumUartLineBytes != 0 || model.UartInputEndpointId is not null || model.OverlengthUartResponseBytes is not null)
                throw new InvalidDataException("UART parser settings require a declared command model.");
        }
        if (model.EchoPulse is { } pulse)
        {
            if (model.UartCommands is null || string.IsNullOrWhiteSpace(pulse.InputEndpointId)
                || !double.IsFinite(pulse.Gain) || pulse.Gain <= 0 || !double.IsFinite(pulse.Offset)
                || pulse.MinimumPulseWidthNs <= 0 || pulse.MaximumPulseWidthNs < pulse.MinimumPulseWidthNs
                || string.IsNullOrWhiteSpace(pulse.ResponseCommandToken)
                || !Enum.IsDefined(pulse.NoPulseBehavior)
                || string.IsNullOrEmpty(pulse.ResponsePrefix) || string.IsNullOrEmpty(pulse.ResponseSuffix)
                || pulse.ResponsePrefix.Length > 128 || pulse.ResponseSuffix.Length > 128
                || pulse.ResponsePrefix.Any(character => character is '\0' or > '\x7f')
                || pulse.ResponseSuffix.Any(character => character is '\0' or > '\x7f') || !pulse.ResponseSuffix.EndsWith('\n')
                || !model.UartCommands.Any(command => command.CommandToken == pulse.ResponseCommandToken
                    && command.NumericResponseCommandToken == pulse.ResponseCommandToken))
                throw new InvalidDataException("Echo pulse model is invalid or has no matching numeric UART response command.");
            if (pulse.NoPulseBehavior == ControllerEchoNoPulseBehavior.RetainLastValidReading && pulse.InitialInvalidResponseBytes is null)
                throw new InvalidDataException("Retained no-pulse behavior requires a measured initial invalid-reading response.");
            if (pulse.InitialInvalidResponseBytes is { } invalidResponse
                && (invalidResponse.Length is 0 or > 4096 || invalidResponse[^1] != (byte)'\n'
                    || invalidResponse.Any(value => value == 0 || value > 0x7f)))
                throw new InvalidDataException("Initial invalid sensor response must be bounded ASCII ending in LF.");
        }
        else if (model.UartCommands?.Any(command => command.NumericResponseCommandToken is not null) == true)
            throw new InvalidDataException("Numeric UART responses require a measured echo pulse model.");
        if (model.ExactUartLines is { } exactLines)
        {
            if (model.UartCommands is null || model.MaximumUartLineBytes <= 0
                || exactLines.Any(rule => rule is null || rule.RequestBytes is null || rule.RequestBytes.Length is 0 or > 512
                    || rule.RequestBytes.Length > model.MaximumUartLineBytes || rule.ResponseBytes is null || rule.ResponseBytes.Length is 0 or > 4096
                    || rule.ResponseBytes[^1] != (byte)'\n' || rule.RequestBytes.Any(value => value is 0 or > 0x7f)
                    || rule.ResponseBytes.Any(value => value is 0 or > 0x7f))
                || exactLines.Select(rule => Convert.ToHexString(rule.RequestBytes)).Distinct(StringComparer.Ordinal).Count() != exactLines.Count)
                throw new InvalidDataException("Exact UART line rules must contain unique bounded ASCII request/response pairs.");
        }
        if (model.UartCommands is null) return;
        if (model.MaximumUartLineBytes is <= 0 or > 512 || string.IsNullOrWhiteSpace(model.UartInputEndpointId)
            || model.UartCommands.Count == 0)
            throw new InvalidDataException("UART command model has invalid bounds or no input endpoint.");
        if (model.UartCommands.Any(command => command is null)
            || model.UartCommands.Select(command => command.ResponseEndpointId).Distinct(StringComparer.Ordinal).Count() != 1)
            throw new InvalidDataException("One UART transducer must route every response through one output endpoint.");
        var commandKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in model.UartCommands)
        {
            if (command is null || string.IsNullOrWhiteSpace(command.CommandToken) || command.CommandToken.Length > 32
                || command.CommandToken.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '?'))
                || command.OperandCount is < 0 or > 16 || command.MinimumOperand > command.MaximumOperand
                || string.IsNullOrWhiteSpace(command.ResponseEndpointId)
                || command.OperandBindings is null
                || (command.NumericResponseCommandToken is not null
                    ? command.ResponseBytes is not null || command.NumericResponseCommandToken != command.CommandToken || command.IsUnsupported
                    : command.IsUnsupported ? command.ResponseBytes is not null : command.ResponseBytes is null or { Length: 0 or > 4096 })
                || (command.ResponseBytes is { Length: > 0 } responseBytes && (responseBytes.Length > 4096 || responseBytes[^1] != (byte)'\n'))
                || !commandKeys.Add(command.CommandToken + "\u001f" + command.OperandCount.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                throw new InvalidDataException("UART command rule is malformed, duplicated, or exceeds its bounds.");
            if (command.OperandBindings.Any(binding => binding is null)
                || command.OperandBindings.Count > command.OperandCount
                || command.OperandBindings.Select(binding => binding.OperandIndex).Distinct().Count() != command.OperandBindings.Count
                || command.OperandBindings.Any(binding => binding.OperandIndex < 0 || binding.OperandIndex >= command.OperandCount
                    || string.IsNullOrWhiteSpace(binding.InputEndpointId)
                    || !model.Rules.Any(rule => StringComparer.Ordinal.Equals(rule.InputEndpointId, binding.InputEndpointId)
                        && rule.InputKind == ControllerSignalKind.AnalogSample)))
                throw new InvalidDataException("UART operand bindings must map every operand to a supported numeric model input.");
            if (command.ResponseBytes?.Any(value => value == 0 || value > 0x7f) == true)
                throw new InvalidDataException("UART response templates must contain bounded ASCII bytes only.");
        }
        if (model.OverlengthUartResponseBytes is { } overlengthResponse
            && (overlengthResponse.Length is 0 or > 4096 || overlengthResponse[^1] != (byte)'\n'
                || overlengthResponse.Any(value => value == 0 || value > 0x7f)))
            throw new InvalidDataException("Overlength UART response template must contain bounded ASCII bytes ending in LF.");
        if (model.InitialUartOutputBytes is { } initialOutput
            && (initialOutput.Length is 0 or > 4096 || initialOutput[^1] != (byte)'\n'
                || initialOutput.Any(value => value == 0 || value > 0x7f)))
            throw new InvalidDataException("Initial UART output must contain bounded ASCII bytes ending in LF.");
    }
}

public static class ControllerProfileValidator
{
    public static ControllerProfileValidation Validate(ControllerProfileIdentity expected, ControllerProfileIdentity actual)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);
        var mismatches = new List<string>();
        CheckDigest(expected.FirmwareSha256, actual.FirmwareSha256, "firmware_sha256", mismatches);
        CheckDigest(expected.RobotGraphSha256, actual.RobotGraphSha256, "robot_graph_sha256", mismatches);
        CheckDigest(expected.ComponentDefinitionsSha256, actual.ComponentDefinitionsSha256, "component_definitions_sha256", mismatches);
        CheckDigest(expected.PeripheralMapSha256, actual.PeripheralMapSha256, "peripheral_map_sha256", mismatches);
        CheckDigest(expected.InitialStateSha256, actual.InitialStateSha256, "initial_state_sha256", mismatches);
        CheckDigest(expected.InputEnvelopeSha256, actual.InputEnvelopeSha256, "input_envelope_sha256", mismatches);
        if (string.IsNullOrWhiteSpace(expected.ControllerType) || string.IsNullOrWhiteSpace(actual.ControllerType)) mismatches.Add("controller_type_missing");
        if (string.IsNullOrWhiteSpace(expected.BoardContractId) || string.IsNullOrWhiteSpace(actual.BoardContractId)
            || string.IsNullOrWhiteSpace(expected.BoardContractVersion) || string.IsNullOrWhiteSpace(actual.BoardContractVersion)) mismatches.Add("board_contract_missing");
        if (string.IsNullOrWhiteSpace(expected.CharacterizationSchemaVersion) || string.IsNullOrWhiteSpace(actual.CharacterizationSchemaVersion)) mismatches.Add("characterization_schema_version_missing");
        if (string.IsNullOrWhiteSpace(expected.GeneratorVersion) || string.IsNullOrWhiteSpace(actual.GeneratorVersion)) mismatches.Add("generator_version_missing");
        if (string.IsNullOrWhiteSpace(expected.ModelSchemaVersion) || string.IsNullOrWhiteSpace(actual.ModelSchemaVersion)) mismatches.Add("model_schema_version_missing");
        if (string.IsNullOrWhiteSpace(expected.EvaluatorVersion) || string.IsNullOrWhiteSpace(actual.EvaluatorVersion)) mismatches.Add("evaluator_version_missing");
        if (expected.SchemaVersion != actual.SchemaVersion) mismatches.Add("schema_version");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.FirmwareSha256, actual.FirmwareSha256)) mismatches.Add("firmware_sha256");
        if (expected.ControllerType != actual.ControllerType) mismatches.Add("controller_type");
        if (expected.BoardContractId != actual.BoardContractId || expected.BoardContractVersion != actual.BoardContractVersion) mismatches.Add("board_contract");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.RobotGraphSha256, actual.RobotGraphSha256)) mismatches.Add("robot_graph_sha256");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.ComponentDefinitionsSha256, actual.ComponentDefinitionsSha256)) mismatches.Add("component_definitions_sha256");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.PeripheralMapSha256, actual.PeripheralMapSha256)) mismatches.Add("peripheral_map_sha256");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.InitialStateSha256, actual.InitialStateSha256)) mismatches.Add("initial_state_sha256");
        if (expected.CharacterizationSchemaVersion != actual.CharacterizationSchemaVersion) mismatches.Add("characterization_schema_version");
        if (expected.GeneratorVersion != actual.GeneratorVersion) mismatches.Add("generator_version");
        if (expected.ModelSchemaVersion != actual.ModelSchemaVersion) mismatches.Add("model_schema_version");
        if (expected.EvaluatorVersion != actual.EvaluatorVersion) mismatches.Add("evaluator_version");
        if (!StringComparer.OrdinalIgnoreCase.Equals(expected.InputEnvelopeSha256, actual.InputEnvelopeSha256)) mismatches.Add("input_envelope_sha256");
        return new ControllerProfileValidation(mismatches.Count == 0, mismatches.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static void CheckDigest(string expected, string actual, string field, ICollection<string> mismatches)
    {
        static bool IsSha256(string value) => value.Length == 64 && value.All(character => Uri.IsHexDigit(character));
        if (!IsSha256(expected) || !IsSha256(actual)) mismatches.Add(field + "_invalid");
        else if (!StringComparer.OrdinalIgnoreCase.Equals(expected, actual)) mismatches.Add(field);
    }
}
