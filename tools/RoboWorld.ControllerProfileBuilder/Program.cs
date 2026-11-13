using System.Security.Cryptography;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RoboWorld.Domain;

if (args.Length == 5 && args[0] == "--replay-actuation-trace")
    return await ReplayUnqualifiedActuationTraceAsync(args[1], args[2], args[3], args[4]);

if (args.Length is not (6 or 8 or 10 or 11 or 12 or 13 or 16))
{
    Console.Error.WriteLine("Usage: ControllerProfileBuilder <motor-campaign-dir> <robot.json> <components-dir> <firmware.elf> <RenodeMotorEventHost.cs> <output-dir> [optional campaign/fit pairs]; the 10–13 argument echo-pulse form accepts echo pulse and peripheral pairs, then optional UART line-boundary, no-echo, and controller-fault campaigns. Append <loaded-trajectory-summary.json> <baseline-candidate-model.json> <trajectory-source-campaign-dir> to evaluate a bounded VALIDATED profile from the complete measured evidence.");
    return 2;
}

var campaignDirectory = Path.GetFullPath(args[0]);
var robotPath = Path.GetFullPath(args[1]);
var componentDirectory = Path.GetFullPath(args[2]);
var firmwarePath = Path.GetFullPath(args[3]);
var renodeHostPath = Path.GetFullPath(args[4]);
var outputDirectory = Path.GetFullPath(args[5]);
var peripheralOnlyPair = args.Length == 8 && Path.GetFileName(args[7]).Contains("peripheral-fit", StringComparison.OrdinalIgnoreCase);
var echoPulseAndPeripheralPair = args.Length is 10 or 11 or 12 or 13 or 16 && Path.GetFileName(args[7]).Contains("echo-pulse-fit", StringComparison.OrdinalIgnoreCase);
var sensorCampaignDirectory = args.Length >= 8 && !peripheralOnlyPair && !echoPulseAndPeripheralPair ? Path.GetFullPath(args[6]) : null;
var sensorFitPath = args.Length >= 8 && !peripheralOnlyPair && !echoPulseAndPeripheralPair ? Path.GetFullPath(args[7]) : null;
var echoPulseCampaignDirectory = echoPulseAndPeripheralPair ? Path.GetFullPath(args[6]) : null;
var echoPulseFitPath = echoPulseAndPeripheralPair ? Path.GetFullPath(args[7]) : null;
var peripheralCampaignDirectory = peripheralOnlyPair ? Path.GetFullPath(args[6]) : args.Length is 10 or 11 or 12 or 13 or 16 ? Path.GetFullPath(args[8]) : null;
var peripheralFitPath = peripheralOnlyPair ? Path.GetFullPath(args[7]) : args.Length is 10 or 11 or 12 or 13 or 16 ? Path.GetFullPath(args[9]) : null;
var lineBoundaryCampaignDirectory = args.Length >= 11 ? Path.GetFullPath(args[10]) : null;
var noEchoCampaignDirectory = args.Length >= 12 ? Path.GetFullPath(args[11]) : null;
var controllerFaultCampaignDirectory = args.Length >= 13 ? Path.GetFullPath(args[12]) : null;
var loadedTrajectorySummaryPath = args.Length == 16 ? Path.GetFullPath(args[13]) : null;
var loadedTrajectoryBaselineModelPath = args.Length == 16 ? Path.GetFullPath(args[14]) : null;
var loadedTrajectorySourceCampaignDirectory = args.Length == 16 ? Path.GetFullPath(args[15]) : null;
var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

using var evaluation = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(campaignDirectory, "evaluation-UNQUALIFIED.json")));
using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(campaignDirectory, "sequence-manifest.json")));
using var robotDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(robotPath));
using var sensorFit = sensorFitPath is null ? null : JsonDocument.Parse(await File.ReadAllBytesAsync(sensorFitPath));
using var echoPulseFit = echoPulseFitPath is null ? null : JsonDocument.Parse(await File.ReadAllBytesAsync(echoPulseFitPath));
using var peripheralFit = peripheralFitPath is null ? null : JsonDocument.Parse(await File.ReadAllBytesAsync(peripheralFitPath));
if (noEchoCampaignDirectory is not null && (!echoPulseAndPeripheralPair || echoPulseFit is null || peripheralFit is null))
    throw new InvalidDataException("The no-echo state campaign can only extend the measured motor/UART plus echo-pulse candidate.");
var evaluationRoot = evaluation.RootElement;
if (evaluationRoot.GetProperty("status").GetString() != "UNQUALIFIED") throw new InvalidDataException("Profile builder requires an explicitly unqualified evaluator artifact.");
var robot = robotDocument.RootElement;
var controllers = robot.GetProperty("controllers").EnumerateArray().ToArray();
if (controllers.Length != 1) throw new InvalidDataException("This profile builder supports exactly one controller instance.");
var controller = controllers[0];

var modelRules = new List<ControllerModelRule>();
var uartCommands = new List<ControllerUartCommandRule>();
byte[]? overlengthUartResponseBytes = null;
var heldoutUartResponseChecks = (Sequences: 0, Pings: 0, Motors: 0, Moves: 0, Overlength: 0);
var sensorReferenceObservations = new List<SensorReferenceObservation>();
var echoPulseObservations = new List<EchoPulseObservation>();
ControllerEchoPulseRule? echoPulseModel = null;
var echoResponsePrefix = string.Empty;
var echoResponseSuffix = string.Empty;
var heldoutEchoResponseChecks = (Sequences: 0, Replies: 0, GrammarExact: 0);
var heldoutEchoPulseErrors = new List<double>();
var heldoutEchoPulseRawFitErrors = new List<double>();
MixedSensorEnvelopeReview? mixedSensorEnvelopeReview = null;
var exactUartLineRules = new List<ControllerUartExactLineRule>();
var heldoutLineBoundaryChecks = (Sequences: 0, ExactResponses: 0);
var heldoutUartGrammarChecks = (Sequences: 0, ExactResponses: 0);
var heldoutUartTimingErrorsNs = new List<long>();
var heldoutUartTimingTickBoundsNs = new List<long>();
byte[]? statusResponseBytes = null;
byte[]? initialUartOutputBytes = null;
var initialUartBootValidation = (TrainingSequences: 0, HeldoutSequences: 0, Response: (string?)null);
var noEchoValidation = (InitialInvalidResponse: (byte[]?)null, TrainingSequences: 0, HeldoutSequences: 0, HeldoutStaleReplies: 0);
var faultRules = new List<ControllerModelFaultRule>();
var heldoutMcuHaltSequences = 0;
var faultCampaignTrainingSequences = 0;
string? faultManifestSha256 = null;
if (controllerFaultCampaignDirectory is not null)
{
    if (!echoPulseAndPeripheralPair || echoPulseFit is null || peripheralFit is null || noEchoCampaignDirectory is null)
        throw new InvalidDataException("The controller-fault campaign can only extend the measured motor/UART, echo-pulse, and no-echo candidate.");
    using var faultValidation = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(controllerFaultCampaignDirectory, "campaign-validation.json")));
    using var faultManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(controllerFaultCampaignDirectory, "sequence-manifest.json")));
    var faultRoot = faultValidation.RootElement;
    if (faultRoot.GetProperty("status").GetString() != "PASS_MEASURED_REFERENCE_ONLY"
        || faultRoot.GetProperty("model_qualification").GetString() != "UNQUALIFIED"
        || faultRoot.GetProperty("sequence_count").GetInt32() != 8
        || faultRoot.GetProperty("fault_types").GetProperty("mcu-halt").GetInt32() != 4
        || faultRoot.GetProperty("heldout_sequences_per_fault").GetProperty("mcu-halt").GetInt32() != 2)
        throw new InvalidDataException("Controller-fault campaign does not contain the accepted exact-firmware held-out MCU-halt evidence.");
    var faultRows = faultManifest.RootElement.EnumerateArray().ToArray();
    var firmwareHash = HashFile(firmwarePath);
    if (faultRows.Length != 8 || faultRows.Any(row => !ControllerProfileHashes.Matches(row.GetProperty("firmware_sha256").GetString()!, firmwareHash)))
        throw new InvalidDataException("Controller-fault manifest has incomplete sequences or a different reference firmware.");
    foreach (var row in faultRows.Where(row => row.GetProperty("fault_type").GetString() == "mcu-halt"))
    {
        var tracePath = Path.Combine(controllerFaultCampaignDirectory, row.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException("Controller-fault trace hash does not match its sequence manifest.");
        var sequenceId = row.GetProperty("sequence_id").GetString()!;
        var measured = faultRoot.GetProperty("sequences").EnumerateArray()
            .Single(item => item.GetProperty("sequence_id").GetString() == sequenceId);
        if (!measured.GetProperty("post_halt_ping_no_reply").GetBoolean()
            || measured.GetProperty("post_halt_pwm_observations").GetInt32() < 120
            || measured.GetProperty("godot_post_halt_wheel_observations").GetInt32() != 21
            || measured.GetProperty("latched_pwm").EnumerateArray().Any(value => Math.Abs(value.GetDouble() - 0.5) > 1e-9))
            throw new InvalidDataException($"Exact MCU-halt sequence {sequenceId} does not prove UART silence, retained PWM, and live physics.");
        if (row.GetProperty("split").GetString() == "training") faultCampaignTrainingSequences++;
        else if (row.GetProperty("split").GetString() == "heldout") heldoutMcuHaltSequences++;
        else throw new InvalidDataException("Controller-fault sequence uses an unsupported corpus split.");
    }
    if (faultRows.Count(row => row.GetProperty("fault_type").GetString() == "mcu-halt") != 4
        || faultCampaignTrainingSequences != 2 || heldoutMcuHaltSequences != 2)
        throw new InvalidDataException("MCU-halt induction requires two whole training and two whole held-out sequences.");
    faultManifestSha256 = HashFile(Path.Combine(controllerFaultCampaignDirectory, "sequence-manifest.json"));
    faultRules.Add(new ControllerModelFaultRule("controller.fault", "MCU_HALTED", ControllerModelFaultBehavior.HaltAndLatchOutputs));
}
if (peripheralFit is not null)
{
    if (sensorFit is not null) throw new InvalidDataException("The raycast-range sensor candidate cannot be combined with the raw-UART peripheral candidate; a pulse-derived sensor model is required.");
    var peripheralRoot = peripheralFit.RootElement;
    if (peripheralRoot.GetProperty("status").GetString() != "RAW_PERIPHERAL_FIT_PASS_PROFILE_UNQUALIFIED")
        throw new InvalidDataException("Raw peripheral candidate fit did not pass its declared motor gates.");
    var peripheralManifestPath = Path.Combine(peripheralCampaignDirectory!, "sequence-manifest.json");
    if (!ControllerProfileHashes.Matches(peripheralRoot.GetProperty("campaign_manifest_sha256").GetString()!, HashFile(peripheralManifestPath)))
        throw new InvalidDataException("Raw peripheral fit does not match the supplied campaign manifest.");
    using var peripheralManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(peripheralManifestPath));
    var peripheralRows = peripheralManifest.RootElement.EnumerateArray().ToArray();
    var firmwareHash = HashFile(firmwarePath);
    if (!ControllerProfileHashes.Matches("5D5973A2E119167EF3C2EF18E8929E8AD9735AD65A63B639179256547575B6E9", firmwareHash))
        throw new InvalidDataException("Raw UART line-limit rule is source-audited only for the reference drive firmware SHA-256.");
    if (peripheralRows.Any(row => !ControllerProfileHashes.Matches(row.GetProperty("firmware_sha256").GetString()!, firmwareHash)))
        throw new InvalidDataException("Raw peripheral campaign firmware does not match the selected controller image.");
    var expectedTraining = peripheralRows.Where(row => row.GetProperty("split").GetString() == "training")
        .Select(row => row.GetProperty("sequence_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
    var expectedHeldout = peripheralRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .Select(row => row.GetProperty("sequence_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
    if (!expectedTraining.SequenceEqual(peripheralRoot.GetProperty("training_sequences").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal))
        || !expectedHeldout.SequenceEqual(peripheralRoot.GetProperty("heldout_sequences").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal)))
        throw new InvalidDataException("Raw peripheral fit training/held-out sequence IDs differ from its manifest.");
    foreach (var side in new[] { "left", "right" })
    {
        var directionFit = peripheralRoot.GetProperty("motor_output_fit").GetProperty(side);
        if (directionFit.GetProperty("training_direction_mismatches_including_zero").GetInt32() != 0
            || directionFit.GetProperty("heldout_direction_mismatches").GetInt32() != 0
            || directionFit.GetProperty("grouped_direction_cv_folds").EnumerateArray().Any(fold => fold.GetProperty("mismatches").GetInt32() != 0))
            throw new InvalidDataException($"Raw peripheral {side} direction evidence does not support a zero-mismatch sign-level rule.");
    }
    foreach (var (side, channel, pin) in new[] { ("left", 1, "pd2"), ("right", 2, "pd3") })
    {
        var fitted = peripheralRoot.GetProperty("motor_output_fit").GetProperty(side);
        modelRules.Add(new ControllerModelRule(
            $"uart.motor.{side}_percent", ControllerSignalKind.AnalogSample,
            $"tim3.ch{channel}.duty_fraction", ControllerSignalKind.PwmCapture,
            ControllerModelOperator.MagnitudeAffine, fitted.GetProperty("gain").GetDouble() / 100.0,
            fitted.GetProperty("offset").GetDouble(), -100, 100, "fraction"));
        modelRules.Add(new ControllerModelRule(
            $"uart.motor.{side}_percent", ControllerSignalKind.AnalogSample,
            $"gpio.{pin}.direction", ControllerSignalKind.GpioLevel,
            ControllerModelOperator.SignLevel, 0, 0, -100, 100, "level"));
    }
    var learnedResponses = LearnTrainingUartResponses(peripheralCampaignDirectory!, peripheralRows);
    initialUartBootValidation = LearnAndVerifyBootResponse(peripheralCampaignDirectory!, peripheralRows);
    initialUartOutputBytes = Encoding.ASCII.GetBytes(initialUartBootValidation.Response! + "\n");
    var pingResponse = learnedResponses["PING"];
    var motorResponse = learnedResponses["MOTOR"];
    var moveResponse = learnedResponses["MOVE"];
    var overlengthResponse = learnedResponses["OVERLENGTH"];
    overlengthUartResponseBytes = overlengthResponse;
    uartCommands.Add(new ControllerUartCommandRule("PING", 0, -100, 100, "uart.tx", [], pingResponse));
    uartCommands.Add(new ControllerUartCommandRule("MOTOR", 2, -100, 100, "uart.tx",
        [new ControllerUartOperandBinding(0, "uart.motor.left_percent"), new ControllerUartOperandBinding(1, "uart.motor.right_percent")], motorResponse));
    uartCommands.Add(new ControllerUartCommandRule("MOVE", 2, 100, 100, "uart.tx", [], moveResponse));
    uartCommands.Add(echoPulseFit is null
        ? new ControllerUartCommandRule("SENSOR?", 0, 0, 0, "uart.tx", [], null, IsUnsupported: true)
        : new ControllerUartCommandRule("SENSOR?", 0, 0, 0, "uart.tx", [], null, NumericResponseCommandToken: "SENSOR?"));
    heldoutUartResponseChecks = VerifyHeldoutUartResponses(peripheralCampaignDirectory!, peripheralRows, pingResponse, motorResponse, moveResponse, overlengthResponse);
}
else
{
    foreach (var model in evaluationRoot.GetProperty("models").EnumerateArray())
    {
        var channel = model.GetProperty("channel").GetInt32();
        if (channel is not (1 or 2)) throw new InvalidDataException("Unsupported fitted motor channel.");
        var side = channel == 1 ? "left" : "right";
        modelRules.Add(new ControllerModelRule(
            $"uart.motor.{side}_percent", ControllerSignalKind.AnalogSample,
            $"tim3.ch{channel}.signed_duty", ControllerSignalKind.PwmCapture,
            Enum.Parse<ControllerModelOperator>(model.GetProperty("operator").GetString()!.Replace("_", "", StringComparison.Ordinal), ignoreCase: true),
            model.GetProperty("gain_per_percentage_point").GetDouble(), model.GetProperty("offset").GetDouble(),
            -100, 100, "fraction_signed"));
    }
}
if (modelRules.Count != (peripheralFit is null ? 2 : 4)) throw new InvalidDataException("Fitted campaign did not provide the expected motor channels and peripheral observables.");
if (sensorFit is not null)
{
    var sensorRoot = sensorFit.RootElement;
    if (sensorRoot.GetProperty("status").GetString() != "NUMERIC_SENSOR_RESPONSE_CANDIDATE_PASS")
        throw new InvalidDataException("Sensor candidate did not pass its independent numeric response gates.");
    var sensorManifestPath = Path.Combine(sensorCampaignDirectory!, "sensor-sequence-manifest.json");
    if (!ControllerProfileHashes.Matches(sensorRoot.GetProperty("campaign_manifest_sha256").GetString()!, HashFile(sensorManifestPath)))
        throw new InvalidDataException("Sensor model fit does not match the supplied campaign manifest.");
    using var sensorManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(sensorManifestPath));
    var sensorRows = sensorManifest.RootElement.EnumerateArray().ToArray();
    var expectedTraining = sensorRows.Where(row => row.GetProperty("split").GetString() == "training")
        .Select(row => row.GetProperty("sequence_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
    var expectedHeldout = sensorRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .Select(row => row.GetProperty("sequence_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
    if (!expectedTraining.SequenceEqual(sensorRoot.GetProperty("training_sequences").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal))
        || !expectedHeldout.SequenceEqual(sensorRoot.GetProperty("heldout_sequences").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal)))
        throw new InvalidDataException("Sensor fit training/held-out sequence IDs differ from its manifest.");
    foreach (var row in sensorRows)
    {
        var tracePath = Path.Combine(sensorCampaignDirectory!, row.GetProperty("trace").GetString()!);
        var evaluationPath = Path.Combine(sensorCampaignDirectory!, row.GetProperty("sensor_evaluation").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath))
            || !ControllerProfileHashes.Matches(row.GetProperty("sensor_evaluation_sha256").GetString()!, HashFile(evaluationPath)))
            throw new InvalidDataException("Sensor campaign trace/evaluation hash mismatch.");
        using var sequenceEvaluation = JsonDocument.Parse(await File.ReadAllBytesAsync(evaluationPath));
        var sequenceRoot = sequenceEvaluation.RootElement;
        if (sequenceRoot.GetProperty("causally_matched_valid_replies").GetInt32() != 4)
            throw new InvalidDataException("Sensor sequence does not contain four causal firmware replies.");
        foreach (var reply in sequenceRoot.GetProperty("replies").EnumerateArray())
        {
            if (!reply.GetProperty("valid").GetBoolean()) throw new InvalidDataException("Sensor campaign contains an invalid firmware reply.");
            sensorReferenceObservations.Add(new SensorReferenceObservation(
                row.GetProperty("sequence_id").GetString()!, row.GetProperty("split").GetString()!,
                reply.GetProperty("raycast_distance_mm").GetDouble() / 1000.0,
                reply.GetProperty("reply_mm").GetDouble(), reply.GetProperty("reference_sample_sim_time_ns").GetInt64()));
        }
    }
    var sensorRule = sensorRoot.GetProperty("model_document").GetProperty("rules").EnumerateArray().Single();
    modelRules.Add(new ControllerModelRule(
        sensorRule.GetProperty("inputEndpointId").GetString()!, Enum.Parse<ControllerSignalKind>(sensorRule.GetProperty("inputKind").GetString()!, true),
        sensorRule.GetProperty("outputEndpointId").GetString()!, Enum.Parse<ControllerSignalKind>(sensorRule.GetProperty("outputKind").GetString()!, true),
        Enum.Parse<ControllerModelOperator>(sensorRule.GetProperty("operator").GetString()!, true), sensorRule.GetProperty("gain").GetDouble(),
        sensorRule.GetProperty("offset").GetDouble(), sensorRule.GetProperty("minimumInput").GetDouble(), sensorRule.GetProperty("maximumInput").GetDouble(),
        sensorRule.GetProperty("unit").GetString()!));
    VerifySensorFitWasLearnedFromTraining(sensorRoot, sensorReferenceObservations, modelRules[^1]);
}
if (echoPulseFit is not null)
{
    if (peripheralFit is null || echoPulseCampaignDirectory is null)
        throw new InvalidDataException("The pulse-width sensor model is currently supported only with the measured raw motor/UART candidate.");
    var fitRoot = echoPulseFit.RootElement;
    if (fitRoot.GetProperty("status").GetString() != "PULSE_WIDTH_RESPONSE_FIT_PASS")
        throw new InvalidDataException("Echo pulse fit did not pass its declared whole-sequence gates.");
    var sensorManifestPath = Path.Combine(echoPulseCampaignDirectory, "sensor-sequence-manifest.json");
    if (!ControllerProfileHashes.Matches(fitRoot.GetProperty("campaign_manifest_sha256").GetString()!, HashFile(sensorManifestPath)))
        throw new InvalidDataException("Echo pulse fit does not match the supplied sensor campaign manifest.");
    using var pulseManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(sensorManifestPath));
    var pulseRows = pulseManifest.RootElement.EnumerateArray().ToArray();
    var fitTraining = fitRoot.GetProperty("training_sequences").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
    var fitHeldout = fitRoot.GetProperty("heldout_sequences").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal).ToArray();
    var manifestTraining = pulseRows.Where(row => row.GetProperty("split").GetString() == "training").Select(row => row.GetProperty("sequence_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
    var manifestHeldout = pulseRows.Where(row => row.GetProperty("split").GetString() == "heldout").Select(row => row.GetProperty("sequence_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
    if (!fitTraining.SequenceEqual(manifestTraining) || !fitHeldout.SequenceEqual(manifestHeldout)
        || fitTraining.Intersect(fitHeldout, StringComparer.Ordinal).Any())
        throw new InvalidDataException("Echo pulse fit sequence partitions differ from the supplied whole-sequence manifest.");
    var referenceFirmwareHash = HashFile(firmwarePath);
    if (pulseRows.Any(row => !ControllerProfileHashes.Matches(row.GetProperty("firmware_sha256").GetString()!, referenceFirmwareHash)))
        throw new InvalidDataException("Echo pulse campaign firmware does not match the selected controller image.");
    foreach (var row in pulseRows.OrderBy(row => row.GetProperty("split").GetString() == "training" ? 0 : 1))
    {
        var tracePath = Path.Combine(echoPulseCampaignDirectory, row.GetProperty("trace").GetString()!);
        var evaluationPath = Path.Combine(echoPulseCampaignDirectory, row.GetProperty("sensor_evaluation").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath))
            || !ControllerProfileHashes.Matches(row.GetProperty("sensor_evaluation_sha256").GetString()!, HashFile(evaluationPath)))
            throw new InvalidDataException("Echo pulse sequence trace/evaluation hash mismatch.");
        using var sequenceEvaluation = JsonDocument.Parse(await File.ReadAllBytesAsync(evaluationPath));
        var records = (await File.ReadAllLinesAsync(tracePath)).Where(line => line.Length > 0).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var edges = records.Where(record => record.RootElement.GetProperty("RecordType").GetString() == "DistanceEchoEdge")
                .Select(record => (Time: record.RootElement.GetProperty("SimTimeNs").GetInt64(), High: record.RootElement.GetProperty("High").GetBoolean())).ToArray();
            var replies = sequenceEvaluation.RootElement.GetProperty("replies").EnumerateArray().ToArray();
            var sequenceLines = ExtractDistanceReplies(records);
            var split = row.GetProperty("split").GetString()!;
            if (split == "heldout") heldoutEchoResponseChecks.Sequences++;
            if (sequenceLines.Count != replies.Length) throw new InvalidDataException($"Echo pulse UART reply count differs from evaluation in {row.GetProperty("sequence_id").GetString()}.");
            for (var index = 0; index < replies.Length; index++)
            {
                var reply = replies[index];
                if (!reply.GetProperty("valid").GetBoolean()) throw new InvalidDataException("Echo pulse campaign contains a reply outside the valid sensor domain.");
                var replyMm = reply.GetProperty("reply_mm").GetDouble();
                var responseMatch = Regex.Match(sequenceLines[index], "^(?<prefix>DIST mm=)(?<value>[0-9]+)(?<suffix> valid=1)$", RegexOptions.CultureInvariant);
                if (!responseMatch.Success || double.Parse(responseMatch.Groups["value"].Value, CultureInfo.InvariantCulture) != replyMm)
                    throw new InvalidDataException("Echo pulse trace UART output does not match the sensor evaluator reply grammar/value.");
                if (split == "training")
                {
                    if (echoResponsePrefix.Length == 0)
                    {
                        echoResponsePrefix = responseMatch.Groups["prefix"].Value;
                        echoResponseSuffix = responseMatch.Groups["suffix"].Value + "\n";
                    }
                    if (echoResponsePrefix != responseMatch.Groups["prefix"].Value || echoResponseSuffix != responseMatch.Groups["suffix"].Value + "\n")
                        throw new InvalidDataException("Training sensor UART response grammar is not consistent.");
                }
                var sampleTime = reply.GetProperty("reference_sample_sim_time_ns").GetInt64();
                var fallTime = reply.GetProperty("reference_echo_fall_sim_time_ns").GetInt64();
                var rise = edges.SingleOrDefault(edge => edge.Time == sampleTime && edge.High);
                if (rise.Time != sampleTime || !edges.Any(edge => edge.Time == fallTime && !edge.High) || fallTime <= sampleTime)
                    throw new InvalidDataException("Sensor reply lacks a causally paired measured GPIO echo pulse.");
                echoPulseObservations.Add(new EchoPulseObservation(row.GetProperty("sequence_id").GetString()!, split,
                    fallTime - sampleTime, replyMm, sampleTime, reply.GetProperty("response_sim_time_bracket_ns")[0].GetInt64()));
                if (split == "heldout")
                {
                    heldoutEchoResponseChecks.Replies++;
                    if (sequenceLines[index].StartsWith(echoResponsePrefix, StringComparison.Ordinal)
                        && sequenceLines[index].EndsWith(echoResponseSuffix.TrimEnd('\n'), StringComparison.Ordinal))
                        heldoutEchoResponseChecks.GrammarExact++;
                }
            }
        }
        finally
        {
            foreach (var record in records) record.Dispose();
        }
    }
    var trainingObservations = echoPulseObservations.Where(item => item.Split == "training").ToArray();
    var heldoutObservations = echoPulseObservations.Where(item => item.Split == "heldout").ToArray();
    if (manifestTraining.Length != 4 || manifestHeldout.Length != 2 || trainingObservations.Length != 16 || heldoutObservations.Length != 8
        || fitRoot.GetProperty("training_observations").GetInt32() != trainingObservations.Length)
        throw new InvalidDataException("Echo pulse campaign must retain four training and two whole held-out sequences (16/8 causal replies).");
    var pulseFit = FitEchoPulse(trainingObservations);
    var fitCandidate = fitRoot.GetProperty("candidate");
    if (Math.Abs(pulseFit.Gain - fitCandidate.GetProperty("gain_mm_per_ns").GetDouble()) > 1e-12
        || Math.Abs(pulseFit.Offset - fitCandidate.GetProperty("offset_mm").GetDouble()) > 1e-9)
        throw new InvalidDataException("Echo pulse fit parameters do not reproduce independently from training trace edges/replies.");
    var minimumWidth = trainingObservations.Min(item => item.PulseWidthNs);
    var maximumWidth = trainingObservations.Max(item => item.PulseWidthNs);
    var fitEnvelope = fitCandidate.GetProperty("training_envelope_ns");
    if (fitEnvelope.GetProperty("minimum").GetInt64() != minimumWidth || fitEnvelope.GetProperty("maximum").GetInt64() != maximumWidth)
        throw new InvalidDataException("Echo pulse fitted envelope differs from the raw training traces.");
    if (heldoutObservations.Any(item => item.PulseWidthNs < minimumWidth || item.PulseWidthNs > maximumWidth))
        throw new InvalidDataException("Held-out echo pulse width is outside the whole-sequence training envelope.");
    var groupedTrainingErrors = new List<double>();
    foreach (var sequenceId in manifestTraining)
    {
        var foldTraining = trainingObservations.Where(item => item.SequenceId != sequenceId).ToArray();
        var foldValidation = trainingObservations.Where(item => item.SequenceId == sequenceId).ToArray();
        if (foldValidation.Length == 0) throw new InvalidDataException("Echo pulse grouped-CV fold has no held-out training sequence.");
        var foldFit = FitEchoPulse(foldTraining);
        groupedTrainingErrors.AddRange(foldValidation.Select(item => Math.Abs(item.ReplyMm - (foldFit.Gain * item.PulseWidthNs + foldFit.Offset))));
    }
    var groupedP95 = Percentile(groupedTrainingErrors, 0.95);
    if (fitRoot.GetProperty("threshold_declared_before_fit_mm_p95").GetDouble() != 2.0
        || groupedP95 > 2.0
        || Math.Abs(groupedP95 - fitRoot.GetProperty("grouped_training_cv").GetProperty("p95_error_mm").GetDouble()) > 1e-8)
        throw new InvalidDataException("Independent whole-sequence echo pulse cross-validation disagrees with or exceeds the predeclared 2 mm p95 gate.");
    if (noEchoCampaignDirectory is not null)
        noEchoValidation = ValidateNoEchoCampaign(noEchoCampaignDirectory, firmwarePath);
    echoPulseModel = new ControllerEchoPulseRule("gpio.pa0.echo", pulseFit.Gain, pulseFit.Offset,
        minimumWidth, maximumWidth, "SENSOR?", echoResponsePrefix, echoResponseSuffix,
        noEchoCampaignDirectory is null ? ControllerEchoNoPulseBehavior.Reject : ControllerEchoNoPulseBehavior.RetainLastValidReading,
        noEchoValidation.InitialInvalidResponse);
    if (heldoutEchoResponseChecks.GrammarExact != heldoutEchoResponseChecks.Replies
        || heldoutEchoResponseChecks.Replies != fitRoot.GetProperty("heldout_observations").GetArrayLength())
        throw new InvalidDataException("Held-out echo-pulse UART reply grammar/observation count differs from the fit artifact.");
    var fittedHeldout = fitRoot.GetProperty("heldout_observations").EnumerateArray().ToArray();
    if (fittedHeldout.Length != heldoutObservations.Length)
        throw new InvalidDataException("Held-out raw echo-pulse observation count differs from the fit artifact.");
    for (var index = 0; index < heldoutObservations.Length; index++)
    {
        var observation = heldoutObservations[index];
        if (observation.QueryLowerBoundSimTimeNs < observation.RiseTimeNs + observation.PulseWidthNs)
            throw new InvalidDataException("A measured held-out SENSOR? response could precede its complete echo pulse.");
        var fittedObservation = fittedHeldout[index];
        if (fittedObservation.GetProperty("sequence_id").GetString() != observation.SequenceId
            || fittedObservation.GetProperty("pulse_width_ns").GetInt64() != observation.PulseWidthNs
            || fittedObservation.GetProperty("reply_mm").GetDouble() != observation.ReplyMm)
            throw new InvalidDataException("Held-out echo pulse fit observations do not match the raw sensor campaign.");
        var predictedMm = ControllerModelEvaluator.EvaluateEchoPulse(echoPulseModel, observation.PulseWidthNs);
        if (predictedMm != ControllerModelEvaluator.EvaluateEchoPulse(echoPulseModel, observation.PulseWidthNs))
            throw new InvalidDataException("Echo pulse evaluator failed deterministic held-out replay.");
        heldoutEchoPulseErrors.Add(Math.Abs(predictedMm - observation.ReplyMm));
        heldoutEchoPulseRawFitErrors.Add(Math.Abs((echoPulseModel.Gain * observation.PulseWidthNs + echoPulseModel.Offset) - observation.ReplyMm));
    }
    var fitRawErrors = fitRoot.GetProperty("heldout_error_mm");
    if (Math.Abs(Percentile(heldoutEchoPulseRawFitErrors, 0.95) - fitRawErrors.GetProperty("p95").GetDouble()) > 1e-8
        || Math.Abs(heldoutEchoPulseRawFitErrors.Max() - fitRawErrors.GetProperty("max").GetDouble()) > 1e-8)
        throw new InvalidDataException("Independent held-out raw pulse fit errors differ from the Python fitter report.");
    if (Percentile(heldoutEchoPulseErrors, 0.95) > fitRoot.GetProperty("threshold_declared_before_fit_mm_p95").GetDouble())
        throw new InvalidDataException("Discrete firmware-format held-out sensor p95 error exceeds its predeclared threshold.");
}
if (echoPulseModel is not null && peripheralCampaignDirectory is not null
    && File.Exists(Path.Combine(peripheralCampaignDirectory, "mixed-campaign-validation.json")))
    mixedSensorEnvelopeReview = ValidateMixedSensorEnvelope(peripheralCampaignDirectory, firmwarePath, echoPulseModel);
if (lineBoundaryCampaignDirectory is not null)
{
    if (peripheralFit is null) throw new InvalidDataException("UART line-boundary evidence requires the raw motor/UART profile candidate.");
    var boundaryManifestPath = Path.Combine(lineBoundaryCampaignDirectory, "sequence-manifest.json");
    var boundaryEvaluationPath = Path.Combine(lineBoundaryCampaignDirectory, "line-boundary-evaluation.json");
    using var boundaryManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(boundaryManifestPath));
    using var boundaryEvaluation = JsonDocument.Parse(await File.ReadAllBytesAsync(boundaryEvaluationPath));
    var boundaryManifestBytes = await File.ReadAllBytesAsync(boundaryManifestPath);
    if (boundaryEvaluation.RootElement.GetProperty("status").GetString() != "REFERENCE_FIRMWARE_UART_GRAMMAR_AND_LINE_BOUNDARY_PASS_NOT_PROFILE_QUALIFICATION"
        || !ControllerProfileHashes.Matches(boundaryEvaluation.RootElement.GetProperty("campaign_manifest_sha256").GetString()!, HashBytes(boundaryManifestBytes))
        || !ControllerProfileHashes.Matches(boundaryEvaluation.RootElement.GetProperty("firmware_sha256").GetString()!, HashFile(firmwarePath))
        || boundaryEvaluation.RootElement.GetProperty("heldout_grammar_exact_matches").GetInt32() != 10)
        throw new InvalidDataException("UART grammar/boundary evaluation is stale, failed, or uses another firmware image.");
    var boundaryRows = boundaryManifest.RootElement.EnumerateArray().ToArray();
    if (boundaryRows.Length != 6 || boundaryRows.Count(row => row.GetProperty("split").GetString() == "training") != 4
        || boundaryRows.Count(row => row.GetProperty("split").GetString() == "heldout") != 2
        || boundaryRows.Any(row => !row.GetProperty("line_boundary_probe").GetBoolean() || !row.GetProperty("uart_grammar_probe").GetBoolean()
            || !ControllerProfileHashes.Matches(row.GetProperty("firmware_sha256").GetString()!, HashFile(firmwarePath))))
        throw new InvalidDataException("UART grammar/boundary manifest must identify six source-matched sequences with four training and two whole held-out probes.");
    var expectedGrammar = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["STATUS?"] = "STATUS MCU=OK UART=OK",
        ["MOTOR 101 0"] = "ERR RANGE",
        ["MOTOR -101 0"] = "ERR RANGE",
        ["MOTOR 1"] = "ERR CMD",
        ["ping"] = "ERR CMD"
    };
    foreach (var row in boundaryRows.OrderBy(item => item.GetProperty("split").GetString() == "training" ? 0 : 1))
    {
        var sequenceId = row.GetProperty("sequence_id").GetString()!;
        var training = row.GetProperty("split").GetString() == "training";
        var tracePath = Path.Combine(lineBoundaryCampaignDirectory, row.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException("UART boundary sequence trace hash mismatch.");
        var inputs = ReadUartLines(tracePath, "controller_to_mcu");
        var outputs = ReadUartLines(tracePath, "mcu_to_controller").Where(line => !Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (inputs.Count != outputs.Length) throw new InvalidDataException("UART boundary trace request/reply line count differs.");
        var timing = MeasureModelUartTiming(tracePath, inputs, outputs);
        if (timing.ModelTimestampOutsideObservedBracketNs.Any(error => error > timing.LargestPhysicsTickIntervalNs))
            throw new InvalidDataException($"{sequenceId}: model UART response timestamp exceeds the one-physics-tick timing gate.");
        if (!training)
        {
            heldoutUartTimingErrorsNs.AddRange(timing.ModelTimestampOutsideObservedBracketNs);
            heldoutUartTimingTickBoundsNs.AddRange(timing.ObservedBracketWidthsNs);
        }
        var matches = Enumerable.Range(0, inputs.Count).Where(index => inputs[index].Bytes.Length is 47 or 48).ToArray();
        if (matches.Length != 2 || matches.Count(index => inputs[index].Bytes.Length == 47) != 1
            || matches.Count(index => inputs[index].Bytes.Length == 48) != 1)
            throw new InvalidDataException("UART boundary trace must contain exactly one 47-byte and one 48-byte line.");
        var line47Index = matches.Single(index => inputs[index].Bytes.Length == 47);
        var line48Index = matches.Single(index => inputs[index].Bytes.Length == 48);
        VerifyUartCausality(inputs[line47Index], outputs[line47Index], sequenceId);
        VerifyUartCausality(inputs[line48Index], outputs[line48Index], sequenceId);
        var response47 = outputs[line47Index].Bytes;
        if (!StringComparer.Ordinal.Equals(Encoding.ASCII.GetString(response47), "ERR CMD")
            || !StringComparer.Ordinal.Equals(Encoding.ASCII.GetString(outputs[line48Index].Bytes), "ERR LINE")
            || overlengthUartResponseBytes is null
            || !outputs[line48Index].Bytes.Append((byte)'\n').SequenceEqual(overlengthUartResponseBytes))
            throw new InvalidDataException("Measured UART line boundary differs from 47-byte ERR CMD / 48-byte ERR LINE.");
        if (training)
        {
            var exactRule = new ControllerUartExactLineRule(inputs[line47Index].Bytes.ToArray(), response47.Append((byte)'\n').ToArray());
            AddOrVerifyTrainingExactLine(exactRule, exactUartLineRules, "UART 47-byte boundary");
        }
        else
        {
            heldoutLineBoundaryChecks.Sequences++;
            if (!exactUartLineRules.Any(rule => rule.RequestBytes.AsSpan().SequenceEqual(inputs[line47Index].Bytes)
                && rule.ResponseBytes.AsSpan().SequenceEqual(response47.Append((byte)'\n').ToArray())))
                throw new InvalidDataException("Held-out 47-byte malformed line differs from its training-derived exact rule.");
            heldoutLineBoundaryChecks.ExactResponses += 2;
        }

        foreach (var (requestText, expectedReply) in expectedGrammar)
        {
            var requestBytes = Encoding.ASCII.GetBytes(requestText);
            var pairIndices = Enumerable.Range(0, inputs.Count)
                .Where(index => inputs[index].Bytes.AsSpan().SequenceEqual(requestBytes)).ToArray();
            if (pairIndices.Length != 1) throw new InvalidDataException($"{sequenceId} must contain one exact {requestText} grammar probe.");
            var index = pairIndices[0];
            VerifyUartCausality(inputs[index], outputs[index], sequenceId);
            var responseBytes = outputs[index].Bytes;
            if (!StringComparer.Ordinal.Equals(Encoding.ASCII.GetString(responseBytes), expectedReply))
                throw new InvalidDataException($"{sequenceId} {requestText} reply differs from the measured reference grammar.");
            if (requestText == "STATUS?")
            {
                var learnedResponse = responseBytes.Append((byte)'\n').ToArray();
                if (training)
                {
                    if (statusResponseBytes is null) statusResponseBytes = learnedResponse;
                    else if (!statusResponseBytes.AsSpan().SequenceEqual(learnedResponse))
                        throw new InvalidDataException("Training STATUS? response bytes are inconsistent.");
                }
                else if (statusResponseBytes is null || !statusResponseBytes.AsSpan().SequenceEqual(learnedResponse))
                    throw new InvalidDataException("Held-out STATUS? response differs from its training-derived rule.");
            }
            else if (training)
            {
                AddOrVerifyTrainingExactLine(new ControllerUartExactLineRule(requestBytes, responseBytes.Append((byte)'\n').ToArray()),
                    exactUartLineRules, requestText);
            }
            else if (!exactUartLineRules.Any(rule => rule.RequestBytes.AsSpan().SequenceEqual(requestBytes)
                && rule.ResponseBytes.AsSpan().SequenceEqual(responseBytes.Append((byte)'\n').ToArray())))
                throw new InvalidDataException($"Held-out {requestText} response differs from its training-derived exact rule.");
            if (!training) heldoutUartGrammarChecks.ExactResponses++;
        }
        if (!training) heldoutUartGrammarChecks.Sequences++;
    }
    if (exactUartLineRules.Count != 5 || heldoutLineBoundaryChecks.Sequences != 2 || heldoutLineBoundaryChecks.ExactResponses != 4
        || heldoutUartGrammarChecks.Sequences != 2 || heldoutUartGrammarChecks.ExactResponses != 10 || statusResponseBytes is null)
        throw new InvalidDataException("UART grammar and boundary held-out acceptance did not cover both whole sequences exactly.");
    uartCommands.Add(new ControllerUartCommandRule("STATUS?", 0, 0, 0, "uart.tx", [], statusResponseBytes));
}
var modelOperationBudget = modelRules.Count + uartCommands.Count + (echoPulseModel is null ? 0 : 1)
    + exactUartLineRules.Count + faultRules.Count;
// The raw UART campaign is bound to the reference drive ELF. Its reproducible source declares
// LINE_CAPACITY 48, so 47 payload bytes is the largest accepted line before the terminator.
var modelDocument = new ControllerModelDocument(1,
    controllerFaultCampaignDirectory is not null ? "rover-motor-uart-echo-noecho-grammar-controller-fault-candidate-v8"
        : noEchoCampaignDirectory is not null ? "rover-motor-uart-echo-pulse-noecho-grammar-candidate-v7"
        : lineBoundaryCampaignDirectory is not null ? "rover-motor-uart-echo-pulse-grammar-candidate-v6"
        : echoPulseModel is not null ? "rover-motor-uart-echo-pulse-candidate-v3"
        : peripheralFit is not null ? "rover-motor-uart-peripheral-candidate-v2"
        : sensorFit is null ? "rover-motor-affine-candidate-v1" : "rover-motor-sensor-affine-candidate-v2",
    modelOperationBudget, modelRules,
    uartCommands.Count == 0 ? 0 : peripheralFit is null ? 64 : 47,
    uartCommands.Count == 0 ? null : uartCommands, uartCommands.Count == 0 ? null : "uart.rx",
    overlengthUartResponseBytes, echoPulseModel, exactUartLineRules, initialUartOutputBytes,
    faultRules.Count == 0 ? null : faultRules);
var envelope = peripheralFit is not null
    ? echoPulseModel is null
        ? new ControllerInputEnvelope(faultRules.Count == 0 ? ["uart.rx"] : ["uart.rx", "controller.fault"],
            faultRules.Count == 0 ? [ControllerSignalKind.UartRxByte] : [ControllerSignalKind.UartRxByte, ControllerSignalKind.Fault],
            0, long.MaxValue, modelOperationBudget, 115200)
        : new ControllerInputEnvelope(faultRules.Count == 0 ? ["gpio.pa0.echo", "uart.rx"] : ["controller.fault", "gpio.pa0.echo", "uart.rx"],
            faultRules.Count == 0 ? [ControllerSignalKind.GpioLevel, ControllerSignalKind.UartRxByte] : [ControllerSignalKind.Fault, ControllerSignalKind.GpioLevel, ControllerSignalKind.UartRxByte],
            0, long.MaxValue, modelOperationBudget, 115200)
    : new ControllerInputEnvelope(modelRules.Select(rule => rule.InputEndpointId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), [ControllerSignalKind.AnalogSample],
        0, long.MaxValue, 256, 0);

var corpusManifests = new List<string> { "motor=" + HashFile(Path.Combine(campaignDirectory, "sequence-manifest.json")) };
if (sensorCampaignDirectory is not null) corpusManifests.Add("sensor=" + HashFile(Path.Combine(sensorCampaignDirectory, "sensor-sequence-manifest.json")));
if (echoPulseCampaignDirectory is not null) corpusManifests.Add("echo-pulse=" + HashFile(Path.Combine(echoPulseCampaignDirectory, "sensor-sequence-manifest.json")));
if (peripheralCampaignDirectory is not null) corpusManifests.Add("peripheral=" + HashFile(Path.Combine(peripheralCampaignDirectory, "sequence-manifest.json")));
if (lineBoundaryCampaignDirectory is not null) corpusManifests.Add("line-boundary=" + HashFile(Path.Combine(lineBoundaryCampaignDirectory, "sequence-manifest.json")));
if (noEchoCampaignDirectory is not null)
{
    corpusManifests.Add("no-echo=" + HashFile(Path.Combine(noEchoCampaignDirectory, "sequence-manifest.json")));
    corpusManifests.Add("no-echo-validation=" + HashFile(Path.Combine(noEchoCampaignDirectory, "campaign-validation.json")));
}
if (faultManifestSha256 is not null) corpusManifests.Add("controller-fault=" + faultManifestSha256);
var traceCorpusSha256 = HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", corpusManifests)));
var generatorVersion = controllerFaultCampaignDirectory is not null ? "goal03-bounded-rover-controller-2.0.0"
    : noEchoCampaignDirectory is not null ? "goal03-motor-uart-echo-pulse-noecho-grammar-1.0.0"
    : lineBoundaryCampaignDirectory is not null ? "goal03-motor-uart-echo-pulse-grammar-1.0.0"
    : echoPulseModel is not null ? "goal03-motor-uart-echo-pulse-1.0.0"
    : peripheralFit is not null ? "goal03-motor-peripheral-uart-transducer-1.3.0"
    : sensorFit is null ? "goal03-group-cv-model-fit-1.0.0" : "goal03-motor-sensor-group-cv-1.1.0";
var evaluatorVersion = controllerFaultCampaignDirectory is not null ? "campaign-evaluator-2+uart-transducer-6+echo-pulse-state-4+sensor-envelope-800-2500-1+numeric-sensor-tolerance-2mm-1+noecho-retain-1+measured-mcu-halt-latch-2+fault-source-diagnostic-1+uart-response-timing-bracket-1+qualification-evidence-1"
    : noEchoCampaignDirectory is not null ? "campaign-evaluator-2+uart-transducer-6+exact-line-rule-2+echo-pulse-state-3+heldout-pulse-replay-1+line-bound-47+status-range-grammar-1+initial-uart-output-1+noecho-retain-1+invalid-initial-sensor-reply-1+uart-response-timing-bracket-1"
    : lineBoundaryCampaignDirectory is not null ? "campaign-evaluator-2+uart-transducer-6+exact-line-rule-2+echo-pulse-state-3+heldout-pulse-replay-1+line-bound-47+status-range-grammar-1+initial-uart-output-1+echo-noecho-contract-1+uart-response-timing-bracket-1"
    : echoPulseModel is not null ? "campaign-evaluator-2+uart-transducer-5+echo-pulse-state-3+heldout-pulse-replay-1+line-bound-47+echo-noecho-contract-1"
    : peripheralFit is not null ? "campaign-evaluator-2+uart-transducer-5+raw-motor-peripheral-fit-1+echo-pulse-state-1+line-bound-47"
    : sensorFit is null ? "campaign-evaluator-2" : "campaign-evaluator-2+sensor-numeric-response-1";
var identity = ControllerProfileIdentityFactory.CreateFromProjectFiles(robotPath, componentDirectory, firmwarePath,
    renodeHostPath, controller.GetProperty("id").GetString()!, envelope, generatorVersion, evaluatorVersion);

var sequenceRows = manifest.RootElement.EnumerateArray().ToArray();
var trainingCount = sequenceRows.Count(row => row.GetProperty("split").GetString() == "training");
var heldoutRows = sequenceRows.Where(row => row.GetProperty("split").GetString() == "heldout")
    .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
    .Select(row => $"motor:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}").ToList();
var sensorTrainingCount = 0;
if (sensorCampaignDirectory is not null)
{
    using var sensorManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(sensorCampaignDirectory, "sensor-sequence-manifest.json")));
    var sensorRows = sensorManifest.RootElement.EnumerateArray().ToArray();
    sensorTrainingCount = sensorRows.Count(row => row.GetProperty("split").GetString() == "training");
    heldoutRows.AddRange(sensorRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
        .Select(row => $"sensor:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}"));
}
var peripheralTrainingCount = 0;
if (peripheralCampaignDirectory is not null)
{
    using var peripheralManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(peripheralCampaignDirectory, "sequence-manifest.json")));
    var peripheralRows = peripheralManifest.RootElement.EnumerateArray().ToArray();
    peripheralTrainingCount = peripheralRows.Count(row => row.GetProperty("split").GetString() == "training");
    heldoutRows.AddRange(peripheralRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
        .Select(row => $"peripheral:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}"));
}
var echoPulseTrainingCount = 0;
if (echoPulseCampaignDirectory is not null)
{
    using var pulseManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(echoPulseCampaignDirectory, "sensor-sequence-manifest.json")));
    var pulseRows = pulseManifest.RootElement.EnumerateArray().ToArray();
    echoPulseTrainingCount = pulseRows.Count(row => row.GetProperty("split").GetString() == "training");
    heldoutRows.AddRange(pulseRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
        .Select(row => $"echo-pulse:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}"));
}
var lineBoundaryTrainingCount = 0;
if (lineBoundaryCampaignDirectory is not null)
{
    using var boundaryManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(lineBoundaryCampaignDirectory, "sequence-manifest.json")));
    var boundaryRows = boundaryManifest.RootElement.EnumerateArray().ToArray();
    lineBoundaryTrainingCount = boundaryRows.Count(row => row.GetProperty("split").GetString() == "training");
    heldoutRows.AddRange(boundaryRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
        .Select(row => $"line-boundary:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}"));
}
var noEchoTrainingCount = 0;
if (noEchoCampaignDirectory is not null)
{
    using var noEchoManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(noEchoCampaignDirectory, "sequence-manifest.json")));
    var noEchoRows = noEchoManifest.RootElement.EnumerateArray().ToArray();
    noEchoTrainingCount = noEchoRows.Count(row => row.GetProperty("split").GetString() == "training");
    heldoutRows.AddRange(noEchoRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
        .Select(row => $"no-echo:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}"));
}
if (controllerFaultCampaignDirectory is not null)
{
    using var faultManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(controllerFaultCampaignDirectory, "sequence-manifest.json")));
    var faultRows = faultManifest.RootElement.EnumerateArray().Where(row => row.GetProperty("fault_type").GetString() == "mcu-halt").ToArray();
    heldoutRows.AddRange(faultRows.Where(row => row.GetProperty("split").GetString() == "heldout")
        .OrderBy(row => row.GetProperty("sequence_id").GetString(), StringComparer.Ordinal)
        .Select(row => $"controller-fault:{row.GetProperty("sequence_id").GetString()}|{row.GetProperty("trace_sha256").GetString()}"));
}
heldoutRows.Sort(StringComparer.Ordinal);
var heldoutHash = HashBytes(Encoding.UTF8.GetBytes(string.Join("\n", heldoutRows)));
var modelHash = ControllerProfileHashes.Compute(modelDocument);
var validationMetrics = new Dictionary<string, double>(StringComparer.Ordinal)
{
    ["training_sequence_count"] = trainingCount + sensorTrainingCount + peripheralTrainingCount + echoPulseTrainingCount + lineBoundaryTrainingCount + noEchoTrainingCount + faultCampaignTrainingSequences,
    ["heldout_sequence_count"] = heldoutRows.Count,
    ["heldout_motor_command_count"] = peripheralFit is null ? evaluationRoot.GetProperty("heldout_events").GetInt32() : peripheralFit.RootElement.GetProperty("heldout_commands").GetInt32(),
    ["heldout_duty_error_p95_percentage_points"] = peripheralFit is null
        ? evaluationRoot.GetProperty("heldout_duty_error_percentage_points").GetProperty("p95").GetDouble()
        : Math.Max(peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty("left").GetProperty("heldout_error_percentage_points").GetProperty("p95").GetDouble(),
            peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty("right").GetProperty("heldout_error_percentage_points").GetProperty("p95").GetDouble()),
    ["heldout_duty_error_max_percentage_points"] = peripheralFit is null
        ? evaluationRoot.GetProperty("heldout_duty_error_percentage_points").GetProperty("max").GetDouble()
        : Math.Max(peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty("left").GetProperty("heldout_error_percentage_points").GetProperty("max").GetDouble(),
            peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty("right").GetProperty("heldout_error_percentage_points").GetProperty("max").GetDouble()),
    ["uart_receive_bracket_p95_width_ns"] = peripheralFit is null ? evaluationRoot.GetProperty("uart_receive_sim_time_bracket_ns").GetProperty("p95_width").GetDouble() : 0
};
if (peripheralFit is not null)
{
    var timingObservations = peripheralFit.RootElement.GetProperty("heldout_observations").EnumerateArray().ToArray();
    validationMetrics["scheduled_input_to_output_latency_min_ns"] = timingObservations.Min(row => row.GetProperty("command_to_output_latency_min_ns").GetDouble());
    validationMetrics["scheduled_input_to_output_latency_max_ns"] = timingObservations.Max(row => row.GetProperty("command_to_output_latency_max_ns").GetDouble());
    validationMetrics["heldout_direction_mismatches"] = peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty("left").GetProperty("heldout_direction_mismatches").GetInt32()
        + peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty("right").GetProperty("heldout_direction_mismatches").GetInt32();
}
if (sensorFit is not null)
{
    validationMetrics["heldout_sensor_response_error_p95_mm"] = sensorFit.RootElement.GetProperty("heldout_error_mm").GetProperty("p95").GetDouble();
    validationMetrics["heldout_sensor_response_error_max_mm"] = sensorFit.RootElement.GetProperty("heldout_error_mm").GetProperty("max").GetDouble();
}
if (echoPulseFit is not null)
{
    validationMetrics["heldout_echo_pulse_response_error_p95_mm"] = Percentile(heldoutEchoPulseErrors, 0.95);
    validationMetrics["heldout_echo_pulse_response_error_max_mm"] = heldoutEchoPulseErrors.Max();
    validationMetrics["heldout_echo_uart_exact_grammar_fraction"] = (double)heldoutEchoResponseChecks.GrammarExact / heldoutEchoResponseChecks.Replies;
}
if (mixedSensorEnvelopeReview is not null)
{
    validationMetrics["mixed_heldout_sensor_valid_replies"] = mixedSensorEnvelopeReview.ValidReplies;
    validationMetrics["mixed_heldout_sensor_in_domain_replies"] = mixedSensorEnvelopeReview.InDomainValidReplies;
    validationMetrics["mixed_heldout_sensor_out_of_domain_replies"] = mixedSensorEnvelopeReview.OutOfDomainInputs.Count;
    validationMetrics["mixed_heldout_sensor_in_domain_error_p95_mm"] = mixedSensorEnvelopeReview.InDomainErrorP95Mm;
    validationMetrics["mixed_heldout_sensor_in_domain_error_max_mm"] = mixedSensorEnvelopeReview.InDomainErrorMaxMm;
}
if (lineBoundaryCampaignDirectory is not null)
    validationMetrics["heldout_uart_line_boundary_exact_response_count"] = heldoutLineBoundaryChecks.ExactResponses;
if (lineBoundaryCampaignDirectory is not null)
    validationMetrics["heldout_uart_command_grammar_exact_response_count"] = heldoutUartGrammarChecks.ExactResponses;
if (noEchoCampaignDirectory is not null)
{
    validationMetrics["heldout_no_echo_sequences"] = noEchoValidation.HeldoutSequences;
    validationMetrics["heldout_no_echo_stale_sensor_replies"] = noEchoValidation.HeldoutStaleReplies;
    validationMetrics["heldout_initial_invalid_sensor_sequences"] = noEchoValidation.HeldoutSequences;
}
if (controllerFaultCampaignDirectory is not null)
    validationMetrics["heldout_measured_mcu_halt_sequences"] = heldoutMcuHaltSequences;
var profileIdSeed = HashBytes(Encoding.UTF8.GetBytes(ControllerProfileHashes.Compute(identity) + modelHash + heldoutHash));
var supportedBehaviors = new List<string>();
var unsupportedBehaviors = new List<string>();
if (peripheralFit is not null)
{
    supportedBehaviors.AddRange(["training-derived PING response", "training-derived valid MOTOR command response",
        "training-derived observed MOVE rejection response", "training-derived observed overlength-line rejection response",
        "measured per-tick PWM duty magnitude and direction for valid MOTOR operands",
        "causal UART command responses with normal-operation response bounds measured to 120 Hz fixed-step resolution"]);
    unsupportedBehaviors.AddRange(["malformed other than exact measured lines, unknown and unmeasured range-invalid UART commands",
        "sub-tick UART byte emission and framing timing; replies are bounded by measured fixed-step response intervals",
        "unseen firmware state and interrupts"]);
    if (controllerFaultCampaignDirectory is not null)
        supportedBehaviors.Add("measured MCU_HALTED transition retains last peripheral outputs and suppresses subsequent UART replies");
    else unsupportedBehaviors.Add("measured MCU halt and latched-output transition");
    unsupportedBehaviors.Add("other controller fault codes and peripheral corruption");
    if (echoPulseModel is not null)
    {
        supportedBehaviors.Add("measured GPIO echo pulse width maps to training-derived SENSOR? response grammar");
        if (mixedSensorEnvelopeReview is not null)
        {
            if (mixedSensorEnvelopeReview.InDomainValidReplies > 0)
                supportedBehaviors.Add("mixed held-out SENSOR? replies within the learned pulse-width envelope are evaluated against exact firmware");
            if (mixedSensorEnvelopeReview.OutOfDomainInputs.Count > 0)
                unsupportedBehaviors.Add("echo pulse widths outside the measured 0.8–2.5 m normal-operation envelope; deterministic mode fails closed with an explicit diagnostic and requires an exact-mode restart");
        }
        if (noEchoCampaignDirectory is not null)
        {
            supportedBehaviors.Add("training-derived SENSOR? invalid response before first complete echo pulse");
            supportedBehaviors.Add("measured no-echo input retains last valid echo reading and valid flag");
            unsupportedBehaviors.Add("incomplete-high timeout, overlapping/noisy pulse recovery, and sensor reset behavior");
        }
        else unsupportedBehaviors.Add("sensor no-echo, timeout, overlap, noise, and recovery behavior");
    }
    else unsupportedBehaviors.Add("SENSOR? and live echo pulse capture");
    if (lineBoundaryCampaignDirectory is not null)
    {
        supportedBehaviors.Add("training-derived STATUS? response and exact measured MOTOR range/malformed command responses");
        supportedBehaviors.Add("training-derived exact 47-byte malformed UART line response and measured 48-byte overlength response");
    }
    else unsupportedBehaviors.Add("held-out UART line acceptance/rejection transition at 47/48 bytes");
}
else
{
    supportedBehaviors.Add(sensorFit is null ? "parsed signed motor percentage inputs map to sampled TIM3 PWM duty on channels 1 and 2"
        : "parsed signed motor percentage inputs map to sampled TIM3 PWM duty on channels 1 and 2");
    unsupportedBehaviors.AddRange(["raw UART command and response grammar", "GPIO direction and PWM timing",
        "sensor echo generation/timing and noisy or invalid inputs", "fault and latch transitions", "loaded trajectory and motion fidelity", "unseen firmware state and interrupts"]);
}
var profile = new DeterministicControllerProfile(
    1, "rover-unqualified-" + profileIdSeed[..16], identity, envelope, modelHash, traceCorpusSha256,
    heldoutHash, heldoutRows.Count, validationMetrics, supportedBehaviors, unsupportedBehaviors,
    "UNQUALIFIED");

var observations = evaluationRoot.GetProperty("heldout_observations").EnumerateArray().ToArray();
var heldoutErrors = new List<double>();
var heldoutDirectionMismatches = 0;
if (peripheralFit is not null)
{
    var fittedObservations = peripheralFit.RootElement.GetProperty("heldout_observations").EnumerateArray().ToArray();
    if (fittedObservations.Length != peripheralFit.RootElement.GetProperty("heldout_commands").GetInt32())
        throw new InvalidDataException("Raw peripheral held-out observations do not match the complete held-out command count.");
    foreach (var observation in fittedObservations)
    {
        foreach (var (side, channel, pin, inputProperty, dutyProperty, directionProperty) in new[]
                 { ("left", 1, "pd2", "left_input_percent", "left_duty_fraction", "left_direction_forward"),
                   ("right", 2, "pd3", "right_input_percent", "right_duty_fraction", "right_direction_forward") })
        {
            var inputScalar = observation.GetProperty(inputProperty).GetDouble();
            var inputEvent = new ControllerInputEvent("mcu-drive", observation.GetProperty("sample_sim_time_ns").GetInt64(),
                checked((ulong)(heldoutErrors.Count + 1)), $"uart.motor.{side}_percent", ControllerSignalKind.AnalogSample, inputScalar);
            var mapped = ControllerModelEvaluator.Evaluate(modelDocument, inputEvent, modelDocument.MaximumRules);
            var deterministicRepeat = ControllerModelEvaluator.Evaluate(modelDocument, inputEvent, modelDocument.MaximumRules);
            if (!mapped.SequenceEqual(deterministicRepeat)) throw new InvalidDataException("Raw peripheral candidate failed deterministic replay on a held-out observation.");
            var duty = mapped.Single(item => item.EndpointId == $"tim3.ch{channel}.duty_fraction");
            var direction = mapped.Single(item => item.EndpointId == $"gpio.{pin}.direction");
            var expectedDirection = observation.GetProperty(directionProperty).GetBoolean() ? 1d : 0d;
            if (direction.Scalar != expectedDirection) heldoutDirectionMismatches++;
            heldoutErrors.Add(Math.Abs(duty.Scalar - observation.GetProperty(dutyProperty).GetDouble()) * 100);
        }
    }
    if (heldoutDirectionMismatches != 0)
        throw new InvalidDataException($"Deterministic model direction outputs disagree with {heldoutDirectionMismatches} held-out GPIO observations.");
    foreach (var side in new[] { "left", "right" })
    {
        var fitted = peripheralFit.RootElement.GetProperty("motor_output_fit").GetProperty(side).GetProperty("heldout_error_percentage_points");
        var errors = fittedObservations.Select(observation =>
        {
            var input = observation.GetProperty($"{side}_input_percent").GetDouble();
            var expected = observation.GetProperty($"{side}_duty_fraction").GetDouble();
            var output = ControllerModelEvaluator.Evaluate(modelDocument,
                new ControllerInputEvent("mcu-drive", 0, 1, $"uart.motor.{side}_percent", ControllerSignalKind.AnalogSample, input), modelDocument.MaximumRules)
                .Single(item => item.EndpointId == $"tim3.ch{(side == "left" ? 1 : 2)}.duty_fraction").Scalar;
            return Math.Abs(output - expected) * 100;
        }).Order().ToArray();
        var p95 = errors[(int)Math.Ceiling(0.95 * errors.Length) - 1];
        if (Math.Abs(p95 - fitted.GetProperty("p95").GetDouble()) > 1e-8 || Math.Abs(errors[^1] - fitted.GetProperty("max").GetDouble()) > 1e-8)
            throw new InvalidDataException($"Deterministic model {side} held-out duty metrics differ from the raw peripheral fit.");
    }
}
else for (var observationIndex = 0; observationIndex < observations.Length; observationIndex++)
{
    var observation = observations[observationIndex];
    for (var channel = 0; channel < 2; channel++)
    {
        var inputScalar = observation.GetProperty("input")[channel].GetDouble();
        var observed = observation.GetProperty("observed_output")[channel].GetDouble();
        var endpoint = channel == 0 ? "uart.motor.left_percent" : "uart.motor.right_percent";
        var output = ControllerModelEvaluator.Evaluate(modelDocument,
            new ControllerInputEvent("mcu-drive", observation.GetProperty("sim_time_ns").GetInt64(),
                checked((ulong)(observationIndex * 2 + channel + 1)), endpoint, ControllerSignalKind.AnalogSample, inputScalar), modelDocument.MaximumRules).Single();
        var deterministicRepeat = ControllerModelEvaluator.Evaluate(modelDocument,
            new ControllerInputEvent("mcu-drive", observation.GetProperty("sim_time_ns").GetInt64(),
                checked((ulong)(observationIndex * 2 + channel + 1)), endpoint, ControllerSignalKind.AnalogSample, inputScalar), modelDocument.MaximumRules).Single();
        if (output != deterministicRepeat) throw new InvalidDataException("Model evaluator failed replay determinism on a held-out observation.");
        heldoutErrors.Add(Math.Abs(output.Scalar - observed) * 100);
    }
}
var heldoutSensorErrors = new List<double>();
if (sensorFit is not null)
{
    foreach (var observation in sensorReferenceObservations.Where(item => item.Split == "heldout"))
    {
        var inputScalar = observation.InputMeters;
        var observed = observation.OutputMm;
        var sequence = checked((ulong)(heldoutSensorErrors.Count + 1));
        var input = new ControllerInputEvent("mcu-drive", observation.SimTimeNs, sequence,
            "sensor.distance_m", ControllerSignalKind.AnalogSample, inputScalar, Unit: "meters");
        var output = ControllerModelEvaluator.Evaluate(modelDocument, input, modelDocument.MaximumRules).Single(item => item.EndpointId == "uart.sensor.distance_mm");
        var repeat = ControllerModelEvaluator.Evaluate(modelDocument, input, modelDocument.MaximumRules).Single(item => item.EndpointId == "uart.sensor.distance_mm");
        if (output != repeat) throw new InvalidDataException("Sensor candidate failed deterministic replay on held-out observations.");
        heldoutSensorErrors.Add(Math.Abs(output.Scalar - observed));
    }
}
if (peripheralFit is null && heldoutErrors.Count != evaluationRoot.GetProperty("heldout_duty_error_percentage_points").GetProperty("n").GetInt32())
    throw new InvalidDataException("Domain evaluator held-out observation count does not match the campaign evaluator.");
var sortedErrors = heldoutErrors.Order().ToArray();
var domainP95 = sortedErrors[(int)Math.Ceiling(0.95 * sortedErrors.Length) - 1];
var domainMax = sortedErrors[^1];
if (Math.Abs(domainP95 - validationMetrics["heldout_duty_error_p95_percentage_points"]) > 1e-8
    || Math.Abs(domainMax - validationMetrics["heldout_duty_error_max_percentage_points"]) > 1e-8)
    throw new InvalidDataException("Domain evaluator held-out error differs from the independently computed campaign result.");
var sortedSensorErrors = heldoutSensorErrors.Order().ToArray();
var domainSensorP95 = sortedSensorErrors.Length == 0 ? 0 : sortedSensorErrors[(int)Math.Ceiling(0.95 * sortedSensorErrors.Length) - 1];
var domainSensorMax = sortedSensorErrors.Length == 0 ? 0 : sortedSensorErrors[^1];
if (sensorFit is not null && (heldoutSensorErrors.Count != sensorFit.RootElement.GetProperty("heldout_error_mm").GetProperty("n").GetInt32()
    || Math.Abs(domainSensorP95 - sensorFit.RootElement.GetProperty("heldout_error_mm").GetProperty("p95").GetDouble()) > 1e-8
    || Math.Abs(domainSensorMax - sensorFit.RootElement.GetProperty("heldout_error_mm").GetProperty("max").GetDouble()) > 1e-8))
    throw new InvalidDataException("Domain evaluator sensor error differs from the independent fit result.");
var rejectedAsUnqualified = false;
try
{
    _ = new DeterministicModelBackend([new ValidatedModelAssignment("mcu-drive", profile, identity, modelDocument)]);
}
catch (InvalidDataException exception) when (exception.Message == "Only profiles explicitly labeled VALIDATED may execute.")
{
    rejectedAsUnqualified = true;
}
if (!rejectedAsUnqualified) throw new InvalidDataException("Candidate profile was not rejected specifically because its fidelity label is UNQUALIFIED.");

Directory.CreateDirectory(outputDirectory);
var modelPath = Path.Combine(outputDirectory, "candidate-model-UNQUALIFIED.json");
var profilePath = Path.Combine(outputDirectory, "candidate-profile-UNQUALIFIED.json");
await File.WriteAllBytesAsync(modelPath, JsonSerializer.SerializeToUtf8Bytes(modelDocument, jsonOptions));
await File.WriteAllBytesAsync(profilePath, JsonSerializer.SerializeToUtf8Bytes(profile, jsonOptions));
var persistedModel = JsonSerializer.Deserialize<ControllerModelDocument>(await File.ReadAllBytesAsync(modelPath), jsonOptions)
    ?? throw new InvalidDataException("Persisted candidate model could not be loaded.");
var persistedProfile = JsonSerializer.Deserialize<DeterministicControllerProfile>(await File.ReadAllBytesAsync(profilePath), jsonOptions)
    ?? throw new InvalidDataException("Persisted candidate profile could not be loaded.");
if (!ControllerProfileHashes.Matches(persistedProfile.ModelDocumentSha256, ControllerProfileHashes.Compute(persistedModel))
    || !ControllerProfileHashes.Matches(persistedProfile.Identity.InputEnvelopeSha256, ControllerProfileHashes.Compute(persistedProfile.Envelope)))
    throw new InvalidDataException("Persisted candidate artifacts failed their embedded content hashes.");
var persistedPreflightRejected = false;
try
{
    _ = new DeterministicModelBackend([new ValidatedModelAssignment("mcu-drive", persistedProfile, persistedProfile.Identity, persistedModel)]);
}
catch (InvalidDataException exception) when (exception.Message == "Only profiles explicitly labeled VALIDATED may execute.")
{
    persistedPreflightRejected = true;
}
if (!persistedPreflightRejected) throw new InvalidDataException("Persisted candidate profile did not fail closed for its UNQUALIFIED fidelity label.");
var validation = new
{
    status = "UNQUALIFIED",
    domainEvaluatorHeldoutOutputs = heldoutErrors.Count + heldoutSensorErrors.Count + heldoutEchoPulseErrors.Count + (mixedSensorEnvelopeReview?.InDomainValidReplies ?? 0) + heldoutLineBoundaryChecks.ExactResponses + heldoutUartGrammarChecks.ExactResponses + noEchoValidation.HeldoutStaleReplies,
    heldoutDirectionMismatches,
    supportedUartResponses = peripheralFit is null ? null : new
    {
        training_sequences = peripheralFit.RootElement.GetProperty("training_sequences").GetArrayLength(),
        heldout_sequences = heldoutUartResponseChecks.Sequences,
        heldout_ping_exact_matches = heldoutUartResponseChecks.Pings,
        heldout_motor_exact_matches = heldoutUartResponseChecks.Motors,
        heldout_move_rejection_exact_matches = heldoutUartResponseChecks.Moves,
        heldout_overlength_exact_matches = heldoutUartResponseChecks.Overlength,
        heldout_exact_match_count = heldoutUartResponseChecks.Pings + heldoutUartResponseChecks.Motors
            + heldoutUartResponseChecks.Moves + heldoutUartResponseChecks.Overlength
    },
    initialUartOutput = peripheralFit is null ? null : new
    {
        observed_response = initialUartBootValidation.Response,
        training_whole_sequences = initialUartBootValidation.TrainingSequences,
        heldout_whole_sequences = initialUartBootValidation.HeldoutSequences,
        heldout_exact_matches = initialUartBootValidation.HeldoutSequences,
        exact = true
    },
    uartResponseCausality = peripheralFit is null ? null : "PASS: paired trace responses do not precede host-monotonic or scheduled simulation input times",
    heldoutUartResponseTiming = lineBoundaryCampaignDirectory is null ? null : new
    {
        observations = heldoutUartTimingErrorsNs.Count,
        model_timestamp_inside_observed_response_bracket = heldoutUartTimingErrorsNs.Count(error => error == 0),
        model_timestamp_outside_bracket_p95_ns = heldoutUartTimingErrorsNs.Count == 0 ? (double?)null : Percentile(heldoutUartTimingErrorsNs.Select(value => (double)value).ToArray(), 0.95),
        model_timestamp_outside_bracket_max_ns = heldoutUartTimingErrorsNs.Count == 0 ? (long?)null : heldoutUartTimingErrorsNs.Max(),
        largest_observed_response_bracket_ns = heldoutUartTimingTickBoundsNs.Count == 0 ? (long?)null : heldoutUartTimingTickBoundsNs.Max(),
        acceptance = "PASS: model emits the response at the scheduled input timestamp; its maximum distance outside the measured response-time bracket is no greater than one observed physics-tick interval",
        scope = "held-out whole UART grammar and line-boundary traces; UART byte emission is bounded by completed Renode time boundaries, not measured at sub-tick precision"
    },
    deterministicReplay = "PASS",
    persistedArtifactRoundTrip = "PASS: model and profile deserialize with matching embedded hashes",
    runtimePreflight = "PASS: rejected specifically for UNQUALIFIED fidelity label after identity and artifact hash verification",
    heldoutDutyErrorPercentagePoints = new { n = heldoutErrors.Count, p95 = domainP95, max = domainMax },
    heldoutSensorResponseErrorMm = sensorFit is null ? null : new { n = heldoutSensorErrors.Count, p95 = domainSensorP95, max = domainSensorMax },
    heldoutEchoPulseResponse = echoPulseFit is null ? null : new
    {
        sequences = heldoutEchoResponseChecks.Sequences,
        observations = heldoutEchoPulseErrors.Count,
        training_sequences = echoPulseFit.RootElement.GetProperty("training_sequences").GetArrayLength(),
        training_observations = echoPulseFit.RootElement.GetProperty("training_observations").GetInt32(),
        grouped_training_cv_p95_mm = echoPulseFit.RootElement.GetProperty("grouped_training_cv").GetProperty("p95_error_mm").GetDouble(),
        heldout_raw_affine_p95_mm = echoPulseFit.RootElement.GetProperty("heldout_error_mm").GetProperty("p95").GetDouble(),
        uart_grammar_exact_matches = heldoutEchoResponseChecks.GrammarExact,
        uart_grammar_exact_fraction = validationMetrics["heldout_echo_uart_exact_grammar_fraction"],
        firmware_integer_mm_error_p95 = validationMetrics["heldout_echo_pulse_response_error_p95_mm"],
        firmware_integer_mm_error_max = validationMetrics["heldout_echo_pulse_response_error_max_mm"],
        declared_threshold_mm_p95 = echoPulseFit.RootElement.GetProperty("threshold_declared_before_fit_mm_p95").GetDouble(),
        query_after_pulse_causality = "PASS: every held-out UART response lower simulation-time bound follows its paired GPIO falling edge"
    },
    mixedHeldoutSensorEnvelope = mixedSensorEnvelopeReview,
    heldoutNoEchoState = noEchoCampaignDirectory is null ? null : new
    {
        training_whole_sequences = noEchoValidation.TrainingSequences,
        heldout_whole_sequences = noEchoValidation.HeldoutSequences,
        heldout_stale_reply_count = noEchoValidation.HeldoutStaleReplies,
        initial_invalid_response = Encoding.ASCII.GetString(noEchoValidation.InitialInvalidResponse ?? []),
        exact_invalid_reply_response = true,
        retained_last_valid_reading = true,
        independent_campaign_validation = "PASS_MEASURED_REFERENCE_ONLY"
    },
    heldoutControllerFaults = controllerFaultCampaignDirectory is null ? null : new
    {
        fault_code = "MCU_HALTED",
        training_sequences = faultCampaignTrainingSequences,
        heldout_sequences = heldoutMcuHaltSequences,
        heldout_sequences_with_no_uart_reply = heldoutMcuHaltSequences,
        heldout_sequences_with_latched_pwm_and_live_physics = heldoutMcuHaltSequences,
        modeled_behavior = "retain last peripheral outputs and suppress subsequent UART replies",
        route_fault_boundary = "GPIO route faults remain owned by the physical device/runtime path"
    },
    heldoutUartLineBoundary = lineBoundaryCampaignDirectory is null ? null : new
    {
        sequences = heldoutLineBoundaryChecks.Sequences,
        exact_responses = heldoutLineBoundaryChecks.ExactResponses,
        line_47_response = "ERR CMD",
        line_48_response = "ERR LINE",
        training_derived_exact_47_byte_line_rule = exactUartLineRules.Any(rule => rule.RequestBytes.Length == 47),
        command_grammar_sequences = heldoutUartGrammarChecks.Sequences,
        command_grammar_exact_responses = heldoutUartGrammarChecks.ExactResponses,
        source_firmware_sha256 = "5D5973A2E119167EF3C2EF18E8929E8AD9735AD65A63B639179256547575B6E9"
    },
    heldoutUartCommandGrammar = lineBoundaryCampaignDirectory is null ? null : new
    {
        exact_response_count = heldoutUartGrammarChecks.ExactResponses,
        status_reply = "STATUS MCU=OK UART=OK",
        range_error_for_positive_and_negative_out_of_range_motor_operands = true,
        malformed_operand_count_and_case_sensitive_token_replies = "ERR CMD"
    },
    runtimeEligibility = "REJECTED_BY_PROFILE_FIDELITY_LABEL",
    limits = profile.UnsupportedBehaviors
};
await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "candidate-validation-UNQUALIFIED.json"), JsonSerializer.SerializeToUtf8Bytes(validation, jsonOptions));

var qualificationBlockers = new List<string>();
if (peripheralFit is null || domainP95 > 2.0 || heldoutDirectionMismatches != 0)
    qualificationBlockers.Add("held-out PWM duty p95 must be at most 2 percentage points with exact direction GPIO outputs");
if (heldoutUartResponseChecks.Sequences < 2 || heldoutUartResponseChecks.Pings != heldoutUartResponseChecks.Sequences
    || heldoutUartResponseChecks.Motors < 20 || initialUartBootValidation.HeldoutSequences < 2)
    qualificationBlockers.Add("whole-sequence held-out startup, PING, and normal MOTOR UART responses are incomplete");
if (echoPulseFit is null || echoPulseModel is null
    || echoPulseFit.RootElement.GetProperty("status").GetString() != "PULSE_WIDTH_RESPONSE_FIT_PASS"
    || !echoPulseFit.RootElement.GetProperty("heldout_within_training_envelope").GetBoolean()
    || echoPulseFit.RootElement.GetProperty("grouped_training_cv").GetProperty("p95_error_mm").GetDouble() > 2.0
    || echoPulseFit.RootElement.GetProperty("heldout_error_mm").GetProperty("p95").GetDouble() > 2.0
    || heldoutEchoResponseChecks.GrammarExact != heldoutEchoResponseChecks.Replies
    || validationMetrics["heldout_echo_pulse_response_error_p95_mm"] > 2.0)
    qualificationBlockers.Add("whole-sequence held-out echo pulse response, exact UART grammar, or the predeclared 2 mm sensor threshold failed");
else
{
    var minimumPulseFor800Mm = (long)Math.Round(2 * 0.8 / 343d * 1_000_000_000d, MidpointRounding.AwayFromZero);
    var maximumPulseFor2500Mm = (long)Math.Round(2 * 2.5 / 343d * 1_000_000_000d, MidpointRounding.AwayFromZero);
    if (echoPulseModel.MinimumPulseWidthNs > minimumPulseFor800Mm || echoPulseModel.MaximumPulseWidthNs < maximumPulseFor2500Mm)
        qualificationBlockers.Add("measured pulse-width envelope does not cover the declared 0.8–2.5 m normal-operation range");
}
if (mixedSensorEnvelopeReview is null || mixedSensorEnvelopeReview.MatchedQueries < 8
    || mixedSensorEnvelopeReview.ValidReplies < 6 || mixedSensorEnvelopeReview.InitialInvalidReplies < 2
    || mixedSensorEnvelopeReview.InDomainValidReplies < 4 || mixedSensorEnvelopeReview.InDomainErrorP95Mm > 2.0
    || mixedSensorEnvelopeReview.OutOfDomainInputs.Any(item => item.RaycastDistanceMm is >= 800 and <= 2500))
    qualificationBlockers.Add("mixed held-out sensor behavior must meet the 2 mm gate in-domain and reject only inputs outside the declared range");
if (lineBoundaryCampaignDirectory is null || heldoutLineBoundaryChecks.Sequences < 2 || heldoutLineBoundaryChecks.ExactResponses < 4
    || heldoutUartGrammarChecks.Sequences < 2 || heldoutUartGrammarChecks.ExactResponses < 10)
    qualificationBlockers.Add("whole-sequence held-out UART line-boundary and supported command-grammar evidence is incomplete");
if (noEchoCampaignDirectory is null || noEchoValidation.HeldoutSequences < 2 || noEchoValidation.HeldoutStaleReplies < 4)
    qualificationBlockers.Add("whole-sequence held-out initial-invalid and stale-reading no-echo behavior is incomplete");
if (controllerFaultCampaignDirectory is null || heldoutMcuHaltSequences < 2)
    qualificationBlockers.Add("the separately supported MCU_HALTED latch transition lacks two held-out whole sequences");
if (heldoutUartTimingErrorsNs.Count < 52 || heldoutUartTimingErrorsNs.Max() > 8_333_334)
    qualificationBlockers.Add("held-out UART response-time brackets exceed the measured one-tick timing limit");

LoadedTrajectoryQualificationReview? trajectoryQualification = null;
if (loadedTrajectorySummaryPath is null || loadedTrajectoryBaselineModelPath is null)
    qualificationBlockers.Add("a hash-verified held-out loaded-rover trajectory comparison is required before profile qualification");
else if (loadedTrajectorySourceCampaignDirectory is null)
    qualificationBlockers.Add("the held-out trajectory source campaign directory is required for provenance validation");
else
    trajectoryQualification = ValidateLoadedTrajectoryEvidence(loadedTrajectorySummaryPath,
        loadedTrajectoryBaselineModelPath, loadedTrajectorySourceCampaignDirectory, modelDocument, jsonOptions);
if (trajectoryQualification is null)
    qualificationBlockers.Add("loaded-rover trajectory evidence did not verify against the candidate motor model");

var qualificationPassed = qualificationBlockers.Count == 0;
var qualifiedProfileId = "rover-validated-" + profileIdSeed[..16];
var qualificationReport = new
{
    status = qualificationPassed ? "VALIDATED_FOR_MEASURED_NORMAL_OPERATION_ENVELOPE" : "UNQUALIFIED",
    fidelityLabel = qualificationPassed ? "VALIDATED" : "UNQUALIFIED",
    profileId = qualificationPassed ? qualifiedProfileId : profile.ProfileId,
    candidateModelSha256 = modelHash,
    supportedSensorRangeMeters = new { minimum = 0.8, maximum = 2.5 },
    sensorOutOfDomainPolicy = "Reject with E_CONTROLLER_MODEL_UNSUPPORTED and require restart in exact firmware mode; never extrapolate.",
    qualificationBlockers,
    motorDutyErrorP95PercentagePoints = domainP95,
    directionMismatches = heldoutDirectionMismatches,
    echoPulseTrainingEnvelopeNs = echoPulseModel is null ? null : new { minimum = echoPulseModel.MinimumPulseWidthNs, maximum = echoPulseModel.MaximumPulseWidthNs },
    echoPulseHeldoutErrorP95Mm = echoPulseFit?.RootElement.GetProperty("heldout_error_mm").GetProperty("p95").GetDouble(),
    mixedInDomainSensorErrorP95Mm = mixedSensorEnvelopeReview?.InDomainErrorP95Mm,
    mixedRejectedOutOfDomainReplies = mixedSensorEnvelopeReview?.OutOfDomainInputs.Count,
    loadedTrajectory = trajectoryQualification
};
string? validatedProfileFileSha256 = null;
string? validatedModelFileSha256 = null;
if (qualificationPassed)
{
    var validatedProfile = profile with
    {
        ProfileId = qualifiedProfileId,
        SupportedBehaviors = supportedBehaviors.Append("two whole-sequence held-out loaded-rover trajectories pass the declared 0.02 m fidelity gate").ToArray(),
        FidelityLabel = "VALIDATED"
    };
    var validatedProfilePath = Path.Combine(outputDirectory, "candidate-profile-VALIDATED.json");
    var validatedModelPath = Path.Combine(outputDirectory, "candidate-model-VALIDATED.json");
    await File.WriteAllBytesAsync(validatedProfilePath, JsonSerializer.SerializeToUtf8Bytes(validatedProfile, jsonOptions));
    await File.WriteAllBytesAsync(validatedModelPath, JsonSerializer.SerializeToUtf8Bytes(modelDocument, jsonOptions));
    var validatedProfileHash = HashFile(validatedProfilePath);
    validatedProfileFileSha256 = validatedProfileHash;
    validatedModelFileSha256 = HashFile(validatedModelPath);
    var validatedAssignment = ControllerProfileArtifactLoader.LoadAssignment("mcu-drive",
        new ProjectControllerProfileReference(validatedProfile.ProfileId, validatedProfileHash),
        validatedProfilePath, validatedModelPath, identity);
    await using var preflightedBackend = new DeterministicModelBackend([validatedAssignment]);
    await preflightedBackend.ResetAsync(0);
}
await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "candidate-qualification-report.json"),
    JsonSerializer.SerializeToUtf8Bytes(new
    {
        qualification = qualificationReport,
        validatedProfileFileSha256,
        validatedModelFileSha256
    }, jsonOptions));

Console.WriteLine(JsonSerializer.Serialize(new { profileId = qualificationPassed ? qualifiedProfileId : profile.ProfileId,
    fidelityLabel = qualificationReport.fidelityLabel, qualification = qualificationReport.status, modelHash,
    domainEvaluatorHeldoutOutputs = heldoutErrors.Count + heldoutSensorErrors.Count + heldoutEchoPulseErrors.Count + (mixedSensorEnvelopeReview?.InDomainValidReplies ?? 0) + heldoutLineBoundaryChecks.ExactResponses + heldoutUartGrammarChecks.ExactResponses + noEchoValidation.HeldoutStaleReplies,
    heldoutDutyErrorP95PercentagePoints = domainP95,
    heldoutSensorResponseErrorP95Mm = sensorFit is null ? (double?)null : domainSensorP95,
    heldoutEchoPulseResponseErrorP95Mm = echoPulseFit is null ? (double?)null : Percentile(heldoutEchoPulseErrors, 0.95), outputDirectory }));
return 0;

static string HashFile(string path) => HashBytes(File.ReadAllBytes(path));
static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
static LoadedTrajectoryQualificationReview ValidateLoadedTrajectoryEvidence(string summaryPath, string baselineModelPath,
    string campaignDirectory, ControllerModelDocument candidateModel, JsonSerializerOptions jsonOptions)
{
    using var summary = JsonDocument.Parse(File.ReadAllBytes(summaryPath));
    var root = summary.RootElement;
    if (root.GetProperty("status").GetString() != "CHARACTERIZATION_ONLY_PARTIAL_TRAJECTORY_PASS"
        || root.GetProperty("candidate_fidelity_label").GetString() != "UNQUALIFIED"
        || root.GetProperty("production_profile_preflight_changed").GetBoolean())
        throw new InvalidDataException("Loaded-trajectory evidence has an unexpected status or changed production profile preflight.");
    var baselineModel = JsonSerializer.Deserialize<ControllerModelDocument>(File.ReadAllBytes(baselineModelPath), jsonOptions)
        ?? throw new InvalidDataException("Loaded-trajectory baseline model is empty.");
    var sequences = root.GetProperty("sequences").EnumerateArray().ToArray();
    if (sequences.Length != 2) throw new InvalidDataException("Loaded-trajectory qualification requires both whole held-out sequences.");
    var baselineModelHash = ControllerProfileHashes.Compute(baselineModel);
    if (sequences.Any(sequence => !ControllerProfileHashes.Matches(sequence.GetProperty("candidate_model_sha256").GetString()!, baselineModelHash)))
        throw new InvalidDataException("Loaded-trajectory evidence is not bound to the supplied baseline candidate model.");
    var baselineSurfaceHash = ComputeMotorSurfaceHash(baselineModel, jsonOptions);
    var candidateSurfaceHash = ComputeMotorSurfaceHash(candidateModel, jsonOptions);
    if (!ControllerProfileHashes.Matches(baselineSurfaceHash, candidateSurfaceHash))
        throw new InvalidDataException("Loaded-trajectory evidence uses a different motor/UART/fault surface from the candidate being qualified.");

    var campaignRoot = Path.GetFullPath(campaignDirectory);
    var sequenceManifestPath = Path.Combine(campaignRoot, "sequence-manifest.json");
    var sequenceManifestHash = HashFile(sequenceManifestPath);
    using var sequenceManifest = JsonDocument.Parse(File.ReadAllBytes(sequenceManifestPath));
    var maximumDisplacementErrorM = 0d;
    var maximumPositionErrorM = 0d;
    var totalAlignedSamples = 0;
    foreach (var sequence in sequences)
    {
        var sequenceId = sequence.GetProperty("sequence_id").GetString()!;
        if (sequence.GetProperty("source_sequence_split").GetString() != "heldout"
            || !StringComparer.Ordinal.Equals(sequence.GetProperty("source_sequence_manifest_sha256").GetString(), sequenceManifestHash)
            || sequence.GetProperty("trajectory_gate").GetString() != "PASS"
            || sequence.GetProperty("exact_actuator_state_changes").GetInt32() != sequence.GetProperty("model_actuator_state_changes").GetInt32())
            throw new InvalidDataException($"{sequenceId}: loaded-trajectory sequence is not a passing complete held-out comparison.");
        var sourceId = sequence.GetProperty("source_sequence_id").GetString();
        var sourceRow = sequenceManifest.RootElement.EnumerateArray().SingleOrDefault(row => row.GetProperty("sequence_id").GetString() == sourceId);
        if (sourceRow.ValueKind != JsonValueKind.Object || sourceRow.GetProperty("split").GetString() != "heldout"
            || !ControllerProfileHashes.Matches(sourceRow.GetProperty("trace_sha256").GetString()!, sequence.GetProperty("heldout_trace_sha256").GetString()!))
            throw new InvalidDataException($"{sequenceId}: loaded-trajectory source trace is not authenticated by its C01 manifest.");
        var tracePath = Path.Combine(campaignRoot, sourceRow.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(HashFile(tracePath), sequence.GetProperty("heldout_trace_sha256").GetString()!))
            throw new InvalidDataException($"{sequenceId}: loaded-trajectory source trace hash mismatch.");

        var sequenceDirectory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(summaryPath))!, sequenceId);
        foreach (var (fileName, propertyName) in new[]
                 { ("exact-actuation.csv", "exact_actuator_trace_sha256"), ("model-actuation.csv", "model_actuator_trace_sha256"),
                   ("godot-exact-trajectory.csv", "exact_trajectory_sha256"), ("godot-model-trajectory.csv", "model_trajectory_sha256"),
                   ("godot-exact.log", "exact_godot_log_sha256"), ("godot-model.log", "model_godot_log_sha256") })
        {
            var path = Path.Combine(sequenceDirectory, fileName);
            if (!ControllerProfileHashes.Matches(HashFile(path), sequence.GetProperty(propertyName).GetString()!))
                throw new InvalidDataException($"{sequenceId}: loaded-trajectory source artifact hash mismatch: {fileName}.");
        }
        var allowedDisplacementM = sequence.GetProperty("allowed_displacement_difference_m").GetDouble();
        var displacementErrorM = sequence.GetProperty("net_displacement_difference_m").GetDouble();
        var positionErrorM = sequence.GetProperty("chassis_position_error_max_m").GetDouble();
        if (displacementErrorM > allowedDisplacementM || positionErrorM > 0.02
            || sequence.GetProperty("aligned_samples").GetInt32() < 20
            || sequence.GetProperty("exact_samples").GetInt32() != sequence.GetProperty("model_samples").GetInt32())
            throw new InvalidDataException($"{sequenceId}: loaded-trajectory agreement does not meet the held-out fidelity gate.");
        maximumDisplacementErrorM = Math.Max(maximumDisplacementErrorM, displacementErrorM);
        maximumPositionErrorM = Math.Max(maximumPositionErrorM, positionErrorM);
        totalAlignedSamples += sequence.GetProperty("aligned_samples").GetInt32();
    }
    return new LoadedTrajectoryQualificationReview(sequences.Length, totalAlignedSamples,
        maximumDisplacementErrorM, maximumPositionErrorM, baselineModelHash, candidateSurfaceHash);
}

static string ComputeMotorSurfaceHash(ControllerModelDocument model, JsonSerializerOptions jsonOptions)
{
    var surface = new
    {
        model.ModelId, model.MaximumRules, model.Rules, model.MaximumUartLineBytes, model.UartCommands,
        model.UartInputEndpointId, model.OverlengthUartResponseBytes, model.InitialUartOutputBytes,
        model.ExactUartLines, model.FaultRules
    };
    return HashBytes(JsonSerializer.SerializeToUtf8Bytes(surface, jsonOptions));
}
static List<string> ExtractDistanceReplies(IReadOnlyList<JsonDocument> records)
{
    var bytes = records.Where(record => record.RootElement.GetProperty("RecordType").GetString() == "UartChunk"
            && record.RootElement.GetProperty("Direction").GetString() == "mcu_to_controller")
        .SelectMany(record => Convert.FromBase64String(record.RootElement.GetProperty("BytesBase64").GetString()!)).ToArray();
    return Encoding.ASCII.GetString(bytes).Split('\n').Select(line => line.TrimEnd('\r'))
        .Where(line => line.StartsWith("DIST mm=", StringComparison.Ordinal)).ToList();
}
static MixedSensorEnvelopeReview ValidateMixedSensorEnvelope(string campaignDirectory, string firmwarePath,
    ControllerEchoPulseRule pulseModel)
{
    var validationPath = Path.Combine(campaignDirectory, "mixed-campaign-validation.json");
    var manifestPath = Path.Combine(campaignDirectory, "sequence-manifest.json");
    var validationBytes = File.ReadAllBytes(validationPath);
    using var validation = JsonDocument.Parse(validationBytes);
    var validationRoot = validation.RootElement;
    var manifestBytes = File.ReadAllBytes(manifestPath);
    if (validationRoot.GetProperty("status").GetString() != "PASS_EXACT_FIRMWARE_MIXED_CONTROLLER_SENSOR_REFERENCE_ONLY"
        || validationRoot.GetProperty("model_qualification").GetString() != "NOT_ESTABLISHED"
        || !ControllerProfileHashes.Matches(validationRoot.GetProperty("sequence_manifest_sha256").GetString()!, HashBytes(manifestBytes))
        || !ControllerProfileHashes.Matches(validationRoot.GetProperty("firmware_sha256").GetString()!, HashFile(firmwarePath)))
        throw new InvalidDataException("Mixed exact-firmware campaign validation is stale, failed, or bound to another firmware image.");
    using var manifest = JsonDocument.Parse(manifestBytes);
    var rows = manifest.RootElement.EnumerateArray().ToArray();
    var trainingRows = rows.Where(row => row.GetProperty("split").GetString() == "training").ToArray();
    var heldoutRows = rows.Where(row => row.GetProperty("split").GetString() == "heldout").ToArray();
    if (trainingRows.Length != 4 || heldoutRows.Length != 2)
        throw new InvalidDataException("Mixed sensor comparison requires four training and two whole held-out sequences.");
    var independentSequences = validationRoot.GetProperty("sequences").EnumerateArray()
        .ToDictionary(row => row.GetProperty("sequence_id").GetString()!, StringComparer.Ordinal);
    var errors = new List<double>();
    var outOfDomain = new List<MixedSensorOutOfDomainInput>();
    var validReplies = 0;
    var invalidReplies = 0;
    var matchedQueries = 0;
    foreach (var row in heldoutRows)
    {
        var sequenceId = row.GetProperty("sequence_id").GetString()!;
        if (!ControllerProfileHashes.Matches(row.GetProperty("firmware_sha256").GetString()!, HashFile(firmwarePath)))
            throw new InvalidDataException($"{sequenceId}: mixed held-out firmware does not match the selected ELF.");
        var tracePath = Path.Combine(campaignDirectory, row.GetProperty("trace").GetString()!);
        var evaluationPath = Path.Combine(campaignDirectory, row.GetProperty("sensor_evaluation").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath))
            || !ControllerProfileHashes.Matches(row.GetProperty("sensor_evaluation_sha256").GetString()!, HashFile(evaluationPath)))
            throw new InvalidDataException($"{sequenceId}: mixed held-out trace or sensor evaluation hash mismatch.");
        if (!independentSequences.TryGetValue(sequenceId, out var independentlyValidated)
            || !ControllerProfileHashes.Matches(independentlyValidated.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException($"{sequenceId}: independent campaign validation does not match its sensor trace.");

        using var sensorEvaluation = JsonDocument.Parse(File.ReadAllBytes(evaluationPath));
        if (sensorEvaluation.RootElement.GetProperty("status").GetString() != "REFERENCE_FIRMWARE_OBSERVATION_NOT_MODEL_QUALIFICATION")
            throw new InvalidDataException($"{sequenceId}: sensor evaluation has an unexpected qualification label.");
        var inputs = ReadUartLines(tracePath, "controller_to_mcu");
        var outputs = ReadUartLines(tracePath, "mcu_to_controller")
            .Where(line => !Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (inputs.Count != outputs.Length) throw new InvalidDataException($"{sequenceId}: mixed UART inputs and outputs do not pair one-to-one.");
        var sensorIndexes = Enumerable.Range(0, inputs.Count)
            .Where(index => inputs[index].Bytes.AsSpan().SequenceEqual("SENSOR?"u8)).ToArray();
        var references = sensorEvaluation.RootElement.GetProperty("replies").EnumerateArray().ToArray();
        if (sensorIndexes.Length != references.Length || sensorIndexes.Length < 3)
            throw new InvalidDataException($"{sequenceId}: mixed sensor query/reference counts differ or lack three queries.");
        var edges = File.ReadLines(tracePath).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            var rises = edges.Where(item => item.RootElement.GetProperty("RecordType").GetString() == "DistanceEchoEdge"
                    && item.RootElement.GetProperty("High").GetBoolean())
                .Select(item => item.RootElement.GetProperty("SimTimeNs").GetInt64()).ToHashSet();
            var falls = edges.Where(item => item.RootElement.GetProperty("RecordType").GetString() == "DistanceEchoEdge"
                    && !item.RootElement.GetProperty("High").GetBoolean())
                .Select(item => item.RootElement.GetProperty("SimTimeNs").GetInt64()).ToHashSet();
            for (var query = 0; query < sensorIndexes.Length; query++)
            {
                var index = sensorIndexes[query];
                var input = inputs[index];
                var output = outputs[index];
                VerifyUartCausality(input, output, sequenceId);
                var reference = references[query];
                var response = Encoding.ASCII.GetString(output.Bytes);
                var match = Regex.Match(response, "^DIST mm=(?<mm>[0-9]+) valid=(?<valid>[01])$", RegexOptions.CultureInvariant);
                if (!match.Success) throw new InvalidDataException($"{sequenceId}: mixed held-out SENSOR? reply grammar differs from exact firmware.");
                var actualMm = int.Parse(match.Groups["mm"].Value, CultureInfo.InvariantCulture);
                var valid = match.Groups["valid"].Value == "1";
                if (valid != reference.GetProperty("valid").GetBoolean() || actualMm != reference.GetProperty("reply_mm").GetInt32())
                    throw new InvalidDataException($"{sequenceId}: sensor evaluator and raw UART response disagree.");
                matchedQueries++;
                if (!valid)
                {
                    var expectedInitial = pulseModel.InitialInvalidResponseBytes;
                    if (expectedInitial is null || !output.Bytes.Append((byte)'\n').SequenceEqual(expectedInitial))
                        throw new InvalidDataException($"{sequenceId}: initial invalid sensor reply differs from the training-derived model bytes.");
                    invalidReplies++;
                    continue;
                }

                validReplies++;
                var riseNs = reference.GetProperty("reference_sample_sim_time_ns").GetInt64();
                var fallNs = reference.GetProperty("reference_echo_fall_sim_time_ns").GetInt64();
                if (!rises.Contains(riseNs) || !falls.Contains(fallNs) || fallNs <= riseNs
                    || output.SimTimeLowerBoundNs < fallNs)
                    throw new InvalidDataException($"{sequenceId}: mixed sensor reply is not causally after its exact GPIO echo pulse.");
                var widthNs = fallNs - riseNs;
                if (widthNs < pulseModel.MinimumPulseWidthNs || widthNs > pulseModel.MaximumPulseWidthNs)
                {
                    var rejected = false;
                    try { _ = ControllerModelEvaluator.EvaluateEchoPulse(pulseModel, widthNs); }
                    catch (InvalidDataException) { rejected = true; }
                    if (!rejected) throw new InvalidDataException("The controller evaluator extrapolated an echo pulse outside its certified envelope.");
                    outOfDomain.Add(new MixedSensorOutOfDomainInput(sequenceId, widthNs, actualMm,
                        reference.GetProperty("raycast_distance_mm").GetDouble()));
                    continue;
                }

                var predictedMm = ControllerModelEvaluator.EvaluateEchoPulse(pulseModel, widthNs);
                var responseText = Encoding.ASCII.GetString(output.Bytes);
                var responseSuffix = pulseModel.ResponseSuffix.TrimEnd('\n');
                if (!responseText.StartsWith(pulseModel.ResponsePrefix, StringComparison.Ordinal)
                    || !responseText.EndsWith(responseSuffix, StringComparison.Ordinal))
                    throw new InvalidDataException($"{sequenceId}: in-domain model sensor response does not match the measured UART grammar.");
                var valueLength = responseText.Length - pulseModel.ResponsePrefix.Length - responseSuffix.Length;
                var valueText = valueLength <= 0 ? string.Empty : responseText.Substring(pulseModel.ResponsePrefix.Length, valueLength);
                if (!long.TryParse(valueText, NumberStyles.None, CultureInfo.InvariantCulture, out var modeledMm))
                    throw new InvalidDataException($"{sequenceId}: in-domain model sensor response has a malformed integer value.");
                var responseErrorMm = Math.Abs(predictedMm - actualMm);
                var runtimeValueErrorMm = Math.Abs(modeledMm - actualMm);
                if (responseErrorMm > 2.0 || runtimeValueErrorMm > 2.0)
                    throw new InvalidDataException($"{sequenceId}: in-domain model sensor value exceeds the predeclared 2 mm tolerance.");
                errors.Add(responseErrorMm);
            }
        }
        finally
        {
            foreach (var edge in edges) edge.Dispose();
        }
    }
    if (matchedQueries < 6 || validReplies < 4 || invalidReplies < 2 || errors.Count == 0)
        throw new InvalidDataException("Mixed held-out sensor comparison lacks the required exact invalid and valid responses.");
    var p95 = Percentile(errors, 0.95);
    if (p95 > 2.0) throw new InvalidDataException("In-envelope mixed held-out sensor response error exceeds the predeclared 2 mm p95 gate.");
    var status = outOfDomain.Count == 0 ? "PASS_IN_DOMAIN_NO_EXTRAPOLATION_REQUIRED" : "PARTIAL_IN_DOMAIN_PASS_WITH_FAIL_CLOSED_OUT_OF_DOMAIN_INPUTS";
    return new MixedSensorEnvelopeReview(status, heldoutRows.Length, matchedQueries, validReplies, invalidReplies,
        errors.Count, p95, errors.Max(), pulseModel.MinimumPulseWidthNs, pulseModel.MaximumPulseWidthNs, outOfDomain);
}

static (double Gain, double Offset) FitEchoPulse(IReadOnlyList<EchoPulseObservation> observations)
{
    if (observations.Count < 2) throw new InvalidDataException("Echo pulse training set is too small to fit a response.");
    return FitAffine(observations.Select(item => ((double)item.PulseWidthNs, item.ReplyMm)).ToArray());
}
static void VerifySensorFitWasLearnedFromTraining(JsonElement fit, IReadOnlyList<SensorReferenceObservation> observations, ControllerModelRule rule)
{
    const double thresholdMm = 2.0;
    if (fit.GetProperty("threshold_declared_before_fit_mm_p95").GetDouble() != thresholdMm
        || fit.GetProperty("selected_operator").GetString() != "affine" || rule.Operator != ControllerModelOperator.Affine)
        throw new InvalidDataException("Sensor candidate did not use the predeclared threshold and selected affine operator.");
    var training = observations.Where(item => item.Split == "training").ToArray();
    var heldout = observations.Where(item => item.Split == "heldout").ToArray();
    var trainingGroups = training.Select(item => item.SequenceId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    if (trainingGroups.Length != 4 || heldout.Select(item => item.SequenceId).Distinct(StringComparer.Ordinal).Count() != 2)
        throw new InvalidDataException("Sensor campaign requires four training and two whole held-out sequences.");
    var affineErrors = new List<double>();
    var constantErrors = new List<double>();
    foreach (var sequenceId in trainingGroups)
    {
        var foldTrain = training.Where(item => item.SequenceId != sequenceId).ToArray();
        var foldValid = training.Where(item => item.SequenceId == sequenceId).ToArray();
        var constantOffset = foldTrain.Average(item => item.OutputMm);
        constantErrors.AddRange(foldValid.Select(item => Math.Abs(item.OutputMm - constantOffset)));
        var (foldGain, foldOffset) = FitAffine(foldTrain.Select(item => (item.InputMeters, item.OutputMm)).ToArray());
        affineErrors.AddRange(foldValid.Select(item => Math.Abs(item.OutputMm - (foldGain * item.InputMeters + foldOffset))));
    }
    var constantP95 = Percentile(constantErrors, 0.95);
    var affineP95 = Percentile(affineErrors, 0.95);
    var candidateSelection = fit.GetProperty("candidate_selection");
    if (Math.Abs(constantP95 - candidateSelection.GetProperty("constant").GetProperty("grouped_training_cv").GetProperty("p95_error_mm").GetDouble()) > 1e-6
        || Math.Abs(affineP95 - candidateSelection.GetProperty("affine").GetProperty("grouped_training_cv").GetProperty("p95_error_mm").GetDouble()) > 1e-6
        || constantP95 <= thresholdMm || affineP95 > thresholdMm)
        throw new InvalidDataException("Independent grouped sensor cross-validation disagrees with model selection or the 2 mm gate.");
    var (gain, offset) = FitAffine(training.Select(item => (item.InputMeters, item.OutputMm)).ToArray());
    var minimumInput = training.Min(item => item.InputMeters);
    var maximumInput = training.Max(item => item.InputMeters);
    if (Math.Abs(gain - rule.Gain) > 1e-6 || Math.Abs(offset - rule.Offset) > 1e-6
        || Math.Abs(minimumInput - rule.MinimumInput) > 1e-9 || Math.Abs(maximumInput - rule.MaximumInput) > 1e-9)
        throw new InvalidDataException("Sensor model parameters or input envelope were not fitted from the training observations.");
    var heldoutErrors = heldout.Select(item => Math.Abs(item.OutputMm - (gain * item.InputMeters + offset))).ToArray();
    var heldoutP95 = Percentile(heldoutErrors, 0.95);
    var heldoutMax = heldoutErrors.Max();
    var recordedHeldout = fit.GetProperty("heldout_error_mm");
    if (heldout.Any(item => item.InputMeters < minimumInput || item.InputMeters > maximumInput)
        || heldoutP95 > thresholdMm
        || Math.Abs(heldoutP95 - recordedHeldout.GetProperty("p95").GetDouble()) > 1e-6
        || Math.Abs(heldoutMax - recordedHeldout.GetProperty("max").GetDouble()) > 1e-6)
        throw new InvalidDataException("Independent sensor held-out replay disagrees with the candidate or exceeds its envelope/gate.");
}
static void AddOrVerifyTrainingExactLine(ControllerUartExactLineRule candidate,
    ICollection<ControllerUartExactLineRule> learnedRules, string label)
{
    var existing = learnedRules.SingleOrDefault(rule => rule.RequestBytes.AsSpan().SequenceEqual(candidate.RequestBytes));
    if (existing is null) learnedRules.Add(candidate);
    else if (!existing.ResponseBytes.AsSpan().SequenceEqual(candidate.ResponseBytes))
        throw new InvalidDataException($"Training {label} request/response pairs are inconsistent.");
}

static (double Gain, double Offset) FitAffine(IReadOnlyList<(double InputMeters, double OutputMm)> points)
{
    if (points.Count == 0) throw new InvalidDataException("Sensor fitting fold is empty.");
    var meanInput = points.Average(point => point.InputMeters);
    var meanOutput = points.Average(point => point.OutputMm);
    var denominator = points.Sum(point => Math.Pow(point.InputMeters - meanInput, 2));
    if (denominator <= 0) throw new InvalidDataException("Sensor training input has no range variation.");
    var gain = points.Sum(point => (point.InputMeters - meanInput) * (point.OutputMm - meanOutput)) / denominator;
    return (gain, meanOutput - gain * meanInput);
}
static double Percentile(IReadOnlyList<double> values, double percentile)
{
    if (values.Count == 0) throw new InvalidDataException("Cannot compute error percentile from an empty set.");
    var sorted = values.Order().ToArray();
    return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
}
static (byte[] InitialInvalidResponse, int TrainingSequences, int HeldoutSequences, int HeldoutStaleReplies)
    ValidateNoEchoCampaign(string campaignDirectory, string firmwarePath)
{
    using var validation = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(campaignDirectory, "campaign-validation.json")));
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(campaignDirectory, "sequence-manifest.json")));
    var validationRoot = validation.RootElement;
    if (validationRoot.GetProperty("status").GetString() != "PASS_MEASURED_REFERENCE_ONLY")
        throw new InvalidDataException("No-echo reference campaign has not passed its independent validator.");
    var rows = manifest.RootElement.EnumerateArray().ToArray();
    var trainingRows = rows.Where(row => row.GetProperty("split").GetString() == "training").ToArray();
    var heldoutRows = rows.Where(row => row.GetProperty("split").GetString() == "heldout").ToArray();
    if (trainingRows.Length < 2 || heldoutRows.Length < 2 || rows.Any(row => !ControllerProfileHashes.Matches(
            validationRoot.GetProperty("firmware_sha256").GetString()!, row.GetProperty("firmware_sha256").GetString()!)))
        throw new InvalidDataException("No-echo campaign requires at least two whole training/held-out sequences from one firmware image.");
    var validationSequences = validationRoot.GetProperty("sequences").EnumerateArray().ToDictionary(
        row => row.GetProperty("sequence_id").GetString()!, StringComparer.Ordinal);
    var firmwareHash = HashFile(firmwarePath);
    var initialInvalidByTraining = new HashSet<string>(StringComparer.Ordinal);
    var trainingInvalidSequences = new HashSet<string>(StringComparer.Ordinal);
    var heldoutInvalidSequences = new HashSet<string>(StringComparer.Ordinal);
    var heldoutStaleReplies = 0;
    foreach (var row in rows.OrderBy(row => row.GetProperty("split").GetString() == "training" ? 0 : 1))
    {
        var sequenceId = row.GetProperty("sequence_id").GetString()!;
        var sequenceSplit = row.GetProperty("split").GetString()!;
        if (!ControllerProfileHashes.Matches(firmwareHash, row.GetProperty("firmware_sha256").GetString()!))
            throw new InvalidDataException($"{sequenceId}: no-echo trace firmware differs from the selected ELF.");
        var tracePath = Path.Combine(campaignDirectory, row.GetProperty("trace").GetString()!);
        var evaluationPath = Path.Combine(campaignDirectory, row.GetProperty("evaluation").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath))
            || !ControllerProfileHashes.Matches(row.GetProperty("evaluation_sha256").GetString()!, HashFile(evaluationPath)))
            throw new InvalidDataException($"{sequenceId}: no-echo trace/evaluation hash mismatch.");
        if (!validationSequences.TryGetValue(sequenceId, out var validatedSequence)
            || !ControllerProfileHashes.Matches(validatedSequence.GetProperty("trace_sha256").GetString()!, HashFile(tracePath))
            || !ControllerProfileHashes.Matches(validatedSequence.GetProperty("evaluation_sha256").GetString()!, HashFile(evaluationPath)))
            throw new InvalidDataException($"{sequenceId}: independent no-echo validation does not match its manifest trace.");
        using var sequenceEvaluation = JsonDocument.Parse(File.ReadAllBytes(evaluationPath));
        if (sequenceEvaluation.RootElement.GetProperty("status").GetString() != "MEASURED_EXACT_FIRMWARE_NO_ECHO_BEHAVIOR_NOT_MODEL_QUALIFICATION")
            throw new InvalidDataException($"{sequenceId}: no-echo trace is not marked as measured exact-firmware behavior.");
        var samples = new List<(long TimeNs, double Meters)>();
        var edges = new List<(long TimeNs, bool High)>();
        foreach (var line in File.ReadLines(tracePath))
        {
            using var record = JsonDocument.Parse(line);
            var root = record.RootElement;
            if (root.GetProperty("RecordType").GetString() == "DistanceSample")
                samples.Add((root.GetProperty("SimTimeNs").GetInt64(), root.GetProperty("DistanceMeters").GetDouble()));
            else if (root.GetProperty("RecordType").GetString() == "DistanceEchoEdge")
                edges.Add((root.GetProperty("SimTimeNs").GetInt64(), root.GetProperty("High").GetBoolean()));
        }
        samples.Sort((left, right) => left.TimeNs.CompareTo(right.TimeNs));
        var noEchoTimes = samples.Where(sample => sample.Meters < 0).Select(sample => sample.TimeNs).ToArray();
        var inRange = samples.Where(sample => sample.Meters >= 0).ToArray();
        var rises = edges.Where(edge => edge.High).OrderBy(edge => edge.TimeNs).ToArray();
        var falls = edges.Where(edge => !edge.High).OrderBy(edge => edge.TimeNs).ToArray();
        if (noEchoTimes.Length == 0 || inRange.Length == 0 || rises.Length != inRange.Length || falls.Length != inRange.Length)
            throw new InvalidDataException($"{sequenceId}: trace lacks one complete pulse per valid sample and at least one no-echo sample.");
        var pulses = new List<(long RiseNs, long FallNs)>();
        for (var index = 0; index < inRange.Length; index++)
        {
            var expectedWidthNs = (long)Math.Round(2 * inRange[index].Meters / 343d * 1_000_000_000d, MidpointRounding.AwayFromZero);
            if (rises[index].TimeNs != inRange[index].TimeNs || falls[index].TimeNs != rises[index].TimeNs + expectedWidthNs)
                throw new InvalidDataException($"{sequenceId}: measured PA0 pulse does not match the sampled raycast time/width.");
            pulses.Add((rises[index].TimeNs, falls[index].TimeNs));
        }
        var inputs = ReadUartLines(tracePath, "controller_to_mcu");
        var outputs = ReadUartLines(tracePath, "mcu_to_controller")
            .Where(line => !Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (inputs.Count != outputs.Length) throw new InvalidDataException($"{sequenceId}: input/output UART lines do not pair one-to-one.");
        var sequenceInvalid = false;
        var sequenceStale = 0;
        for (var index = 0; index < inputs.Count; index++)
        {
            VerifyUartCausality(inputs[index], outputs[index], sequenceId);
            if (!inputs[index].Bytes.AsSpan().SequenceEqual("SENSOR?"u8)) continue;
            var response = Encoding.ASCII.GetString(outputs[index].Bytes);
            var responseMatch = Regex.Match(response, "^DIST mm=(?<mm>[0-9]+) valid=(?<valid>[01])$", RegexOptions.CultureInvariant);
            if (!responseMatch.Success) throw new InvalidDataException($"{sequenceId}: SENSOR? reply grammar differs from measured firmware output.");
            var responseLowerBound = outputs[index].SimTimeLowerBoundNs;
            var completedPulses = pulses.Where(pulse => pulse.FallNs <= responseLowerBound).ToArray();
            var noEchoObserved = noEchoTimes.Any(time => time <= responseLowerBound);
            var mm = int.Parse(responseMatch.Groups["mm"].Value, CultureInfo.InvariantCulture);
            var valid = responseMatch.Groups["valid"].Value == "1";
            if (completedPulses.Length == 0)
            {
                if (valid || mm != 0) throw new InvalidDataException($"{sequenceId}: firmware emitted a valid sensor value before any complete echo pulse.");
                sequenceInvalid = true;
                var lineBytes = Encoding.ASCII.GetBytes(response + "\n");
                if (sequenceSplit == "training") initialInvalidByTraining.Add(Convert.ToHexString(lineBytes));
                else if (initialInvalidByTraining.Count > 0 && !initialInvalidByTraining.Contains(Convert.ToHexString(lineBytes)))
                    throw new InvalidDataException($"{sequenceId}: held-out initial invalid SENSOR? reply differs from training.");
                continue;
            }
            if (!valid) throw new InvalidDataException($"{sequenceId}: firmware cleared a valid reading without a completed invalidating echo pulse.");
            if (noEchoObserved)
            {
                var pulse = completedPulses[^1];
                var pulseWidthUs = (pulse.FallNs - pulse.RiseNs) / 1000;
                var expectedMm = (pulseWidthUs * 343) / 2000;
                if (Math.Abs(mm - expectedMm) > 1)
                    throw new InvalidDataException($"{sequenceId}: no-echo SENSOR? reply did not retain the most recent complete echo value.");
                sequenceStale++;
            }
        }
        if (!sequenceInvalid) throw new InvalidDataException($"{sequenceId}: trace did not observe the boot-time invalid sensor response.");
        if (sequenceSplit == "training") trainingInvalidSequences.Add(sequenceId);
        else
        {
            heldoutInvalidSequences.Add(sequenceId);
            heldoutStaleReplies += sequenceStale;
            if (sequenceStale == 0) throw new InvalidDataException($"{sequenceId}: held-out sequence has no causally checked post-no-echo response.");
        }
    }
    if (initialInvalidByTraining.Count != 1 || trainingInvalidSequences.Count < 2 || heldoutInvalidSequences.Count < 2 || heldoutStaleReplies < 2)
        throw new InvalidDataException("No-echo campaign lacks stable training-derived invalid reply, repeated training coverage, or held-out stale-state replies.");
    var initialInvalidResponse = Convert.FromHexString(initialInvalidByTraining.Single());
    return (initialInvalidResponse, trainingInvalidSequences.Count, heldoutInvalidSequences.Count, heldoutStaleReplies);
}
static Dictionary<string, byte[]> LearnTrainingUartResponses(string campaignDirectory, IReadOnlyList<JsonElement> manifestRows)
{
    var responses = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
    {
        ["PING"] = new(StringComparer.Ordinal), ["MOTOR"] = new(StringComparer.Ordinal),
        ["MOVE"] = new(StringComparer.Ordinal), ["OVERLENGTH"] = new(StringComparer.Ordinal)
    };
    var trainingSequences = 0;
    foreach (var row in manifestRows.Where(row => row.GetProperty("split").GetString() == "training"))
    {
        trainingSequences++;
        var tracePath = Path.Combine(campaignDirectory, row.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException("UART training trace hash differs from its sequence manifest.");
        var inputs = ReadUartLines(tracePath, "controller_to_mcu");
        var outputs = ReadUartLines(tracePath, "mcu_to_controller").Where(line => !Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (inputs.Count != outputs.Length) throw new InvalidDataException($"{row.GetProperty("sequence_id").GetString()}: input and output UART lines do not pair one-to-one.");
        for (var index = 0; index < inputs.Count; index++)
        {
            VerifyUartCausality(inputs[index], outputs[index], row.GetProperty("sequence_id").GetString()!);
            var command = Encoding.ASCII.GetString(inputs[index].Bytes);
            var response = Encoding.ASCII.GetString(outputs[index].Bytes);
            if (command == "PING")
            {
                if (!response.StartsWith("PONG ", StringComparison.Ordinal) || response.Length != 13
                    || response.AsSpan(5).ToString().Any(character => !Uri.IsHexDigit(character)))
                    throw new InvalidDataException("Training PING response does not match the observed build-identity grammar.");
                responses["PING"].Add(response);
            }
            else if (IsValidMotorCommand(command))
            {
                if (response != "OK MOTOR") throw new InvalidDataException("Training valid MOTOR command did not return its observed success response.");
                responses["MOTOR"].Add(response);
            }
            else if (inputs[index].Bytes.Length > 63)
            {
                if (response != "ERR LINE") throw new InvalidDataException("Training overlength UART line did not return its observed rejection response.");
                responses["OVERLENGTH"].Add(response);
            }
            else if (command == "MOVE 100 100")
            {
                if (response != "ERR CMD") throw new InvalidDataException("Training MOVE rejection did not return its observed command error.");
                responses["MOVE"].Add(response);
            }
        }
    }
    if (trainingSequences < 2 || responses.Any(pair => pair.Value.Count != 1))
        throw new InvalidDataException("Training traces do not provide one stable observed response for PING and valid MOTOR.");
    return responses.ToDictionary(pair => pair.Key, pair => Encoding.ASCII.GetBytes(pair.Value.Single() + "\n"), StringComparer.Ordinal);
}

static (int TrainingSequences, int HeldoutSequences, string Response) LearnAndVerifyBootResponse(string campaignDirectory,
    IReadOnlyList<JsonElement> manifestRows)
{
    var responses = new HashSet<string>(StringComparer.Ordinal);
    var count = 0;
    foreach (var row in manifestRows.Where(row => row.GetProperty("split").GetString() == "training"))
    {
        var tracePath = Path.Combine(campaignDirectory, row.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException("UART training trace hash differs while learning the boot output.");
        var bootLines = ReadUartLines(tracePath, "mcu_to_controller")
            .Where(line => Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (bootLines.Length != 1) throw new InvalidDataException("Each training trace must contain exactly one initial MCU boot line.");
        var response = Encoding.ASCII.GetString(bootLines[0].Bytes);
        if (!Regex.IsMatch(response, "^BOOT [0-9a-f]{8}$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Initial MCU boot response does not match the observed firmware build-ID grammar.");
        responses.Add(response);
        count++;
    }
    if (count < 2 || responses.Count != 1) throw new InvalidDataException("Training sequences disagree on the initial MCU boot output.");
    var trainingResponse = Encoding.ASCII.GetBytes(responses.Single() + "\n");
    var heldoutCount = 0;
    foreach (var row in manifestRows.Where(row => row.GetProperty("split").GetString() == "heldout"))
    {
        var tracePath = Path.Combine(campaignDirectory, row.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException("UART held-out trace hash differs while validating the boot output.");
        var bootLines = ReadUartLines(tracePath, "mcu_to_controller")
            .Where(line => Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (bootLines.Length != 1 || !bootLines[0].Bytes.Append((byte)'\n').SequenceEqual(trainingResponse))
            throw new InvalidDataException("Held-out initial MCU boot output differs from the training-derived response.");
        heldoutCount++;
    }
    if (heldoutCount != 2) throw new InvalidDataException("Initial boot output requires two whole held-out sequences.");
    return (count, heldoutCount, responses.Single());
}

static (int Sequences, int Pings, int Motors, int Moves, int Overlength) VerifyHeldoutUartResponses(string campaignDirectory,
    IReadOnlyList<JsonElement> manifestRows, byte[] pingResponse, byte[] motorResponse, byte[] moveResponse, byte[] overlengthResponse)
{
    var heldoutSequences = 0;
    var pingCount = 0;
    var motorCount = 0;
    var moveCount = 0;
    var overlengthCount = 0;
    foreach (var row in manifestRows.Where(row => row.GetProperty("split").GetString() == "heldout"))
    {
        heldoutSequences++;
        var tracePath = Path.Combine(campaignDirectory, row.GetProperty("trace").GetString()!);
        if (!ControllerProfileHashes.Matches(row.GetProperty("trace_sha256").GetString()!, HashFile(tracePath)))
            throw new InvalidDataException("UART held-out trace hash differs from its sequence manifest.");
        var inputs = ReadUartLines(tracePath, "controller_to_mcu");
        var outputs = ReadUartLines(tracePath, "mcu_to_controller").Where(line => !Encoding.ASCII.GetString(line.Bytes).StartsWith("BOOT ", StringComparison.Ordinal)).ToArray();
        if (inputs.Count != outputs.Length) throw new InvalidDataException($"{row.GetProperty("sequence_id").GetString()}: input and output UART lines do not pair one-to-one.");
        for (var index = 0; index < inputs.Count; index++)
        {
            VerifyUartCausality(inputs[index], outputs[index], row.GetProperty("sequence_id").GetString()!);
            var command = Encoding.ASCII.GetString(inputs[index].Bytes);
            var response = outputs[index].Bytes;
            if (command == "PING")
            {
                pingCount++;
                if (!Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(response) + "\n").AsSpan().SequenceEqual(pingResponse))
                    throw new InvalidDataException("Held-out PING response differs from the training-derived response template.");
            }
            else if (IsValidMotorCommand(command))
            {
                motorCount++;
                if (!Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(response) + "\n").AsSpan().SequenceEqual(motorResponse))
                    throw new InvalidDataException("Held-out MOTOR response differs from the training-derived response template.");
            }
            else if (inputs[index].Bytes.Length > 63)
            {
                overlengthCount++;
                if (!Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(response) + "\n").AsSpan().SequenceEqual(overlengthResponse))
                    throw new InvalidDataException("Held-out overlength response differs from the training-derived response template.");
            }
            else if (command == "MOVE 100 100")
            {
                moveCount++;
                if (!Encoding.ASCII.GetBytes(Encoding.ASCII.GetString(response) + "\n").AsSpan().SequenceEqual(moveResponse))
                    throw new InvalidDataException("Held-out MOVE rejection differs from the training-derived response template.");
            }
        }
    }
    if (heldoutSequences < 2 || pingCount < 2 || motorCount < 2 || moveCount < 2 || overlengthCount < 2)
        throw new InvalidDataException("Whole held-out traces do not cover repeated PING, MOTOR, MOVE-rejection, and overlength responses.");
    return (heldoutSequences, pingCount, motorCount, moveCount, overlengthCount);
}

static void VerifyUartCausality(UartTraceLine input, UartTraceLine output, string sequenceId)
{
    if (output.HostMonotonicTicks < input.HostMonotonicTicks
        || input.ScheduledSimTimeNs is long scheduled && output.SimTimeLowerBoundNs < scheduled)
        throw new InvalidDataException($"{sequenceId}: observed UART response precedes its scheduled input.");
}

static UartTimingMeasurement MeasureModelUartTiming(string tracePath, IReadOnlyList<UartTraceLine> inputs, IReadOnlyList<UartTraceLine> outputs)
{
    if (inputs.Count != outputs.Count || inputs.Count == 0)
        throw new InvalidDataException("UART timing measurement requires paired non-empty command and response lines.");
    var boundaries = File.ReadLines(tracePath).Select(line =>
    {
        using var record = JsonDocument.Parse(line);
        var root = record.RootElement;
        return root.GetProperty("RecordType").GetString() == "RenodeTimeBoundary"
            ? root.GetProperty("SimTimeNs").GetInt64() : (long?)null;
    }).Where(time => time is not null).Select(time => time!.Value).Distinct().Order().ToArray();
    var tickIntervals = boundaries.Zip(boundaries.Skip(1), (left, right) => right - left).Where(delta => delta > 0).ToArray();
    if (tickIntervals.Length == 0) throw new InvalidDataException("UART timing trace has no advancing Renode time boundaries.");
    var maxTickIntervalNs = tickIntervals.Max();
    var errors = new List<long>(inputs.Count);
    var widths = new List<long>(inputs.Count);
    for (var index = 0; index < inputs.Count; index++)
    {
        var inputTimeNs = inputs[index].ScheduledSimTimeNs
            ?? throw new InvalidDataException("UART request has no scheduled simulation timestamp.");
        VerifyUartCausality(inputs[index], outputs[index], Path.GetFileName(tracePath));
        var outputLowerBoundNs = outputs[index].SimTimeLowerBoundNs;
        var outputUpperBoundNs = boundaries.FirstOrDefault(time => time > outputLowerBoundNs);
        if (outputUpperBoundNs <= outputLowerBoundNs)
            throw new InvalidDataException("UART response has no following Renode boundary for its simulation-time bracket.");
        var errorNs = inputTimeNs < outputLowerBoundNs ? outputLowerBoundNs - inputTimeNs
            : inputTimeNs > outputUpperBoundNs ? inputTimeNs - outputUpperBoundNs : 0;
        errors.Add(errorNs);
        widths.Add(outputUpperBoundNs - outputLowerBoundNs);
    }
    return new UartTimingMeasurement(errors, widths, maxTickIntervalNs);
}

static async Task<int> ReplayUnqualifiedActuationTraceAsync(string profilePath, string modelPath, string tracePath, string outputDirectory)
{
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    var profile = JsonSerializer.Deserialize<DeterministicControllerProfile>(await File.ReadAllBytesAsync(profilePath), options)
        ?? throw new InvalidDataException("Candidate profile is empty.");
    var model = JsonSerializer.Deserialize<ControllerModelDocument>(await File.ReadAllBytesAsync(modelPath), options)
        ?? throw new InvalidDataException("Candidate model is empty.");
    if (profile.FidelityLabel != "UNQUALIFIED")
        throw new InvalidDataException("Offline characterization replay is only for an explicitly UNQUALIFIED candidate.");
    if (!ControllerProfileHashes.Matches(profile.ModelDocumentSha256, ControllerProfileHashes.Compute(model)))
        throw new InvalidDataException("Offline replay candidate model hash does not match its profile.");

    var rows = File.ReadLines(tracePath).Select(line => JsonDocument.Parse(line)).ToArray();
    try
    {
        var traceHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(tracePath)));
        var sequenceManifestPath = Path.Combine(Path.GetDirectoryName(tracePath)!, "sequence-manifest.json");
        using var sequenceManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(sequenceManifestPath));
        var sequenceRow = sequenceManifest.RootElement.EnumerateArray().SingleOrDefault(row =>
            StringComparer.Ordinal.Equals(row.GetProperty("trace").GetString(), Path.GetFileName(tracePath)));
        if (sequenceRow.ValueKind != JsonValueKind.Object
            || sequenceRow.GetProperty("split").GetString() != "heldout"
            || !ControllerProfileHashes.Matches(sequenceRow.GetProperty("trace_sha256").GetString()!, traceHash))
            throw new InvalidDataException("Offline trajectory replay requires a whole held-out trace authenticated by its C01 sequence manifest.");
        var boundaries = rows.Select(document => document.RootElement)
            .Where(root => root.TryGetProperty("RecordType", out var recordType) && recordType.GetString() == "RenodeTimeBoundary")
            .Select(root => (SimTimeNs: root.GetProperty("SimTimeNs").GetInt64(), HostTicks: root.GetProperty("HostMonotonicTicks").GetInt64()))
            .DistinctBy(item => item.SimTimeNs).OrderBy(item => item.SimTimeNs).ToArray();
        if (boundaries.Length < 2) throw new InvalidDataException("Actuation replay trace has fewer than two Renode time boundaries.");
        var startTimeNs = boundaries[0].SimTimeNs;
        var exactEvents = new List<ActuationReplayEvent> { new(startTimeNs, 0, 0) };
        var exactLeft = 0d;
        var exactRight = 0d;
        var exactObservations = 0;
        foreach (var document in rows)
        {
            var root = document.RootElement;
            if (!root.TryGetProperty("RecordType", out var recordType) || recordType.GetString() != "PwmObservation") continue;
            var nextLeft = root.GetProperty("Channel1SignedDuty").GetDouble();
            var nextRight = root.GetProperty("Channel2SignedDuty").GetDouble();
            exactObservations++;
            if (Math.Abs(nextLeft - exactLeft) > 1e-12 || Math.Abs(nextRight - exactRight) > 1e-12)
            {
                exactLeft = nextLeft;
                exactRight = nextRight;
                exactEvents.Add(new ActuationReplayEvent(root.GetProperty("SimTimeNs").GetInt64(), exactLeft, exactRight));
            }
        }
        var uartInputs = rows.Select(document => document.RootElement)
            .Where(root => root.TryGetProperty("RecordType", out var type) && type.GetString() == "UartChunk"
                && root.GetProperty("Direction").GetString() == "controller_to_mcu")
            .Select(root => new
            {
                TimeNs = root.GetProperty("ScheduledSimTimeNs").ValueKind == JsonValueKind.Number
                    ? root.GetProperty("ScheduledSimTimeNs").GetInt64() : throw new InvalidDataException("UART input trace omits its scheduled simulation time."),
                Sequence = root.GetProperty("Sequence").GetUInt64(),
                Bytes = Convert.FromBase64String(root.GetProperty("BytesBase64").GetString()!)
            }).OrderBy(item => item.TimeNs).ThenBy(item => item.Sequence).ToArray();
        if (uartInputs.Length == 0 || exactObservations == 0) throw new InvalidDataException("Actuation replay trace lacks UART inputs or PWM observations.");
        if (uartInputs.Any(input => input.TimeNs < startTimeNs || input.TimeNs > boundaries[^1].SimTimeNs))
            throw new InvalidDataException("UART input timestamp falls outside the recorded Renode boundary interval.");

        var testOnlyProfile = profile with { FidelityLabel = "VALIDATED" };
        await using var backend = new DeterministicModelBackend(
            [new ValidatedModelAssignment("mcu-drive", testOnlyProfile, profile.Identity, model)]);
        await backend.ResetAsync(startTimeNs);
        var modelEvents = new List<ActuationReplayEvent> { new(startTimeNs, 0, 0) };
        var leftDuty = 0d;
        var rightDuty = 0d;
        var leftForward = false;
        var rightForward = false;
        var modelOutputEvents = 0;
        var costSamples = new List<ModelReplayCostSample>(boundaries.Length - 1);
        var inputIndex = 0;
        var exactBoundaryIntervalsNs = new List<double>(boundaries.Length - 1);
        var modelScheduleInputNs = new List<double>(uartInputs.Length);
        var modelAdvanceToNs = new List<double>(boundaries.Length - 1);
        var combinedModelBoundaryCostNs = new List<double>(boundaries.Length - 1);
        for (var boundaryIndex = 1; boundaryIndex < boundaries.Length; boundaryIndex++)
        {
            var boundary = boundaries[boundaryIndex];
            var scheduledInputCount = 0;
            var scheduleCostNs = 0d;
            while (inputIndex < uartInputs.Length && uartInputs[inputIndex].TimeNs <= boundary.SimTimeNs)
            {
                var input = uartInputs[inputIndex++];
                var scheduleStartTicks = Stopwatch.GetTimestamp();
                backend.ScheduleInput(new ControllerInputEvent("mcu-drive", input.TimeNs, input.Sequence,
                    model.UartInputEndpointId ?? throw new InvalidDataException("Candidate does not declare its UART input endpoint."),
                    ControllerSignalKind.UartRxByte, Bytes: input.Bytes, Unit: "bytes"));
                var elapsedScheduleNs = Stopwatch.GetElapsedTime(scheduleStartTicks).Ticks * 100d;
                scheduleCostNs += elapsedScheduleNs;
                modelScheduleInputNs.Add(elapsedScheduleNs);
                scheduledInputCount++;
            }
            var advanceStartTicks = Stopwatch.GetTimestamp();
            var result = await backend.AdvanceToAsync(boundary.SimTimeNs);
            var elapsedAdvanceNs = Stopwatch.GetElapsedTime(advanceStartTicks).Ticks * 100d;
            modelAdvanceToNs.Add(elapsedAdvanceNs);
            var hostTickDelta = boundary.HostTicks - boundaries[boundaryIndex - 1].HostTicks;
            if (hostTickDelta <= 0) throw new InvalidDataException("Renode host-monotonic boundary timestamps are not increasing.");
            var exactBoundaryNs = hostTickDelta * (1_000_000_000d / Stopwatch.Frequency);
            exactBoundaryIntervalsNs.Add(exactBoundaryNs);
            combinedModelBoundaryCostNs.Add(scheduleCostNs + elapsedAdvanceNs);
            costSamples.Add(new ModelReplayCostSample(boundary.SimTimeNs, exactBoundaryNs, scheduleCostNs,
                elapsedAdvanceNs, scheduledInputCount, result.Outputs.Count));
            if (!result.IsSupported || result.Diagnostics.Any(diagnostic => diagnostic.IsFault))
                throw new InvalidDataException("Unqualified candidate rejected the held-out actuation input trace: "
                    + string.Join("; ", result.Diagnostics.Select(diagnostic => diagnostic.Code + ":" + diagnostic.Message)));
            foreach (var output in result.Outputs)
            {
                if (output.Kind == ControllerSignalKind.PwmCapture && output.EndpointId == "tim3.ch1.duty_fraction") leftDuty = output.Scalar;
                else if (output.Kind == ControllerSignalKind.PwmCapture && output.EndpointId == "tim3.ch2.duty_fraction") rightDuty = output.Scalar;
                else if (output.Kind == ControllerSignalKind.GpioLevel && output.EndpointId == "gpio.pd2.direction") leftForward = output.Scalar == 1;
                else if (output.Kind == ControllerSignalKind.GpioLevel && output.EndpointId == "gpio.pd3.direction") rightForward = output.Scalar == 1;
            }
            var nextLeft = leftDuty == 0 ? 0 : leftForward ? leftDuty : -leftDuty;
            var nextRight = rightDuty == 0 ? 0 : rightForward ? rightDuty : -rightDuty;
            if (Math.Abs(nextLeft - modelEvents[^1].LeftSignedDuty) > 1e-12
                || Math.Abs(nextRight - modelEvents[^1].RightSignedDuty) > 1e-12)
            {
                var outputTimeNs = result.Outputs.Where(output => output.Kind is ControllerSignalKind.PwmCapture or ControllerSignalKind.GpioLevel)
                    .Select(output => output.SimTimeNs).DefaultIfEmpty(boundary.SimTimeNs).Max();
                modelEvents.Add(new ActuationReplayEvent(outputTimeNs, nextLeft, nextRight));
                modelOutputEvents++;
            }
        }
        if (inputIndex != uartInputs.Length) throw new InvalidDataException("Not all held-out UART inputs were evaluated by the recorded Renode time boundaries.");

        Directory.CreateDirectory(outputDirectory);
        WriteReplayEvents(Path.Combine(outputDirectory, "exact-actuation.csv"), exactEvents);
        WriteReplayEvents(Path.Combine(outputDirectory, "model-actuation.csv"), modelEvents);
        var costSamplePath = Path.Combine(outputDirectory, "controller-cost-samples.csv");
        WriteModelReplayCostSamples(costSamplePath, costSamples);
        var steadyExactIntervals = exactBoundaryIntervalsNs.Skip(Math.Min(10, exactBoundaryIntervalsNs.Count)).ToArray();
        var steadyModelAdvance = modelAdvanceToNs.Skip(Math.Min(10, modelAdvanceToNs.Count)).ToArray();
        var steadyCombinedModel = combinedModelBoundaryCostNs.Skip(Math.Min(10, combinedModelBoundaryCostNs.Count)).ToArray();
        var controllerCost = new
        {
            exactRenodeBoundaryIntervals = new
            {
                n = steadyExactIntervals.Length,
                medianNs = Percentile(steadyExactIntervals, 0.50),
                p95Ns = Percentile(steadyExactIntervals, 0.95),
                maxNs = steadyExactIntervals.Max()
            },
            deterministicModelAdvanceTo = new
            {
                n = steadyModelAdvance.Length,
                medianNs = Percentile(steadyModelAdvance, 0.50),
                p95Ns = Percentile(steadyModelAdvance, 0.95),
                maxNs = steadyModelAdvance.Max()
            },
            deterministicModelScheduleInput = new
            {
                n = modelScheduleInputNs.Count,
                medianNs = Percentile(modelScheduleInputNs, 0.50),
                p95Ns = Percentile(modelScheduleInputNs, 0.95),
                maxNs = modelScheduleInputNs.Max()
            },
            deterministicModelBoundaryTotal = new
            {
                n = steadyCombinedModel.Length,
                medianNs = Percentile(steadyCombinedModel, 0.50),
                p95Ns = Percentile(steadyCombinedModel, 0.95),
                maxNs = steadyCombinedModel.Max()
            },
            characterizationOnlyBoundaryMedianRatio = Percentile(steadyExactIntervals, 0.50) / Percentile(steadyCombinedModel, 0.50),
            stopwatchFrequencyHz = Stopwatch.Frequency,
            warmupBoundarySamplesExcluded = Math.Min(10, exactBoundaryIntervalsNs.Count),
            scope = "Model values measure the in-process Domain backend plus input scheduling; exact values are wall intervals between C01 Renode advancement boundary records and include the exact host's per-tick peripheral monitor work. This ratio is a component-cost characterization, not matched runtime performance or FPS/RTF acceptance."
        };
        var report = new
        {
            status = "CHARACTERIZATION_ONLY_UNQUALIFIED_CANDIDATE",
            productionProfilePreflightChanged = false,
            candidateFidelityLabel = profile.FidelityLabel,
            candidateProfileId = profile.ProfileId,
            candidateProfileFile = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(profilePath)),
            candidateProfileFileSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(profilePath))),
            candidateModelFile = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(modelPath)),
            candidateModelFileSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(modelPath))),
            candidateModelSha256 = profile.ModelDocumentSha256,
            sourceSequenceId = sequenceRow.GetProperty("sequence_id").GetString(),
            sourceSequenceSplit = sequenceRow.GetProperty("split").GetString(),
            sourceTraceFile = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(tracePath)),
            sourceTraceSha256 = traceHash,
            sourceSequenceManifestFile = Path.GetRelativePath(Environment.CurrentDirectory, Path.GetFullPath(sequenceManifestPath)),
            sourceSequenceManifestSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sequenceManifestPath))),
            sequenceStartSimTimeNs = startTimeNs,
            scheduledUartInputs = uartInputs.Length,
            exactPwmObservations = exactObservations,
            exactActuatorStateChanges = exactEvents.Count - 1,
            modelActuatorStateChanges = modelOutputEvents,
            performance = controllerCost,
            controllerCostSamplesCsv = "controller-cost-samples.csv",
            controllerCostSamplesSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(costSamplePath))),
            exactActuatorTraceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "exact-actuation.csv")))),
            modelActuatorTraceSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(outputDirectory, "model-actuation.csv")))),
            exactActuatorTrace = "exact-actuation.csv",
            modelActuatorTrace = "model-actuation.csv",
            note = "The backend was instantiated only inside this offline characterization tool using an in-memory label override. The candidate artifact stays UNQUALIFIED and production model preflight is unchanged."
        };
        var serializedReport = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "exact-actuation-replay-manifest.json"), serializedReport);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "model-actuation-replay-manifest.json"), serializedReport);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "actuation-replay-manifest.json"), serializedReport);
        Console.WriteLine(JsonSerializer.Serialize(report));
        return 0;
    }
    finally
    {
        foreach (var document in rows) document.Dispose();
    }
}

static void WriteReplayEvents(string path, IReadOnlyList<ActuationReplayEvent> events)
{
    var builder = new StringBuilder("sim_time_ns,left_signed_duty,right_signed_duty\n");
    foreach (var item in events)
        builder.Append(item.SimTimeNs.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(item.LeftSignedDuty.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(item.RightSignedDuty.ToString("R", CultureInfo.InvariantCulture)).AppendLine();
    File.WriteAllText(path, builder.ToString());
}

static void WriteModelReplayCostSamples(string path, IReadOnlyList<ModelReplayCostSample> samples)
{
    var builder = new StringBuilder("sim_time_ns,exact_renode_boundary_interval_ns,model_schedule_input_ns,model_advance_to_ns,scheduled_uart_input_count,model_output_count\n");
    foreach (var item in samples)
        builder.Append(item.SimTimeNs.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(item.ExactRenodeBoundaryIntervalNs.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(item.ModelScheduleInputNs.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(item.ModelAdvanceToNs.ToString("R", CultureInfo.InvariantCulture)).Append(',')
            .Append(item.ScheduledUartInputCount.ToString(CultureInfo.InvariantCulture)).Append(',')
            .Append(item.ModelOutputCount.ToString(CultureInfo.InvariantCulture)).AppendLine();
    File.WriteAllText(path, builder.ToString());
}

static bool IsValidMotorCommand(string line)
{
    var tokens = line.Split(' ', StringSplitOptions.None);
    return tokens.Length == 3 && tokens[0] == "MOTOR"
        && int.TryParse(tokens[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var left)
        && int.TryParse(tokens[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var right)
        && left is >= -100 and <= 100 && right is >= -100 and <= 100;
}

static IReadOnlyList<UartTraceLine> ReadUartLines(string tracePath, string direction)
{
    var result = new List<UartTraceLine>();
    var buffer = new List<byte>();
    long lineHostMonotonicTicks = 0;
    long lineSimTimeLowerBoundNs = 0;
    long? lineScheduledSimTimeNs = null;
    foreach (var line in File.ReadLines(tracePath))
    {
        using var record = JsonDocument.Parse(line);
        var root = record.RootElement;
        if (root.GetProperty("RecordType").GetString() != "UartChunk" || root.GetProperty("Direction").GetString() != direction) continue;
        var bytes = Convert.FromBase64String(root.GetProperty("BytesBase64").GetString()!);
        lineHostMonotonicTicks = root.GetProperty("HostMonotonicTicks").GetInt64();
        lineSimTimeLowerBoundNs = root.GetProperty("LastCompletedBoundaryNsLowerBound").GetInt64();
        lineScheduledSimTimeNs = root.GetProperty("ScheduledSimTimeNs").ValueKind == JsonValueKind.Number
            ? root.GetProperty("ScheduledSimTimeNs").GetInt64() : null;
        foreach (var value in bytes)
        {
            if (value == (byte)'\n')
            {
                if (buffer.Count > 0 && buffer[^1] == (byte)'\r') buffer.RemoveAt(buffer.Count - 1);
                result.Add(new UartTraceLine(buffer.ToArray(), lineHostMonotonicTicks, lineSimTimeLowerBoundNs, lineScheduledSimTimeNs));
                buffer.Clear();
            }
            else buffer.Add(value);
        }
    }
    if (buffer.Count != 0) throw new InvalidDataException($"UART trace {tracePath} ends with an incomplete {direction} line.");
    return result;
}

sealed record SensorReferenceObservation(string SequenceId, string Split, double InputMeters, double OutputMm, long SimTimeNs);
sealed record EchoPulseObservation(string SequenceId, string Split, long PulseWidthNs, double ReplyMm, long RiseTimeNs, long QueryLowerBoundSimTimeNs);
sealed record UartTraceLine(byte[] Bytes, long HostMonotonicTicks, long SimTimeLowerBoundNs, long? ScheduledSimTimeNs);
sealed record UartTimingMeasurement(IReadOnlyList<long> ModelTimestampOutsideObservedBracketNs,
    IReadOnlyList<long> ObservedBracketWidthsNs, long LargestPhysicsTickIntervalNs);
sealed record ActuationReplayEvent(long SimTimeNs, double LeftSignedDuty, double RightSignedDuty);
sealed record ModelReplayCostSample(long SimTimeNs, double ExactRenodeBoundaryIntervalNs,
    double ModelScheduleInputNs, double ModelAdvanceToNs, int ScheduledUartInputCount, int ModelOutputCount);
sealed record MixedSensorOutOfDomainInput(string SequenceId, long PulseWidthNs, int ExactFirmwareReplyMm,
    double RaycastDistanceMm);
sealed record MixedSensorEnvelopeReview(string Status, int HeldoutSequences, int MatchedQueries, int ValidReplies,
    int InitialInvalidReplies, int InDomainValidReplies, double InDomainErrorP95Mm, double InDomainErrorMaxMm,
    long MinimumPulseWidthNs, long MaximumPulseWidthNs, IReadOnlyList<MixedSensorOutOfDomainInput> OutOfDomainInputs);
sealed record LoadedTrajectoryQualificationReview(int HeldoutSequences, int AlignedSamples,
    double MaximumDisplacementErrorM, double MaximumChassisPositionErrorM,
    string BaselineModelSha256, string CandidateMotorSurfaceSha256);
