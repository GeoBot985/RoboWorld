using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using RoboWorld.Contracts.V1;
using RoboWorld.Domain;
using RoboWorld.RobotService;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}

var repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
var candidateDirectory = Path.Combine(repositoryRoot, "tests", "fixtures", "controller-profiles", "pulse-candidate");
var candidateProfilePath = Path.Combine(candidateDirectory, "profile.json");
var candidateModelPath = Path.Combine(candidateDirectory, "model.json");
var candidateProfileNode = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllBytesAsync(candidateProfilePath))!.AsObject();
var testFirmwarePath = Path.Combine(repositoryRoot, "samples", "firmware", "stm32-drive", "build", "firmware.elf");
candidateProfileNode["identity"]!["firmwareSha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(testFirmwarePath)));
var candidateProfileBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidateProfileNode);
var candidateProfile = System.Text.Json.JsonDocument.Parse(candidateProfileBytes).RootElement;
var candidateProfileId = candidateProfile.GetProperty("profileId").GetString()!;
var candidateProfileSha = Convert.ToHexString(SHA256.HashData(candidateProfileBytes));
var profileStoreRoot = Path.Combine(Path.GetTempPath(), "roboworld-profile-preflight-" + Guid.NewGuid().ToString("N"));
try
{
    var installDirectory = Path.Combine(profileStoreRoot, candidateProfileId);
    Directory.CreateDirectory(installDirectory);
    await File.WriteAllBytesAsync(Path.Combine(installDirectory, "profile.json"), candidateProfileBytes);
    File.Copy(candidateModelPath, Path.Combine(installDirectory, "model.json"));
    var startup = new ControllerProfileStartupReference("rover-01", "mcu-drive", candidateProfileId, candidateProfileSha,
        Path.Combine(repositoryRoot, "examples", "robots", "rover-01.robot.json"), "gpio.pa0.echo");
    var projectReference = new ProjectControllerProfileReference(candidateProfileId, candidateProfileSha);
    var startupController = new RenodeControllerOptions("mcu-drive", "mcu-board",
        testFirmwarePath, "motor-left", "motor-right");
    try
    {
        _ = ControllerProfileAssignmentResolver.LoadAssignments([startupController], [startup],
            new Dictionary<string, ProjectControllerProfileReference>(StringComparer.Ordinal) { ["rover-01/mcu-drive"] = projectReference },
            profileStoreRoot, Path.Combine(repositoryRoot, "examples", "components"),
            Path.Combine(repositoryRoot, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs"));
        throw new InvalidOperationException("Unqualified candidate unexpectedly passed service profile preflight.");
    }
    catch (InvalidDataException exception) when (exception.Message == "Only profiles explicitly labeled VALIDATED may be loaded for runtime execution.")
    {
        Require(true, "service profile resolver verifies project hash and current runtime identity, then rejects the unqualified candidate");
    }
    var serviceDll = Path.Combine(AppContext.BaseDirectory, "RoboWorld.RobotService.dll");
    var dotnetHost = Path.Combine(repositoryRoot, ".toolchain", "dotnet", "dotnet.exe");
    var configuration = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
    {
        id = "mcu-drive", componentInstanceId = "mcu-board",
        firmwarePath = startupController.FirmwarePath, channel1ActuatorId = "motor-left", channel2ActuatorId = "motor-right"
    })));
    var profileArgument = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(startup)));
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }
    };
    process.StartInfo.ArgumentList.Add(serviceDll);
    process.StartInfo.ArgumentList.Add("--renode-motor-host");
    process.StartInfo.ArgumentList.Add("--controller-mode=fast");
    process.StartInfo.ArgumentList.Add($"--root={repositoryRoot}");
    process.StartInfo.ArgumentList.Add($"--controller-profiles-root={profileStoreRoot}");
    process.StartInfo.ArgumentList.Add($"--component-directory={Path.Combine(repositoryRoot, "examples", "components")}");
    process.StartInfo.ArgumentList.Add($"--peripheral-map-source={Path.Combine(repositoryRoot, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs")}");
    process.StartInfo.ArgumentList.Add($"--controller-config={configuration}");
    process.StartInfo.ArgumentList.Add($"--controller-profile-reference={profileArgument}");
    process.Start();
    var stdoutTask = process.StandardOutput.ReadToEndAsync();
    var stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
    var processOutput = await stdoutTask + await stderrTask;
    Require(process.ExitCode == 2 && processOutput.Contains("Only profiles explicitly labeled VALIDATED", StringComparison.Ordinal),
        "Robot Service explicit fast mode rejects an unqualified project-pinned candidate before starting the runtime");

    var renodeIdsBeforeAutoFallback = GetRenodeProcessIds();
    var autoLines = new ConcurrentQueue<string>();
    var autoErrors = new ConcurrentQueue<string>();
    var autoReady = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    using var autoProcess = new Process
    {
        StartInfo = new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        },
        EnableRaisingEvents = true
    };
    autoProcess.OutputDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is not { } line) return;
        autoLines.Enqueue(line);
        if (line.Contains("READY RoboWorld RobotService", StringComparison.Ordinal)) autoReady.TrySetResult(line);
    };
    autoProcess.ErrorDataReceived += (_, eventArgs) =>
    {
        if (eventArgs.Data is { } line) autoErrors.Enqueue(line);
    };
    autoProcess.StartInfo.ArgumentList.Add(serviceDll);
    autoProcess.StartInfo.ArgumentList.Add("--renode-motor-host");
    autoProcess.StartInfo.ArgumentList.Add("--controller-mode=auto");
    autoProcess.StartInfo.ArgumentList.Add($"--root={repositoryRoot}");
    autoProcess.StartInfo.ArgumentList.Add($"--controller-profiles-root={profileStoreRoot}");
    autoProcess.StartInfo.ArgumentList.Add($"--component-directory={Path.Combine(repositoryRoot, "examples", "components")}");
    autoProcess.StartInfo.ArgumentList.Add($"--peripheral-map-source={Path.Combine(repositoryRoot, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs")}");
    autoProcess.StartInfo.ArgumentList.Add($"--controller-config={configuration}");
    autoProcess.StartInfo.ArgumentList.Add($"--controller-profile-reference={profileArgument}");
    autoProcess.StartInfo.ArgumentList.Add("--test-shutdown-after-ms=15000");
    autoProcess.Start();
    autoProcess.BeginOutputReadLine();
    autoProcess.BeginErrorReadLine();
    var readyLine = await autoReady.Task.WaitAsync(TimeSpan.FromSeconds(30));
    var readyMatch = System.Text.RegularExpressions.Regex.Match(readyLine, @"events_port=(\d+) uart=tcp://127\.0\.0\.1:(\d+)");
    Require(readyMatch.Success, "Auto fallback starts the exact Renode service and publishes its loopback endpoints");
    var eventPort = int.Parse(readyMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    var uartPort = int.Parse(readyMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    var privateEndpointsLine = autoLines.FirstOrDefault(line => line.StartsWith("RENODE_PRIVATE_ENDPOINTS ", StringComparison.Ordinal));
    if (privateEndpointsLine is null)
    {
        var privateEndpointDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < privateEndpointDeadline && privateEndpointsLine is null)
        {
            await Task.Delay(20);
            privateEndpointsLine = autoLines.FirstOrDefault(line => line.StartsWith("RENODE_PRIVATE_ENDPOINTS ", StringComparison.Ordinal));
        }
    }
    if (privateEndpointsLine is null) throw new InvalidDataException("Auto fallback did not report its private Renode listeners.");
    var privateEndpointMatch = System.Text.RegularExpressions.Regex.Match(privateEndpointsLine, @"monitor_port=(\d+) uart_ports=(\d+) clock_port=(\d+)");
    if (!privateEndpointMatch.Success) throw new InvalidDataException("Auto fallback private Renode endpoint report is malformed.");
    var monitorPort = int.Parse(privateEndpointMatch.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    var renodeUartPort = int.Parse(privateEndpointMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    var clockPort = int.Parse(privateEndpointMatch.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    using (var autoUartClient = new TcpClient())
    using (var autoEventClient = new TcpClient())
    {
        await autoUartClient.ConnectAsync("127.0.0.1", uartPort).WaitAsync(TimeSpan.FromSeconds(5));
        var autoUart = autoUartClient.GetStream();
        Require(await ReadLineAsync(autoUart, TimeSpan.FromSeconds(5)) == "BOOT 81ace641",
            "Auto fallback boots the pinned real STM32 firmware over its service UART");
        await autoEventClient.ConnectAsync("127.0.0.1", eventPort).WaitAsync(TimeSpan.FromSeconds(5));
        var autoEventStream = autoEventClient.GetStream();
        var autoHello = await ReadEnvelopeAsync(autoEventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Require(autoHello.PayloadCase == Envelope.PayloadOneofCase.Hello && autoHello.Hello.ClientKind == "robotservice",
            "Auto fallback accepts the Godot simulation event stream before advancing firmware");
        var clockEpochNs = autoHello.SimTimeNs;
        await WriteEnvelopeAsync(autoEventStream, new Envelope
        {
            ProtocolMajor = 1, ProtocolMinor = 0, SessionId = autoHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-01",
            Hello = new Hello { ClientKind = "godot", InstanceVersion = "auto-fallback-acceptance" }
        }, CancellationToken.None);
        await autoUart.WriteAsync(Encoding.ASCII.GetBytes("PING\n"));
        try
        {
            await SendTickAndReadAckAsync(autoEventStream, autoHello.SessionId, "rover-01", 1, CancellationToken.None, clockEpochNs);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Auto fallback tick failed. Child stdout: " + string.Join(" | ", autoLines)
                + " Child stderr: " + string.Join(" | ", autoErrors), exception);
        }
        Require(await ReadLineAsync(autoUart, TimeSpan.FromSeconds(5)) == "PONG 81ace641",
            "Auto fallback executes a real firmware PING/PONG after profile preflight fails");
        await autoProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    var autoOutput = string.Join("\n", autoLines);
    Require(autoProcess.ExitCode == 0
        && autoOutput.Contains("CONTROLLER_EXECUTION_SELECTED kind=Renode", StringComparison.Ordinal)
        && autoOutput.Contains("No usable validated profile; selected exact firmware before run", StringComparison.Ordinal)
        && autoOutput.Contains("runtime_cleanup=complete", StringComparison.Ordinal)
        && !autoErrors.Any(line => line.Contains("Unhandled exception", StringComparison.OrdinalIgnoreCase)),
        "Auto fallback reports the rejected profile, completes exact-mode cancellation, and shuts down cleanly. "
        + $"exit={autoProcess.ExitCode}; stop_log={autoOutput.Contains("runtime_cleanup=complete", StringComparison.Ordinal)}; stderr={string.Join(" | ", autoErrors)}");
    var portStatuses = new Dictionary<int, bool>();
    foreach (var port in new[] { eventPort, uartPort, monitorPort, renodeUartPort, clockPort }.Distinct())
        portStatuses[port] = await IsTcpPortListeningAsync(port);
    var releasedPorts = portStatuses.Values.All(isListening => !isListening);
    var autoRenodeIdsAfter = GetRenodeProcessIds();
    Require(releasedPorts && autoRenodeIdsAfter.Except(renodeIdsBeforeAutoFallback).Count() == 0,
        "Auto fallback releases event/UART/Renode listeners and leaves no new Renode process. "
        + $"listenersStillOpen={string.Join(',', portStatuses.Where(pair => pair.Value).Select(pair => pair.Key))}; "
        + $"newRenodePids={string.Join(',', autoRenodeIdsAfter.Except(renodeIdsBeforeAutoFallback))}");
}
finally
{
    var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var fullStoreRoot = Path.GetFullPath(profileStoreRoot);
    if (fullStoreRoot.StartsWith(tempRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && Directory.Exists(fullStoreRoot)) Directory.Delete(fullStoreRoot, recursive: true);
}

var model = new ControllerModelDocument(1, "service-host-integration", 6,
[
    new ControllerModelRule("uart.motor.left_percent", ControllerSignalKind.AnalogSample, "tim3.ch1.duty_fraction", ControllerSignalKind.PwmCapture,
        ControllerModelOperator.MagnitudeAffine, 0.01, 0, -100, 100, "fraction"),
    new ControllerModelRule("uart.motor.left_percent", ControllerSignalKind.AnalogSample, "gpio.pd2.direction", ControllerSignalKind.GpioLevel,
        ControllerModelOperator.SignLevel, 0, 0, -100, 100, "level"),
    new ControllerModelRule("uart.motor.right_percent", ControllerSignalKind.AnalogSample, "tim3.ch2.duty_fraction", ControllerSignalKind.PwmCapture,
        ControllerModelOperator.MagnitudeAffine, 0.01, 0, -100, 100, "fraction"),
    new ControllerModelRule("uart.motor.right_percent", ControllerSignalKind.AnalogSample, "gpio.pd3.direction", ControllerSignalKind.GpioLevel,
        ControllerModelOperator.SignLevel, 0, 0, -100, 100, "level")
], 48,
[
    new ControllerUartCommandRule("MOTOR", 2, -100, 100, "uart.tx",
    [
        new ControllerUartOperandBinding(0, "uart.motor.left_percent"),
        new ControllerUartOperandBinding(1, "uart.motor.right_percent")
    ], Encoding.ASCII.GetBytes("OK MOTOR\n"))
], "uart.rx", Encoding.ASCII.GetBytes("ERR LINE\n"), InitialUartOutputBytes: Encoding.ASCII.GetBytes("BOOT test\n"));
var envelope = new ControllerInputEnvelope(["uart.rx"], [ControllerSignalKind.UartRxByte], 0, 5_000_000_000, 16, 115200);
var identity = new ControllerProfileIdentity(1, new string('a', 64), "stm32f407", "board-test", "1", new string('b', 64),
    new string('c', 64), new string('d', 64), new string('e', 64), "trace-schema-1", "test-generator", "1", "test-evaluator",
    ControllerProfileHashes.Compute(envelope));
var profile = new DeterministicControllerProfile(1, "profile-service-test", identity, envelope, ControllerProfileHashes.Compute(model),
    new string('f', 64), new string('1', 64), 2, new Dictionary<string, double>(), ["measured test behavior"], [], "VALIDATED");
var assignment = new ValidatedModelAssignment("mcu-drive", profile, identity, model);
var controller = new RenodeControllerOptions("mcu-drive", "mcu-board", "unused-for-model", "motor-left", "motor-right");
const long modelStartupEpochNs = 10_000_000;
await using var host = new DeterministicModelEventHost("rover-test", [controller], [assignment],
    new DeterministicModelBackend([assignment]), initialSimTimeNs: modelStartupEpochNs);
using var cancellation = new CancellationTokenSource();
var hostTask = host.RunAsync(cancellation.Token);
var endpoints = await host.Ready.WaitAsync(TimeSpan.FromSeconds(5));

using var uartClient = new TcpClient();
await uartClient.ConnectAsync("127.0.0.1", endpoints.UartPorts[controller.Id]);
var uart = uartClient.GetStream();
Require(await ReadLineAsync(uart, TimeSpan.FromSeconds(5)) == "BOOT test", "model UART emits the training-derived boot stream before accepting commands");

using var eventClient = new TcpClient();
await eventClient.ConnectAsync("127.0.0.1", endpoints.EventPort);
var eventStream = eventClient.GetStream();
var hello = await ReadEnvelopeAsync(eventStream, CancellationToken.None);
Require(hello.PayloadCase == Envelope.PayloadOneofCase.Hello && hello.Hello.ClientKind == "robotservice", "model host performs the Robot Service event-stream hello");
Require(hello.SimTimeNs == modelStartupEpochNs, "validated model host announces the same startup simulation epoch as Exact Renode");
await WriteEnvelopeAsync(eventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = hello.SessionId, WorldId = "roboworld-local", RobotId = "rover-test",
    Hello = new Hello { ClientKind = "godot", InstanceVersion = "integration-test" }
}, CancellationToken.None);

var observed = new List<Envelope>();
// Complete one simulation-event read before the UART bytes arrive. The model host
// must retain those bytes for the next authoritative tick boundary.
await WriteEnvelopeAsync(eventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = hello.SessionId, WorldId = "roboworld-local", RobotId = "rover-test",
    Sequence = 1, SimTimeNs = modelStartupEpochNs,
    Tick = new SimulationTick { TickIndex = 1, StartNs = modelStartupEpochNs, DeltaNs = 10_000_000, RealTimeFactor = 1 }
}, CancellationToken.None);
while (true)
{
    var response = await ReadEnvelopeAsync(eventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    observed.Add(response);
    if (response.PayloadCase == Envelope.PayloadOneofCase.Tick) break;
}
Require(!observed.Any(item => item.PayloadCase == Envelope.PayloadOneofCase.HardwareEvent),
    "UART input arriving after a tick was read cannot affect that preceding tick");

await uart.WriteAsync(Encoding.ASCII.GetBytes("MOTOR -50 25\r\n"));
// Allow the independent UART read loop to timestamp and stage the bytes before
// the subsequent simulation event is submitted.
await Task.Delay(10);
await WriteEnvelopeAsync(eventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = hello.SessionId, WorldId = "roboworld-local", RobotId = "rover-test",
    Sequence = 2, SimTimeNs = modelStartupEpochNs + 10_000_000,
    Tick = new SimulationTick { TickIndex = 2, StartNs = modelStartupEpochNs + 10_000_000, DeltaNs = 10_000_000, RealTimeFactor = 1 }
}, CancellationToken.None);
while (true)
{
    var response = await ReadEnvelopeAsync(eventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    observed.Add(response);
    if (response.PayloadCase == Envelope.PayloadOneofCase.Tick) break;
}
var pwm = observed.Where(item => item.PayloadCase == Envelope.PayloadOneofCase.HardwareEvent).ToArray();
Require(pwm.Length == 2 && pwm.Any(item => item.ComponentId == "motor-left" && item.HardwareEvent.Scalar == -0.5)
    && pwm.Any(item => item.ComponentId == "motor-right" && item.HardwareEvent.Scalar == 0.25),
    "timestamped UART motor commands route signed deterministic PWM outputs to graph-selected actuators");
Require(observed.Where(item => item.PayloadCase == Envelope.PayloadOneofCase.Tick).Select(item => item.Tick.TickIndex).SequenceEqual(Enumerable.Range(1, (int)observed.Count(item => item.PayloadCase == Envelope.PayloadOneofCase.Tick)).Select(value => (ulong)value)),
    "model host acknowledges authoritative ticks in order after routing controller outputs");
Require(await ReadLineAsync(uart, TimeSpan.FromSeconds(5)) == "OK MOTOR", "model UART returns the characterized response bytes");

cancellation.Cancel();
await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("PASS: deterministic service host completed graceful cancellation after closing its streams.");

var faultModel = model with
{
    ModelId = "service-host-measured-controller-fault",
    MaximumRules = model.MaximumRules + 1,
    FaultRules = [new ControllerModelFaultRule("controller.fault", "MCU_HALTED", ControllerModelFaultBehavior.HaltAndLatchOutputs)]
};
var faultEnvelope = envelope with
{
    SupportedEndpoints = ["controller.fault", "uart.rx"],
    SupportedSignals = [ControllerSignalKind.Fault, ControllerSignalKind.UartRxByte]
};
var faultIdentity = identity with { InputEnvelopeSha256 = ControllerProfileHashes.Compute(faultEnvelope) };
var faultProfile = profile with
{
    ProfileId = "profile-service-measured-fault-test", Identity = faultIdentity, Envelope = faultEnvelope,
    ModelDocumentSha256 = ControllerProfileHashes.Compute(faultModel), SupportedBehaviors = ["measured MCU halt with latched outputs"]
};
var faultAssignment = new ValidatedModelAssignment("mcu-fault", faultProfile, faultIdentity, faultModel);
var faultController = new RenodeControllerOptions("mcu-fault", "mcu-fault-board", "unused-for-model", "motor-left", "motor-right");
await using var faultHost = new DeterministicModelEventHost("rover-fault-test", [faultController], [faultAssignment],
    new DeterministicModelBackend([faultAssignment]), testMcuHaltAtSimTimeNs: 10_000_000);
using var faultCancellation = new CancellationTokenSource();
var faultHostTask = faultHost.RunAsync(faultCancellation.Token);
var faultEndpoints = await faultHost.Ready.WaitAsync(TimeSpan.FromSeconds(5));
using var faultUartClient = new TcpClient();
await faultUartClient.ConnectAsync("127.0.0.1", faultEndpoints.UartPorts[faultController.Id]);
var faultUart = faultUartClient.GetStream();
Require(await ReadLineAsync(faultUart, TimeSpan.FromSeconds(5)) == "BOOT test", "fault-aware model host starts from its measured boot bytes");
using var faultEventClient = new TcpClient();
await faultEventClient.ConnectAsync("127.0.0.1", faultEndpoints.EventPort);
var faultEventStream = faultEventClient.GetStream();
var faultHello = await ReadEnvelopeAsync(faultEventStream, CancellationToken.None);
await WriteEnvelopeAsync(faultEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = faultHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-fault-test",
    Hello = new Hello { ClientKind = "godot", InstanceVersion = "integration-test" }
}, CancellationToken.None);
await faultUart.WriteAsync(Encoding.ASCII.GetBytes("MOTOR 50 50\n"));
// The test is asserting output from an already received UART line. Let the
// independent socket reader enqueue that line before submitting its tick.
await Task.Delay(25);
await WriteEnvelopeAsync(faultEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = faultHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-fault-test",
    Sequence = 1, SimTimeNs = 0,
    Tick = new SimulationTick { TickIndex = 1, StartNs = 0, DeltaNs = 10_000_000, RealTimeFactor = 1 }
}, CancellationToken.None);
var firstFaultRunEvents = new List<Envelope>();
while (true)
{
    var response = await ReadEnvelopeAsync(faultEventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    firstFaultRunEvents.Add(response);
    if (response.PayloadCase == Envelope.PayloadOneofCase.Tick) break;
}
Require(firstFaultRunEvents.Count(item => item.PayloadCase == Envelope.PayloadOneofCase.HardwareEvent
        && item.HardwareEvent.Kind == HardwareEvent.Types.Kind.PwmOutput) == 2
    && await ReadLineAsync(faultUart, TimeSpan.FromSeconds(5)) == "OK MOTOR",
    "fault integration establishes two live motor outputs before the measured MCU halt");
await WriteEnvelopeAsync(faultEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = faultHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-fault-test",
    Sequence = 2, SimTimeNs = 10_000_000,
    Tick = new SimulationTick { TickIndex = 2, StartNs = 10_000_000, DeltaNs = 10_000_000, RealTimeFactor = 1 }
}, CancellationToken.None);
var haltDiagnostic = await ReadEnvelopeAsync(faultEventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
var haltTickAck = await ReadEnvelopeAsync(faultEventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
Require(haltDiagnostic.PayloadCase == Envelope.PayloadOneofCase.Diagnostic
    && haltDiagnostic.Diagnostic.Status == ComponentDiagnostic.Types.Status.Failed
    && haltDiagnostic.Diagnostic.ReasonCode == "E_CONTROLLER_HALTED"
    && haltDiagnostic.Diagnostic.ComponentPath == "rover-fault-test/mcu-fault-board"
    && haltDiagnostic.Diagnostic.RootFaultId == "mcu-fault"
    && haltDiagnostic.SimTimeNs == 10_000_000
    && haltTickAck.PayloadCase == Envelope.PayloadOneofCase.Tick && haltTickAck.Tick.TickIndex == 2,
    "Robot Service reports the measured halt at its simulation boundary and continues ordered tick acknowledgments");
await faultUart.WriteAsync(Encoding.ASCII.GetBytes("PING\n"));
await Task.Delay(25);
await WriteEnvelopeAsync(faultEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = faultHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-fault-test",
    Sequence = 3, SimTimeNs = 20_000_000,
    Tick = new SimulationTick { TickIndex = 3, StartNs = 20_000_000, DeltaNs = 10_000_000, RealTimeFactor = 1 }
}, CancellationToken.None);
var postHaltTickAck = await ReadEnvelopeAsync(faultEventStream, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
var postHaltPingSilent = false;
try { _ = await ReadLineAsync(faultUart, TimeSpan.FromMilliseconds(150)); }
catch (OperationCanceledException) { postHaltPingSilent = true; }
Require(postHaltTickAck.PayloadCase == Envelope.PayloadOneofCase.Tick && postHaltTickAck.Tick.TickIndex == 3 && postHaltPingSilent,
    "faulted model keeps the last physical output command, suppresses later UART replies, and continues simulation ticks");
faultCancellation.Cancel();
await faultHostTask.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("PASS: deterministic service host completed graceful cancellation after a measured controller fault.");

var echoModel = new ControllerModelDocument(1, "service-host-subtick-echo", 2, [], 32,
[
    new ControllerUartCommandRule("SENSOR?", 0, 0, 0, "uart.tx", [], null, NumericResponseCommandToken: "SENSOR?")
], "uart.rx", EchoPulse: new ControllerEchoPulseRule("gpio.pa0.echo", 0.0001715, 0,
    5_000_000, 20_000_000, "SENSOR?", "DIST mm=", " valid=1\n",
    ControllerEchoNoPulseBehavior.RetainLastValidReading, Encoding.ASCII.GetBytes("DIST mm=0 valid=0\n")));
var echoEnvelope = new ControllerInputEnvelope(["uart.rx", "gpio.pa0.echo"],
    [ControllerSignalKind.UartRxByte, ControllerSignalKind.GpioLevel], 0, 5_000_000_000, 16, 115200);
var echoIdentity = identity with { InputEnvelopeSha256 = ControllerProfileHashes.Compute(echoEnvelope) };
var echoProfile = profile with { ProfileId = "profile-service-echo-test", Identity = echoIdentity, Envelope = echoEnvelope,
    ModelDocumentSha256 = ControllerProfileHashes.Compute(echoModel), SupportedBehaviors = ["timestamped echo pulse"] };
var echoAssignment = new ValidatedModelAssignment("mcu-echo", echoProfile, echoIdentity, echoModel);
var echoOptions = new RenodeControllerOptions("mcu-echo", "mcu-board", "unused-for-model", string.Empty, string.Empty);
await using var echoHost = new DeterministicModelEventHost("rover-echo-test", [echoOptions], [echoAssignment]);
using var echoCancellation = new CancellationTokenSource();
var echoHostTask = echoHost.RunAsync(echoCancellation.Token);
var echoEndpoints = await echoHost.Ready.WaitAsync(TimeSpan.FromSeconds(5));
using var echoUartClient = new TcpClient();
await echoUartClient.ConnectAsync("127.0.0.1", echoEndpoints.UartPorts[echoOptions.Id]);
var echoUart = echoUartClient.GetStream();
using var echoEventClient = new TcpClient();
await echoEventClient.ConnectAsync("127.0.0.1", echoEndpoints.EventPort);
var echoEventStream = echoEventClient.GetStream();
var echoHello = await ReadEnvelopeAsync(echoEventStream, CancellationToken.None);
await WriteEnvelopeAsync(echoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = echoHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-echo-test",
    Hello = new Hello { ClientKind = "godot", InstanceVersion = "integration-test" }
}, CancellationToken.None);
await WriteEnvelopeAsync(echoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = echoHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-echo-test",
    Sequence = 1, SimTimeNs = 10_000_000,
    HardwareEvent = new HardwareEvent { Kind = HardwareEvent.Types.Kind.SensorSample, EndpointId = "distance.echo", Scalar = -1,
        Unit = "meters", EffectiveSimTimeNs = 10_000_000 }
}, CancellationToken.None);
await echoUart.WriteAsync(Encoding.ASCII.GetBytes("SENSOR?\n"));
await SendTickAndReadAckAsync(echoEventStream, echoHello.SessionId, "rover-echo-test", 1, CancellationToken.None);
var invalidEchoResponse = await ReadLineAsync(echoUart, TimeSpan.FromSeconds(5));
Require(invalidEchoResponse == "DIST mm=0 valid=0",
    $"no-echo service input preserves the training-derived initial invalid response (received '{invalidEchoResponse}')");
for (ulong tickIndex = 2; tickIndex <= 3; tickIndex++)
    await SendTickAndReadAckAsync(echoEventStream, echoHello.SessionId, "rover-echo-test", tickIndex, CancellationToken.None);
await WriteEnvelopeAsync(echoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = echoHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-echo-test",
    Sequence = 2, SimTimeNs = 40_000_000,
    HardwareEvent = new HardwareEvent { Kind = HardwareEvent.Types.Kind.SensorSample, EndpointId = "distance.echo", Scalar = 2,
        Unit = "meters", EffectiveSimTimeNs = 40_000_000 }
}, CancellationToken.None);
for (ulong tickIndex = 4; tickIndex <= 6; tickIndex++)
    await SendTickAndReadAckAsync(echoEventStream, echoHello.SessionId, "rover-echo-test", tickIndex, CancellationToken.None);
await echoUart.WriteAsync(Encoding.ASCII.GetBytes("SENSOR?\n"));
await SendTickAndReadAckAsync(echoEventStream, echoHello.SessionId, "rover-echo-test", 7, CancellationToken.None);
var echoResponse = await ReadLineAsync(echoUart, TimeSpan.FromSeconds(5));
Require(echoResponse == "DIST mm=2000 valid=1",
    $"distance sensor pulse falling edge retains its sub-tick timestamp through deterministic firmware response (received '{echoResponse}')");
await WriteEnvelopeAsync(echoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = echoHello.SessionId, WorldId = "roboworld-local", RobotId = "rover-echo-test",
    Sequence = 3, SimTimeNs = 80_000_000,
    HardwareEvent = new HardwareEvent { Kind = HardwareEvent.Types.Kind.SensorSample, EndpointId = "distance.echo", Scalar = -1,
        Unit = "meters", EffectiveSimTimeNs = 80_000_000 }
}, CancellationToken.None);
await SendTickAndReadAckAsync(echoEventStream, echoHello.SessionId, "rover-echo-test", 8, CancellationToken.None);
await echoUart.WriteAsync(Encoding.ASCII.GetBytes("SENSOR?\n"));
await SendTickAndReadAckAsync(echoEventStream, echoHello.SessionId, "rover-echo-test", 9, CancellationToken.None);
var retainedEchoResponse = await ReadLineAsync(echoUart, TimeSpan.FromSeconds(5));
Require(retainedEchoResponse == echoResponse && retainedEchoResponse == "DIST mm=2000 valid=1",
    $"measured no-echo event is not converted into a pulse and retains the last valid reading (received '{retainedEchoResponse}')");
echoCancellation.Cancel();
await echoHostTask.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("PASS: deterministic echo service host completed graceful cancellation.");

var rejectingNoEchoModel = echoModel with
{
    ModelId = "service-host-noecho-reject",
    EchoPulse = echoModel.EchoPulse! with
    {
        NoPulseBehavior = ControllerEchoNoPulseBehavior.Reject,
        InitialInvalidResponseBytes = null
    }
};
var rejectingNoEchoProfile = echoProfile with
{
    ProfileId = "profile-service-noecho-reject-test",
    ModelDocumentSha256 = ControllerProfileHashes.Compute(rejectingNoEchoModel),
    SupportedBehaviors = ["valid measured echo only"]
};
var rejectingNoEchoAssignment = new ValidatedModelAssignment("mcu-echo-reject", rejectingNoEchoProfile,
    echoIdentity, rejectingNoEchoModel);
var rejectingNoEchoOptions = new RenodeControllerOptions("mcu-echo-reject", "mcu-board-reject", "unused-for-model", string.Empty, string.Empty);
await using var rejectingNoEchoHost = new DeterministicModelEventHost("rover-echo-reject-test",
    [rejectingNoEchoOptions], [rejectingNoEchoAssignment]);
using var rejectingNoEchoCancellation = new CancellationTokenSource();
var rejectingNoEchoHostTask = rejectingNoEchoHost.RunAsync(rejectingNoEchoCancellation.Token);
var rejectingNoEchoEndpoints = await rejectingNoEchoHost.Ready.WaitAsync(TimeSpan.FromSeconds(5));
using var rejectingNoEchoEventClient = new TcpClient();
await rejectingNoEchoEventClient.ConnectAsync("127.0.0.1", rejectingNoEchoEndpoints.EventPort);
var rejectingNoEchoEventStream = rejectingNoEchoEventClient.GetStream();
var rejectingNoEchoHello = await ReadEnvelopeAsync(rejectingNoEchoEventStream, CancellationToken.None);
await WriteEnvelopeAsync(rejectingNoEchoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = rejectingNoEchoHello.SessionId,
    WorldId = "roboworld-local", RobotId = "rover-echo-reject-test",
    Hello = new Hello { ClientKind = "godot", InstanceVersion = "integration-test" }
}, CancellationToken.None);
await WriteEnvelopeAsync(rejectingNoEchoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = rejectingNoEchoHello.SessionId,
    WorldId = "roboworld-local", RobotId = "rover-echo-reject-test", Sequence = 1,
    SimTimeNs = 10_000_000,
    HardwareEvent = new HardwareEvent { Kind = HardwareEvent.Types.Kind.SensorSample, EndpointId = "distance.echo",
        Scalar = -1, Unit = "meters", EffectiveSimTimeNs = 10_000_000 }
}, CancellationToken.None);
await WriteEnvelopeAsync(rejectingNoEchoEventStream, new Envelope
{
    ProtocolMajor = 1, ProtocolMinor = 0, SessionId = rejectingNoEchoHello.SessionId,
    WorldId = "roboworld-local", RobotId = "rover-echo-reject-test", Sequence = 1,
    SimTimeNs = 0,
    Tick = new SimulationTick { TickIndex = 1, StartNs = 0, DeltaNs = 10_000_000, RealTimeFactor = 1 }
}, CancellationToken.None);
var noEchoDiagnostic = await ReadEnvelopeAsync(rejectingNoEchoEventStream, CancellationToken.None)
    .WaitAsync(TimeSpan.FromSeconds(5));
Require(noEchoDiagnostic.PayloadCase == Envelope.PayloadOneofCase.Diagnostic
    && noEchoDiagnostic.Diagnostic.Status == ComponentDiagnostic.Types.Status.Failed
    && noEchoDiagnostic.Diagnostic.ReasonCode == "E_CONTROLLER_MODEL_UNSUPPORTED"
    && noEchoDiagnostic.Diagnostic.Reason.Contains("restart in exact firmware mode", StringComparison.Ordinal),
    "Out-of-domain no-echo input sends an explicit exact-mode diagnostic before model shutdown.");
var noEchoHostRejected = false;
try { await rejectingNoEchoHostTask.WaitAsync(TimeSpan.FromSeconds(5)); }
catch (InvalidDataException exception) when (exception.Message.Contains("No-echo sensor behavior is outside", StringComparison.Ordinal))
{
    noEchoHostRejected = true;
}
Require(noEchoHostRejected, "Model host terminates the unsupported no-echo session after reporting the diagnostic.");

static async Task<string> ReadLineAsync(NetworkStream stream, TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    var bytes = new List<byte>();
    var one = new byte[1];
    while (true)
    {
        await stream.ReadExactlyAsync(one, cancellation.Token);
        if (one[0] == (byte)'\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
        bytes.Add(one[0]);
    }
}

static HashSet<int> GetRenodeProcessIds()
{
    var processIds = new HashSet<int>();
    foreach (var process in Process.GetProcesses())
    {
        try
        {
            if (process.ProcessName.Contains("renode", StringComparison.OrdinalIgnoreCase)) processIds.Add(process.Id);
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }
    return processIds;
}

static async Task<bool> IsTcpPortListeningAsync(int port)
{
    using var client = new TcpClient();
    try
    {
        await client.ConnectAsync("127.0.0.1", port).WaitAsync(TimeSpan.FromSeconds(2));
        return true;
    }
    catch (SocketException) { return false; }
    catch (TimeoutException) { return false; }
}

static async Task<Envelope> ReadEnvelopeAsync(NetworkStream stream, CancellationToken cancellationToken)
{
    var header = new byte[4];
    await stream.ReadExactlyAsync(header, cancellationToken);
    var length = BinaryPrimitives.ReadUInt32BigEndian(header);
    if (length is 0 or > 64 * 1024) throw new InvalidDataException("Invalid test frame length.");
    var payload = new byte[length];
    await stream.ReadExactlyAsync(payload, cancellationToken);
    return Envelope.Parser.ParseFrom(payload);
}

static async Task WriteEnvelopeAsync(NetworkStream stream, Envelope envelope, CancellationToken cancellationToken)
{
    var payload = envelope.ToByteArray();
    var frame = new byte[payload.Length + 4];
    BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
    payload.CopyTo(frame, 4);
    await stream.WriteAsync(frame, cancellationToken);
}

static async Task SendTickAndReadAckAsync(NetworkStream stream, string sessionId, string robotId, ulong tickIndex,
    CancellationToken cancellationToken, long initialSimTimeNs = 0)
{
    var startNs = checked(initialSimTimeNs + (long)(tickIndex - 1) * 10_000_000);
    await WriteEnvelopeAsync(stream, new Envelope
    {
        ProtocolMajor = 1, ProtocolMinor = 0, SessionId = sessionId, WorldId = "roboworld-local", RobotId = robotId,
        Sequence = tickIndex, SimTimeNs = startNs,
        Tick = new SimulationTick { TickIndex = tickIndex, StartNs = startNs, DeltaNs = 10_000_000, RealTimeFactor = 1 }
    }, cancellationToken);
    while (true)
    {
        var response = await ReadEnvelopeAsync(stream, cancellationToken).WaitAsync(TimeSpan.FromSeconds(5));
        if (response.PayloadCase == Envelope.PayloadOneofCase.Diagnostic)
            throw new InvalidDataException("Model host reported a diagnostic: " + response.Diagnostic.Reason);
        if (response.PayloadCase == Envelope.PayloadOneofCase.Tick)
        {
            if (response.Tick.TickIndex != tickIndex) throw new InvalidDataException("Model host acknowledged an unexpected tick.");
            return;
        }
    }
}

static string FindRepositoryRoot(string start)
{
    var directory = new DirectoryInfo(start);
    while (directory is not null)
    {
        if (File.Exists(Path.Combine(directory.FullName, "RoboWorld.slnx"))) return directory.FullName;
        directory = directory.Parent;
    }
    throw new DirectoryNotFoundException("Could not locate RoboWorld.slnx from the service test output directory.");
}
