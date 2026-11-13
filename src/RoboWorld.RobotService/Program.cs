using RoboWorld.Contracts;
using RoboWorld.Domain;
using RoboWorld.RobotService;
using System.Text;
using System.Text.Json;

if (args.Contains("--renode-motor-host", StringComparer.Ordinal))
{
    var modeArgument = args.FirstOrDefault(value => value.StartsWith("--controller-mode=", StringComparison.Ordinal));
    var requestedMode = modeArgument is null ? ControllerExecutionMode.ExactFirmware : modeArgument["--controller-mode=".Length..] switch
    {
        "exact" => ControllerExecutionMode.ExactFirmware,
        "fast" => ControllerExecutionMode.ValidatedModel,
        "auto" => ControllerExecutionMode.Automatic,
        _ => throw new InvalidDataException("Controller mode must be exact, fast, or auto.")
    };
    var rootArgument = args.FirstOrDefault(value => value.StartsWith("--root=", StringComparison.Ordinal));
    var root = rootArgument is null ? Environment.CurrentDirectory : rootArgument["--root=".Length..];
    using var shutdown = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
    var testShutdownOption = args.FirstOrDefault(value => value.StartsWith("--test-shutdown-after-ms=", StringComparison.Ordinal));
    if (testShutdownOption is not null)
    {
        if (!int.TryParse(testShutdownOption.AsSpan("--test-shutdown-after-ms=".Length), out var testShutdownAfterMs)
            || testShutdownAfterMs is < 1000 or > 120_000)
            throw new InvalidDataException("Test shutdown delay must be between 1000 and 120000 milliseconds.");
        shutdown.CancelAfter(testShutdownAfterMs);
    }
    var faultOption = args.FirstOrDefault(value => value.StartsWith("--test-route-fault-after-ms=", StringComparison.Ordinal));
    int? faultAfterMs = faultOption is not null && int.TryParse(faultOption.AsSpan("--test-route-fault-after-ms=".Length), out var parsedFaultAfterMs) ? parsedFaultAfterMs : null;
    var routeFaultSimTimeOption = args.FirstOrDefault(value => value.StartsWith("--test-route-fault-at-sim-time-ns=", StringComparison.Ordinal));
    long? routeFaultAtSimTimeNs = null;
    if (routeFaultSimTimeOption is not null)
    {
        if (!long.TryParse(routeFaultSimTimeOption.AsSpan("--test-route-fault-at-sim-time-ns=".Length), out var parsedRouteFaultSimTimeNs)
            || parsedRouteFaultSimTimeNs <= 0) throw new InvalidDataException("Route-fault simulation time must be a positive integer nanosecond boundary.");
        routeFaultAtSimTimeNs = parsedRouteFaultSimTimeNs;
    }
    var haltOption = args.FirstOrDefault(value => value.StartsWith("--test-mcu-halt-after-ms=", StringComparison.Ordinal));
    int? haltAfterMs = haltOption is not null && int.TryParse(haltOption.AsSpan("--test-mcu-halt-after-ms=".Length), out var parsedHaltAfterMs) ? parsedHaltAfterMs : null;
    var haltSimTimeOption = args.FirstOrDefault(value => value.StartsWith("--test-mcu-halt-at-sim-time-ns=", StringComparison.Ordinal));
    long? cpuHaltAtSimTimeNs = null;
    if (haltSimTimeOption is not null)
    {
        if (!long.TryParse(haltSimTimeOption.AsSpan("--test-mcu-halt-at-sim-time-ns=".Length), out var parsedHaltSimTimeNs)
            || parsedHaltSimTimeNs <= 0) throw new InvalidDataException("MCU-halt simulation time must be a positive integer nanosecond boundary.");
        cpuHaltAtSimTimeNs = parsedHaltSimTimeNs;
    }
    var modelHaltSimTimeOption = args.FirstOrDefault(value => value.StartsWith("--test-model-mcu-halt-at-sim-time-ns=", StringComparison.Ordinal));
    long? modelHaltAtSimTimeNs = null;
    if (modelHaltSimTimeOption is not null)
    {
        if (!long.TryParse(modelHaltSimTimeOption.AsSpan("--test-model-mcu-halt-at-sim-time-ns=".Length), out var parsedModelHaltSimTimeNs)
            || parsedModelHaltSimTimeNs <= 0) throw new InvalidDataException("Model MCU-halt simulation time must be a positive integer nanosecond boundary.");
        modelHaltAtSimTimeNs = parsedModelHaltSimTimeNs;
    }
    var haltControllerOption = args.FirstOrDefault(value => value.StartsWith("--test-mcu-halt-controller-id=", StringComparison.Ordinal));
    var haltControllerId = haltControllerOption is null ? null : haltControllerOption["--test-mcu-halt-controller-id=".Length..];
    var firmwareOption = args.FirstOrDefault(value => value.StartsWith("--firmware=", StringComparison.Ordinal));
    var firmwarePath = firmwareOption is null ? null : firmwareOption["--firmware=".Length..];
    var performanceDirectoryOption = args.FirstOrDefault(value => value.StartsWith("--perf-trace-dir=", StringComparison.Ordinal));
    var performanceDirectory = performanceDirectoryOption is null ? null : performanceDirectoryOption["--perf-trace-dir=".Length..];
    var performanceScenarioOption = args.FirstOrDefault(value => value.StartsWith("--perf-scenario=", StringComparison.Ordinal));
    var performanceScenario = performanceScenarioOption is null ? "unspecified" : performanceScenarioOption["--perf-scenario=".Length..];
    var characterizationOption = args.FirstOrDefault(value => value.StartsWith("--characterization-trace=", StringComparison.Ordinal));
    var characterizationTrace = characterizationOption is null ? null : characterizationOption["--characterization-trace=".Length..];
    var channel1Option = args.FirstOrDefault(value => value.StartsWith("--actuator-channel1=", StringComparison.Ordinal));
    var channel2Option = args.FirstOrDefault(value => value.StartsWith("--actuator-channel2=", StringComparison.Ordinal));
    var channel1 = channel1Option is null ? "motor-left" : channel1Option["--actuator-channel1=".Length..];
    var channel2 = channel2Option is null ? "motor-right" : channel2Option["--actuator-channel2=".Length..];
    var controllerConfigurations = args.Where(value => value.StartsWith("--controller-config=", StringComparison.Ordinal))
        .Select(value =>
        {
            try { return JsonSerializer.Deserialize<RenodeControllerOptions>(Encoding.UTF8.GetString(Convert.FromBase64String(value["--controller-config=".Length..])), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Controller configuration is empty."); }
            catch (FormatException exception) { throw new InvalidDataException("Controller configuration is not valid Base64 JSON.", exception); }
        }).ToArray();
    var profileReferences = args.Where(value => value.StartsWith("--controller-profile-reference=", StringComparison.Ordinal))
        .Select(value =>
        {
            try { return JsonSerializer.Deserialize<ControllerProfileStartupReference>(Encoding.UTF8.GetString(Convert.FromBase64String(value["--controller-profile-reference=".Length..])), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidDataException("Controller profile startup reference is empty."); }
            catch (FormatException exception) { throw new InvalidDataException("Controller profile startup reference is not valid Base64 JSON.", exception); }
            catch (JsonException exception) { throw new InvalidDataException("Controller profile startup reference is not valid JSON.", exception); }
        }).ToArray();
    var profilesRootOption = args.FirstOrDefault(value => value.StartsWith("--controller-profiles-root=", StringComparison.Ordinal));
    var profilesRoot = profilesRootOption is null ? Path.Combine(root, "controller-profiles") : profilesRootOption["--controller-profiles-root=".Length..];
    var componentDirectoryOption = args.FirstOrDefault(value => value.StartsWith("--component-directory=", StringComparison.Ordinal));
    var componentDirectory = componentDirectoryOption is null ? Path.Combine(root, "examples", "components") : componentDirectoryOption["--component-directory=".Length..];
    var peripheralMapOption = args.FirstOrDefault(value => value.StartsWith("--peripheral-map-source=", StringComparison.Ordinal));
    var peripheralMapSource = peripheralMapOption is null ? Path.Combine(root, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs") : peripheralMapOption["--peripheral-map-source=".Length..];
    var projectProfileReferences = profileReferences.ToDictionary(reference => reference.RobotId + "/" + reference.ControllerId,
        reference => new ProjectControllerProfileReference(reference.ProfileId, reference.ProfileSha256), StringComparer.Ordinal);
    IReadOnlyList<ValidatedModelAssignment> modelAssignments = [];
    ControllerBackendResolution resolution;
    try
    {
        resolution = ControllerBackendSelector.Resolve(requestedMode, () =>
        {
            modelAssignments = ControllerProfileAssignmentResolver.LoadAssignments(controllerConfigurations, profileReferences,
                projectProfileReferences, profilesRoot, componentDirectory, peripheralMapSource);
            return new DeterministicModelBackend(modelAssignments);
        });
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine($"CONTROLLER_EXECUTION_REJECTED mode={requestedMode} reason={exception.Message}");
        Environment.Exit(2);
        return;
    }
    Console.WriteLine($"CONTROLLER_EXECUTION_SELECTED kind={resolution.Kind} reason={resolution.Reason}");
    if (resolution.Kind == ControllerBackendKind.DeterministicModel)
    {
        var modelBackend = resolution.ModelBackend as DeterministicModelBackend
            ?? throw new InvalidDataException("Validated model selection did not return the deterministic runtime backend.");
        await using var modelHost = new DeterministicModelEventHost("rover-01", controllerConfigurations, modelAssignments, modelBackend,
            modelHaltAtSimTimeNs, performanceDirectory, performanceScenario,
            RobotServiceSimulationClock.ControllerStartupEpochNs);
        await modelHost.RunAsync(shutdown.Token);
        return;
    }
    await using var host = new RenodeMotorEventHost(root, faultAfterMs, haltAfterMs, firmwarePath, channel1, channel2, haltControllerId,
        controllerConfigurations.Length == 0 ? null : controllerConfigurations, performanceDirectory, performanceScenario, characterizationTrace,
        routeFaultAtSimTimeNs, cpuHaltAtSimTimeNs);
    await host.RunAsync(shutdown.Token);
    if (testShutdownOption is not null && shutdown.IsCancellationRequested)
        Console.WriteLine("STOPPED RoboWorld RobotService reason=test-shutdown-after-ms runtime_cleanup=complete");
    return;
}

if (args.Contains("--demo-worker", StringComparer.Ordinal))
{
    Console.WriteLine("READY RoboWorld demo service health=healthy mode=T02");
    var crashAfter = args.FirstOrDefault(value => value.StartsWith("--crash-after-ms=", StringComparison.Ordinal));
    if (crashAfter is not null && int.TryParse(crashAfter.AsSpan("--crash-after-ms=".Length), out var crashMs))
    {
        await Task.Delay(Math.Max(crashMs, 1));
        Console.Error.WriteLine("INTENTIONAL T02 worker lifecycle probe exit");
        Environment.Exit(17);
    }
    using var quit = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; quit.Cancel(); };
    try { await Task.Delay(Timeout.InfiniteTimeSpan, quit.Token); }
    catch (OperationCanceledException) { }
    Console.WriteLine("STOPPED RoboWorld demo service");
    return;
}

Console.WriteLine($"RoboWorld RobotService scaffold (contract schema v{ContractVersion.SchemaVersion}).");
Console.WriteLine("No emulator or peripheral behavior is implemented in T00.");
