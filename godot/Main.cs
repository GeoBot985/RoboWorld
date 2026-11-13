using System.Diagnostics;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using RoboWorld.Domain;
using RoboWorld.Contracts.V1;
using Godot;

public partial class Main : Node3D
{
    private const long ClockDenominator = 120;
    private const long ClockNumeratorNs = 1_000_000_000;
    private readonly List<RigidBody3D> _bodies = [];
    private readonly Dictionary<string, RigidBody3D> _linkBodies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Node3D> _componentVisuals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Camera3D> _robotCameras = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TreeItem> _diagnosticRows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _diagnosticLastStates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CollisionShape3D> _shopCollisionShapes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Joint3D> _jointNodes = new(StringComparer.Ordinal);
    private const int HardwareEventQueueCapacity = 512;
    private readonly BoundedInbox<Envelope> _hardwareEvents = new(HardwareEventQueueCapacity);
    private TaskCompletionSource<SimulationTick>? _clockAckSource;
    private TaskCompletionSource<bool>? _clockAckResumeBoundarySource;
    private bool _clockAckResumeBoundaryCallbackActive;
    private ulong _lastAcknowledgedClockTickIndex;
    private long _clockAckArrivalStopwatchTicks;
    private bool _transportPauseActive;
    private bool _physicsStepGranted;
    private int _physicsCallbacksSuppressedForAck;
    private int _physicsCallbacksSincePreviousFrame;
    private long _physicsCallbackSequence;
    private bool _performanceStartupPauseActive;
    private bool _performanceFinishRequested;
    private bool _clockSyncFaulted;
    private NetworkStream? _eventStream;
    private string _eventSessionId = "";
    private ulong _clockTickIndex;
    private Process? _worker;
    private string _lastWorkerError = "none";
    private CancellationTokenSource? _eventBridgeStop;
    private Label _metrics = null!;
    private Label _workerStatus = null!;
    private Label _controllerModeStatus = null!;
    private Label _viewModeStatus = null!;
    private Button _robotViewButton = null!;
    private OptionButton _robotCameraPicker = null!;
    private OptionButton _controllerModePicker = null!;
    private Label _componentStatus = null!;
    private Label _diagnosticDetails = null!;
    private Label _shopDetails = null!;
    private Tree _diagnosticTree = null!;
    private OptionButton _snapTargetPicker = null!;
    private OptionButton _shopMountMode = null!;
    private Button _snapShopButton = null!;
    private Button _resizeShopButton = null!;
    private Button _removeShopButton = null!;
    private Button _addFreeShopButton = null!;
    private Button _editShopPoseButton = null!;
    private Window _shopPoseWindow = null!;
    private SpinBox[] _shopPosePositionInputs = [];
    private SpinBox[] _shopPoseRotationInputs = [];
    private Label _shopPoseStatus = null!;
    private Window _electricalEditorWindow = null!;
    private Tree _electricalConnectionTree = null!;
    private OptionButton _electricalFromPicker = null!;
    private OptionButton _electricalToPicker = null!;
    private LineEdit _electricalBusInput = null!;
    private Label _electricalEditorStatus = null!;
    private Label _electricalSummary = null!;
    private Button _addElectricalConnectionButton = null!;
    private Button _removeElectricalConnectionButton = null!;
    private readonly List<(string ParentComponentId, string ParentPortId, string ChildPortId)> _snapCandidates = [];
    private SpinBox[] _shopSizeInputs = [];
    private FileDialog _saveProjectDialog = null!;
    private FileDialog _openProjectDialog = null!;
    private long _physicsTicks;
    private long _simTimeNs;
    private long _clockForwardTimeNs;
    private long _clockRemainder;
    private long _lastPhysicsDeltaNs;
    private long _previousPhysicsDeltaNs;
    private long _sessionStarted;
    private string _workerState = "STARTING";
    private bool _paused;
    private int _renderCap;
    private double _lastMeasureTime;
    private long _lastMeasureTicks;
    private double _measuredPhysicsHz;
    private WorldDefinition _world = null!;
    private Node3D? _worldCameraPivot;
    private Camera3D? _worldCamera;
    private bool _worldOrbitDragging;
    private bool _worldPanDragging;
    private bool _robotViewActive;
    private bool _cameraFollowSmoke;
    private bool _cameraFollowSmokeComplete;
    private bool _worldCollisionSmoke;
    private Camera3D? _cameraFollowProbe;
    private RigidBody3D? _cameraFollowBody;
    private Transform3D _cameraRelativeTransform;
    private Vector3 _cameraFollowInitialBodyPosition;
    private Vector3 _cameraFollowInitialWorldPosition;
    private Quaternion _cameraFollowInitialWorldRotation;
    private Vector2 _worldCameraLastPointer;
    private GodotPhysicsWorldAdapter _physicsAdapter = null!;
    private PhysicalSnapshot? _latestSnapshot;
    private RigidBody3D? _probe;
    private readonly StringBuilder _probeTrajectory = new();
    private readonly StringBuilder _actuationSmokeTrajectory = new();
    private List<(long SimTimeNs, double Left, double Right)> _actuationReplayEvents = [];
    private int _actuationReplayIndex;
    private long? _actuationReplayEndSimTimeNs;
    private string? _actuationReplayOutputPath;
    private string _projectRoot = "";
    private bool _benchmarkWritten;
    private bool _injectWorkerFailure;
    private bool _workerFailureSeen;
    private bool _workerRestarted;
    private ulong _workerFailureAtMs;
    private bool _benchmarkRun;
    private bool _eventBridgeConnected;
    private bool _clockEpochReady;
    private bool _eventBridgePending;
    private bool _testClockEpochRace;
    private long _lastLeftEventSimTimeNs = -1;
    private long _lastRightEventSimTimeNs = -1;
    private readonly Dictionary<string, long> _actuatorEventTimes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _actuatorDuties = new(StringComparer.Ordinal);
    private readonly HashSet<string> _haltedActuatorIds = new(StringComparer.Ordinal);
    private ulong _lastEventSequence;
    private string _uartEndpoint = "UART  INACTIVE";
    private readonly SortedDictionary<string, string> _uartEndpoints = new(StringComparer.Ordinal);
    private bool _actuationSmoke;
    private bool _actuationNoLoadSmoke;
    private bool _actuationLoadedSmoke;
    private bool _requireClockVerificationForSmoke;
    private bool _routeFaultSmoke;
    private long? _routeFaultAtSimTimeNs;
    private bool _mcuHaltSmoke;
    private long? _mcuHaltAtSimTimeNs;
    private long _actuationSmokeStartedHostTicks;
    private long _actuationSmokeStartSimTimeNs;
    private double? _testActuationSmokeDurationSeconds;
    private long _testActuationSmokeClockDurationNs = 1_000_000_000;
    private long _armActuationStartedPhysicsTick;
    private bool _armActuationSmoke;
    private bool _multiControllerIsolationSmoke;
    private Quaternion _armInitialRelativeRotation = Quaternion.Identity;
    private bool _armInitialRotationCaptured;
    private double _armPeakDuty;
    private double _motorLeftDuty;
    private double _motorRightDuty;
    private readonly Dictionary<string, int> _motorActuatorChannels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _motorDuties = new(StringComparer.Ordinal);
    private string? _motorChannel1ActuatorId;
    private string? _motorChannel2ActuatorId;
    private bool _sensorE2e;
    private Node3D? _distanceSensor;
    private string? _distanceSensorComponentId;
    private StaticBody3D? _sensorE2eObstacle;
    private float[] _sensorObstacleZSequence = [];
    private int _sensorNoEchoAfterSample = -1;
    private int _sensorSampleCount;
    private int _sensorObstacleSequenceIndex = -1;
    private string? _evidenceTaskOverride;
    private long _lastSensorSampleNs = -1;
    private bool _pauseE2e;
    private bool _pauseResumePending;
    private bool _pauseResumed;
    private long _pauseStartedSimTimeNs;
    private long _pauseResumePhysicsTick;
    private ProjectPackageInfo _project = null!;
    private AssemblyGraph _activeGraph = null!;
    private RobotAuthoringDraft? _authoringDraft;
    private RobotAuthoringViewport? _authoringViewport;
    private AssemblyGraph? _authoringTemplateGraph;
    private Action? _refreshAuthoringWorkspace;
    private IReadOnlyDictionary<string, ComponentDefinition> _catalogue = null!;
    private IReadOnlyDictionary<string, RobotFirmwareArtifact> _firmwareArtifacts = new Dictionary<string, RobotFirmwareArtifact>(StringComparer.Ordinal);
    private string? _selectedShopCatalogueId;
    private string? _selectedShopInstanceId;
    private string? _highlightedComponentId;
    private GeometryInstance3D? _highlightedVisual;
    private string _firmwareSha256 = "unknown";
    private string _lastDiagnosticReason = "none";
    private string _lastDiagnosticComponent = "none";
    private long _uartRxBytes;
    private long _uartTxBytes;
    private long _renodeSimTimeNs;
    private double _renodeMeasuredRtf;
    private uint _sensorDistanceMm;
    private bool _sensorReadingValid;
    private bool _mcuHalted;
    private bool _driverRouteFailed;
    private bool _serviceProcessFailed;
    private bool _startupFailed;
    private bool _authoringMode;
    private bool _authoringSmokeMode;
    private bool _authoringLaunchSmoke;
    private bool _assemblySmoke;
    private bool _shopSnapSmoke;
    private bool _checkpointPhysicsSmoke;
    private bool _quickActuationSmoke;
    private string _activeRobotId = "rover-01";
    private string? _projectArgumentPath;
    private string? _temporaryRunPackagePath;
    private bool _handingOffTemporaryRunPackage;
    private GodotPerformanceTrace? _performanceTrace;
    private string? _characterizationTracePath;
    private string _controllerExecutionMode = "exact";
    private bool _controllerModeArgumentProvided;
    private string? _activeControllerExecutionMode;
    private string _activeControllerBackend = "preflight pending";
    private string _controllerExecutionReason = "Waiting for Robot Service preflight.";
    private string? _performanceScenario;
    private int _performanceDurationSeconds;
    private int _performanceSimDurationSeconds;
    private int _performanceRenderCap = 60;
    private bool _presentationDecimationActive;
    private bool _presentationRequestOnNextLoop = true;
    private long _presentationFrameIndex;
    private long _performancePhaseStartTicks;
    private long _performancePhaseStartSimNs;
    private long _lastPerformanceFrameTicks;
    private bool _performanceFinished;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _sessionStarted = Stopwatch.GetTimestamp();
        _projectRoot = ResolveApplicationRoot();
        var examples = Path.Combine(_projectRoot, "examples");
        var defaultFirmware = Path.Combine(_projectRoot, "samples", "firmware", "stm32-drive", "build", "firmware.elf");
        var args = OS.GetCmdlineUserArgs();
        _authoringSmokeMode = args.Contains("--authoring-smoke", StringComparer.Ordinal);
        _cameraFollowSmoke = args.Contains("--camera-follow-smoke", StringComparer.Ordinal);
        _worldCollisionSmoke = args.Contains("--world-collision-smoke", StringComparer.Ordinal);
        var controllerModeArgument = args.FirstOrDefault(arg => arg.StartsWith("--controller-mode=", StringComparison.Ordinal));
        if (controllerModeArgument is not null)
        {
            _controllerModeArgumentProvided = true;
            _controllerExecutionMode = controllerModeArgument["--controller-mode=".Length..];
            if (_controllerExecutionMode is not ("exact" or "fast" or "auto")) throw new InvalidDataException("Controller mode must be exact, fast, or auto.");
        }
        var characterizationArgument = args.FirstOrDefault(arg => arg.StartsWith("--characterization-trace=", StringComparison.Ordinal));
        if (characterizationArgument is not null) _characterizationTracePath = characterizationArgument["--characterization-trace=".Length..];
        var routeFaultSimTimeArgument = args.FirstOrDefault(arg => arg.StartsWith("--test-route-fault-at-sim-time-ns=", StringComparison.Ordinal));
        if (routeFaultSimTimeArgument is not null)
        {
            if (!long.TryParse(routeFaultSimTimeArgument.AsSpan("--test-route-fault-at-sim-time-ns=".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var routeFaultSimTimeNs) || routeFaultSimTimeNs <= 0)
                throw new InvalidDataException("Route-fault simulation time must be a positive integer nanosecond boundary.");
            _routeFaultAtSimTimeNs = routeFaultSimTimeNs;
        }
        var mcuHaltSimTimeArgument = args.FirstOrDefault(arg => arg.StartsWith("--test-mcu-halt-at-sim-time-ns=", StringComparison.Ordinal));
        if (mcuHaltSimTimeArgument is not null)
        {
            if (!long.TryParse(mcuHaltSimTimeArgument.AsSpan("--test-mcu-halt-at-sim-time-ns=".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var mcuHaltSimTimeNs) || mcuHaltSimTimeNs <= 0)
                throw new InvalidDataException("MCU-halt simulation time must be a positive integer nanosecond boundary.");
            _mcuHaltAtSimTimeNs = mcuHaltSimTimeNs;
        }
        var performanceScenarioArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-scenario=", StringComparison.Ordinal));
        var performanceOutputArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-output-dir=", StringComparison.Ordinal));
        var performanceDurationArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-duration-seconds=", StringComparison.Ordinal));
        var performanceSimDurationArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-sim-duration-seconds=", StringComparison.Ordinal));
        var performanceRenderArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-render-fps=", StringComparison.Ordinal));
        var performanceFlushTicksArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-flush-ticks-each-record=", StringComparison.Ordinal));
        if (performanceScenarioArgument is not null)
        {
            _performanceScenario = performanceScenarioArgument["--perf-scenario=".Length..];
            if (_performanceScenario is not ("headless" or "idle" or "actuated" or "sensor" or "two-mcu" or "four-mcu"))
                throw new InvalidDataException($"Unknown performance scenario '{_performanceScenario}'.");
            if (performanceOutputArgument is null || string.IsNullOrWhiteSpace(performanceOutputArgument["--perf-output-dir=".Length..]))
                throw new InvalidDataException("Performance mode requires --perf-output-dir=<directory>.");
            _performanceDurationSeconds = performanceDurationArgument is not null && int.TryParse(performanceDurationArgument.AsSpan("--perf-duration-seconds=".Length), out var parsedDuration) ? parsedDuration : 60;
            _performanceSimDurationSeconds = performanceSimDurationArgument is not null && int.TryParse(performanceSimDurationArgument.AsSpan("--perf-sim-duration-seconds=".Length), out var parsedSimDuration) ? parsedSimDuration : 0;
            _performanceRenderCap = performanceRenderArgument is not null && int.TryParse(performanceRenderArgument.AsSpan("--perf-render-fps=".Length), out var parsedRenderCap) ? parsedRenderCap : 60;
            if (_performanceDurationSeconds <= 0 || _performanceSimDurationSeconds < 0 || _performanceRenderCap is not (0 or 15 or 30 or 60 or 120 or 144))
                throw new InvalidDataException("Performance duration must be positive and render FPS must be 0, 15, 30, 60, 120 or 144.");
            var flushTicksEachRecord = performanceFlushTicksArgument is not null && bool.TryParse(performanceFlushTicksArgument["--perf-flush-ticks-each-record=".Length..], out var parsedFlushTicks) && parsedFlushTicks;
            _performanceTrace = new GodotPerformanceTrace(performanceOutputArgument["--perf-output-dir=".Length..], _performanceScenario, flushTicksEachRecord);
            Console.WriteLine($"PERF_CONFIG scenario={_performanceScenario} duration_seconds={_performanceDurationSeconds} sim_duration_seconds={_performanceSimDurationSeconds} render_cap={_performanceRenderCap} flush_ticks_each_record={flushTicksEachRecord} physics_hz=120 stopwatch_frequency={Stopwatch.Frequency} output={_performanceTrace.DirectoryPath}");
        }
        var projectArgument = args.FirstOrDefault(arg => arg.StartsWith("--project=", StringComparison.Ordinal));
        _projectArgumentPath = projectArgument is null ? null : projectArgument["--project=".Length..];
        var temporaryRunPackageArgument = args.FirstOrDefault(arg => arg.StartsWith("--temporary-run-package=", StringComparison.Ordinal));
        _temporaryRunPackagePath = temporaryRunPackageArgument is null ? null : Path.GetFullPath(temporaryRunPackageArgument["--temporary-run-package=".Length..]);
        try
        {
            _project = projectArgument is null
                ? ProjectPackageStore.OpenDirectory(examples, defaultFirmware)
                : ProjectPackageStore.OpenPackage(projectArgument["--project=".Length..]);
        }
        catch (Exception exception)
        {
            _startupFailed = true;
            Console.Error.WriteLine($"PROJECT_LOAD_FAILED {exception.Message}");
            GetTree().Quit(1);
            return;
        }
        if (!_controllerModeArgumentProvided) _controllerExecutionMode = _project.ControllerExecutionSettings.Mode;
        // The clock bridge commits one authoritative 120 Hz interval, then needs
        // the next fixed-physics callback to integrate that interval. In the live
        // controller runtime, run Godot's fixed callback loop above 2x and scale
        // each server step to exactly 1/120 s. Preserve the base engine
        // clock for isolated T02 physics probes. The simulation clock itself
        // remains integer nanoseconds and is never derived from Engine.TimeScale.
        if (_controllerExecutionMode == "fast" || _performanceTrace is not null)
        {
            Engine.PhysicsTicksPerSecond = 250;
            Engine.TimeScale = Engine.PhysicsTicksPerSecond / 120d;
            Engine.PhysicsJitterFix = 0;
            Console.WriteLine($"PHYSICS_CLOCK_CONFIGURATION physics_ticks_setting_hz={Engine.PhysicsTicksPerSecond} time_scale={Engine.TimeScale.ToString("F9", CultureInfo.InvariantCulture)} physics_jitter_fix=0 target_server_step_hz=120 authoritative_clock_hz=120 render_cap={(_performanceTrace is null ? _renderCap : _performanceRenderCap)}");
        }
        if (args.Contains("--new-empty-robot", StringComparer.Ordinal) || args.Any(arg => arg.StartsWith("--authoring-draft=", StringComparison.Ordinal)))
        {
            _authoringMode = true;
            var draftPath = args.FirstOrDefault(arg => arg.StartsWith("--authoring-draft=", StringComparison.Ordinal))?["--authoring-draft=".Length..];
            _catalogue = FixtureLoader.LoadCatalogue(_project.ComponentDirectory);
            _firmwareArtifacts = RobotFirmwareCatalog.FromProject(_project).ToDictionary(item => item.Id, StringComparer.Ordinal);
            _world = FixtureLoader.LoadWorld(_project.WorldPath);
            var requestedDraft = draftPath is null ? null : RobotAuthoringDraftStore.Open(draftPath, _catalogue);
            var projectGraphs = _project.RobotPaths.Select(path => FixtureLoader.LoadRobot(path, _project.ComponentDirectory)).ToArray();
            _authoringTemplateGraph = requestedDraft is null ? projectGraphs.FirstOrDefault()
                : projectGraphs.SingleOrDefault(graph => graph.Id == requestedDraft.Id);
            _physicsAdapter = new GodotPhysicsWorldAdapter();
            BuildCameraAndLighting();
            BuildWorld();
            var draft = requestedDraft ?? RobotAuthoringDraft.CreateEmpty("robot-" + Guid.NewGuid().ToString("N"), "New Robot");
            BuildAuthoringWorkspace(draft);
            if (args.Contains("--authoring-smoke", StringComparer.Ordinal)) RunAuthoringSmoke(draft);
            return;
        }
        var robotGraphs = _project.RobotPaths.Select(path => FixtureLoader.LoadRobot(path, _project.ComponentDirectory)).ToArray();
        var requestedRobotId = args.FirstOrDefault(arg => arg.StartsWith("--robot-id=", StringComparison.Ordinal))?["--robot-id=".Length..];
        var robot = requestedRobotId is null
            ? robotGraphs[0]
            : robotGraphs.SingleOrDefault(candidate => candidate.Id == requestedRobotId)
                ?? throw new InvalidDataException($"Robot id '{requestedRobotId}' is not present in this project.");
        _activeRobotId = robot.Id;
        if (_project.ControllersByRobotId.TryGetValue(robot.Id, out var robotControllers) && robotControllers.Count > 0)
            _firmwareSha256 = robotControllers[0].FirmwareSha256;
        var additionalRobots = requestedRobotId is null
            ? robotGraphs.Skip(1).Where(candidate => candidate.Id == "arm-01").ToArray()
            : [];
        var catalogue = FixtureLoader.LoadCatalogue(_project.ComponentDirectory);
        _activeGraph = robot;
        _catalogue = catalogue;
        _world = FixtureLoader.LoadWorld(_project.WorldPath);
        _physicsAdapter = new GodotPhysicsWorldAdapter();

        _actuationNoLoadSmoke = args.Contains("--actuation-smoke", StringComparer.Ordinal);
        _actuationLoadedSmoke = args.Contains("--actuation-loaded-smoke", StringComparer.Ordinal);
        var testSmokeDurationArgument = args.FirstOrDefault(arg => arg.StartsWith("--test-actuation-smoke-duration-seconds=", StringComparison.Ordinal));
        if (testSmokeDurationArgument is not null)
        {
            if (!double.TryParse(testSmokeDurationArgument["--test-actuation-smoke-duration-seconds=".Length..], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var duration) || !double.IsFinite(duration) || duration is < 1 or > 120)
                throw new InvalidDataException("Test actuation smoke duration must be finite and within 1–120 wall seconds.");
            _testActuationSmokeDurationSeconds = duration;
        }
        _requireClockVerificationForSmoke = args.Contains("--require-clock-verification", StringComparer.Ordinal);
        var testSmokeClockNsArgument = args.FirstOrDefault(arg => arg.StartsWith("--test-actuation-smoke-clock-ns=", StringComparison.Ordinal));
        if (testSmokeClockNsArgument is not null
            && (!long.TryParse(testSmokeClockNsArgument.AsSpan("--test-actuation-smoke-clock-ns=".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out _testActuationSmokeClockDurationNs)
                || _testActuationSmokeClockDurationNs <= 0))
            throw new InvalidDataException("Test actuation smoke clock duration must be positive integer nanoseconds.");
        if (_requireClockVerificationForSmoke) Console.WriteLine($"ACTUATION_SMOKE_CLOCK_GATE required_sim_ns={_testActuationSmokeClockDurationNs} mode=enabled");
        _actuationSmoke = _actuationNoLoadSmoke || _actuationLoadedSmoke;
        var actuationReplayArgument = args.FirstOrDefault(arg => arg.StartsWith("--actuation-replay=", StringComparison.Ordinal));
        var actuationReplayOutputArgument = args.FirstOrDefault(arg => arg.StartsWith("--actuation-replay-output=", StringComparison.Ordinal));
        var actuationReplayEpochArgument = args.FirstOrDefault(arg => arg.StartsWith("--actuation-replay-epoch-ns=", StringComparison.Ordinal));
        if (actuationReplayArgument is not null || actuationReplayOutputArgument is not null)
        {
            if (actuationReplayArgument is null || actuationReplayOutputArgument is null)
                throw new InvalidDataException("Actuation replay requires both --actuation-replay=<csv> and --actuation-replay-output=<csv>.");
            _actuationReplayEvents = ReadActuationReplayEvents(actuationReplayArgument["--actuation-replay=".Length..]);
            if (_actuationReplayEvents.Count < 2) throw new InvalidDataException("Actuation replay requires at least two timestamped output states.");
            var replayEpochNs = actuationReplayEpochArgument is null ? _actuationReplayEvents[0].SimTimeNs
                : long.TryParse(actuationReplayEpochArgument.AsSpan("--actuation-replay-epoch-ns=".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsedReplayEpoch) && parsedReplayEpoch >= 0
                    ? parsedReplayEpoch : throw new InvalidDataException("Actuation replay epoch must be a non-negative simulation timestamp.");
            if (_actuationReplayEvents[0].SimTimeNs != replayEpochNs)
                throw new InvalidDataException("Actuation replay's first output state must match its authoritative simulation epoch.");
            _actuationReplayOutputPath = Path.GetFullPath(actuationReplayOutputArgument["--actuation-replay-output=".Length..]);
            var replayEndArgument = args.FirstOrDefault(arg => arg.StartsWith("--actuation-replay-end-sim-time-ns=", StringComparison.Ordinal));
            var replayEndNs = replayEndArgument is null ? checked(_actuationReplayEvents[^1].SimTimeNs + 2_500_000_000L)
                : long.TryParse(replayEndArgument.AsSpan("--actuation-replay-end-sim-time-ns=".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out var parsedReplayEnd) ? parsedReplayEnd
                    : throw new InvalidDataException("Actuation replay end time must be a non-negative simulation timestamp.");
            if (replayEndNs <= _actuationReplayEvents[^1].SimTimeNs)
                throw new InvalidDataException("Actuation replay horizon must follow every actuator event.");
            _actuationReplayEndSimTimeNs = replayEndNs;
            _simTimeNs = replayEpochNs;
            _clockForwardTimeNs = replayEpochNs;
            _actuationSmoke = _actuationLoadedSmoke = true;
            _actuationSmokeStartedHostTicks = Stopwatch.GetTimestamp();
            _actuationSmokeStartSimTimeNs = replayEpochNs;
            Console.WriteLine($"ACTUATION_REPLAY mode=offline source=precomputed-actuator-trace clock=120Hz physics=live epoch_ns={replayEpochNs} end_ns={replayEndNs} input_states={_actuationReplayEvents.Count} worker=disabled output={_actuationReplayOutputPath}");
        }
        _routeFaultSmoke = args.Contains("--actuation-fault-smoke", StringComparer.Ordinal);
        _mcuHaltSmoke = args.Contains("--actuation-halt-smoke", StringComparer.Ordinal);
        _sensorE2e = args.Contains("--sensor-e2e", StringComparer.Ordinal) || _performanceScenario == "sensor";
        var evidenceTaskArgument = args.FirstOrDefault(arg => arg.StartsWith("--evidence-task=", StringComparison.Ordinal));
        if (evidenceTaskArgument is not null)
        {
            var evidenceTask = evidenceTaskArgument["--evidence-task=".Length..];
            if (evidenceTask.Length is < 1 or > 80 || evidenceTask.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
                throw new InvalidDataException("Evidence task must be 1–80 ASCII letters, digits, hyphens or underscores.");
            _evidenceTaskOverride = evidenceTask;
        }
        var sensorObstacleSequenceArgument = args.FirstOrDefault(arg => arg.StartsWith("--sensor-obstacle-z-sequence-m=", StringComparison.Ordinal));
        if (sensorObstacleSequenceArgument is not null)
        {
            _sensorObstacleZSequence = sensorObstacleSequenceArgument["--sensor-obstacle-z-sequence-m=".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : float.NaN)
                .ToArray();
            if (!_sensorE2e || _sensorObstacleZSequence.Length < 3 || _sensorObstacleZSequence.Any(value => !float.IsFinite(value) || value > -0.5f || value < -4.0f))
                throw new InvalidDataException("Sensor obstacle sequence requires --sensor-e2e and at least three finite center-z positions between -0.5 m and -4.0 m.");
            Console.WriteLine($"SENSOR_OBSTACLE_SEQUENCE positions_m={string.Join(',', _sensorObstacleZSequence.Select(value => value.ToString("F2", CultureInfo.InvariantCulture)))} hold_samples=1");
        }
        var sensorNoEchoArgument = args.FirstOrDefault(arg => arg.StartsWith("--sensor-no-echo-after-sample=", StringComparison.Ordinal));
        if (sensorNoEchoArgument is not null)
        {
            if (!int.TryParse(sensorNoEchoArgument.AsSpan("--sensor-no-echo-after-sample=".Length), NumberStyles.None,
                    CultureInfo.InvariantCulture, out _sensorNoEchoAfterSample)
                || !_sensorE2e || _sensorObstacleZSequence.Length < 3 || _sensorNoEchoAfterSample < 1)
                throw new InvalidDataException("Sensor no-echo injection requires --sensor-e2e and a positive sample index.");
            Console.WriteLine($"SENSOR_NO_ECHO_TEST after_sample_index={_sensorNoEchoAfterSample} source=raycast_obstacle_moved_outside_sensor_cone");
        }
        _testClockEpochRace = args.Contains("--test-clock-epoch-race", StringComparer.Ordinal);
        _pauseE2e = args.Contains("--pause-e2e", StringComparer.Ordinal);
        _assemblySmoke = args.Contains("--assembly-smoke", StringComparer.Ordinal);
        _shopSnapSmoke = args.Contains("--shop-snap-smoke", StringComparer.Ordinal);
        _checkpointPhysicsSmoke = args.Contains("--checkpoint-physics-smoke", StringComparer.Ordinal);
        var eventQueueSmoke = args.Contains("--event-queue-smoke", StringComparer.Ordinal);
        _quickActuationSmoke = args.Contains("--actuation-quick-smoke", StringComparer.Ordinal);
        _armActuationSmoke = args.Contains("--arm-actuation-smoke", StringComparer.Ordinal);
        if (_armActuationSmoke && robot.Id != "arm-01") throw new InvalidDataException("Arm actuation smoke requires --robot-id=arm-01.");
        _multiControllerIsolationSmoke = args.Contains("--multi-controller-isolation-smoke", StringComparer.Ordinal);
        if (_multiControllerIsolationSmoke && (robot.Id != "rover-multi-01" || !_project.ControllersByRobotId.TryGetValue(robot.Id, out var multiControllers) || multiControllers.Count != 2))
            throw new InvalidDataException("Multi-controller isolation smoke requires the two-controller rover fixture.");
        if (_quickActuationSmoke) _actuationNoLoadSmoke = _actuationSmoke = true;
        if (_mcuHaltSmoke) _actuationNoLoadSmoke = _actuationSmoke = true;
        var benchmarkArg = args.FirstOrDefault(arg => arg.StartsWith("--benchmark-fps=", StringComparison.Ordinal));
        var benchmarkCap = 0;
        _benchmarkRun = benchmarkArg is not null && int.TryParse(benchmarkArg.AsSpan("--benchmark-fps=".Length), out benchmarkCap) && (benchmarkCap == 15 || benchmarkCap == 60);

        BuildCameraAndLighting();
        BuildWorld();
        BuildRobot(robot, catalogue);
        if (_cameraFollowSmoke)
        {
            var cameraEntry = _robotCameras.FirstOrDefault(entry => entry.Key.StartsWith(robot.Id + "/", StringComparison.Ordinal));
            if (cameraEntry.Value is null) throw new InvalidDataException("Camera-follow smoke requires an enabled camera component on the selected robot.");
            _cameraFollowProbe = cameraEntry.Value;
            _cameraFollowBody = _linkBodies[LinkBodyKey(robot.Id, robot.RootLinkId)];
            _cameraRelativeTransform = _cameraFollowBody.GlobalTransform.AffineInverse() * _cameraFollowProbe.GlobalTransform;
            _cameraFollowInitialBodyPosition = _cameraFollowBody.GlobalPosition;
            _cameraFollowInitialWorldPosition = _cameraFollowProbe.GlobalPosition;
            _cameraFollowInitialWorldRotation = _cameraFollowProbe.GlobalBasis.GetRotationQuaternion();
            Console.WriteLine(FormattableString.Invariant($"CAMERA_FOLLOW_SMOKE_ARMED camera={_cameraFollowProbe.Name} chassis_start=({_cameraFollowInitialBodyPosition.X:F4},{_cameraFollowInitialBodyPosition.Y:F4},{_cameraFollowInitialBodyPosition.Z:F4}) camera_start=({_cameraFollowInitialWorldPosition.X:F4},{_cameraFollowInitialWorldPosition.Y:F4},{_cameraFollowInitialWorldPosition.Z:F4})"));
        }
        foreach (var additionalRobot in additionalRobots) BuildRobot(additionalRobot, catalogue);
        if (_armActuationSmoke)
        {
            var baseBody = _linkBodies[LinkBodyKey("arm-01", "arm-base-link")];
            var childBody = _linkBodies[LinkBodyKey("arm-01", "arm-upper-link")];
            _armInitialRelativeRotation = baseBody.GlobalBasis.GetRotationQuaternion().Inverse() * childBody.GlobalBasis.GetRotationQuaternion();
            _armInitialRotationCaptured = true;
        }
        BuildOverlay(robot, catalogue, robotGraphs);
        BuildDiagnosticPanel(robot, additionalRobots);
        if (_worldCollisionSmoke) return;
        if (_shopSnapSmoke)
        {
            RunShopSnapSmoke();
            return;
        }
        if (_checkpointPhysicsSmoke)
        {
            RunPhysicsCheckpointSmoke();
            return;
        }
        if (eventQueueSmoke)
        {
            RunEventQueueSmoke();
            return;
        }
        _injectWorkerFailure = args.Contains("--benchmark-worker-restart", StringComparer.Ordinal);
        var saveProjectArg = args.FirstOrDefault(arg => arg.StartsWith("--save-project=", StringComparison.Ordinal));
        if (args.Contains("--design-smoke", StringComparer.Ordinal))
        {
            if (saveProjectArg is not null)
            {
                var packageHash = ProjectPackageStore.SavePackage(_project, saveProjectArg["--save-project=".Length..], _activeGraph,
                    new ProjectControllerExecutionSettings(_controllerExecutionMode, _project.ControllerExecutionSettings.ProfileReferences));
                Console.WriteLine($"PROJECT_SAVED id={_project.ProjectId} title={_project.Title} package_sha256={packageHash} firmware_sha256={_project.FirmwareSha256} components={robot.Components.Count}");
            }
            else Console.WriteLine($"PROJECT_LOADED id={_project.ProjectId} title={_project.Title} manifest_sha256={_project.ManifestSha256} firmware_sha256={_project.FirmwareSha256} robot={robot.Id} world={_world.Id} components={robot.Components.Count}");
            GetTree().Quit();
            return;
        }
        if (!_assemblySmoke && _actuationReplayOutputPath is null) StartWorker(_projectRoot);
        _authoringLaunchSmoke = args.Contains("--authoring-launch-smoke", StringComparer.Ordinal);
        if (_benchmarkRun)
        {
            _renderCap = benchmarkCap;
            ConfigureRenderCadence(benchmarkCap);
            CreatePhysicsProbe();
        }
        else if (_performanceTrace is not null)
        {
            _renderCap = _performanceRenderCap;
            ConfigureRenderCadence(_performanceRenderCap);
            if (_performanceScenario != "headless")
            {
                var widthArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-width=", StringComparison.Ordinal));
                var heightArgument = args.FirstOrDefault(arg => arg.StartsWith("--perf-height=", StringComparison.Ordinal));
                if (widthArgument is not null && heightArgument is not null &&
                    int.TryParse(widthArgument.AsSpan("--perf-width=".Length), out var width) && int.TryParse(heightArgument.AsSpan("--perf-height=".Length), out var height) && width >= 640 && height >= 480)
                {
                    DisplayServer.WindowSetMode(DisplayServer.WindowMode.Windowed);
                    DisplayServer.WindowSetSize(new Vector2I(width, height));
                }
            }
        }
        else ConfigureRenderCadence(args.Contains("--renderer-slow", StringComparer.Ordinal) ? 15 : _renderCap);
        if (args.Contains("--renderer-slow", StringComparer.Ordinal)) Console.WriteLine("RENDERER_SLOW cap_fps=15 physics_mode=fixed-120hz synchronized=true");
        _performanceStartupPauseActive = _performanceTrace is not null;
        if (_performanceStartupPauseActive) _transportPauseActive = true;
        UpdateTreePauseState();
    }

    public override async void _PhysicsProcess(double delta)
    {
        Interlocked.Increment(ref _physicsCallbacksSincePreviousFrame);
        var callbackSequence = Interlocked.Increment(ref _physicsCallbackSequence);
        if (_performanceTrace is not null)
        {
            var ackPending = Volatile.Read(ref _clockAckSource) is not null;
            _performanceTrace.RecordPhysicsCallback(new GodotPhysicsCallbackSample(
                callbackSequence, Stopwatch.GetTimestamp(), delta, _transportPauseActive, ackPending,
                _physicsStepGranted, _paused, _clockSyncFaulted, _clockTickIndex, _physicsTicks,
                CapturePhysicsStateFingerprint()));
        }
        var resumeBoundarySource = Volatile.Read(ref _clockAckResumeBoundarySource);
        if (resumeBoundarySource is not null)
        {
            _clockAckResumeBoundaryCallbackActive = true;
            try { resumeBoundarySource.TrySetResult(true); }
            finally { _clockAckResumeBoundaryCallbackActive = false; }
            return;
        }
        if (_startupFailed || _paused || _clockSyncFaulted) return;
        if (_performanceFinishRequested && !_physicsStepGranted && Volatile.Read(ref _clockAckSource) is null)
        {
            _transportPauseActive = true;
            UpdateTreePauseState();
            return;
        }
        if (_transportPauseActive)
        {
            if (Volatile.Read(ref _clockAckSource) is not null)
                Interlocked.Increment(ref _physicsCallbacksSuppressedForAck);
            return;
        }
        if (_physicsStepGranted) { _physicsStepGranted = false; return; }
        // Do not start another controller interval after the measured horizon.
        // A granted callback is allowed through above so its acknowledged Godot
        // physics integration can complete before the run is closed.
        if (_performanceFinishRequested) return;
        var perfCallbackStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        long perfCallbackActiveBeforeWaitNs = 0;
        long perfSensorRaycastNs = 0;
        long perfSensorSerializationNs = 0;
        long perfSensorTransportNs = 0;
        long perfTickSerializationNs = 0;
        long perfTickTransportNs = 0;
        long perfTickAckWaitNs = 0;
        long ackArrivalWaitNs = 0;
        long ackDispatchDelayNs = 0;
        long perfEventDrainNs = 0;
        long perfTorqueNs = 0;
        long perfSnapshotNs = 0;
        long perfDiagnosticsNs = 0;
        var physicsCallbackCommit = true;
        var perfCommittedTick = false;
        ulong perfTickId = 0;
        long perfTickSimTimeNs = _simTimeNs;
        var physicsCallbacksDuringAckWait = 0;
        _physicsTicks++;
        _previousPhysicsDeltaNs = _lastPhysicsDeltaNs;
        var clockNumerator = ClockNumeratorNs + _clockRemainder;
        var physicsDeltaNs = clockNumerator / ClockDenominator;
        _clockRemainder = clockNumerator % ClockDenominator;
        _lastPhysicsDeltaNs = physicsDeltaNs;
        _simTimeNs += physicsDeltaNs;
        while (_actuationReplayIndex < _actuationReplayEvents.Count
            && _actuationReplayEvents[_actuationReplayIndex].SimTimeNs <= _simTimeNs)
        {
            var replay = _actuationReplayEvents[_actuationReplayIndex++];
            if (_motorChannel1ActuatorId is null || _motorChannel2ActuatorId is null)
                throw new InvalidDataException("Actuation replay requires two graph-resolved motor channels.");
            _motorLeftDuty = replay.Left;
            _motorRightDuty = replay.Right;
            _motorDuties[_motorChannel1ActuatorId] = replay.Left;
            _motorDuties[_motorChannel2ActuatorId] = replay.Right;
            _physicsAdapter.SetMotorDuty(_motorChannel1ActuatorId, replay.Left);
            _physicsAdapter.SetMotorDuty(_motorChannel2ActuatorId, replay.Right);
        }
        if (_eventBridgeConnected && _physicsTicks > 1)
        {
            var deltaNs = physicsDeltaNs;
            _clockTickIndex++;
            if (_clockTickIndex > 1)
            {
            var tickIndex = _clockTickIndex - 1;
            var tick = new SimulationTick { TickIndex = tickIndex, StartNs = _clockForwardTimeNs, DeltaNs = _previousPhysicsDeltaNs, RealTimeFactor = _measuredPhysicsHz == 0 ? 1 : _measuredPhysicsHz / 120d };

            try
            {
                if (_eventStream is null) throw new IOException("Clock bridge is connected without an event stream.");
                if (_physicsTicks == 2) Console.WriteLine($"GODOT_CLOCK_BRIDGE_READY sim_time_ns={_simTimeNs}");
                var tickEndNs = checked(tick.StartNs + tick.DeltaNs);
                if (tickEndNs - _lastSensorSampleNs >= 100_000_000 && _distanceSensor is not null && _distanceSensorComponentId is not null
                    && _project.ControllersByRobotId.TryGetValue(_activeRobotId, out var sensorAssignments)
                    && sensorAssignments.Any(assignment => RobotElectricalRouter.HasDistanceSensorRoute(_activeGraph, _catalogue, assignment.ComponentInstanceId)))
                {
                    if (_sensorObstacleZSequence.Length > 0 && _sensorE2eObstacle is not null)
                    {
                        var positionIndex = Math.Min(_sensorSampleCount, _sensorObstacleZSequence.Length - 1);
                        var noEcho = _sensorNoEchoAfterSample >= 0 && _sensorSampleCount >= _sensorNoEchoAfterSample;
                        _sensorE2eObstacle.Position = new Vector3(noEcho ? 10f : 0f, 0, _sensorObstacleZSequence[positionIndex]);
                        if (_sensorObstacleSequenceIndex != positionIndex)
                        {
                            _sensorObstacleSequenceIndex = positionIndex;
                            Console.WriteLine($"GODOT_SENSOR_OBSTACLE index={positionIndex} sim_time_ns={tickEndNs} center_z_m={_sensorObstacleZSequence[positionIndex].ToString("F2", CultureInfo.InvariantCulture)} state={(noEcho ? "no_echo" : "in_range")}");
                        }
                        else if (noEcho && _sensorSampleCount == _sensorNoEchoAfterSample)
                            Console.WriteLine($"GODOT_SENSOR_OBSTACLE index={positionIndex} sim_time_ns={tickEndNs} state=no_echo obstacle_x_m=10");
                    }
                    var sensorStartTicks = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                    var forward = -_distanceSensor.GlobalBasis.Z;
                    forward.Y = 0;
                    if (forward.LengthSquared() < 0.001f) forward = Vector3.Forward;
                    forward = forward.Normalized();
                    var origin = _distanceSensor.GlobalPosition + (forward * 0.24f);
                    var query = PhysicsRayQueryParameters3D.Create(origin, origin + (forward * 4f));
                    query.CollisionMask = 1;
                    var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
                    if (_performanceTrace is not null) perfSensorRaycastNs = Stopwatch.GetElapsedTime(sensorStartTicks).Ticks * 100L;
                    var distanceMeters = hit.Count > 0 && hit.TryGetValue("position", out var hitPosition)
                        ? origin.DistanceTo((Vector3)hitPosition)
                        : -1d;
                    WriteEnvelopeSyncMeasured(_eventStream, new Envelope
                    {
                        ProtocolMajor = 1, ProtocolMinor = 0, SessionId = _eventSessionId,
                        WorldId = _world.Id, RobotId = _activeRobotId, ComponentId = _distanceSensorComponentId,
                        Sequence = (ulong)tickIndex, SimTimeNs = tickEndNs,
                        HardwareEvent = new HardwareEvent
                        {
                            Kind = HardwareEvent.Types.Kind.SensorSample, EndpointId = "distance.echo",
                            Scalar = distanceMeters, Unit = "meters", EffectiveSimTimeNs = tickEndNs,
                            FidelityNote = "Godot physics raycast translated to an ultrasonic trigger/echo pulse on STM32F407 PA0."
                        }
                    }, _performanceTrace is not null, out perfSensorSerializationNs, out perfSensorTransportNs);
                    if (_lastSensorSampleNs < 0) Console.WriteLine($"GODOT_RAY origin={origin} target={query.To} hit={hit.Count}");
                    _lastSensorSampleNs = tickEndNs;
                    _sensorSampleCount++;
                    Console.WriteLine($"GODOT_DISTANCE_SAMPLE sim_time_ns={tickEndNs} meters={(distanceMeters < 0 ? "no-hit" : distanceMeters.ToString("F3", CultureInfo.InvariantCulture))}");
                }
                var ackSource = new TaskCompletionSource<SimulationTick>(TaskCreationOptions.RunContinuationsAsynchronously);
                Interlocked.Exchange(ref _clockAckSource, ackSource);
                Interlocked.Exchange(ref _clockAckArrivalStopwatchTicks, 0);
                var ackWaitStart = Stopwatch.GetTimestamp();
                SimulationTick ack;
                var yieldedForAck = false;
                var receivedAck = false;
                try
                {
                    Interlocked.Exchange(ref _physicsCallbacksSuppressedForAck, 0);
                    WriteEnvelopeSyncMeasured(_eventStream, new Envelope { ProtocolMajor = 1, ProtocolMinor = 0, SessionId = _eventSessionId, WorldId = "roboworld-local", RobotId = "rover-01", Sequence = tickIndex, SimTimeNs = tick.StartNs, Tick = tick }, _performanceTrace is not null, out perfTickSerializationNs, out perfTickTransportNs);
                    ackWaitStart = Stopwatch.GetTimestamp();
                    var ackTask = ackSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (ackTask.IsCompleted) ack = ackTask.GetAwaiter().GetResult();
                    else
                    {
                        yieldedForAck = true;
                        if (_performanceTrace is not null) perfCallbackActiveBeforeWaitNs = Stopwatch.GetElapsedTime(perfCallbackStart).Ticks * 100L;
                        // Hold only the fixed physics boundary while the existing worker acknowledgment is in flight.
                        // ProcessMode.Always keeps the Godot main loop rendering and servicing UI during this wait.
                        _transportPauseActive = true;
                        UpdateTreePauseState();
                        ack = await ackTask;
                        receivedAck = true;
                        perfCallbackStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
                        var resumeBoundary = new TaskCompletionSource<bool>();
                        Interlocked.Exchange(ref _clockAckResumeBoundarySource, resumeBoundary);
                        try
                        {
                            await resumeBoundary.Task;
                            physicsCallbackCommit = _clockAckResumeBoundaryCallbackActive;
                        }
                        finally { Interlocked.CompareExchange(ref _clockAckResumeBoundarySource, null, resumeBoundary); }
                    }
                    var ackContinuationTicks = Stopwatch.GetTimestamp();
                    var ackArrivalTicks = Interlocked.Exchange(ref _clockAckArrivalStopwatchTicks, 0);
                    if (ackArrivalTicks > 0)
                    {
                        ackArrivalWaitNs = Stopwatch.GetElapsedTime(ackWaitStart, ackArrivalTicks).Ticks * 100L;
                        ackDispatchDelayNs = Stopwatch.GetElapsedTime(ackArrivalTicks, ackContinuationTicks).Ticks * 100L;
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref _clockAckSource, null, ackSource);
                physicsCallbacksDuringAckWait = Interlocked.Exchange(ref _physicsCallbacksSuppressedForAck, 0);
                    if (yieldedForAck)
                    {
                        _transportPauseActive = false;
                        _physicsStepGranted = receivedAck && !physicsCallbackCommit;
                        UpdateTreePauseState();
                    }
                }
                if (_performanceTrace is not null) perfTickAckWaitNs = Stopwatch.GetElapsedTime(ackWaitStart).Ticks * 100L;
                if (ack.TickIndex != tickIndex || ack.StartNs != tick.StartNs || ack.DeltaNs != tick.DeltaNs)
                    throw new InvalidDataException($"Clock acknowledgment mismatch for tick {tickIndex}.");
                _clockForwardTimeNs = tickEndNs;
                _lastAcknowledgedClockTickIndex = tickIndex;
                perfCommittedTick = true;
                perfTickId = tickIndex;
                perfTickSimTimeNs = tickEndNs;
                if (_performanceTrace is not null && _performanceSimDurationSeconds > 0 && _performancePhaseStartTicks > 0 &&
                    _clockForwardTimeNs - _performancePhaseStartSimNs >= (long)_performanceSimDurationSeconds * 1_000_000_000L)
                    _performanceFinishRequested = true;
                if (_performanceTrace is not null && _performancePhaseStartTicks == 0)
                {
                    _performancePhaseStartTicks = Stopwatch.GetTimestamp();
                    _performancePhaseStartSimNs = tickEndNs;
                    _lastPerformanceFrameTicks = _performancePhaseStartTicks;
                    Interlocked.Exchange(ref _physicsCallbacksSincePreviousFrame, 0);
                    Console.WriteLine($"PERF_PHASE_BEGIN scenario={_performanceScenario} tick_id={tickIndex} sim_time_ns={tickEndNs} mono_ticks={_performancePhaseStartTicks} duration_seconds={_performanceDurationSeconds} sim_duration_seconds={_performanceSimDurationSeconds}");
                }
                if (tickIndex % 120 == 0) Console.WriteLine(FormattableString.Invariant($"GODOT_CLOCK_TICK tick={tickIndex} start_ns={tick.StartNs} delta_ns={tick.DeltaNs} sim_time_ns={tickEndNs} godot_physics_tick={_physicsTicks} measured_physics_hz={_measuredPhysicsHz:F2} measured_rtf={(_measuredPhysicsHz == 0 ? 1 : _measuredPhysicsHz / 120d):F3}"));
            }
            catch (Exception exception)
            {
                _clockSyncFaulted = true;
                _physicsStepGranted = false;
                HardwareEventError($"clock synchronization failed: {exception.Message}");
                _eventBridgeConnected = false;
                UpdateTreePauseState();
                return;
            }
            }
        }
        var now = Time.GetTicksMsec() / 1000d;
        if (_lastMeasureTime > 0 && now - _lastMeasureTime >= 1)
        {
            _measuredPhysicsHz = (_physicsTicks - _lastMeasureTicks) / (now - _lastMeasureTime);
            _lastMeasureTime = now;
            _lastMeasureTicks = _physicsTicks;
        }
        else if (_lastMeasureTime == 0) { _lastMeasureTime = now; _lastMeasureTicks = _physicsTicks; }
        var eventDrainStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        DrainHardwareEvents();
        if (_performanceTrace is not null) perfEventDrainNs = Stopwatch.GetElapsedTime(eventDrainStart).Ticks * 100L;
        var torqueStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        _physicsAdapter.ApplyMotorTorques();
        if (_performanceTrace is not null) perfTorqueNs = Stopwatch.GetElapsedTime(torqueStart).Ticks * 100L;
        foreach (var body in _bodies) if (body.GlobalPosition.Y < -2) body.QueueFree();
        var snapshotStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        var snapshot = _latestSnapshot = _physicsAdapter.CaptureState(_physicsTicks, _simTimeNs);
        if (_performanceTrace is not null) perfSnapshotNs = Stopwatch.GetElapsedTime(snapshotStart).Ticks * 100L;
        var diagnosticStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        UpdateDiagnosticRows(snapshot);
        if (_performanceTrace is not null) perfDiagnosticsNs = Stopwatch.GetElapsedTime(diagnosticStart).Ticks * 100L;
        if (_performanceTrace is not null && _performancePhaseStartTicks > 0 && perfCommittedTick)
        {
            var chassis = snapshot.Bodies.FirstOrDefault(body => body.ComponentId == "chassis");
            var leftWheel = snapshot.Bodies.FirstOrDefault(body => body.ComponentId == "wheel-left");
            var rightWheel = snapshot.Bodies.FirstOrDefault(body => body.ComponentId == "wheel-right");
            var speed = chassis is null ? 0 : Math.Sqrt(chassis.LinearVelocityMps[0] * chassis.LinearVelocityMps[0] + chassis.LinearVelocityMps[2] * chassis.LinearVelocityMps[2]);
            _performanceTrace.RecordTick(new GodotTickPerformanceSample(
                perfTickId, perfTickSimTimeNs, Stopwatch.GetTimestamp(), perfCallbackActiveBeforeWaitNs + (_performanceTrace is null ? 0 : Stopwatch.GetElapsedTime(perfCallbackStart).Ticks * 100L),
                Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000d,
                perfSensorRaycastNs, perfSensorSerializationNs, perfSensorTransportNs,
                perfTickSerializationNs, perfTickTransportNs, perfTickAckWaitNs,
                ackArrivalWaitNs, ackDispatchDelayNs,
                perfEventDrainNs, perfTorqueNs, perfSnapshotNs, perfDiagnosticsNs,
                _hardwareEvents.ApproximateCount, _hardwareEvents.HighWatermark, snapshot.Bodies.Count,
                chassis?.PositionM[0] ?? 0, chassis?.PositionM[2] ?? 0, speed, _motorLeftDuty, _motorRightDuty,
                physicsCallbacksDuringAckWait, physicsCallbackCommit,
                leftWheel?.PositionM[0] ?? 0, leftWheel?.PositionM[1] ?? 0, leftWheel?.PositionM[2] ?? 0,
                leftWheel?.AngularVelocityRadps[0] ?? 0, leftWheel?.AngularVelocityRadps[1] ?? 0, leftWheel?.AngularVelocityRadps[2] ?? 0,
                rightWheel?.PositionM[0] ?? 0, rightWheel?.PositionM[1] ?? 0, rightWheel?.PositionM[2] ?? 0,
                rightWheel?.AngularVelocityRadps[0] ?? 0, rightWheel?.AngularVelocityRadps[1] ?? 0, rightWheel?.AngularVelocityRadps[2] ?? 0));
        }
        if (_assemblySmoke && _physicsTicks >= 120)
        {
            var rover = snapshot.Bodies.Single(body => body.ComponentId == "chassis");
            var armBase = snapshot.Bodies.Single(body => body.ComponentId == "arm-base");
            var armBeam = snapshot.Bodies.Single(body => body.ComponentId == "arm-beam");
            if (Math.Abs(armBase.PositionM[0] + 2) > 0.05 || Math.Abs(armBase.PositionM[1] - 0.055) > 0.03 ||
                Math.Abs(armBeam.PositionM[0] + 2) > 0.05 || Math.Abs(armBeam.PositionM[1] - 0.305) > 0.05)
                throw new InvalidOperationException("Second robot did not preserve its initial link placement during the 120-step physics smoke.");
            Console.WriteLine(FormattableString.Invariant($"ASSEMBLY_PHYSICS_SMOKE ticks={_physicsTicks} bodies={snapshot.Bodies.Count} rover=({rover.PositionM[0]:F3},{rover.PositionM[1]:F3},{rover.PositionM[2]:F3}) arm_base=({armBase.PositionM[0]:F3},{armBase.PositionM[1]:F3},{armBase.PositionM[2]:F3}) arm_beam=({armBeam.PositionM[0]:F3},{armBeam.PositionM[1]:F3},{armBeam.PositionM[2]:F3}) placement=PASS"));
            GetTree().Quit();
        }
        if (_pauseE2e && !_pauseResumePending && !_pauseResumed && _clockTickIndex >= 120)
        {
            _pauseStartedSimTimeNs = _simTimeNs;
            _pauseResumePending = true;
            _paused = true;
            UpdateTreePauseState();
            Console.WriteLine($"SIM_PAUSE sim_time_ns={_pauseStartedSimTimeNs} physics_ticks={_physicsTicks}");
            _ = ResumePauseE2eAsync();
        }
        if (_multiControllerIsolationSmoke && _clockTickIndex >= 960)
        {
            Console.WriteLine($"MULTI_CONTROLLER_ISOLATION_SMOKE complete controllers=2 clock_ticks={_clockTickIndex} shared_clock=PASS");
            GetTree().Quit();
        }
        if (_pauseE2e && _pauseResumed && !_benchmarkWritten && _physicsTicks >= _pauseResumePhysicsTick + 120)
        {
            _benchmarkWritten = true;
            Console.WriteLine($"SIM_PAUSE_E2E complete physics_ticks={_physicsTicks} sim_time_ns={_simTimeNs}");
            GetTree().Quit();
        }
        if (_probe is not null && !_benchmarkWritten)
        {
            var state = snapshot.Bodies.Single(item => item.ComponentId == "physics-probe");
            _probeTrajectory.Append(_physicsTicks).Append(',').Append(_simTimeNs).Append(',')
                .Append(state.PositionM[0].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.PositionM[1].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.PositionM[2].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.LinearVelocityMps[0].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.LinearVelocityMps[1].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(state.LinearVelocityMps[2].ToString("F6", CultureInfo.InvariantCulture)).AppendLine();
            if (_simTimeNs >= 10_000_000_000)
            {
                var path = GetEvidencePath("T02", $"trajectory-fps{_renderCap}.csv");
                File.WriteAllText(path, "physics_tick,sim_time_ns,x_m,y_m,z_m,vx_mps,vy_mps,vz_mps\n" + _probeTrajectory);
                _benchmarkWritten = true;
                Console.WriteLine($"BENCHMARK complete cap={_renderCap} physics_ticks={_physicsTicks} sim_time_ns={_simTimeNs} measured_physics_hz={_measuredPhysicsHz:F2} render_fps={Engine.GetFramesPerSecond()} probe_y={state.PositionM[1]:F6} worker_failure_seen={_workerFailureSeen} worker_restarted={_workerRestarted} worker_state={_workerState} trajectory={path}");
                GetTree().Quit();
            }
        }
        var routedWheelIds = _activeGraph?.Joints.Where(joint => joint.ActuatorComponentId is not null)
            .Select(joint => _activeGraph.Links.Single(link => link.Id == joint.ChildLinkId).BaseComponentId).Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var rootComponentId = _activeGraph?.Links.SingleOrDefault(link => link.Id == _activeGraph.RootLinkId)?.BaseComponentId;
        if (_actuationSmoke && _actuationSmokeStartedHostTicks > 0 && _activeGraph is not null
            && routedWheelIds.Length == 2 && rootComponentId is not null
            && (_actuationReplayOutputPath is not null || _physicsTicks % 6 == 0)
            && routedWheelIds.All(id => snapshot.Bodies.Any(item => item.ComponentId == id)))
        {
            var chassis = snapshot.Bodies.Single(item => item.ComponentId == rootComponentId);
            var leftId = _motorChannel1ActuatorId is null ? routedWheelIds[0]
                : _activeGraph.Joints.Single(joint => joint.ActuatorComponentId == _motorChannel1ActuatorId).ChildLinkId;
            var rightId = _motorChannel2ActuatorId is null ? routedWheelIds[1]
                : _activeGraph.Joints.Single(joint => joint.ActuatorComponentId == _motorChannel2ActuatorId).ChildLinkId;
            leftId = _activeGraph.Links.Single(link => link.Id == leftId).BaseComponentId;
            rightId = _activeGraph.Links.Single(link => link.Id == rightId).BaseComponentId;
            var left = snapshot.Bodies.Single(item => item.ComponentId == leftId);
            var right = snapshot.Bodies.Single(item => item.ComponentId == rightId);
            var leftOmega = left.AngularVelocityRadps[0];
            var rightOmega = right.AngularVelocityRadps[0];
            _actuationSmokeTrajectory.Append(_physicsTicks).Append(',').Append(_simTimeNs).Append(',')
                .Append(chassis.PositionM[0].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(chassis.PositionM[2].ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(leftOmega.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(rightOmega.ToString("F6", CultureInfo.InvariantCulture)).Append(',')
                .Append(_motorLeftDuty.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                .Append(_motorRightDuty.ToString("F3", CultureInfo.InvariantCulture)).AppendLine();
        }
    }

    public override void _Process(double delta)
    {
        if (_authoringLaunchSmoke && (_workerState.StartsWith("READY", StringComparison.Ordinal) || _workerState.StartsWith("FAILED", StringComparison.Ordinal)))
        {
            Console.WriteLine($"AUTHORING_RUNTIME_LAUNCH state={_workerState} robot={_activeRobotId} graph_components={_activeGraph.Components.Count}");
            GetTree().Quit(_workerState.StartsWith("READY", StringComparison.Ordinal) ? 0 : 1);
            return;
        }
        if (_startupFailed) return;
        if (_worldCollisionSmoke)
        {
            var origin = new Vector3(0, 0.3f, -0.41f);
            var query = PhysicsRayQueryParameters3D.Create(origin, origin + Vector3.Forward * 4f);
            query.CollisionMask = 1;
            var hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
            var colliderName = hit.TryGetValue("collider", out var colliderValue) && colliderValue.AsGodotObject() is Node colliderNode ? colliderNode.Name.ToString() : "none";
            if (colliderName != "sensor-crate")
            {
                if (_physicsTicks < 3) return;
                Console.Error.WriteLine($"WAREHOUSE_COLLISION_SMOKE status=FAIL collider={colliderName} distance={(hit.TryGetValue("position", out var hitPos) ? origin.DistanceTo((Vector3)hitPos) : -1):F3}m");
                GetTree().Quit(1);
                return;
            }
            var distance = origin.DistanceTo((Vector3)hit["position"]);
            if (distance is < 3.0f or > 3.5f) throw new InvalidDataException($"Warehouse sensor target was hit at unexpected distance {distance:F3} m.");
            Console.WriteLine(FormattableString.Invariant($"WAREHOUSE_COLLISION_SMOKE status=PASS world={_world.Id} version={_world.SchemaVersion} objects={_world.Objects.Count} collider={colliderName} distance_m={distance:F3} static_obstacles={_world.Objects.Count(item => item.BodyType == "static" && item.Tags?.Contains("obstacle", StringComparer.Ordinal) == true)}"));
            GetTree().Quit();
            return;
        }
        VerifyCameraFollowsRobot();
        UpdatePresentationCadence();
        var perfProcessStart = _performanceTrace is null ? 0 : Stopwatch.GetTimestamp();
        var actuationSmokeDuration = _testActuationSmokeDurationSeconds ?? (_quickActuationSmoke ? 8.0
            : _sensorObstacleZSequence.Length > 0 ? 18.0 : _actuationLoadedSmoke ? 12.0 : 32.0);
        var smokeWallDurationMet = _actuationSmokeStartedHostTicks > 0
            && (Stopwatch.GetTimestamp() - _actuationSmokeStartedHostTicks) / (double)Stopwatch.Frequency >= actuationSmokeDuration;
        var smokeClockVerificationMet = !_requireClockVerificationForSmoke
            || (_actuationSmokeStartedHostTicks > 0 && _clockForwardTimeNs - _actuationSmokeStartSimTimeNs >= _testActuationSmokeClockDurationNs);
        if (_actuationReplayOutputPath is not null && _actuationReplayEndSimTimeNs is long replayEndNs && _simTimeNs >= replayEndNs)
        {
            var rows = _actuationSmokeTrajectory.ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(_actuationReplayOutputPath)!);
            File.WriteAllText(_actuationReplayOutputPath,
                "physics_tick,sim_time_ns,chassis_x_m,chassis_z_m,left_wheel_omega_x_radps,right_wheel_omega_x_radps,left_signed_duty,right_signed_duty\n" + rows);
            Console.WriteLine($"ACTUATION_REPLAY complete physics_ticks={_physicsTicks} sim_time_ns={_simTimeNs} trajectory_rows={rows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length} output={_actuationReplayOutputPath}");
            GetTree().Quit();
            return;
        }
        if (_actuationSmoke && smokeWallDurationMet && smokeClockVerificationMet)
        {
            Console.WriteLine($"ACTUATION_SMOKE_CLOCK_GATE observed_sim_ns={Math.Max(0, _clockForwardTimeNs - _actuationSmokeStartSimTimeNs)} required_sim_ns={_testActuationSmokeClockDurationNs} status=PASS");
            var evidenceTask = _evidenceTaskOverride ?? (_activeRobotId == "rover-slow-01" ? "T07" : _sensorE2e ? "T04" : "T03");
            var filename = _activeRobotId == "rover-slow-01" ? "motor-variant-motion.csv" : _actuationLoadedSmoke ? "godot-actuation-loaded.csv" : "godot-actuation-smoke.csv";
            var path = GetEvidencePath(evidenceTask, filename);
            File.WriteAllText(path, "physics_tick,sim_time_ns,chassis_x_m,chassis_z_m,left_wheel_omega_x_radps,right_wheel_omega_x_radps,left_signed_duty,right_signed_duty\n" + _actuationSmokeTrajectory);
            var lines = _actuationSmokeTrajectory.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var first = lines.FirstOrDefault()?.Split(',');
            var last = lines.LastOrDefault()?.Split(',');
            Console.WriteLine($"ACTUATION_SMOKE complete rows={lines.Length} left={_motorLeftDuty.ToString("F3", CultureInfo.InvariantCulture)} right={_motorRightDuty.ToString("F3", CultureInfo.InvariantCulture)} z_start={(first is null ? "n/a" : first[3])} z_end={(last is null ? "n/a" : last[3])} clock_elapsed_ns={Math.Max(0, _clockForwardTimeNs - _actuationSmokeStartSimTimeNs)} trajectory={path}");
            GetTree().Quit();
        }
        if (_armActuationSmoke && _armActuationStartedPhysicsTick > 0 && _physicsTicks - _armActuationStartedPhysicsTick >= 360)
        {
            var baseBody = _linkBodies[LinkBodyKey("arm-01", "arm-base-link")];
            var childBody = _linkBodies[LinkBodyKey("arm-01", "arm-upper-link")];
            var currentRelative = baseBody.GlobalBasis.GetRotationQuaternion().Inverse() * childBody.GlobalBasis.GetRotationQuaternion();
            var angle = (_armInitialRelativeRotation.Inverse() * currentRelative).GetAngle();
            Console.WriteLine(FormattableString.Invariant($"ARM_ACTUATION_SMOKE complete actuator=arm-motor peak_duty={_armPeakDuty:F3} angle_rad={angle:F4} elapsed_ticks={_physicsTicks - _armActuationStartedPhysicsTick}"));
            if (!_armInitialRotationCaptured || angle < 0.03) throw new InvalidOperationException($"Firmware-driven arm motion was below 0.03 rad: {angle:F4} rad.");
            GetTree().Quit();
        }
        if (_injectWorkerFailure && _workerFailureSeen && !_workerRestarted && Time.GetTicksMsec() - _workerFailureAtMs > 750)
        {
            _workerRestarted = true;
            RestartWorker();
        }
        var elapsed = Math.Max((Stopwatch.GetTimestamp() - _sessionStarted) / (double)Stopwatch.Frequency, 0.001);
        var simSeconds = _simTimeNs / 1_000_000_000d;
        if (_metrics is not null)
        {
            _metrics.Text = $"RTF {simSeconds / elapsed:F2}   Physics {(_measuredPhysicsHz == 0 ? 120 : _measuredPhysicsHz):F0}/120 Hz   FPS {Engine.GetFramesPerSecond()} (cap {_renderCap})   Tick {_physicsTicks}   Sim {simSeconds:F2}s";
            if (_workerStatus is not null) _workerStatus.Text = $"Robot Service  {_workerState}";
            if (_diagnosticRows.Count > 0) UpdateDiagnosticRows(_latestSnapshot ?? new PhysicalSnapshot(_physicsTicks, _simTimeNs, []));
            if (_latestSnapshot is not null && _componentStatus is not null)
            {
                var chassis = _latestSnapshot.Bodies.SingleOrDefault(body => body.ComponentId == "chassis");
                _componentStatus.Text = chassis is null
                    ? "World  OPERATIONAL    Robot  UNKNOWN\nMCU  UNKNOWN"
                    : $"World  OPERATIONAL    Robot  OPERATIONAL · pose ({chassis.PositionM[0]:F2}, {chassis.PositionM[1]:F2}, {chassis.PositionM[2]:F2}) m\nMCU  {(_eventBridgeConnected ? "ACTIVE · Renode outputs linked" : "STARTING · waiting for Renode")}    {_latestSnapshot.Bodies.Count} physics bodies\n{_uartEndpoint}";
            }
        }
        if (_performanceTrace is not null && _performancePhaseStartTicks > 0)
        {
            var frameEndTicks = Stopwatch.GetTimestamp();
            var frameIntervalNs = Stopwatch.GetElapsedTime(_lastPerformanceFrameTicks, frameEndTicks).Ticks * 100L;
            var visibleChassisX = 0d;
            var visibleChassisZ = 0d;
            if (_activeGraph is not null && _linkBodies.TryGetValue(LinkBodyKey(_activeGraph.Id, _activeGraph.RootLinkId), out var visibleChassis))
            {
                visibleChassisX = visibleChassis.GlobalPosition.X;
                visibleChassisZ = visibleChassis.GlobalPosition.Z;
            }
            _performanceTrace.RecordFrame(new GodotFramePerformanceSample(
                frameEndTicks, frameIntervalNs, Stopwatch.GetElapsedTime(perfProcessStart, frameEndTicks).Ticks * 100L,
                Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000d, Engine.GetFramesPerSecond(), visibleChassisX, visibleChassisZ,
                Interlocked.Exchange(ref _physicsCallbacksSincePreviousFrame, 0)));
            _lastPerformanceFrameTicks = frameEndTicks;
            var phaseComplete = _performanceSimDurationSeconds > 0
                ? _clockForwardTimeNs - _performancePhaseStartSimNs >= (long)_performanceSimDurationSeconds * 1_000_000_000L
                : Stopwatch.GetElapsedTime(_performancePhaseStartTicks, frameEndTicks).TotalSeconds >= _performanceDurationSeconds;
            if (phaseComplete)
            {
                _performanceFinishRequested = true;
                if (Volatile.Read(ref _clockAckSource) is null && !_physicsStepGranted)
                    FinishPerformanceRun(frameEndTicks);
            }
        }
    }

    public override void _Notification(int what) { if (what == NotificationWMCloseRequest) { StopWorker(); StopEventBridge(); } }
    public override void _ExitTree()
    {
        if (_performanceTrace is not null) { Console.Error.WriteLine($"PERF_PHASE_INTERRUPTED scenario={_performanceScenario} tick_id={_clockTickIndex} sim_time_ns={_clockForwardTimeNs}"); _performanceTrace.Dispose(); _performanceTrace = null; }
        StopWorker(); StopEventBridge();
        if (_project is not null) ProjectPackageStore.DeleteTemporaryProject(_project);
        if (!_handingOffTemporaryRunPackage && _temporaryRunPackagePath is not null)
            try { File.Delete(_temporaryRunPackagePath); } catch (Exception exception) { Console.Error.WriteLine($"TEMP_RUN_PACKAGE_CLEANUP_FAILED {exception.Message}"); }
    }

    private void FinishPerformanceRun(long endTicks)
    {
        if (_performanceFinished || _performanceTrace is null) return;
        _performanceFinished = true;
        var elapsedNs = Stopwatch.GetElapsedTime(_performancePhaseStartTicks, endTicks).Ticks * 100L;
        var committedSimNs = Math.Max(0, _clockForwardTimeNs - _performancePhaseStartSimNs);
        Console.WriteLine(FormattableString.Invariant($"PERF_PHASE_END scenario={_performanceScenario} tick_id={_lastAcknowledgedClockTickIndex} sim_time_ns={_clockForwardTimeNs} mono_ticks={endTicks} wall_elapsed_ns={elapsedNs} committed_sim_ns={committedSimNs} rtf={(elapsedNs == 0 ? 0 : committedSimNs / (double)elapsedNs):F6} ticks={_lastAcknowledgedClockTickIndex}"));
        _performanceTrace.Dispose();
        _performanceTrace = null;
        GetTree().Quit();
    }

    private void BuildCameraAndLighting()
    {
        AddChild(new WorldEnvironment { Environment = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0.045f, 0.065f, 0.09f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.68f, 0.75f, 0.87f), AmbientLightEnergy = 0.55f } });
        _worldCameraPivot = new Node3D { Name = "WorldViewOrbitPivot", Position = new Vector3(0, 0.55f, 0) };
        _worldCamera = new Camera3D { Name = "WorldViewCamera", Position = new Vector3(4.5f, 3.05f, 5.5f), RotationDegrees = new Vector3(-24, 37, 0), Current = true, Fov = 58 };
        _worldCameraPivot.AddChild(_worldCamera);
        AddChild(_worldCameraPivot);
        _worldCamera.LookAt(Vector3.Zero, Vector3.Up);
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-48, -32, 0), ShadowEnabled = true, LightEnergy = 1.2f });
    }

    private void BuildWorld()
    {
        PhysicsServer3D.AreaSetParam(GetWorld3D().Space, PhysicsServer3D.AreaParameter.Gravity, (float)Math.Abs(_world.GravityMps2[1]));
        var room = _world.Objects.Single(item => item.Primitive == "room");
        var width = (float)room.SizeM[0]; var height = (float)room.SizeM[1]; var depth = (float)room.SizeM[2];
        AddStaticBox("RoomFloor", new Vector3(0, -0.1f, 0), new Vector3(width, 0.2f, depth), new Color(0.20f, 0.25f, 0.31f));
        AddStaticBox("RoomBackWall", new Vector3(0, height / 2, -depth / 2), new Vector3(width, height, 0.12f), new Color(0.10f, 0.15f, 0.22f));
        AddStaticBox("RoomFrontWall", new Vector3(0, height / 2, depth / 2), new Vector3(width, height, 0.12f), new Color(0.10f, 0.15f, 0.22f));
        AddStaticBox("RoomLeftWall", new Vector3(-width / 2, height / 2, 0), new Vector3(0.12f, height, depth), new Color(0.10f, 0.15f, 0.22f));
        AddStaticBox("RoomRightWall", new Vector3(width / 2, height / 2, 0), new Vector3(0.12f, height, depth), new Color(0.10f, 0.15f, 0.22f));
        if (_sensorE2e) _sensorE2eObstacle = AddStaticBox("SensorE2EObstacle", new Vector3(0, 0, -2.0f), new Vector3(1.0f, 2.0f, 0.12f), new Color(0.85f, 0.25f, 0.15f));
        foreach (var item in _world.Objects.Where(item => item.Primitive != "room"
            && !(_sensorE2e && item.Tags?.Contains("sensor_reference", StringComparer.Ordinal) == true)))
        {
            var p = ToVector(item.PositionM);
            var rotation = ToQuaternion(item.RotationXyzw ?? [0, 0, 0, 1]);
            var size = ToVector(item.SizeM);
            var color = WorldObjectColor(item.AssetRef, item.Tags ?? []);
            var visualOnly = item.Tags?.Contains("visual_only", StringComparer.Ordinal) == true;
            if (visualOnly)
            {
                AddWorldVisual(item, p, rotation, size, color);
                continue;
            }
            if (item.BodyType == "dynamic")
            {
                if (item.Primitive == "cylinder") AddDynamicCylinder(item.Id, p, (float)(item.RadiusM ?? size.X / 2), size.Y, (float)item.MassKg, rotation, color, (float)item.FrictionCoefficient);
                else AddDynamicWorldBox(item.Id, p, size, rotation, (float)item.MassKg, color, (float)item.FrictionCoefficient);
            }
            else if (item.Primitive == "box") AddStaticBox(item.Id, p, size, color, rotation, (float)item.FrictionCoefficient);
            else if (item.Primitive == "cylinder") AddStaticCylinder(item.Id, p, (float)(item.RadiusM ?? size.X / 2), size.Y, color, rotation, (float)item.FrictionCoefficient);
            else if (item.Primitive == "sphere") AddStaticSphere(item.Id, p, (float)(item.RadiusM ?? size.X / 2), color, (float)item.FrictionCoefficient);
            if (item.Tags?.Contains("sign", StringComparer.Ordinal) == true)
                AddWorldSign(item, p, rotation, size);
        }
        AddGrid(width, depth);
    }

    private void CreatePhysicsProbe()
    {
        _probe = new RigidBody3D { Name = "PhysicsFrameRateProbe", Position = new Vector3(-2, 1.5f, -2), Mass = 0.5f, ContinuousCd = true, CanSleep = false };
        _probe.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.1f } });
        _probe.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = 0.1f, Height = 0.2f }, MaterialOverride = Material(new Color(0.93f, 0.56f, 0.18f)) });
        AddChild(_probe);
        _physicsAdapter.BindBody("physics-probe", _probe);
        _physicsAdapter.RegisterBody("physics-probe", 0.5);
    }

    private void BuildRobot(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        var root = graph.Links.Single(link => link.Id == graph.RootLinkId);
        var chassisInstance = graph.Components.Single(component => component.Id == root.BaseComponentId);
        var chassisDefinition = catalogue[chassisInstance.CatalogueId];
        var chassisSize = chassisInstance.SizeOverrideM ?? chassisDefinition.SizeM;
        var rootRotation = ToQuaternion(root.RotationXyzw);
        var chassisMassKg = graph.Components.Where(component => component.LinkId == root.Id).Sum(component => catalogue[component.CatalogueId].MassKg);
        var casterFriction = graph.Components.Select(component => catalogue[component.CatalogueId])
            .Where(definition => definition.Model == "passive_ball_caster" && definition.FrictionCoefficient.HasValue)
            .Select(definition => definition.FrictionCoefficient!.Value).DefaultIfEmpty(double.NaN).Min();
        var useLowFrictionSupport = double.IsFinite(casterFriction)
            && graph.Components.Count(component => catalogue[component.CatalogueId].Model == "passive_ball_caster") > 1;
        var chassis = new RigidBody3D
        {
            Name = graph.Id,
            Mass = (float)Math.Max(chassisMassKg, chassisDefinition.MassKg),
            Position = ToVector(root.PositionM) + rootRotation * ToVector(chassisInstance.PositionM),
            Quaternion = rootRotation * ToQuaternion(chassisInstance.RotationXyzw),
            ContinuousCd = true,
            CanSleep = false,
            Freeze = _actuationNoLoadSmoke,
            PhysicsMaterialOverride = useLowFrictionSupport
                    ? new PhysicsMaterial { Friction = (float)casterFriction }
                    : null
        };
        chassis.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = ToVector(chassisSize) } });
        var chassisMesh = new MeshInstance3D { Name = chassisDefinition.Id, Mesh = new BoxMesh { Size = ToVector(chassisSize) }, MaterialOverride = Material(new Color(0.11f, 0.61f, 0.54f)) };
        chassis.AddChild(chassisMesh);
        _componentVisuals[chassisInstance.Id] = chassisMesh;
        AddChild(chassis); _bodies.Add(chassis);
        _physicsAdapter.BindBody(chassisInstance.Id, chassis);
        _physicsAdapter.RegisterBody(chassisInstance.Id, chassisDefinition.MassKg);
        var linkBodies = new Dictionary<string, RigidBody3D>(StringComparer.Ordinal) { [root.Id] = chassis };
        _linkBodies[LinkBodyKey(graph.Id, root.Id)] = chassis;
        var linksById = graph.Links.ToDictionary(link => link.Id, StringComparer.Ordinal);
        var bodyByActuator = new Dictionary<string, (RigidBody3D Parent, RigidBody3D Child, Vector3 AxisLocal)>(StringComparer.Ordinal);
        foreach (var instance in graph.Components.Where(component => component.Id != chassisInstance.Id))
        {
            var definition = catalogue[instance.CatalogueId];
            var instanceSize = instance.SizeOverrideM ?? definition.SizeM;
            var node = MakeComponentVisual(instance.Id, definition, instance.SizeOverrideM);
            var link = linksById[instance.LinkId];
            var linkRotation = ToQuaternion(link.RotationXyzw);
            var componentWorldPosition = ToVector(link.PositionM) + linkRotation * ToVector(instance.PositionM);
            var componentWorldRotation = linkRotation * ToQuaternion(instance.RotationXyzw);
            if (definition.Model == "rgb_camera")
            {
                var cameraProperties = instance.Camera ?? definition.Camera ?? new CameraProperties(75, 640, 480, 0.05, 100, true);
                if (cameraProperties.Enabled)
                {
                    var cameraNode = new Camera3D
                    {
                        Name = instance.Id + "ViewportCamera",
                        // Place the optical origin beyond the camera housing's front face.
                        Position = new Vector3(0, 0.18f, -(float)instanceSize[2] * 0.58f),
                        Current = false,
                        Fov = (float)cameraProperties.HorizontalFovDeg,
                        KeepAspect = Camera3D.KeepAspectEnum.Width,
                        CullMask = uint.MaxValue,
                        Near = (float)cameraProperties.NearClipM,
                        Far = (float)cameraProperties.FarClipM
                    };
                    node.AddChild(cameraNode);
                    _robotCameras[$"{graph.Id}/{instance.Id}"] = cameraNode;
                    Console.WriteLine(FormattableString.Invariant($"ROBOT_CAMERA_READY robot={graph.Id} component={instance.Id} name={instance.DisplayName ?? instance.Id} fov_horizontal_deg={cameraProperties.HorizontalFovDeg:F1} resolution_metadata={cameraProperties.ResolutionWidthPx}x{cameraProperties.ResolutionHeightPx} near_m={cameraProperties.NearClipM:F3} far_m={cameraProperties.FarClipM:F1}"));
                }
            }
            if (definition.Model == "wheel")
            {
                var radius = (float)(instance.SizeOverrideM is null ? definition.RadiusM ?? instanceSize[2] / 2 : instanceSize[2] / 2);
                var wheel = new RigidBody3D
                {
                    Name = instance.Id,
                    Position = componentWorldPosition,
                    Quaternion = componentWorldRotation,
                    Mass = (float)definition.MassKg,
                    ContinuousCd = true,
                    CanSleep = false,
                    CollisionLayer = _actuationNoLoadSmoke ? 0u : 1u,
                    CollisionMask = _actuationNoLoadSmoke ? 0u : 1u,
                    ContactMonitor = true,
                    MaxContactsReported = 8
                };
                wheel.AddChild(new CollisionShape3D
                {
                    Name = instance.Id + "Collision",
                    RotationDegrees = new Vector3(0, 0, 90),
                    Shape = new CylinderShape3D { Radius = radius, Height = (float)instanceSize[1] }
                });
                wheel.AddChild(node);
                _componentVisuals[instance.Id] = node;
                AddChild(wheel); _bodies.Add(wheel);
                _physicsAdapter.BindBody(instance.Id, wheel);
                _physicsAdapter.RegisterBody(instance.Id, definition.MassKg);
                linkBodies[link.Id] = wheel;
                _linkBodies[LinkBodyKey(graph.Id, link.Id)] = wheel;

                var robotJoint = graph.Joints.Single(candidate => candidate.ChildLinkId == link.Id);
                if (robotJoint.ActuatorComponentId is not null)
                {
                    var hinge = new HingeJoint3D
                    {
                        Name = robotJoint.Id,
                        Position = ToVector(robotJoint.ParentFramePositionM),
                        Quaternion = ToQuaternion(robotJoint.ParentFrameRotationXyzw) * new Quaternion(Vector3.Back, ToVector(robotJoint.AxisLocal).Normalized()),
                        ExcludeNodesFromCollision = true,
                        NodeA = "..",
                        NodeB = "../../" + wheel.Name
                    };
                    chassis.AddChild(hinge);
                    _jointNodes[JointNodeKey(graph.Id, robotJoint.Id)] = hinge;
                    bodyByActuator.Add(robotJoint.ActuatorComponentId, (chassis, wheel, ToVector(robotJoint.AxisLocal)));
                }
                continue;
            }
            if (link.Id != graph.RootLinkId && instance.Id == link.BaseComponentId &&
                graph.Joints.Any(candidate => candidate.ChildLinkId == link.Id && candidate.Type is "fixed" or "revolute" or "continuous"))
            {
                var robotJoint = graph.Joints.Single(candidate => candidate.ChildLinkId == link.Id);
                if (!linkBodies.TryGetValue(robotJoint.ParentLinkId, out var parentBody))
                    throw new InvalidDataException($"Parent link {robotJoint.ParentLinkId} must be built before child link {link.Id}.");
                var body = new RigidBody3D
                {
                    Name = instance.Id,
                    Position = componentWorldPosition,
                    Quaternion = componentWorldRotation,
                    Mass = (float)definition.MassKg,
                    ContinuousCd = true,
                    CanSleep = false
                };
                Shape3D shape = definition.Primitive switch
                {
                    "sphere" => new SphereShape3D { Radius = (float)(instance.SizeOverrideM is null ? definition.RadiusM ?? instanceSize[0] / 2 : instanceSize[0] / 2) },
                    "cylinder" => new CylinderShape3D { Radius = (float)(instance.SizeOverrideM is null ? definition.RadiusM ?? instanceSize[0] / 2 : instanceSize[0] / 2), Height = (float)instanceSize[1] },
                    _ => new BoxShape3D { Size = ToVector(instanceSize) }
                };
                body.AddChild(new CollisionShape3D { Name = instance.Id + "Collision", Shape = shape });
                body.AddChild(node);
                AddChild(body);
                _bodies.Add(body);
                linkBodies[link.Id] = body;
                _linkBodies[LinkBodyKey(graph.Id, link.Id)] = body;
                _componentVisuals[instance.Id] = node;
                _physicsAdapter.BindBody(instance.Id, body);
                _physicsAdapter.RegisterBody(instance.Id, definition.MassKg);

                var parentBase = graph.Components.Single(item => item.Id == linksById[robotJoint.ParentLinkId].BaseComponentId);
                var engineJoint = CreateEngineJoint(robotJoint, parentBody, body, parentBase);
                parentBody.AddChild(engineJoint);
                _jointNodes[JointNodeKey(graph.Id, robotJoint.Id)] = engineJoint;
                if (robotJoint.ActuatorComponentId is not null)
                    bodyByActuator.Add(robotJoint.ActuatorComponentId, (parentBody, body, ToVector(robotJoint.AxisLocal)));
                continue;
            }
            if (instance.LinkId != graph.RootLinkId && !linkBodies.TryGetValue(instance.LinkId, out _))
                throw new InvalidDataException($"Component {instance.Id} has no rendered rigid link {instance.LinkId}.");
            node.Position = ToVector(instance.PositionM);
            node.Quaternion = ToQuaternion(instance.RotationXyzw);
            _componentVisuals[instance.Id] = node;
            if (definition.Model == "distance_raycast")
            {
                if (_distanceSensor is not null) throw new InvalidDataException("The current sensor bridge supports one distance raycast per robot.");
                _distanceSensor = node;
                _distanceSensorComponentId = instance.Id;
            }
            var attachmentBody = instance.LinkId == graph.RootLinkId ? chassis : linkBodies[instance.LinkId];
            if ((graph.ComponentMountConnections ?? []).Any(connection => connection.ChildComponentId == instance.Id))
            {
                var collision = MakeComponentCollisionShape(instance.Id, definition, instance.SizeOverrideM);
                collision.Position = node.Position;
                collision.Quaternion = node.Quaternion;
                if (definition.Model == "rgb_camera") collision.Position += new Vector3(0, 0, (float)instanceSize[2] * 0.57f);
                attachmentBody.AddChild(collision);
            }
            else if (definition.Primitive == "sphere")
            {
                var radius = (float)(instance.SizeOverrideM is null ? definition.RadiusM ?? instanceSize[0] / 2 : instanceSize[0] / 2);
                attachmentBody.AddChild(new CollisionShape3D { Name = instance.Id + "Collision", Position = node.Position, Shape = new SphereShape3D { Radius = radius } });
            }
            attachmentBody.AddChild(node);
        }
        foreach (var motor in graph.Components.Where(component => catalogue[component.CatalogueId].Model == "dc_motor"))
        {
            if (!bodyByActuator.TryGetValue(motor.Id, out var binding)) throw new InvalidDataException($"Motor {motor.Id} has no actuated mechanical joint.");
            _physicsAdapter.RegisterMotor(motor.Id, binding.Parent, binding.Child, binding.AxisLocal, catalogue[motor.CatalogueId]);
        }
        AddLabel3D(graph.DisplayName, chassis.Position + new Vector3(0, 0.58f, 0), new Color(0.8f, 0.94f, 0.91f));
        Console.WriteLine($"ROBOT_BUILT id={graph.Id} links={graph.Links.Count} joints={graph.Joints.Count} components={graph.Components.Count}");
    }

    private Node3D MakeComponentVisual(string id, ComponentDefinition definition, double[]? sizeOverrideM = null)
    {
        var sizeM = sizeOverrideM ?? definition.SizeM;
        var color = definition.Category switch { "mobility" => new Color(0.12f, 0.15f, 0.19f), "actuator" => new Color(0.78f, 0.45f, 0.16f), "controller" => new Color(0.22f, 0.48f, 0.82f), "sensor" => new Color(0.46f, 0.78f, 0.93f), _ => new Color(0.72f, 0.67f, 0.3f) };
        if (definition.Model == "rgb_camera")
        {
            var camera = new Node3D { Name = id };
            camera.AddChild(new MeshInstance3D
            {
                Name = id + "Housing",
                Position = new Vector3(0, 0, (float)sizeM[2] * 0.57f),
                Mesh = new BoxMesh { Size = ToVector(sizeM) },
                MaterialOverride = Material(new Color(0.14f, 0.19f, 0.24f))
            });
            camera.AddChild(new MeshInstance3D
            {
                Name = id + "LensMarker",
                Position = new Vector3(0, 0, -(float)sizeM[2] * 0.045f),
                Mesh = new BoxMesh { Size = new Vector3(0.028f, 0.028f, 0.012f) },
                MaterialOverride = Material(new Color(0.04f, 0.11f, 0.17f))
            });
            return camera;
        }
        if (definition.Primitive == "sphere")
        {
            var radius = (float)(sizeOverrideM is null ? definition.RadiusM ?? sizeM[0] / 2 : sizeM[0] / 2);
            return new MeshInstance3D { Name = id, Mesh = new SphereMesh { Radius = radius, Height = radius * 2 }, MaterialOverride = Material(color) };
        }
        if (definition.Model == "wheel")
        {
            var wheelRadius = (float)(sizeOverrideM is null ? definition.RadiusM ?? sizeM[2] / 2 : sizeM[2] / 2);
            return new MeshInstance3D { Name = id, Mesh = new CylinderMesh { TopRadius = wheelRadius, BottomRadius = wheelRadius, Height = (float)sizeM[1] }, RotationDegrees = new Vector3(0, 0, 90), MaterialOverride = Material(color) };
        }
        if (definition.Primitive == "cylinder")
        {
            var radius = (float)(sizeOverrideM is null ? definition.RadiusM ?? sizeM[0] / 2 : sizeM[0] / 2);
            return new MeshInstance3D { Name = id, Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = (float)sizeM[1] }, MaterialOverride = Material(color) };
        }
        return new MeshInstance3D { Name = id, Mesh = new BoxMesh { Size = ToVector(sizeM) }, MaterialOverride = Material(color) };
    }

    private StaticBody3D AddStaticBox(string name, Vector3 position, Vector3 size, Color color, Quaternion? rotation = null, float friction = 0.7f)
    {
        var body = new StaticBody3D { Name = name, Position = position, Quaternion = rotation ?? Quaternion.Identity, PhysicsMaterialOverride = new PhysicsMaterial { Friction = friction } };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Material(color) });
        AddChild(body);
        return body;
    }

    private void AddStaticCylinder(string name, Vector3 position, float radius, float height, Color color, Quaternion rotation, float friction = 0.7f)
    {
        var body = new StaticBody3D { Name = name, Position = position, Quaternion = rotation, PhysicsMaterialOverride = new PhysicsMaterial { Friction = friction } };
        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = radius, Height = height } });
        body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height }, MaterialOverride = Material(color) });
        AddChild(body);
    }

    private void AddStaticSphere(string name, Vector3 position, float radius, Color color, float friction = 0.7f)
    {
        var body = new StaticBody3D { Name = name, Position = position, PhysicsMaterialOverride = new PhysicsMaterial { Friction = friction } };
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = radius } });
        body.AddChild(new MeshInstance3D { Mesh = new SphereMesh { Radius = radius, Height = radius * 2 }, MaterialOverride = Material(color) });
        AddChild(body);
    }

    private void AddDynamicCylinder(string name, Vector3 position, float radius, float height, float mass, Quaternion? rotation = null, Color? color = null, float friction = 0.7f)
    {
        var body = new RigidBody3D { Name = name, Position = position, Quaternion = rotation ?? Quaternion.Identity, Mass = Math.Max(mass, 0.01f), PhysicsMaterialOverride = new PhysicsMaterial { Friction = friction } };
        body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = radius, Height = height } });
        body.AddChild(new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = radius, BottomRadius = radius, Height = height }, MaterialOverride = Material(color ?? new Color(0.35f, 0.79f, 0.9f, 0.75f), true) });
        AddChild(body); _bodies.Add(body);
        _physicsAdapter.BindBody(name, body);
        if (mass > 0) _physicsAdapter.RegisterBody(name, mass);
    }

    private void AddDynamicWorldBox(string name, Vector3 position, Vector3 size, Quaternion rotation, float mass, Color color, float friction = 0.7f)
    {
        var body = new RigidBody3D { Name = name, Position = position, Quaternion = rotation, Mass = Math.Max(mass, 0.01f), ContinuousCd = true, PhysicsMaterialOverride = new PhysicsMaterial { Friction = friction } };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        body.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = Material(color) });
        AddChild(body); _bodies.Add(body);
        _physicsAdapter.BindBody(name, body);
        if (mass > 0) _physicsAdapter.RegisterBody(name, mass);
    }

    private void AddWorldVisual(WorldObject item, Vector3 position, Quaternion rotation, Vector3 size, Color color)
    {
        Mesh mesh = item.Primitive switch
        {
            "cylinder" => new CylinderMesh { TopRadius = (float)(item.RadiusM ?? size.X / 2), BottomRadius = (float)(item.RadiusM ?? size.X / 2), Height = size.Y },
            "sphere" => new SphereMesh { Radius = (float)(item.RadiusM ?? size.X / 2), Height = (float)(2 * (item.RadiusM ?? size.X / 2)) },
            _ => new BoxMesh { Size = size }
        };
        AddChild(new MeshInstance3D { Name = item.Id, Position = position, Quaternion = rotation, Mesh = mesh, MaterialOverride = Material(color) });
    }

    private void AddWorldSign(WorldObject item, Vector3 position, Quaternion rotation, Vector3 size)
    {
        var label = new Label3D
        {
            Name = item.Id + "Label",
            Text = string.IsNullOrWhiteSpace(item.DisplayName) ? item.Id : item.DisplayName,
            Position = position + Vector3.Up * (size.Y * 0.52f),
            Quaternion = rotation,
            FontSize = 48,
            PixelSize = 0.004f,
            Modulate = new Color(0.95f, 0.98f, 1f),
            OutlineSize = 8,
            OutlineModulate = new Color(0.06f, 0.08f, 0.1f)
        };
        AddChild(label);
    }

    private static Color WorldObjectColor(string assetRef, IReadOnlyList<string> tags)
    {
        if (tags.Contains("visual_only", StringComparer.Ordinal))
            return assetRef.Contains("yellow", StringComparison.Ordinal) ? new Color(0.98f, 0.73f, 0.17f) : new Color(0.84f, 0.88f, 0.9f);
        if (assetRef.Contains("crate.blue", StringComparison.Ordinal)) return new Color(0.13f, 0.38f, 0.72f);
        if (assetRef.Contains("crate.green", StringComparison.Ordinal)) return new Color(0.15f, 0.57f, 0.32f);
        if (assetRef.Contains("crate.orange", StringComparison.Ordinal)) return new Color(0.89f, 0.42f, 0.13f);
        if (assetRef.Contains("rack", StringComparison.Ordinal)) return new Color(0.24f, 0.29f, 0.36f);
        if (assetRef.Contains("pillar", StringComparison.Ordinal)) return new Color(0.43f, 0.48f, 0.53f);
        if (assetRef.Contains("sign.blue", StringComparison.Ordinal)) return new Color(0.08f, 0.29f, 0.54f);
        if (assetRef.Contains("sign.orange", StringComparison.Ordinal)) return new Color(0.8f, 0.25f, 0.08f);
        if (tags.Contains("support_surface", StringComparer.Ordinal)) return new Color(0.46f, 0.34f, 0.22f);
        if (tags.Contains("pickable", StringComparer.Ordinal)) return new Color(0.25f, 0.74f, 0.85f);
        return new Color(0.55f, 0.39f, 0.25f);
    }

    private void AddGrid(float width, float depth)
    {
        var mesh = new ImmediateMesh();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines, Material(new Color(0.34f, 0.4f, 0.48f)));
        for (var i = -10; i <= 10; i++)
        {
            mesh.SurfaceAddVertex(new Vector3(i, 0.006f, -depth / 2)); mesh.SurfaceAddVertex(new Vector3(i, 0.006f, depth / 2));
            mesh.SurfaceAddVertex(new Vector3(-width / 2, 0.006f, i)); mesh.SurfaceAddVertex(new Vector3(width / 2, 0.006f, i));
        }
        mesh.SurfaceEnd(); AddChild(new MeshInstance3D { Name = "RoomGrid", Mesh = mesh });
    }

    private void BuildAuthoringWorkspace(RobotAuthoringDraft draft)
    {
        _authoringDraft = draft;
        _authoringViewport = new RobotAuthoringViewport(_catalogue, draft) { Name = "AuthoringViewport" };
        AddChild(_authoringViewport);
        var parts = (Node3D)_authoringViewport;

        var layer = new CanvasLayer { Layer = 10 };
        var panel = new PanelContainer { Position = new Vector2(18, 18), CustomMinimumSize = new Vector2(470, 620), Size = new Vector2(470, 620) };
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(450, 600), SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        var stack = new VBoxContainer();
        stack.AddChild(new Label { Text = "ROBOT SHOP  /  AUTHORING DRAFT", Modulate = new Color(0.6f, 0.93f, 0.83f) });
        var state = new Label { Name = "DraftStatus", Text = draft.Assembly is null ? "New Empty Robot · no root part" : $"{draft.Assembly.Components.Count} parts · unsimulated draft" };
        stack.AddChild(state);
        stack.AddChild(new Label { Text = "Add the hardware below, connect its typed ports, assign an installed firmware artifact, then run in Exact mode.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var picker = new OptionButton();
        picker.Name = "DraftCataloguePicker";
        var orderedCatalogue = _catalogue.Values.OrderBy(item => item.Category).ThenBy(item => item.Id).ToArray();
        foreach (var item in orderedCatalogue) picker.AddItem($"{item.Category} · {item.Id}");
        var defaultStructural = Array.FindIndex(orderedCatalogue, item => item.Id == "rw.primitive.box");
        if (defaultStructural < 0) defaultStructural = Array.FindIndex(orderedCatalogue, item => item.Category is "body" or "structure");
        picker.Select(Math.Max(0, defaultStructural));
        stack.AddChild(picker);
        var partPicker = new OptionButton();
        partPicker.Name = "DraftPartPicker";
        stack.AddChild(partPicker);
        stack.AddChild(new Label { Text = "Controller firmware assignment" });
        var firmwareControllerPicker = new OptionButton { Name = "DraftFirmwareController" };
        var firmwareArtifactPicker = new OptionButton { Name = "DraftFirmwareArtifact" };
        stack.AddChild(firmwareControllerPicker);
        stack.AddChild(firmwareArtifactPicker);
        var firmwareAssignmentStatus = new Label { Name = "DraftFirmwareStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        stack.AddChild(firmwareAssignmentStatus);
        var feedback = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Name = "DraftFeedback" };
        stack.AddChild(new Label { Text = "Bind a motor to a rotating wheel joint" });
        var motorBindingPicker = new OptionButton { Name = "DraftMotorPicker" };
        var wheelJointPicker = new OptionButton { Name = "DraftWheelJointPicker" };
        stack.AddChild(motorBindingPicker);
        stack.AddChild(wheelJointPicker);
        var electricalConnections = new Label { Name = "DraftElectricalConnections", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        stack.AddChild(electricalConnections);
        var parentPortPicker = new OptionButton();
        var childPortPicker = new OptionButton();
        var jointPicker = new OptionButton { Name = "DraftJointPicker" };
        jointPicker.AddItem("Rigid mount"); jointPicker.AddItem("Fixed joint"); jointPicker.AddItem("Revolute joint");
        stack.AddChild(parentPortPicker); stack.AddChild(childPortPicker); stack.AddChild(jointPicker);
        var electricalFrom = new OptionButton(); var electricalTo = new OptionButton();
        var electricalBus = new LineEdit { PlaceholderText = "Optional bus id", CustomMinimumSize = new Vector2(150, 0) };
        stack.AddChild(new Label { Text = "Typed electrical connection" });
        stack.AddChild(electricalFrom); stack.AddChild(electricalTo); stack.AddChild(electricalBus);
        var dimensions = Enumerable.Range(0, 3).Select(_ => new SpinBox { MinValue = 0.005, MaxValue = 10, Step = 0.005, Value = 0.1, CustomMinimumSize = new Vector2(88, 0) }).ToArray();
        var dimensionLabels = new[] { new Label { Text = "X (m)" }, new Label { Text = "Y (m)" }, new Label { Text = "Z (m)" } };
        var dimensionRow = new HBoxContainer();
        foreach (var (input, index) in dimensions.Select((input, index) => (input, index)))
        { var column = new VBoxContainer(); column.AddChild(dimensionLabels[index]); column.AddChild(input); dimensionRow.AddChild(column); }
        stack.AddChild(dimensionRow);
        stack.AddChild(new Label { Text = "Selected component name" });
        var componentNameInput = new LineEdit { Name = "DraftComponentName", PlaceholderText = "Component name" };
        stack.AddChild(componentNameInput);
        var cameraPropertiesInputs = new[]
        {
            MakePoseInput(10, 170, 1, new Vector2(70, 0)),
            MakePoseInput(16, 4096, 16, new Vector2(78, 0)),
            MakePoseInput(16, 4096, 16, new Vector2(78, 0)),
            MakePoseInput(0.005, 5, 0.005, new Vector2(70, 0)),
            MakePoseInput(0.01, 1000, 0.1, new Vector2(78, 0))
        };
        var cameraLabels = new[] { "HFOV °", "Width px", "Height px", "Near m", "Far m" };
        var cameraPropertyRow = new HBoxContainer();
        foreach (var (input, index) in cameraPropertiesInputs.Select((input, index) => (input, index)))
        { var column = new VBoxContainer(); column.AddChild(new Label { Text = cameraLabels[index] }); column.AddChild(input); cameraPropertyRow.AddChild(column); }
        stack.AddChild(new Label { Text = "Camera viewport metadata · render uses the current viewport size" });
        stack.AddChild(cameraPropertyRow);
        var cameraEnabledInput = new CheckButton { Name = "DraftCameraEnabled", Text = "Camera enabled", ButtonPressed = true };
        stack.AddChild(cameraEnabledInput);
        stack.AddChild(MakeButton("Apply name and camera settings", () =>
        {
            try
            {
                if (_authoringDraft?.Assembly is null || _selectedShopInstanceId is null) throw new InvalidDataException("Select a saved component first.");
                var component = _authoringDraft.Assembly.Components.Single(item => item.Id == _selectedShopInstanceId);
                var updated = RobotAssemblyEditor.SetComponentDisplayName(_authoringDraft.Assembly, _catalogue, component.Id,
                    string.IsNullOrWhiteSpace(componentNameInput.Text) ? component.Id : componentNameInput.Text);
                if (_catalogue[component.CatalogueId].Model == "rgb_camera")
                {
                    var camera = new CameraProperties(cameraPropertiesInputs[0].Value, (int)cameraPropertiesInputs[1].Value,
                        (int)cameraPropertiesInputs[2].Value, cameraPropertiesInputs[3].Value, cameraPropertiesInputs[4].Value, cameraEnabledInput.ButtonPressed);
                    updated = RobotAssemblyEditor.SetCameraProperties(updated, _catalogue, component.Id, camera);
                }
                _authoringDraft = _authoringDraft with { Assembly = updated };
                RebuildDraftVisuals(parts, _authoringDraft);
                feedback.Text = _catalogue[component.CatalogueId].Model == "rgb_camera"
                    ? $"Saved name and camera properties for {component.Id}." : $"Saved component name for {component.Id}.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        }));
        var transformInputs = Enumerable.Range(0, 6).Select(index => new SpinBox
        {
            MinValue = index < 3 ? -10 : -360, MaxValue = index < 3 ? 10 : 360,
            Step = index < 3 ? 0.005 : 1, CustomMinimumSize = new Vector2(58, 0)
        }).ToArray();
        var transformLabels = new[] { "X m", "Y m", "Z m", "Rot X°", "Rot Y°", "Rot Z°" };
        var transformGrid = new GridContainer { Columns = 3 };
        foreach (var (input, index) in transformInputs.Select((input, index) => (input, index)))
        { var column = new VBoxContainer(); column.AddChild(new Label { Text = transformLabels[index] }); column.AddChild(input); transformGrid.AddChild(column); }
        stack.AddChild(new Label { Text = "Selected part world transform" });
        stack.AddChild(transformGrid);
        stack.AddChild(MakeButton("Apply transform", () =>
        {
            try
            {
                _authoringViewport!.ApplySelectedWorldPose(
                    new Vector3((float)transformInputs[0].Value, (float)transformInputs[1].Value, (float)transformInputs[2].Value),
                    new Vector3((float)transformInputs[3].Value, (float)transformInputs[4].Value, (float)transformInputs[5].Value));
                feedback.Text = "Applied selected part world transform.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        }));
        var massStatus = new Label { Name = "DraftMassStatus" };
        stack.AddChild(massStatus);
        stack.AddChild(feedback);
        var snapToggle = new CheckButton { Text = "Snap connectors while dragging", ButtonPressed = true };
        snapToggle.Toggled += enabled => _authoringViewport!.SnapEnabled = enabled;
        stack.AddChild(snapToggle);
        var placementMode = new OptionButton();
        placementMode.AddItem("Revolute snap"); placementMode.AddItem("Fixed snap"); placementMode.AddItem("Free placement");
        placementMode.Select(0);
        placementMode.ItemSelected += index =>
        {
            _authoringViewport!.SnapEnabled = index != 2;
            _authoringViewport.JointType = index == 1 ? "fixed" : "revolute";
            snapToggle.ButtonPressed = index != 2;
        };
        stack.AddChild(placementMode);
        stack.AddChild(new Label { Text = "Viewport: drag to move · R then drag to rotate · right-drag orbit · middle-drag pan · wheel zoom · Esc cancels",
            AutowrapMode = TextServer.AutowrapMode.WordSmart });
        _authoringViewport.Feedback += message => feedback.Text = message;
        _authoringViewport.SelectionChanged += id =>
        {
            _selectedShopInstanceId = id;
            if (_authoringDraft?.Assembly?.Components.Any(component => component.Id == id) == true) RefreshParts();
        };
        _authoringViewport.DraftChanged += updated =>
        {
            _authoringDraft = updated;
            _selectedShopInstanceId = _authoringViewport.SelectedId;
            _refreshAuthoringWorkspace?.Invoke();
        };
        void RefreshFirmwareOptions()
        {
            var requestedArtifact = firmwareArtifactPicker.Selected >= 0
                ? firmwareArtifactPicker.GetItemMetadata(firmwareArtifactPicker.Selected).AsString() : null;
            firmwareArtifactPicker.Clear();
            if (_authoringDraft?.Assembly is null || firmwareControllerPicker.Selected < 0)
            {
                firmwareAssignmentStatus.Text = "Add a controller to assign firmware.";
                return;
            }
            var controllerId = firmwareControllerPicker.GetItemMetadata(firmwareControllerPicker.Selected).AsString();
            var controller = _authoringDraft.Assembly.Components.Single(component => component.Id == controllerId);
            var model = _catalogue[controller.CatalogueId].Model;
            var target = model.StartsWith("renode_", StringComparison.Ordinal) ? model["renode_".Length..] : string.Empty;
            foreach (var artifact in _firmwareArtifacts.Values.Where(item => item.Target == target).OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                var index = firmwareArtifactPicker.ItemCount;
                firmwareArtifactPicker.AddItem(artifact.DisplayName + $" · {artifact.Sha256[..8]}");
                firmwareArtifactPicker.SetItemMetadata(index, artifact.Id);
                if (artifact.Id == requestedArtifact) firmwareArtifactPicker.Select(index);
            }
            if (firmwareArtifactPicker.Selected < 0 && firmwareArtifactPicker.ItemCount > 0) firmwareArtifactPicker.Select(0);
            var currentAssignment = (_authoringDraft.FirmwareAssignments ?? []).SingleOrDefault(item => item.ControllerComponentId == controllerId);
            firmwareAssignmentStatus.Text = currentAssignment is null ? "No firmware assigned to the selected controller."
                : $"Assigned {currentAssignment.FirmwareArtifactId} to {controllerId} · SHA-256 {currentAssignment.FirmwareSha256[..12]}…";
        }
        void RefreshParts()
        {
            partPicker.Clear();
            electricalFrom!.Clear(); electricalTo!.Clear();
            firmwareControllerPicker!.Clear();
            motorBindingPicker!.Clear();
            wheelJointPicker!.Clear();
            var components = _authoringDraft!.Assembly?.Components ?? [];
            foreach (var component in components)
            {
                partPicker.AddItem($"{component.Id} · {component.CatalogueId}");
                foreach (var port in _catalogue[component.CatalogueId].ElectricalPorts)
                { electricalFrom.AddItem($"{component.Id}.{port.Id} ({port.Interface})"); electricalTo.AddItem($"{component.Id}.{port.Id} ({port.Interface})"); }
            }
            foreach (var controller in components.Where(component => _catalogue[component.CatalogueId].ElectricalPorts.Any(port => port.Interface == "uart")))
            {
                var index = firmwareControllerPicker.ItemCount;
                firmwareControllerPicker.AddItem($"{controller.Id} · {_catalogue[controller.CatalogueId].Id}");
                firmwareControllerPicker.SetItemMetadata(index, controller.Id);
                if (controller.Id == _selectedShopInstanceId) firmwareControllerPicker.Select(index);
            }
            if (firmwareControllerPicker.Selected < 0 && firmwareControllerPicker.ItemCount > 0) firmwareControllerPicker.Select(0);
            foreach (var motor in components.Where(component => _catalogue[component.CatalogueId].Model == "dc_motor"))
            {
                var index = motorBindingPicker.ItemCount;
                motorBindingPicker.AddItem(motor.Id + " · motor");
                motorBindingPicker.SetItemMetadata(index, motor.Id);
            }
            if (_authoringDraft.Assembly is { } currentGraph)
                foreach (var joint in currentGraph.Joints.Where(joint => (joint.Type is "revolute" or "continuous")
                    && currentGraph.Components.Any(component => component.LinkId == joint.ChildLinkId && _catalogue[component.CatalogueId].Model == "wheel")))
                {
                    var wheelId = currentGraph.Components.Single(component => component.LinkId == joint.ChildLinkId
                        && _catalogue[component.CatalogueId].Model == "wheel").Id;
                    var index = wheelJointPicker.ItemCount;
                    wheelJointPicker.AddItem($"{joint.Id} · {wheelId}" + (joint.ActuatorComponentId is null ? " · unbound" : $" · {joint.ActuatorComponentId}"));
                    wheelJointPicker.SetItemMetadata(index, joint.Id);
                }
            electricalConnections!.Text = components.Count == 0 || _authoringDraft.Assembly is null
                ? "Electrical connections: none"
                : "Electrical connections:\n" + (_authoringDraft.Assembly.ElectricalConnections.Count == 0 ? "(none yet)"
                    : string.Join("\n", _authoringDraft.Assembly.ElectricalConnections.Select(connection => $"{connection.From} → {connection.To} · {connection.Interface}")));
            var assigned = _authoringDraft.FirmwareAssignments ?? [];
            if (assigned.Count > 0) firmwareAssignmentStatus.Text = "Assignments: " + string.Join("; ", assigned.Select(item => $"{item.ControllerComponentId} ← {item.FirmwareArtifactId}"));
            RefreshFirmwareOptions();
            if (components.Count > 0)
            {
                partPicker.Select(Math.Max(0, components.ToList().FindIndex(component => component.Id == _selectedShopInstanceId)));
                var selected = components[partPicker.Selected];
                _selectedShopInstanceId = selected.Id;
                UpdateSelectedPartProperties(selected);
            }
            else massStatus!.Text = "Empty draft · add a structural component to begin.";
        }
        void UpdateSelectedPartProperties(ComponentInstance selected)
        {
            var size = selected.SizeOverrideM ?? _catalogue[selected.CatalogueId].SizeM;
            for (var axis = 0; axis < 3; axis++) dimensions![axis].Value = size[axis];
            var definition = _catalogue[selected.CatalogueId];
            componentNameInput.Text = selected.DisplayName ?? selected.Id;
            var camera = selected.Camera ?? definition.Camera ?? new CameraProperties(75, 640, 480, 0.05, 100, true);
            var cameraValues = new[] { camera.HorizontalFovDeg, camera.ResolutionWidthPx, camera.ResolutionHeightPx, camera.NearClipM, camera.FarClipM };
            for (var inputIndex = 0; inputIndex < cameraValues.Length; inputIndex++) cameraPropertiesInputs[inputIndex].Value = cameraValues[inputIndex];
            cameraEnabledInput.ButtonPressed = camera.Enabled;
            var functionalProperties = definition.Model switch
            {
                "dc_motor" => $" · stall {definition.StallTorqueNm?.ToString("F2", CultureInfo.InvariantCulture) ?? "n/a"} N·m · {definition.NoLoadRpm?.ToString("F0", CultureInfo.InvariantCulture) ?? "n/a"} rpm",
                "wheel" => $" · radius {(selected.SizeOverrideM is null ? definition.RadiusM ?? size[2] / 2 : size[2] / 2).ToString("F3", CultureInfo.InvariantCulture)} m · width {size[1].ToString("F3", CultureInfo.InvariantCulture)} m",
                "distance_raycast" => " · range 4.0 m · aim with component rotation",
                "rgb_camera" => $" · horizontal FOV {camera.HorizontalFovDeg:F0}° · metadata {camera.ResolutionWidthPx}×{camera.ResolutionHeightPx} px · near/far {camera.NearClipM:F2}/{camera.FarClipM:F0} m · {(camera.Enabled ? "enabled" : "disabled")}",
                "dual_hbridge" => " · dual PWM/direction H-bridge",
                _ when definition.Model.StartsWith("renode_", StringComparison.Ordinal) => $" · board {definition.Model["renode_".Length..]}",
                _ => string.Empty
            };
            massStatus!.Text = $"Mass · {definition.MassKg.ToString("F3", CultureInfo.InvariantCulture)} kg · size values in metres{functionalProperties}";
            if (_authoringViewport?.GetSelectedWorldPose() is { } pose)
            {
                var values = new[] { pose.Position.X, pose.Position.Y, pose.Position.Z, pose.RotationDegrees.X, pose.RotationDegrees.Y, pose.RotationDegrees.Z };
                for (var axis = 0; axis < values.Length; axis++) transformInputs![axis].Value = values[axis];
            }
        }
        firmwareControllerPicker.ItemSelected += _ => RefreshFirmwareOptions();
        var assignFirmwareButton = MakeButton("Assign selected firmware", () =>
        {
            try
            {
                if (firmwareControllerPicker.Selected < 0 || firmwareArtifactPicker.Selected < 0)
                    throw new InvalidDataException("Add a controller and select a compatible registered firmware artifact first.");
                var controllerId = firmwareControllerPicker.GetItemMetadata(firmwareControllerPicker.Selected).AsString();
                var artifactId = firmwareArtifactPicker.GetItemMetadata(firmwareArtifactPicker.Selected).AsString();
                var artifact = _firmwareArtifacts[artifactId];
                var assignmentId = "controller-" + controllerId.Replace('.', '-').Replace('_', '-');
                _authoringDraft = _authoringDraft!.AssignFirmware(_catalogue, new RobotFirmwareAssignment(assignmentId,
                    controllerId, artifact.Id, artifact.Board, artifact.Target, artifact.RelativePath, artifact.Sha256));
                RefreshParts();
                firmwareAssignmentStatus.Text = $"Assigned {artifact.DisplayName} to {controllerId} · SHA-256 {artifact.Sha256[..12]}…";
                feedback.Text = "Firmware assignment saved in this draft.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        assignFirmwareButton.Name = "DraftAssignFirmware";
        stack.AddChild(assignFirmwareButton);
        var bindMotorButton = MakeButton("Bind motor to wheel joint", () =>
        {
            try
            {
                if (motorBindingPicker.Selected < 0 || wheelJointPicker.Selected < 0)
                    throw new InvalidDataException("Add a motor and attach a wheel with a revolute joint first.");
                var motorId = motorBindingPicker.GetItemMetadata(motorBindingPicker.Selected).AsString();
                var jointId = wheelJointPicker.GetItemMetadata(wheelJointPicker.Selected).AsString();
                _authoringDraft = _authoringDraft!.SetJointActuator(_catalogue, jointId, motorId);
                feedback.Text = $"Bound motor {motorId} to wheel joint {jointId}.";
                RefreshParts();
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        bindMotorButton.Name = "DraftBindMotor";
        stack.AddChild(bindMotorButton);
        var roverWiringButton = MakeButton("Wire rover electronics preset", () =>
        {
            try
            {
                _authoringDraft = AddRoverElectricalPreset(_authoringDraft!, _catalogue);
                feedback.Text = "Added the selected rover's real controller, driver, motor, sensor and power connections.";
                RefreshParts();
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        roverWiringButton.Name = "DraftWireRoverPreset";
        stack.AddChild(roverWiringButton);
        void RefreshPorts()
        {
            parentPortPicker.Clear(); childPortPicker.Clear();
            var graph = _authoringDraft?.Assembly;
            if (graph is null || graph.Components.Count == 0) return;
            var parent = graph.Components[checked((int)Math.Max(0, partPicker.Selected))];
            var parentDefinition = _catalogue[parent.CatalogueId];
            var childDefinition = _catalogue.Values.OrderBy(item => item.Category).ThenBy(item => item.Id).ElementAt(picker.Selected);
            foreach (var parentPort in parentDefinition.MountPorts)
                foreach (var childPort in childDefinition.MountPorts)
                    if (RobotGraphValidator.CanMate(parentPort, childPort)
                        && RobotAssemblyEditor.IsMountPortAvailable(graph, parent.Id, parentPort.Id))
                    { parentPortPicker.AddItem($"{parent.Id}.{parentPort.Id}"); childPortPicker.AddItem(childPort.Id); }
            if (parentPortPicker.ItemCount > 0) { parentPortPicker.Select(0); childPortPicker.Select(0); }
        }
        partPicker.ItemSelected += index =>
        {
            var selected = _authoringDraft!.Assembly!.Components[checked((int)index)];
            _selectedShopInstanceId = selected.Id;
            UpdateSelectedPartProperties(selected);
            RefreshPorts();
        };
        picker.ItemSelected += _ => RefreshPorts();
        RefreshParts();
        RefreshPorts();
        _refreshAuthoringWorkspace = () =>
        {
            RefreshParts();
            RefreshPorts();
            state.Text = _authoringDraft!.Assembly is null ? "New Empty Robot · no root part" : $"{_authoringDraft.Assembly.Components.Count} parts · unsimulated draft";
        };
        var addDraftButton = MakeButton("Add component", () =>
        {
            var definition = _catalogue.Values.OrderBy(item => item.Category).ThenBy(item => item.Id).ElementAt(picker.Selected);
            var id = CreateAuthoringInstanceId(definition, _authoringDraft!.Assembly?.Components.Select(item => item.Id) ?? []);
            try
            {
                _authoringDraft = _authoringDraft.AddFreeComponent(_catalogue, definition.Id, id);
                if (_authoringDraft.Assembly?.Components.Count == 1)
                {
                    var rootSize = _authoringDraft.Assembly.Components[0].SizeOverrideM ?? definition.SizeM;
                    _authoringDraft = _authoringDraft.SetRootPose(_catalogue, [0, rootSize[1] / 2, 0], [0, 0, 0, 1]);
                }
                _selectedShopInstanceId = id;
                RebuildDraftVisuals(parts, _authoringDraft);
                RefreshParts();
                state.Text = $"{_authoringDraft.Assembly!.Components.Count} parts · unsimulated draft";
                feedback.Text = $"Added {definition.Id}.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        addDraftButton.Name = "DraftAddComponent";
        stack.AddChild(addDraftButton);
        var attachDraftButton = MakeButton("Attach selected part", () =>
        {
            try
            {
                if (_authoringDraft!.Assembly is null || _authoringDraft.Assembly.Components.Count == 0 || parentPortPicker.Selected < 0)
                    throw new InvalidDataException("Choose a parent component and compatible connector ports first.");
                var definition = _catalogue.Values.OrderBy(item => item.Category).ThenBy(item => item.Id).ElementAt(picker.Selected);
                var parentPort = parentPortPicker.GetItemText(parentPortPicker.Selected).Split('.').Last();
                var childPort = childPortPicker.GetItemText(childPortPicker.Selected);
                var parentId = _authoringDraft.Assembly.Components[checked((int)Math.Max(0, partPicker.Selected))].Id;
                var childId = CreateAuthoringInstanceId(definition, _authoringDraft.Assembly.Components.Select(item => item.Id));
                var jointType = jointPicker.Selected switch { 1 => "fixed", 2 => "revolute", _ => null };
                _authoringDraft = _authoringDraft.AddSnappedComponent(_catalogue, parentId, parentPort, definition.Id, childPort, childId, jointType);
                _selectedShopInstanceId = childId; RebuildDraftVisuals(parts, _authoringDraft); RefreshParts(); RefreshPorts();
                state.Text = $"{_authoringDraft.Assembly!.Components.Count} parts · unsimulated draft";
                feedback.Text = $"Attached {childId} at {parentId}.{parentPort}.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        attachDraftButton.Name = "DraftAttachComponent";
        stack.AddChild(attachDraftButton);
        var connectElectricalButton = MakeButton("Connect electrical ports", () =>
        {
            try
            {
                if (_authoringDraft!.Assembly is null || electricalFrom.Selected < 0 || electricalTo.Selected < 0)
                    throw new InvalidDataException("Add components with electrical ports before creating a connection.");
                static string Endpoint(string label) => label[..label.IndexOf(' ')];
                var from = Endpoint(electricalFrom.GetItemText(electricalFrom.Selected));
                var to = Endpoint(electricalTo.GetItemText(electricalTo.Selected));
                _authoringDraft = _authoringDraft.AddElectricalConnection(_catalogue, from, to, electricalBus.Text);
                feedback.Text = $"Connected {from} to {to}; interface validated by the component catalogue.";
                RefreshParts();
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        connectElectricalButton.Name = "DraftConnectElectrical";
        stack.AddChild(connectElectricalButton);
        stack.AddChild(MakeButton("Apply dimensions (m)", () =>
        {
            try
            {
                if (_authoringDraft!.Assembly is null || _selectedShopInstanceId is null) throw new InvalidDataException("Select a component first.");
                var resized = RobotAssemblyEditor.SetComponentSize(_authoringDraft.Assembly, _catalogue, _selectedShopInstanceId, dimensions.Select(input => input.Value).ToArray());
                _authoringDraft = _authoringDraft with { Assembly = resized };
                RebuildDraftVisuals(parts, _authoringDraft);
                feedback.Text = $"Updated {_selectedShopInstanceId} dimensions in metres.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        }));
        var removeDraftButton = MakeButton("Remove selected part", () =>
        {
            var graph = _authoringDraft!.Assembly;
            if (graph is null || graph.Components.Count == 0) { feedback.Text = "The draft is already empty."; return; }
            var selectedId = _selectedShopInstanceId ?? graph.Components.Last().Id;
            try
            {
                _authoringDraft = _authoringDraft.RemoveComponent(_catalogue, selectedId);
                RebuildDraftVisuals(parts, _authoringDraft);
                _selectedShopInstanceId = _authoringDraft.Assembly?.Components.LastOrDefault()?.Id;
                RefreshParts();
                state.Text = _authoringDraft.Assembly is null ? "New Empty Robot · no root part" : $"{_authoringDraft.Assembly.Components.Count} parts · unsimulated draft";
                feedback.Text = $"Removed {selectedId}.";
            }
            catch (Exception exception) { feedback.Text = exception.Message; }
        });
        removeDraftButton.Name = "DraftRemoveComponent";
        stack.AddChild(removeDraftButton);
        var saveDraftButton = MakeButton("Save draft…", () => ShowDraftSaveDialog(feedback));
        saveDraftButton.Name = "DraftSave";
        stack.AddChild(saveDraftButton);
        var openDraftButton = MakeButton("Open draft…", () => ShowDraftOpenDialog(feedback));
        openDraftButton.Name = "DraftOpen";
        stack.AddChild(openDraftButton);
        var saveProjectDialog = new FileDialog
        {
            Name = "DraftSaveProjectDialog", FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem, CurrentFile = _authoringDraft.Id + ".roboworld",
            Filters = ["*.roboworld ; RoboWorld Project"]
        };
        saveProjectDialog.FileSelected += path =>
        {
            try
            {
                var hash = ProjectPackageStore.SaveAuthoringPackage(_project, path, _authoringDraft!, _catalogue, _firmwareArtifacts);
                feedback.Text = $"Saved runnable robot project · SHA-256 {hash[..16]}…";
            }
            catch (Exception exception) { feedback.Text = $"Project save failed: {exception.Message}"; }
        };
        AddChild(saveProjectDialog);
        stack.AddChild(MakeButton("Save robot project…", () => saveProjectDialog.PopupCentered(new Vector2I(950, 650))));
        var openProjectDialog = new FileDialog
        {
            Name = "DraftOpenProjectDialog", FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem, Filters = ["*.roboworld ; RoboWorld Project"]
        };
        openProjectDialog.FileSelected += path => RestartWithProjectRobot(path, _authoringDraft!.Id);
        AddChild(openProjectDialog);
        stack.AddChild(MakeButton("Open this robot project…", () => openProjectDialog.PopupCentered(new Vector2I(950, 650))));
        stack.AddChild(MakeButton("Load starter robot", () =>
        {
            try
            {
                var starter = FixtureLoader.LoadRobot(_project.RobotPaths[0], _project.ComponentDirectory);
                _authoringDraft = RobotAuthoringDraft.FromRunnableAssembly(starter, _catalogue);
                RebuildDraftVisuals(parts, _authoringDraft);
                _selectedShopInstanceId = starter.Components.LastOrDefault()?.Id;
                RefreshParts();
                state.Text = $"Starter · {starter.Components.Count} parts · {starter.Id}";
                feedback.Text = "Loaded the starter graph. Assign firmware explicitly before running.";
                RefreshParts();
            }
            catch (Exception exception) { feedback.Text = $"Starter load failed: {exception.Message}"; }
        }));
        var runDraftButton = MakeButton("Run", () =>
        {
            var issues = RobotAuthoringReadiness.Validate(_authoringDraft!, _catalogue, _firmwareArtifacts);
            if (issues.Count != 0) { feedback.Text = string.Join("\n", issues.Select(issue => issue.Message)); return; }
            try
            {
                var graph = _authoringDraft.RequireAssembly(_catalogue);
                if (_authoringSmokeMode)
                {
                    var output = LaunchDraftSmoke(_authoringDraft!);
                    if (!output.Contains("AUTHORING_RUNTIME_LAUNCH state=READY", StringComparison.Ordinal))
                        throw new InvalidOperationException("Validated draft did not reach Robot Service READY. " + output);
                    feedback.Text = "Validated draft reached Robot Service READY.";
                }
                else
                {
                    var runtimePackage = Path.Combine(Path.GetTempPath(), $"roboworld-run-{Guid.NewGuid():N}.roboworld");
                    ProjectPackageStore.SaveAuthoringPackage(_project, runtimePackage, _authoringDraft!, _catalogue, _firmwareArtifacts);
                    var args = new[] { "--path", ProjectSettings.GlobalizePath("res://"), "--", "--project=" + runtimePackage,
                        "--robot-id=" + graph.Id, "--controller-mode=exact" };
                    var launchArgs = args.Append("--temporary-run-package=" + runtimePackage).ToArray();
                    if (OS.CreateProcess(OS.GetExecutablePath(), launchArgs) <= 0) throw new IOException("Could not start the validated robot simulation.");
                    feedback.Text = "Validated robot launched in the existing simulation runtime.";
                    GetTree().Quit();
                }
            }
            catch (Exception exception) { feedback.Text = $"Cannot run draft: {exception.Message}"; }
        });
        runDraftButton.Name = "DraftRun";
        stack.AddChild(runDraftButton);
        stack.AddChild(MakeButton("Back to simulation", () => RestartWithRobot("rover-01")));
        scroll.AddChild(stack); panel.AddChild(scroll); layer.AddChild(panel); AddChild(layer);
    }

    private void AddDraftVisual(Node3D parent, ComponentInstance component)
    {
        var definition = _catalogue[component.CatalogueId];
        var visual = MakeComponentVisual(component.Id, definition, component.SizeOverrideM);
        visual.Position = ToVector(component.PositionM);
        visual.Quaternion = ToQuaternion(component.RotationXyzw);
        parent.AddChild(visual);
    }

    private void RunAuthoringSmoke(RobotAuthoringDraft initial)
    {
        var path = Path.Combine(Path.GetTempPath(), "roboworld-authoring-smoke-" + Guid.NewGuid().ToString("N") + ".rwdraft");
        var emptyPath = path + ".empty.rwdraft";
        try
        {
            RobotAuthoringDraftStore.Save(path, initial, _catalogue);
            var reopenedEmpty = RobotAuthoringDraftStore.Open(path, _catalogue);
            if (reopenedEmpty.Assembly is not null) throw new InvalidDataException("Empty smoke draft reopened with a hidden assembly.");
            var emptyIssues = RobotAuthoringReadiness.Validate(reopenedEmpty, _catalogue, false);
            if (emptyIssues.Count == 0 || RobotAuthoringDraftStore.Open(path, _catalogue).Assembly is not null)
                throw new InvalidDataException("A failed Run check must report errors and preserve the empty draft.");
            var root = reopenedEmpty.AddFreeComponent(_catalogue, "rw.chassis.rect", "chassis");
            var partial = root.AddFreeComponent(_catalogue, "rw.sensor.distance", "distance");
            RobotAuthoringDraftStore.Save(path, partial, _catalogue);
            var reopenedPartial = RobotAuthoringDraftStore.Open(path, _catalogue);
            if (reopenedPartial.Assembly?.Components.Count != 2) throw new InvalidDataException("Partial authoring graph did not round-trip.");
            var emptyAgain = reopenedPartial.RemoveComponent(_catalogue, "distance").RemoveComponent(_catalogue, "chassis");
            if (emptyAgain.Assembly is not null) throw new InvalidDataException("Removing the final part did not return to an empty draft.");
            var starter = RobotAuthoringDraft.FromRunnableAssembly(FixtureLoader.LoadRobot(_project.RobotPaths[0], _project.ComponentDirectory), _catalogue);
            RobotAuthoringDraftStore.Save(path, starter, _catalogue);
            starter = RobotAuthoringDraftStore.Open(path, _catalogue);
            var firmware = _project.ControllersByRobotId.TryGetValue(starter.Id, out var assignments) && assignments.Count > 0;
            var issues = RobotAuthoringReadiness.Validate(starter, _catalogue, firmware);
            if (issues.Count != 0) throw new InvalidDataException("Runnable starter failed launch readiness: " + string.Join("; ", issues.Select(issue => issue.Message)));
            var templateController = _authoringTemplateGraph?.Components.SingleOrDefault(component =>
                _project.ControllersByRobotId.GetValueOrDefault(_authoringTemplateGraph.Id, []).Any(assignment => assignment.ComponentInstanceId == component.Id));
            if (templateController is null) throw new InvalidDataException("Authoring smoke requires an existing project controller assignment.");
            var addButton = FindChild("DraftAddComponent", true, false) as Button ?? throw new InvalidDataException("Authoring add control is missing.");
            addButton.EmitSignal(Button.SignalName.Pressed);
            if (_authoringDraft?.Assembly?.Components.Count != 1
                || _authoringDraft.Assembly.Components.Single().CatalogueId != "rw.primitive.box"
                || _authoringDraft.Assembly.Links.Single(link => link.IsRoot).BaseComponentId != _authoringDraft.Assembly.Components.Single().Id)
                throw new InvalidDataException("Editor add control did not create the first structural root.");
            var cataloguePicker = FindChild("DraftCataloguePicker", true, false) as OptionButton ?? throw new InvalidDataException("Authoring catalogue control is missing.");
            var orderedCatalogue = _catalogue.Values.OrderBy(item => item.Category).ThenBy(item => item.Id).ToArray();
            var controllerIndex = Array.FindIndex(orderedCatalogue, item => item.Id == templateController.CatalogueId);
            if (controllerIndex < 0) throw new InvalidDataException("Configured controller is not selectable in the catalogue.");
            cataloguePicker.Select(controllerIndex);
            cataloguePicker.EmitSignal(OptionButton.SignalName.ItemSelected, controllerIndex);
            addButton.EmitSignal(Button.SignalName.Pressed);
            if (_authoringDraft?.Assembly?.Components.Count != 2 || !_authoringDraft.Assembly.Components.Any(component => component.CatalogueId == templateController.CatalogueId))
                throw new InvalidDataException("Editor add control did not add the configured controller identity.");
            FindChild("DraftSave", true, false).EmitSignal(Button.SignalName.Pressed);
            var saveDialog = FindChild("DraftSaveDialog", true, false) as FileDialog ?? throw new InvalidDataException("Draft save dialog did not open.");
            saveDialog.EmitSignal(FileDialog.SignalName.FileSelected, path);
            saveDialog.Hide(); saveDialog.QueueFree();
            FindChild("DraftRemoveComponent", true, false).EmitSignal(Button.SignalName.Pressed);
            if (_authoringDraft?.Assembly?.Components.Count != 1) throw new InvalidDataException("Editor remove control did not remove the selected controller.");
            FindChild("DraftRemoveComponent", true, false).EmitSignal(Button.SignalName.Pressed);
            if (_authoringDraft?.Assembly is not null) throw new InvalidDataException("Editor remove control did not return to an empty draft after the root was removed.");
            FindChild("DraftOpen", true, false).EmitSignal(Button.SignalName.Pressed);
            var openDialog = FindChild("DraftOpenDialog", true, false) as FileDialog ?? throw new InvalidDataException("Draft open dialog did not open.");
            openDialog.EmitSignal(FileDialog.SignalName.FileSelected, path);
            openDialog.Hide(); openDialog.QueueFree();
            if (_authoringDraft?.Assembly?.Components.Count != 2) throw new InvalidDataException("Editor open control did not restore the saved draft.");
            var reopenedParts = FindChild("DraftPartPicker", true, false) as OptionButton;
            if (reopenedParts?.ItemCount != 2) throw new InvalidDataException("Editor part picker did not refresh after reopening the saved draft.");

            _authoringDraft = RobotAuthoringDraft.CreateEmpty("rs01c-editor-smoke-" + Guid.NewGuid().ToString("N"), "RS-01C Editor Smoke Rover");
            _selectedShopInstanceId = null;
            _refreshAuthoringWorkspace?.Invoke();
            BuildFunctionalDraftThroughEditorControls(path);
            var assembled = RobotAuthoringDraftStore.Open(path, _catalogue);
            var assembledIssues = RobotAuthoringReadiness.Validate(assembled, _catalogue, _firmwareArtifacts);
            if (assembledIssues.Count != 0)
                throw new InvalidDataException("Editor-assembled rover failed launch readiness: " + string.Join("; ", assembledIssues.Select(issue => issue.Message)));
            var acceptanceDirectory = Path.Combine(_projectRoot, "artifacts", "evidence", "RS01C");
            Directory.CreateDirectory(acceptanceDirectory);
            var acceptancePackage = Path.Combine(acceptanceDirectory, "authoring-functional-rover.roboworld");
            ProjectPackageStore.SaveAuthoringPackage(_project, acceptancePackage, assembled, _catalogue, _firmwareArtifacts);
            File.WriteAllText(Path.Combine(acceptanceDirectory, "robot-id.txt"), assembled.Id);
            var runtimeOutput = LaunchDraftSmoke(assembled);
            if (!runtimeOutput.Contains("AUTHORING_RUNTIME_LAUNCH state=READY", StringComparison.Ordinal))
                throw new InvalidOperationException("Newly assembled simulation did not reach READY. " + runtimeOutput);
            FindChild("DraftRun", true, false).EmitSignal(Button.SignalName.Pressed);
            var feedback = FindChild("DraftFeedback", true, false) as Label;
            if (feedback?.Text != "Validated draft reached Robot Service READY.")
                throw new InvalidDataException("Editor Run control failed to launch its valid draft: " + feedback?.Text);
            RobotAuthoringDraftStore.Save(emptyPath, initial, _catalogue);
            FindChild("DraftOpen", true, false).EmitSignal(Button.SignalName.Pressed);
            var emptyDialog = FindChild("DraftOpenDialog", true, false) as FileDialog ?? throw new InvalidDataException("Empty draft open dialog did not open.");
            emptyDialog.EmitSignal(FileDialog.SignalName.FileSelected, emptyPath);
            emptyDialog.Hide(); emptyDialog.QueueFree();
            var refreshedParts = FindChild("DraftPartPicker", true, false) as OptionButton;
            if (_authoringDraft?.Assembly is not null || refreshedParts?.ItemCount != 0)
                throw new InvalidDataException("Opening an empty draft did not clear the editor part list.");
            Console.WriteLine($"AUTHORING_DRAFT_SMOKE empty_roundtrip=PASS failed_run_preserves_draft=PASS add_remove_root=PASS partial_roundtrip=PASS valid_draft_roundtrip=PASS starter_launch_validation=PASS new_assembly_launch_validation=PASS package_materialization=PASS runtime_launch_ready=PASS editor_add_remove_save_reopen_run=PASS functional_rover_callbacks=PASS components={assembled.Assembly!.Components.Count} joints={assembled.Assembly.Joints.Count} electrical={assembled.Assembly.ElectricalConnections.Count}");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"AUTHORING_DRAFT_SMOKE_FAILED {exception}");
            GetTree().Quit(1);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (File.Exists(emptyPath)) File.Delete(emptyPath); }
    }

    private void RebuildDraftVisuals(Node3D parent, RobotAuthoringDraft draft)
    {
        if (parent is RobotAuthoringViewport viewport)
        {
            viewport.SetDraft(draft, _selectedShopInstanceId);
            return;
        }
        foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); }
        if (draft.Assembly is not null) foreach (var component in draft.Assembly.Components) AddDraftVisual(parent, component);
    }

    private void BuildFunctionalDraftThroughEditorControls(string path)
    {
        var cataloguePicker = FindChild("DraftCataloguePicker", true, false) as OptionButton ?? throw new InvalidDataException("Catalogue picker is missing.");
        var partsPicker = FindChild("DraftPartPicker", true, false) as OptionButton ?? throw new InvalidDataException("Part picker is missing.");
        var add = FindChild("DraftAddComponent", true, false) as Button ?? throw new InvalidDataException("Add component control is missing.");
        var selectCatalogue = new Action<string>(catalogueId =>
        {
            var index = Array.FindIndex(_catalogue.Values.OrderBy(item => item.Category).ThenBy(item => item.Id).ToArray(), item => item.Id == catalogueId);
            if (index < 0) throw new InvalidDataException($"Catalogue component {catalogueId} is unavailable.");
            cataloguePicker.Select(index);
            cataloguePicker.EmitSignal(OptionButton.SignalName.ItemSelected, index);
        });
        var addComponent = new Action<string>(catalogueId =>
        {
            selectCatalogue(catalogueId);
            add.EmitSignal(Button.SignalName.Pressed);
            if (FindChild("DraftFeedback", true, false) is Label { Text: var feedback } && feedback.StartsWith("Added ", StringComparison.Ordinal)) return;
            throw new InvalidDataException($"Editor could not add {catalogueId}: {(FindChild("DraftFeedback", true, false) as Label)?.Text}");
        });

        addComponent("rw.chassis.rect");
        var jointPicker = FindChild("DraftJointPicker", true, false) as OptionButton ?? throw new InvalidDataException("Joint picker is missing.");
        var attach = FindChild("DraftAttachComponent", true, false) as Button ?? throw new InvalidDataException("Attach control is missing.");
        jointPicker.Select(2);
        selectCatalogue("rw.wheel.standard");
        attach.EmitSignal(Button.SignalName.Pressed);
        partsPicker.Select(0); partsPicker.EmitSignal(OptionButton.SignalName.ItemSelected, 0);
        selectCatalogue("rw.wheel.standard");
        attach.EmitSignal(Button.SignalName.Pressed);
        if (_authoringDraft?.Assembly?.Joints.Count != 2 || _authoringDraft.Assembly.Joints.Any(joint => joint.Type != "revolute"))
            throw new InvalidDataException($"Editor attach control did not create two revolute wheel joints: components={_authoringDraft?.Assembly?.Components.Count} joints={_authoringDraft?.Assembly?.Joints.Count} feedback={(FindChild("DraftFeedback", true, false) as Label)?.Text}");

        addComponent("rw.motor.geared");
        addComponent("rw.motor.geared.slow");
        addComponent("rw.driver.dual");
        addComponent("rw.mcu.stm32f407vg");
        addComponent("rw.sensor.distance");
        addComponent("rw.battery.pack12");

        var motorPicker = FindChild("DraftMotorPicker", true, false) as OptionButton ?? throw new InvalidDataException("Motor picker is missing.");
        var wheelJointPicker = FindChild("DraftWheelJointPicker", true, false) as OptionButton ?? throw new InvalidDataException("Wheel-joint picker is missing.");
        var bindMotor = FindChild("DraftBindMotor", true, false) as Button ?? throw new InvalidDataException("Motor bind control is missing.");
        var motors = _authoringDraft!.Assembly!.Components.Where(component => _catalogue[component.CatalogueId].Model == "dc_motor").ToArray();
        var wheelJoints = _authoringDraft.Assembly.Joints.ToArray();
        for (var index = 0; index < 2; index++)
        {
            var motorIndex = Enumerable.Range(0, motorPicker.ItemCount).Single(i => motorPicker.GetItemMetadata(i).AsString() == motors[index].Id);
            var jointIndex = Enumerable.Range(0, wheelJointPicker.ItemCount).Single(i => wheelJointPicker.GetItemMetadata(i).AsString() == wheelJoints[index].Id);
            motorPicker.Select(motorIndex); wheelJointPicker.Select(jointIndex);
            bindMotor.EmitSignal(Button.SignalName.Pressed);
        }
        var preset = FindChild("DraftWireRoverPreset", true, false) as Button ?? throw new InvalidDataException("Electrical preset control is missing.");
        preset.EmitSignal(Button.SignalName.Pressed);
        var firmwarePicker = FindChild("DraftFirmwareArtifact", true, false) as OptionButton ?? throw new InvalidDataException("Firmware picker is missing.");
        var registeredArtifactIndex = Enumerable.Range(0, firmwarePicker.ItemCount)
            .Single(i => firmwarePicker.GetItemMetadata(i).AsString() == "stm32-drive");
        firmwarePicker.Select(registeredArtifactIndex);
        var assignFirmware = FindChild("DraftAssignFirmware", true, false) as Button ?? throw new InvalidDataException("Firmware assignment control is missing.");
        assignFirmware.EmitSignal(Button.SignalName.Pressed);

        FindChild("DraftSave", true, false).EmitSignal(Button.SignalName.Pressed);
        var saveDialog = FindChild("DraftSaveDialog", true, false) as FileDialog ?? throw new InvalidDataException("Draft save dialog did not open.");
        saveDialog.EmitSignal(FileDialog.SignalName.FileSelected, path);
        saveDialog.Hide(); saveDialog.QueueFree();
        FindChild("DraftOpen", true, false).EmitSignal(Button.SignalName.Pressed);
        var openDialog = FindChild("DraftOpenDialog", true, false) as FileDialog ?? throw new InvalidDataException("Draft open dialog did not open.");
        openDialog.EmitSignal(FileDialog.SignalName.FileSelected, path);
        openDialog.Hide(); openDialog.QueueFree();
        if (_authoringDraft?.Assembly?.Components.Count != 9 || _authoringDraft.FirmwareAssignments?.Count != 1
            || _authoringDraft.Assembly.Joints.Count(joint => joint.ActuatorComponentId is not null) != 2)
            throw new InvalidDataException("Editor draft save/reopen lost authored functional component assignments.");
    }

    private string LaunchDraftSmoke(RobotAuthoringDraft draft)
    {
        var package = Path.Combine(Path.GetTempPath(), "roboworld-authoring-smoke-" + Guid.NewGuid().ToString("N") + ".roboworld");
        try
        {
            var graph = draft.RequireAssembly(_catalogue);
            ProjectPackageStore.SaveAuthoringPackage(_project, package, draft, _catalogue, _firmwareArtifacts);
            var start = new ProcessStartInfo(OS.GetExecutablePath()) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("--headless"); start.ArgumentList.Add("--path"); start.ArgumentList.Add(ProjectSettings.GlobalizePath("res://"));
            start.ArgumentList.Add("--"); start.ArgumentList.Add("--project=" + package); start.ArgumentList.Add("--robot-id=" + graph.Id);
            start.ArgumentList.Add("--controller-mode=exact");
            start.ArgumentList.Add("--authoring-launch-smoke"); start.ArgumentList.Add("--temporary-run-package=" + package);
            using var process = Process.Start(start) ?? throw new IOException("Could not start the materialized robot project.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(true); throw new TimeoutException("Materialized robot did not reach a worker state in 90 seconds."); }
            var combined = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
            if (File.Exists(package)) throw new IOException("Simulation exit did not clean up its temporary run package.");
            var evidencePath = Path.Combine(_projectRoot, "artifacts", "evidence", "RS01A", "runtime-launch.log");
            Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
            File.WriteAllText(evidencePath, combined);
            if (process.ExitCode != 0 || !combined.Contains("AUTHORING_RUNTIME_LAUNCH state=READY", StringComparison.Ordinal))
                throw new InvalidOperationException("Materialized simulation did not reach READY. " + combined);
            return combined;
        }
        finally { if (File.Exists(package)) File.Delete(package); }
    }

    private void ShowDraftSaveDialog(Label feedback)
    {
        var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.SaveFile, Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.rwdraft ; RoboWorld Robot Draft"] };
        dialog.Name = "DraftSaveDialog";
        dialog.FileSelected += path =>
        {
            try { var hash = RobotAuthoringDraftStore.Save(path, _authoringDraft!, _catalogue); feedback.Text = $"Draft saved · SHA-256 {hash}"; }
            catch (Exception exception) { feedback.Text = $"Draft save failed: {exception.Message}"; }
        };
        AddChild(dialog);
        if (!_authoringSmokeMode) dialog.PopupCentered(new Vector2I(680, 480));
    }

    private void ShowDraftOpenDialog(Label feedback)
    {
        var dialog = new FileDialog { FileMode = FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.rwdraft ; RoboWorld Robot Draft"] };
        dialog.Name = "DraftOpenDialog";
        dialog.FileSelected += path =>
        {
            try
            {
                _authoringDraft = RobotAuthoringDraftStore.Open(path, _catalogue);
                _selectedShopInstanceId = _authoringDraft.Assembly?.Components.LastOrDefault()?.Id;
                RebuildDraftVisuals(_authoringViewport!, _authoringDraft);
                _refreshAuthoringWorkspace?.Invoke();
                feedback.Text = $"Opened {_authoringDraft.DisplayName}.";
                return;
            }
            catch (Exception exception) { feedback.Text = $"Draft open failed: {exception.Message}"; }
            try
            {
                var executable = OS.GetExecutablePath();
                var args = new[] { "--path", ProjectSettings.GlobalizePath("res://"), "--", "--project=" + _projectArgumentPath,
                    "--authoring-draft=" + path };
                if (OS.CreateProcess(executable, args) <= 0) throw new IOException("Could not open the selected draft.");
                GetTree().Quit();
            }
            catch (Exception exception) { feedback.Text = $"Draft open failed: {exception.Message}"; }
        };
        AddChild(dialog);
        if (!_authoringSmokeMode) dialog.PopupCentered(new Vector2I(680, 480));
    }

    private void BuildOverlay(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue, IReadOnlyList<AssemblyGraph> robotGraphs)
    {
        var layer = new CanvasLayer { Layer = 10 };
        var panel = new PanelContainer
        {
            AnchorRight = 1,
            OffsetLeft = 18,
            OffsetRight = -18,
            OffsetTop = 18,
            OffsetBottom = 300
        };
        var stack = new VBoxContainer();
        stack.AddChild(new Label { Text = "ROBOWORLD  /  SIMULATION", Modulate = new Color(0.6f, 0.93f, 0.83f) });
        _metrics = new Label { Text = "Starting simulation metrics…" };
        _workerStatus = new Label { Text = "Robot Service  STARTING" };
        _controllerModeStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        stack.AddChild(_metrics); stack.AddChild(_workerStatus); stack.AddChild(_controllerModeStatus);
        _componentStatus = new Label { Text = $"World  OPERATIONAL    Robot {graph.DisplayName}  STARTING\nMCU  INACTIVE · firmware not started    {graph.Components.Count} versioned components" };
        stack.AddChild(_componentStatus);
        var controls = new GridContainer { Columns = 5 };
        var robotPicker = new OptionButton { CustomMinimumSize = new Vector2(190, 0) };
        foreach (var robotGraph in robotGraphs) robotPicker.AddItem($"{robotGraph.DisplayName}  [{robotGraph.Id}]");
        var selectedRobotIndex = robotGraphs.ToList().FindIndex(candidate => candidate.Id == graph.Id);
        robotPicker.Select(Math.Max(selectedRobotIndex, 0));
        controls.AddChild(robotPicker);
        controls.AddChild(MakeButton("Load Robot", () => RestartWithRobot(robotGraphs[robotPicker.Selected].Id)));
        controls.AddChild(MakeButton("New Empty Robot", () => StartEmptyAuthoring()));
        controls.AddChild(MakeButton("Open Project", OpenProjectDialog)); controls.AddChild(MakeButton("Save Project", SaveProjectDialog));
        controls.AddChild(MakeButton("Pause / Resume", TogglePause)); controls.AddChild(MakeButton("Restart Worker", RestartWorker));
        controls.AddChild(MakeButton("15 FPS", () => SetRenderCap(15))); controls.AddChild(MakeButton("60 FPS", () => SetRenderCap(60)));
        _controllerModePicker = new OptionButton { CustomMinimumSize = new Vector2(240, 0) };
        _controllerModePicker.AddItem("Exact firmware · Renode");
        _controllerModePicker.AddItem("Auto · validated profile or exact fallback");
        _controllerModePicker.AddItem("Fast model · requires a qualified profile");
        _controllerModePicker.SetItemDisabled(2, _project.ControllerExecutionSettings.ProfileReferences.Count == 0);
        _controllerModePicker.Select(_controllerExecutionMode switch { "auto" => 1, "fast" => 2, _ => 0 });
        _controllerModePicker.ItemSelected += index =>
        {
            _controllerExecutionMode = index switch { 1 => "auto", 2 => "fast", _ => "exact" };
            UpdateControllerModeStatus();
        };
        controls.AddChild(_controllerModePicker);
        controls.AddChild(MakeButton("Restart Simulation", RestartWithControllerMode));
        controls.AddChild(MakeButton("Quit", () => GetTree().Quit()));
        var worldViewButton = MakeButton("World View", SwitchToWorldView);
        worldViewButton.Name = "WorldViewButton";
        _robotViewButton = MakeButton("Robot View", SwitchToRobotView);
        _robotViewButton.Name = "RobotViewButton";
        var activeCameraEntries = _robotCameras.Where(pair => pair.Key.StartsWith(graph.Id + "/", StringComparison.Ordinal))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        _robotViewButton.Disabled = activeCameraEntries.Length == 0;
        _robotCameraPicker = new OptionButton { Name = "RobotCameraPicker", CustomMinimumSize = new Vector2(180, 0), Disabled = activeCameraEntries.Length == 0 };
        foreach (var entry in activeCameraEntries)
        {
            var componentId = entry.Key[(graph.Id.Length + 1)..];
            var instance = graph.Components.Single(component => component.Id == componentId);
            var index = _robotCameraPicker.ItemCount;
            _robotCameraPicker.AddItem(instance.DisplayName ?? instance.Id);
            _robotCameraPicker.SetItemMetadata(index, entry.Key);
        }
        if (_robotCameraPicker.ItemCount > 0) _robotCameraPicker.Select(0);
        _robotCameraPicker.ItemSelected += _ => { if (_robotViewActive) SetSelectedRobotCameraCurrent(); };
        stack.AddChild(controls);
        _viewModeStatus = new Label
        {
            Name = "ViewModeStatus",
            Text = activeCameraEntries.Length == 0 ? "World View · no enabled robot camera; add a camera part to use Robot View." : "World View · select Robot View to use the mounted camera.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        stack.AddChild(_viewModeStatus);
        panel.AddChild(stack); layer.AddChild(panel); AddChild(layer);

        // Keep camera switching reachable above the Robot Shop and diagnostics overlays.
        var viewLayer = new CanvasLayer { Layer = 20 };
        var viewPanel = new PanelContainer
        {
            AnchorLeft = 1,
            AnchorTop = 1,
            AnchorRight = 1,
            AnchorBottom = 1,
            OffsetLeft = -510,
            OffsetTop = -72,
            OffsetRight = -18,
            OffsetBottom = -18
        };
        var viewControls = new HBoxContainer();
        viewControls.AddChild(worldViewButton);
        viewControls.AddChild(_robotViewButton);
        viewControls.AddChild(_robotCameraPicker);
        viewPanel.AddChild(viewControls);
        viewLayer.AddChild(viewPanel);
        AddChild(viewLayer);
        UpdateControllerModeStatus();

        var shopLayer = new CanvasLayer { Layer = 9 };
        var shopPanel = new PanelContainer
        {
            Position = new Vector2(18, 242),
            CustomMinimumSize = new Vector2(524, 382)
        };
        var shopStack = new VBoxContainer();
        shopStack.AddChild(new Label { Text = "ROBOT SHOP  /  VERSIONED CATALOGUE", Modulate = new Color(0.6f, 0.93f, 0.83f) });
        shopStack.AddChild(new Label { Text = "Select a catalogue part, then attach it to a compatible connector.", AutowrapMode = TextServer.AutowrapMode.WordSmart });
        var shopTree = new Tree { Columns = 3, HideRoot = true, CustomMinimumSize = new Vector2(490, 112), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        shopTree.SetColumnTitle(0, "Part");
        shopTree.SetColumnTitle(1, "Category");
        shopTree.SetColumnTitle(2, "Version");
        shopTree.SetColumnExpand(0, true);
        shopTree.SetColumnExpand(1, true);
        shopTree.SetColumnExpand(2, false);
        shopTree.SetColumnCustomMinimumWidth(2, 70);
        var shopRoot = shopTree.CreateItem();
        foreach (var definition in catalogue.Values.OrderBy(item => item.Category, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal))
        {
            var item = shopTree.CreateItem(shopRoot);
            item.SetText(0, definition.Id);
            item.SetText(1, definition.Category);
            item.SetText(2, definition.Version);
            item.SetMetadata(0, definition.Id);
        }
        shopTree.ItemSelected += () =>
        {
            var selected = shopTree.GetSelected();
            if (selected is null) return;
            var id = selected.GetMetadata(0).AsString();
            if (!catalogue.TryGetValue(id, out var definition)) return;
            _selectedShopCatalogueId = id;
            _addFreeShopButton.Disabled = false;
            _shopDetails.Text = $"{definition.Id}@{definition.Version} · {definition.Category} · {definition.MassKg:F3} kg\n{string.Join(" × ", definition.SizeM.Select(value => $"{value:F3}"))} m · {definition.Model} / {definition.Primitive}";
            UpdateSnapCandidates();
        };
        shopStack.AddChild(shopTree);
        _shopDetails = new Label { Text = $"{catalogue.Count} catalogue components loaded.", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        shopStack.AddChild(_shopDetails);
        var sizeRow = new HBoxContainer();
        sizeRow.AddChild(new Label { Text = "Size m", CustomMinimumSize = new Vector2(58, 0) });
        _shopSizeInputs = Enumerable.Range(0, 3).Select(_ => new SpinBox
        {
            MinValue = 0.005,
            MaxValue = 10,
            Step = 0.005,
            Rounded = false,
            Editable = true,
            CustomMinimumSize = new Vector2(66, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            Value = 0.1
        }).ToArray();
        foreach (var input in _shopSizeInputs) { input.Editable = false; sizeRow.AddChild(input); }
        _resizeShopButton = MakeButton("Apply size", ApplySelectedShopSize);
        _resizeShopButton.CustomMinimumSize = new Vector2(98, 0);
        _resizeShopButton.Disabled = true;
        sizeRow.AddChild(_resizeShopButton);
        _removeShopButton = MakeButton("Remove", RemoveSelectedShopPart);
        _removeShopButton.CustomMinimumSize = new Vector2(78, 0);
        _removeShopButton.Disabled = true;
        sizeRow.AddChild(_removeShopButton);
        _editShopPoseButton = MakeButton("Edit pose…", ShowShopPoseEditor);
        _editShopPoseButton.CustomMinimumSize = new Vector2(88, 0);
        _editShopPoseButton.Disabled = true;
        sizeRow.AddChild(_editShopPoseButton);
        shopStack.AddChild(sizeRow);
        var snapRow = new HBoxContainer();
        _shopMountMode = new OptionButton { CustomMinimumSize = new Vector2(120, 0) };
        _shopMountMode.AddItem("Rigid mount");
        _shopMountMode.AddItem("Fixed link");
        _shopMountMode.AddItem("Revolute link");
        _shopMountMode.ItemSelected += _ => UpdateSnapCandidates();
        snapRow.AddChild(_shopMountMode);
        _snapTargetPicker = new OptionButton { CustomMinimumSize = new Vector2(214, 0), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        snapRow.AddChild(_snapTargetPicker);
        _snapShopButton = MakeButton("Add snapped part", AddSelectedShopPart);
        _snapShopButton.CustomMinimumSize = new Vector2(140, 0);
        _snapShopButton.Disabled = true;
        snapRow.AddChild(_snapShopButton);
        shopStack.AddChild(snapRow);
        var electricalRow = new HBoxContainer();
        _addFreeShopButton = MakeButton("Add free part", AddSelectedFreeShopPart);
        _addFreeShopButton.Disabled = true;
        electricalRow.AddChild(_addFreeShopButton);
        electricalRow.AddChild(MakeButton("Electrical connections…", ShowElectricalEditor));
        _electricalSummary = new Label { Text = $"{graph.ElectricalConnections.Count} saved", VerticalAlignment = VerticalAlignment.Center };
        electricalRow.AddChild(_electricalSummary);
        shopStack.AddChild(electricalRow);
        shopPanel.AddChild(shopStack);
        shopLayer.AddChild(shopPanel);
        AddChild(shopLayer);
        BuildShopPoseEditor();
        BuildElectricalEditor();
    }

    private void BuildShopPoseEditor()
    {
        _shopPoseWindow = new Window
        {
            Title = "Component Pose · local to link",
            Size = new Vector2I(430, 260),
            Visible = false,
            Unresizable = true
        };
        _shopPoseWindow.CloseRequested += () => _shopPoseWindow.Hide();
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        var stack = new VBoxContainer();
        stack.AddChild(new Label { Text = "Position in metres. Rotation is an xyzw quaternion; the graph editor normalizes it." });
        var positionRow = new HBoxContainer();
        positionRow.AddChild(new Label { Text = "Position m", CustomMinimumSize = new Vector2(74, 0), VerticalAlignment = VerticalAlignment.Center });
        _shopPosePositionInputs = Enumerable.Range(0, 3).Select(index => MakePoseInput(-10, 10, 0.0001, new Vector2(88, 0))).ToArray();
        foreach (var (input, axis) in _shopPosePositionInputs.Zip(new[] { "X", "Y", "Z" }))
        {
            input.Prefix = axis + " ";
            positionRow.AddChild(input);
        }
        stack.AddChild(positionRow);
        var rotationRow = new HBoxContainer();
        rotationRow.AddChild(new Label { Text = "Rotation", CustomMinimumSize = new Vector2(74, 0), VerticalAlignment = VerticalAlignment.Center });
        _shopPoseRotationInputs = Enumerable.Range(0, 4).Select(index => MakePoseInput(-1, 1, 0.0001, new Vector2(68, 0))).ToArray();
        foreach (var (input, axis) in _shopPoseRotationInputs.Zip(new[] { "X ", "Y ", "Z ", "W " }))
        {
            input.Prefix = axis;
            rotationRow.AddChild(input);
        }
        stack.AddChild(rotationRow);
        stack.AddChild(MakeButton("Apply pose", ApplySelectedShopPose));
        _shopPoseStatus = new Label { Text = "" };
        stack.AddChild(_shopPoseStatus);
        margin.AddChild(stack);
        _shopPoseWindow.AddChild(margin);
        AddChild(_shopPoseWindow);
    }

    private static SpinBox MakePoseInput(double minimum, double maximum, double step, Vector2 minimumSize) => new()
    {
        MinValue = minimum,
        MaxValue = maximum,
        Step = step,
        Rounded = false,
        Editable = true,
        CustomMinimumSize = minimumSize,
        SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
    };

    private void ShowShopPoseEditor()
    {
        if (_selectedShopInstanceId is null) return;
        var component = _activeGraph.Components.SingleOrDefault(item => item.Id == _selectedShopInstanceId);
        if (component is null) return;
        for (var axis = 0; axis < 3; axis++) _shopPosePositionInputs[axis].Value = component.PositionM[axis];
        for (var axis = 0; axis < 4; axis++) _shopPoseRotationInputs[axis].Value = component.RotationXyzw[axis];
        _shopPoseStatus.Text = $"{component.Id} · link {component.LinkId}";
        if (!_shopSnapSmoke) _shopPoseWindow.PopupCentered();
    }

    private void ApplySelectedShopPose()
    {
        if (_selectedShopInstanceId is null) return;
        try
        {
            var component = _activeGraph.Components.Single(item => item.Id == _selectedShopInstanceId);
            var updated = RobotAssemblyEditor.SetComponentPose(_activeGraph, _catalogue, component.Id,
                _shopPosePositionInputs.Select(input => input.Value).ToArray(),
                _shopPoseRotationInputs.Select(input => input.Value).ToArray());
            var moved = updated.Components.Single(item => item.Id == component.Id);
            if (_componentVisuals.TryGetValue(moved.Id, out var visual))
            {
                visual.Position = ToVector(moved.PositionM);
                visual.Quaternion = ToQuaternion(moved.RotationXyzw);
            }
            if (_shopCollisionShapes.TryGetValue(moved.Id, out var collision))
            {
                collision.Position = ToVector(moved.PositionM);
                collision.Quaternion = ToQuaternion(moved.RotationXyzw);
            }
            _activeGraph = updated;
            _shopPoseStatus.Text = $"Applied to {moved.Id}; package stores position m and normalized xyzw.";
            _shopDetails.Text = $"Moved {moved.Id} in link {moved.LinkId}; mechanical connector constraints remain intact.";
            Console.WriteLine($"SHOP_COMPONENT_POSE_EDITED id={moved.Id} link={moved.LinkId} position_m={string.Join(',', moved.PositionM.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} rotation_xyzw={string.Join(',', moved.RotationXyzw.Select(value => value.ToString("F4", CultureInfo.InvariantCulture)))}");
        }
        catch (Exception exception)
        {
            _shopPoseStatus.Text = $"Cannot apply pose: {exception.Message}";
            Console.Error.WriteLine($"SHOP_COMPONENT_POSE_FAILED {exception.Message}");
        }
    }

    private void BuildElectricalEditor()
    {
        _electricalEditorWindow = new Window
        {
            Title = "Electrical Graph Editor",
            Size = new Vector2I(700, 520),
            Visible = false,
            Unresizable = true
        };
        _electricalEditorWindow.CloseRequested += () => _electricalEditorWindow.Hide();
        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        var stack = new VBoxContainer();
        stack.AddChild(new Label
        {
            Text = "Connect typed component ports. The graph validator checks interface, physical pin, bus ID and endpoint validity.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart
        });
        _electricalConnectionTree = new Tree { Columns = 1, HideRoot = true, CustomMinimumSize = new Vector2(660, 250), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _electricalConnectionTree.ItemSelected += () => _removeElectricalConnectionButton.Disabled = _electricalConnectionTree.GetSelected() is null;
        stack.AddChild(_electricalConnectionTree);

        var endpointRow = new HBoxContainer();
        var fromColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        fromColumn.AddChild(new Label { Text = "From port" });
        _electricalFromPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _electricalFromPicker.ItemSelected += _ => RefreshElectricalDestinationPicker();
        fromColumn.AddChild(_electricalFromPicker);
        endpointRow.AddChild(fromColumn);
        var destinationColumn = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        destinationColumn.AddChild(new Label { Text = "To port" });
        _electricalToPicker = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        destinationColumn.AddChild(_electricalToPicker);
        endpointRow.AddChild(destinationColumn);
        stack.AddChild(endpointRow);

        var actionRow = new HBoxContainer();
        actionRow.AddChild(new Label { Text = "Bus ID (optional)", CustomMinimumSize = new Vector2(128, 0), VerticalAlignment = VerticalAlignment.Center });
        _electricalBusInput = new LineEdit { PlaceholderText = "e.g. uart_main", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        actionRow.AddChild(_electricalBusInput);
        _addElectricalConnectionButton = MakeButton("Add connection", AddSelectedElectricalConnection);
        actionRow.AddChild(_addElectricalConnectionButton);
        _removeElectricalConnectionButton = MakeButton("Remove selected", RemoveSelectedElectricalConnection);
        _removeElectricalConnectionButton.Disabled = true;
        actionRow.AddChild(_removeElectricalConnectionButton);
        stack.AddChild(actionRow);
        _electricalEditorStatus = new Label { Text = "", AutowrapMode = TextServer.AutowrapMode.WordSmart };
        stack.AddChild(_electricalEditorStatus);
        margin.AddChild(stack);
        _electricalEditorWindow.AddChild(margin);
        AddChild(_electricalEditorWindow);
        RefreshElectricalEditor();
    }

    private void ShowElectricalEditor()
    {
        RefreshElectricalEditor();
        _electricalEditorWindow.PopupCentered();
    }

    private void RefreshElectricalEditor()
    {
        if (_electricalConnectionTree is null) return;
        var selectedConnectionId = _electricalConnectionTree.GetSelected()?.GetMetadata(0).AsString();
        _electricalConnectionTree.Clear();
        var root = _electricalConnectionTree.CreateItem();
        foreach (var connection in _activeGraph.ElectricalConnections)
        {
            var item = _electricalConnectionTree.CreateItem(root);
            item.SetText(0, $"{connection.Id}  ·  {connection.From} → {connection.To}  ·  {connection.Interface}  ·  pin {connection.PhysicalPin ?? "—"}  ·  bus {connection.BusId ?? "—"}");
            item.SetMetadata(0, connection.Id);
            if (connection.Id == selectedConnectionId) item.Select(0);
        }
        _removeElectricalConnectionButton.Disabled = _electricalConnectionTree.GetSelected() is null;

        var oldFrom = _electricalFromPicker.Selected >= 0 ? _electricalFromPicker.GetItemMetadata(_electricalFromPicker.Selected).AsString() : null;
        var oldTo = _electricalToPicker.Selected >= 0 ? _electricalToPicker.GetItemMetadata(_electricalToPicker.Selected).AsString() : null;
        _electricalFromPicker.Clear();
        foreach (var endpoint in GetElectricalEndpoints())
        {
            var index = _electricalFromPicker.ItemCount;
            _electricalFromPicker.AddItem($"{endpoint.Id}  ·  {endpoint.Interface}");
            _electricalFromPicker.SetItemMetadata(index, endpoint.Id);
            if (endpoint.Id == oldFrom) _electricalFromPicker.Select(index);
        }
        if (_electricalFromPicker.Selected < 0 && _electricalFromPicker.ItemCount > 0) _electricalFromPicker.Select(0);
        RefreshElectricalDestinationPicker(oldTo);
        _electricalEditorStatus.Text = $"{_activeGraph.ElectricalConnections.Count} electrical connections in {_activeGraph.Id}.";
        _electricalSummary.Text = $"{_activeGraph.ElectricalConnections.Count} saved";
    }

    private void RefreshElectricalDestinationPicker(string? requestedEndpoint = null)
    {
        if (_electricalToPicker is null || _electricalFromPicker.Selected < 0) return;
        var fromId = _electricalFromPicker.GetItemMetadata(_electricalFromPicker.Selected).AsString();
        var from = GetElectricalEndpoints().SingleOrDefault(endpoint => endpoint.Id == fromId);
        _electricalToPicker.Clear();
        if (from.Id is null) return;
        foreach (var endpoint in GetElectricalEndpoints().Where(endpoint => endpoint.Interface == from.Interface && endpoint.Id != from.Id))
        {
            var index = _electricalToPicker.ItemCount;
            _electricalToPicker.AddItem($"{endpoint.Id}  ·  {endpoint.Interface}");
            _electricalToPicker.SetItemMetadata(index, endpoint.Id);
            if (endpoint.Id == requestedEndpoint) _electricalToPicker.Select(index);
        }
        if (_electricalToPicker.Selected < 0 && _electricalToPicker.ItemCount > 0) _electricalToPicker.Select(0);
        _addElectricalConnectionButton.Disabled = _electricalToPicker.ItemCount == 0;
    }

    private List<(string Id, string Interface)> GetElectricalEndpoints() => _activeGraph.Components
        .SelectMany(component => _catalogue[component.CatalogueId].ElectricalPorts
            .Select(port => (Id: $"{component.Id}.{port.Id}", port.Interface)))
        .OrderBy(endpoint => endpoint.Id, StringComparer.Ordinal).ToList();

    private void AddSelectedElectricalConnection()
    {
        if (_electricalFromPicker.Selected < 0 || _electricalToPicker.Selected < 0) return;
        var from = _electricalFromPicker.GetItemMetadata(_electricalFromPicker.Selected).AsString();
        var to = _electricalToPicker.GetItemMetadata(_electricalToPicker.Selected).AsString();
        try
        {
            _activeGraph = RobotAssemblyEditor.AddElectricalConnection(_activeGraph, _catalogue, from, to, _electricalBusInput.Text);
            var connection = _activeGraph.ElectricalConnections.Last();
            RefreshElectricalEditor();
            _electricalEditorStatus.Text = $"Added {connection.Id}: {connection.From} → {connection.To}.";
            Console.WriteLine($"SHOP_ELECTRICAL_CONNECTION_ADDED id={connection.Id} from={connection.From} to={connection.To} interface={connection.Interface} pin={connection.PhysicalPin ?? "none"} bus={connection.BusId ?? "none"}");
        }
        catch (Exception exception)
        {
            _electricalEditorStatus.Text = $"Cannot add connection: {exception.Message}";
            Console.Error.WriteLine($"SHOP_ELECTRICAL_CONNECTION_ADD_FAILED {exception.Message}");
        }
    }

    private void RemoveSelectedElectricalConnection()
    {
        var selected = _electricalConnectionTree.GetSelected();
        if (selected is null) return;
        var connectionId = selected.GetMetadata(0).AsString();
        try
        {
            _activeGraph = RobotAssemblyEditor.RemoveElectricalConnection(_activeGraph, _catalogue, connectionId);
            RefreshElectricalEditor();
            _electricalEditorStatus.Text = $"Removed connection {connectionId}.";
            Console.WriteLine($"SHOP_ELECTRICAL_CONNECTION_REMOVED id={connectionId}");
        }
        catch (Exception exception)
        {
            _electricalEditorStatus.Text = $"Cannot remove connection: {exception.Message}";
            Console.Error.WriteLine($"SHOP_ELECTRICAL_CONNECTION_REMOVE_FAILED {exception.Message}");
        }
    }

    private void UpdateSnapCandidates()
    {
        _snapCandidates.Clear();
        _snapTargetPicker.Clear();
        _snapShopButton.Disabled = true;
        if (_selectedShopCatalogueId is null || !_catalogue.TryGetValue(_selectedShopCatalogueId, out var childDefinition)) return;
        var occupied = (_activeGraph.ComponentMountConnections ?? [])
            .SelectMany(connection => new[] { (connection.ParentComponentId, connection.ParentPortId), (connection.ChildComponentId, connection.ChildPortId) })
            .ToHashSet();
        foreach (var joint in _activeGraph.Joints)
        {
            if (joint.ParentPortId is not null)
                occupied.Add((joint.ParentComponentId ?? _activeGraph.Links.Single(link => link.Id == joint.ParentLinkId).BaseComponentId, joint.ParentPortId));
            if (joint.ChildPortId is not null)
                occupied.Add((joint.ChildComponentId ?? _activeGraph.Links.Single(link => link.Id == joint.ChildLinkId).BaseComponentId, joint.ChildPortId));
        }
        foreach (var parent in _activeGraph.Components)
        {
            var parentDefinition = _catalogue[parent.CatalogueId];
            foreach (var parentPort in parentDefinition.MountPorts)
            foreach (var childPort in childDefinition.MountPorts)
            {
                if (occupied.Contains((parent.Id, parentPort.Id)) || !RobotGraphValidator.CanMate(parentPort, childPort)) continue;
                _snapCandidates.Add((parent.Id, parentPort.Id, childPort.Id));
                _snapTargetPicker.AddItem($"{parent.Id}.{parentPort.Id}  ↔  {childPort.Id}");
            }
        }
        _snapShopButton.Disabled = _snapCandidates.Count == 0;
        if (_snapCandidates.Count == 0)
            _shopDetails.Text += "\nNo compatible unoccupied connector is available.";
    }

    private void AddSelectedShopPart()
    {
        if (_selectedShopCatalogueId is null || _snapCandidates.Count == 0 || _snapTargetPicker.Selected < 0) return;
        try
        {
            var target = _snapCandidates[_snapTargetPicker.Selected];
            var childDefinition = _catalogue[_selectedShopCatalogueId];
            var instanceId = CreateShopInstanceId(_selectedShopCatalogueId, _activeGraph.Components.Select(component => component.Id));
            var parentInstance = _activeGraph.Components.Single(component => component.Id == target.ParentComponentId);
            var childLinkMode = _shopMountMode.Selected != 0;
            var jointType = _shopMountMode.Selected == 1 ? "fixed" : "revolute";
            var jointId = CreateShopInstanceId("joint-" + _selectedShopCatalogueId, _activeGraph.Joints.Select(joint => joint.Id));
            var updated = childLinkMode
                ? RobotAssemblyEditor.AddJointedComponent(_activeGraph, _catalogue, target.ParentComponentId, target.ParentPortId,
                    _selectedShopCatalogueId, target.ChildPortId, instanceId, jointType,
                    jointType == "fixed" ? "rw.mount.fixed" : "rw.mount.revolute", jointId)
                : RobotAssemblyEditor.AddSnappedComponent(_activeGraph, _catalogue, target.ParentComponentId,
                    target.ParentPortId, _selectedShopCatalogueId, target.ChildPortId, instanceId);
            var instance = updated.Components.Single(component => component.Id == instanceId);
            var rootLink = updated.Links.Single(link => link.Id == instance.LinkId);
            var rootComponent = updated.Components.Single(component => component.Id == rootLink.BaseComponentId);
            var linkBody = childLinkMode ? CreateJointedLinkBody(updated, rootLink, instance, childDefinition, jointId) :
                _linkBodies.TryGetValue(LinkBodyKey(updated.Id, instance.LinkId), out var existingBody) ? existingBody : throw new InvalidOperationException($"Cannot find link body {instance.LinkId}.");
            var localPosition = ToQuaternion(rootComponent.RotationXyzw).Inverse() * (ToVector(instance.PositionM) - ToVector(rootComponent.PositionM));
            var localRotation = ToQuaternion(rootComponent.RotationXyzw).Inverse() * ToQuaternion(instance.RotationXyzw);
            var visual = MakeComponentVisual(instance.Id, childDefinition);
            visual.Position = localPosition;
            visual.Quaternion = localRotation;
            linkBody.AddChild(visual);
            _componentVisuals[instance.Id] = visual;
            var collision = MakeComponentCollisionShape(instance.Id, childDefinition, instance.SizeOverrideM);
            collision.Position = localPosition;
            collision.Quaternion = localRotation;
            linkBody.AddChild(collision);
            _shopCollisionShapes.Add(instance.Id, collision);
            linkBody.Mass = (float)Math.Max(updated.Components.Where(component => component.LinkId == rootLink.Id)
                .Sum(component => _catalogue[component.CatalogueId].MassKg), 0.001);
            _activeGraph = updated;
            RefreshElectricalEditor();
            if (!childLinkMode)
            {
                _physicsAdapter.BindBody(instance.Id, linkBody);
                _physicsAdapter.RegisterBody(instance.Id, childDefinition.MassKg);
            }
            _selectedShopInstanceId = instance.Id;
            for (var axis = 0; axis < 3; axis++) _shopSizeInputs[axis].Value = (instance.SizeOverrideM ?? childDefinition.SizeM)[axis];
            foreach (var input in _shopSizeInputs) input.Editable = true;
            _resizeShopButton.Disabled = false;
            _editShopPoseButton.Disabled = true;
            _removeShopButton.Disabled = !CanRemoveShopComponent(instance);
            if (!_diagnosticRows.ContainsKey(instance.Id))
            {
                var robotRow = _diagnosticRows["robot"];
                AddDiagnosticRow(robotRow, instance.Id, childDefinition.Id);
                SetDiagnostic(instance.Id, "UNKNOWN", $"Snapped to {target.ParentComponentId}.{target.ParentPortId}; added mass {childDefinition.MassKg:F3} kg. Link inertia uses the root rigid-body approximation.");
            }
            _shopDetails.Text = $"Added {instance.Id} · {childDefinition.Id}@{childDefinition.Version}\nSnapped {target.ParentComponentId}.{target.ParentPortId} ↔ {target.ChildPortId}; {(childLinkMode ? $"{jointType} child link" : $"link mass {linkBody.Mass:F3} kg, inertia approximate")}.";
            Console.WriteLine(childLinkMode
                ? $"SHOP_JOINTED_COMPONENT_ADDED id={instance.Id} joint={jointId} type={jointType} parent={target.ParentComponentId}.{target.ParentPortId} child_port={target.ChildPortId} links={updated.Links.Count} joints={updated.Joints.Count}"
                : $"SHOP_COMPONENT_ADDED id={instance.Id} catalogue={childDefinition.Id}@{childDefinition.Version} parent={target.ParentComponentId}.{target.ParentPortId} child_port={target.ChildPortId} graph_components={updated.Components.Count}");
            UpdateSnapCandidates();
        }
        catch (Exception exception)
        {
            _shopDetails.Text = $"Cannot add part: {exception.Message}";
            Console.Error.WriteLine($"SHOP_COMPONENT_ADD_FAILED {exception.Message}");
        }
    }

    private void AddSelectedFreeShopPart()
    {
        if (_selectedShopCatalogueId is null) return;
        try
        {
            var definition = _catalogue[_selectedShopCatalogueId];
            var instanceId = CreateShopInstanceId(_selectedShopCatalogueId, _activeGraph.Components.Select(component => component.Id));
            var position = new[] { 0d, 0.3d, 0d };
            var updated = RobotAssemblyEditor.AddFreeComponent(_activeGraph, _catalogue, _selectedShopCatalogueId, instanceId, position, [0, 0, 0, 1]);
            var instance = updated.Components.Single(component => component.Id == instanceId);
            if (!_linkBodies.TryGetValue(LinkBodyKey(updated.Id, updated.RootLinkId), out var linkBody))
                throw new InvalidOperationException($"Cannot find root link body {updated.RootLinkId}.");
            var visual = MakeComponentVisual(instance.Id, definition);
            visual.Position = ToVector(instance.PositionM);
            visual.Quaternion = ToQuaternion(instance.RotationXyzw);
            linkBody.AddChild(visual);
            _componentVisuals[instance.Id] = visual;
            var collision = MakeComponentCollisionShape(instance.Id, definition, instance.SizeOverrideM);
            collision.Position = visual.Position;
            collision.Quaternion = visual.Quaternion;
            linkBody.AddChild(collision);
            _shopCollisionShapes.Add(instance.Id, collision);
            linkBody.Mass = (float)Math.Max(updated.Components.Where(component => component.LinkId == updated.RootLinkId)
                .Sum(component => _catalogue[component.CatalogueId].MassKg), 0.001);
            _physicsAdapter.BindBody(instance.Id, linkBody);
            _physicsAdapter.RegisterBody(instance.Id, definition.MassKg);
            _activeGraph = updated;
            RefreshElectricalEditor();
            _selectedShopInstanceId = instance.Id;
            for (var axis = 0; axis < 3; axis++) _shopSizeInputs[axis].Value = definition.SizeM[axis];
            foreach (var input in _shopSizeInputs) input.Editable = true;
            _resizeShopButton.Disabled = false;
            _editShopPoseButton.Disabled = false;
            _removeShopButton.Disabled = !CanRemoveShopComponent(instance);
            if (!_diagnosticRows.ContainsKey(instance.Id))
            {
                var robotRow = _diagnosticRows["robot"];
                AddDiagnosticRow(robotRow, instance.Id, definition.Id);
                SetDiagnostic(instance.Id, "UNKNOWN", $"Free catalogue part on link {updated.RootLinkId}; pose can be edited before connector attachment.");
            }
            _shopDetails.Text = $"Added free part {instance.Id} · {definition.Id}@{definition.Version} on {updated.RootLinkId}. Edit pose or connect compatible ports.";
            Console.WriteLine($"SHOP_FREE_COMPONENT_ADDED id={instance.Id} catalogue={definition.Id}@{definition.Version} link={updated.RootLinkId} position_m={string.Join(',', position.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))}");
            ShowShopPoseEditor();
            UpdateSnapCandidates();
        }
        catch (Exception exception)
        {
            _shopDetails.Text = $"Cannot add free part: {exception.Message}";
            Console.Error.WriteLine($"SHOP_FREE_COMPONENT_ADD_FAILED {exception.Message}");
        }
    }

    private bool CanRemoveShopComponent(ComponentInstance component)
    {
        var rootBase = _activeGraph.Links.Single(link => link.Id == _activeGraph.RootLinkId).BaseComponentId;
        if (component.Id == rootBase) return false;
        foreach (var robotPath in _project.RobotPaths)
        {
            using var robot = JsonDocument.Parse(File.ReadAllText(robotPath));
            var root = robot.RootElement;
            if (root.GetProperty("id").GetString() != _activeGraph.Id || !root.TryGetProperty("controllers", out var controllers)) continue;
            if (controllers.EnumerateArray().Any(controller => controller.GetProperty("component_instance_id").GetString() == component.Id)) return false;
        }
        return true;
    }

    private void RemoveSelectedShopPart()
    {
        if (_selectedShopInstanceId is null) return;
        var selected = _activeGraph.Components.SingleOrDefault(component => component.Id == _selectedShopInstanceId);
        if (selected is null || !CanRemoveShopComponent(selected)) return;
        try
        {
            var previous = _activeGraph;
            var updated = RobotAssemblyEditor.RemoveComponent(previous, _catalogue, selected.Id);
            var removedComponents = previous.Components.Where(component => updated.Components.All(item => item.Id != component.Id)).ToArray();
            var removedLinks = previous.Links.Where(link => updated.Links.All(item => item.Id != link.Id)).ToArray();
            var removedJoints = previous.Joints.Where(joint => updated.Joints.All(item => item.Id != joint.Id)).ToArray();

            foreach (var joint in removedJoints)
            {
                var key = JointNodeKey(previous.Id, joint.Id);
                if (!_jointNodes.Remove(key, out var jointNode)) continue;
                jointNode.QueueFree();
            }
            foreach (var component in removedComponents)
            {
                if (_componentVisuals.Remove(component.Id, out var visual)) visual.QueueFree();
                if (_shopCollisionShapes.Remove(component.Id, out var collision)) collision.QueueFree();
                _physicsAdapter.UnbindBody(component.Id);
                if (_diagnosticRows.ContainsKey(component.Id))
                    SetDiagnostic(component.Id, "INACTIVE", "Removed from the active robot design.");
            }
            foreach (var link in removedLinks)
            {
                var key = LinkBodyKey(previous.Id, link.Id);
                if (!_linkBodies.Remove(key, out var body)) continue;
                _bodies.Remove(body);
                body.QueueFree();
            }
            foreach (var linkId in previous.Links.Select(link => link.Id).Except(removedLinks.Select(link => link.Id), StringComparer.Ordinal))
            {
                var mass = updated.Components.Where(component => component.LinkId == linkId)
                    .Sum(component => _catalogue[component.CatalogueId].MassKg);
                if (_linkBodies.TryGetValue(LinkBodyKey(updated.Id, linkId), out var body)) body.Mass = (float)Math.Max(mass, 0.001);
            }
            _activeGraph = updated;
            RefreshElectricalEditor();
            if (_highlightedComponentId == selected.Id)
            {
                if (_highlightedVisual is not null) _highlightedVisual.MaterialOverlay = null;
                _highlightedVisual = null;
                _highlightedComponentId = null;
            }
            _selectedShopInstanceId = null;
            foreach (var input in _shopSizeInputs) input.Editable = false;
            _resizeShopButton.Disabled = true;
            _editShopPoseButton.Disabled = true;
            _removeShopButton.Disabled = true;
            _shopDetails.Text = $"Removed {selected.Id} and its dependent mechanical subassembly.\n{removedComponents.Length} components, {removedLinks.Length} links, {removedJoints.Length} joints removed.";
            Console.WriteLine($"SHOP_COMPONENT_REMOVED id={selected.Id} components={removedComponents.Length} links={removedLinks.Length} joints={removedJoints.Length} electrical={previous.ElectricalConnections.Count - updated.ElectricalConnections.Count}");
            UpdateSnapCandidates();
        }
        catch (Exception exception)
        {
            _shopDetails.Text = $"Cannot remove part: {exception.Message}";
            Console.Error.WriteLine($"SHOP_COMPONENT_REMOVE_FAILED {exception.Message}");
        }
    }

    private RigidBody3D CreateJointedLinkBody(AssemblyGraph graph, RobotLink link, ComponentInstance baseComponent,
        ComponentDefinition definition, string jointId)
    {
        var bodyPosition = ToVector(link.PositionM) + ToQuaternion(link.RotationXyzw) * ToVector(baseComponent.PositionM);
        var bodyRotation = ToQuaternion(link.RotationXyzw) * ToQuaternion(baseComponent.RotationXyzw);
        var bodyMass = graph.Components.Where(component => component.LinkId == link.Id)
            .Sum(component => _catalogue[component.CatalogueId].MassKg);
        var body = new RigidBody3D
        {
            Name = baseComponent.Id,
            Position = bodyPosition,
            Quaternion = bodyRotation,
            Mass = (float)Math.Max(bodyMass, 0.001),
            ContinuousCd = true,
            CanSleep = false
        };
        AddChild(body);
        _bodies.Add(body);
        _linkBodies.Add(LinkBodyKey(graph.Id, link.Id), body);
        _physicsAdapter.BindBody(baseComponent.Id, body);
        _physicsAdapter.RegisterBody(baseComponent.Id, definition.MassKg);

        var joint = graph.Joints.Single(item => item.Id == jointId);
        var parentLink = graph.Links.Single(item => item.Id == joint.ParentLinkId);
        var parentBase = graph.Components.Single(item => item.Id == parentLink.BaseComponentId);
        if (!_linkBodies.TryGetValue(LinkBodyKey(graph.Id, joint.ParentLinkId), out var parentBody))
            throw new InvalidDataException($"Cannot create joint {joint.Id}; parent link {joint.ParentLinkId} is not built.");
        var engineJoint = CreateEngineJoint(joint, parentBody, body, parentBase);
        parentBody.AddChild(engineJoint);
        _jointNodes[JointNodeKey(graph.Id, joint.Id)] = engineJoint;
        return body;
    }

    private static Joint3D CreateEngineJoint(RobotJoint joint, RigidBody3D parentBody, RigidBody3D childBody, ComponentInstance parentBase)
    {
        var parentBaseRotation = ToQuaternion(parentBase.RotationXyzw);
        var localJointPosition = parentBaseRotation.Inverse() * (ToVector(joint.ParentFramePositionM) - ToVector(parentBase.PositionM));
        var localJointRotation = parentBaseRotation.Inverse() * ToQuaternion(joint.ParentFrameRotationXyzw);
        var nodeB = "../../" + childBody.Name;
        if (joint.Type == "fixed")
        {
            return new Generic6DofJoint3D
            {
                Name = joint.Id,
                Position = localJointPosition,
                Quaternion = localJointRotation,
                ExcludeNodesFromCollision = true,
                NodeA = "..",
                NodeB = nodeB
            };
        }
        if (joint.Type is not ("revolute" or "continuous"))
            throw new InvalidDataException($"Godot runtime does not yet support joint type {joint.Type}.");
        return new HingeJoint3D
        {
            Name = joint.Id,
            Position = localJointPosition,
            Quaternion = localJointRotation * new Quaternion(Vector3.Back, ToVector(joint.AxisLocal).Normalized()),
            ExcludeNodesFromCollision = true,
            NodeA = "..",
            NodeB = nodeB
        };
    }

    private void ApplySelectedShopSize()
    {
        if (_selectedShopInstanceId is null) return;
        try
        {
            var instance = _activeGraph.Components.Single(component => component.Id == _selectedShopInstanceId);
            var definition = _catalogue[instance.CatalogueId];
            var size = _shopSizeInputs.Select(input => input.Value).ToArray();
            var updated = RobotAssemblyEditor.SetComponentSize(_activeGraph, _catalogue, instance.Id, size);
            var updatedInstance = updated.Components.Single(component => component.Id == instance.Id);
            var editedLink = updated.Links.Single(link => link.Id == updatedInstance.LinkId);
            var baseComponent = updated.Components.Single(component => component.Id == editedLink.BaseComponentId);
            var baseRotation = ToQuaternion(baseComponent.RotationXyzw);
            var localPosition = baseRotation.Inverse() * (ToVector(updatedInstance.PositionM) - ToVector(baseComponent.PositionM));
            var localRotation = baseRotation.Inverse() * ToQuaternion(updatedInstance.RotationXyzw);
            var currentVisual = _componentVisuals[instance.Id];
            var replacement = MakeComponentVisual(instance.Id, definition, size);
            replacement.Position = localPosition;
            replacement.Quaternion = localRotation;
            currentVisual.GetParent().AddChild(replacement);
            currentVisual.QueueFree();
            _componentVisuals[instance.Id] = replacement;
            var collision = _shopCollisionShapes[instance.Id];
            collision.Position = localPosition;
            collision.Quaternion = localRotation;
            collision.Shape = MakeComponentShape(definition, size);
            var linkBody = _linkBodies[LinkBodyKey(updated.Id, editedLink.Id)];
            if (editedLink.Id != updated.RootLinkId)
            {
                linkBody.GlobalTransform = new Transform3D(
                    new Basis(ToQuaternion(editedLink.RotationXyzw) * ToQuaternion(baseComponent.RotationXyzw)),
                    ToVector(editedLink.PositionM) + ToQuaternion(editedLink.RotationXyzw) * ToVector(baseComponent.PositionM));
            }
            linkBody.Mass = (float)Math.Max(updated.Components.Where(component => component.LinkId == editedLink.Id)
                .Sum(component => _catalogue[component.CatalogueId].MassKg), 0.001);
            _activeGraph = updated;
            _shopDetails.Text = $"Updated {instance.Id} dimensions to {string.Join(" × ", size.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} m.\nMass remains at versioned profile values; link inertia is approximate.";
            Console.WriteLine($"SHOP_COMPONENT_RESIZED id={instance.Id} size_m={string.Join(',', size.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} mass_kg={definition.MassKg:F3} inertia=godot_composite_approximation");
            UpdateSnapCandidates();
        }
        catch (Exception exception)
        {
            _shopDetails.Text = $"Cannot apply dimensions: {exception.Message}";
            Console.Error.WriteLine($"SHOP_COMPONENT_RESIZE_FAILED {exception.Message}");
        }
    }

    private void RunShopSnapSmoke()
    {
        var packagePath = Path.Combine(Path.GetTempPath(), "roboworld-shop-snap-" + Guid.NewGuid().ToString("N") + ".roboworld");
        ProjectPackageInfo? reopened = null;
        try
        {
            var primitiveProfile = _catalogue["rw.beam.structural"];
            var cylinderProfile = primitiveProfile with { Primitive = "cylinder", RadiusM = 0.05, SizeM = [0.1, 0.3, 0.1] };
            var cylinderVisual = (MeshInstance3D)MakeComponentVisual("primitive-test", cylinderProfile);
            var cylinderCollision = MakeComponentCollisionShape("primitive-test", cylinderProfile);
            if (cylinderVisual.Mesh is not CylinderMesh cylinderMesh || cylinderCollision.Shape is not CylinderShape3D cylinderShape
                || Math.Abs(cylinderMesh.TopRadius - cylinderShape.Radius) > 1e-6
                || Math.Abs(cylinderMesh.Height - cylinderShape.Height) > 1e-6)
                throw new InvalidDataException("Generic cylinder visual and collision geometry do not match.");
            cylinderVisual.Free();
            cylinderCollision.Free();
            Console.WriteLine("SHOP_GENERIC_CYLINDER_GEOMETRY visual_collision_match=PASS");
            _selectedShopCatalogueId = "rw.beam.structural";
            UpdateSnapCandidates();
            if (_snapCandidates.Count == 0) throw new InvalidOperationException("Starter graph has no compatible root-link connector for the structural beam.");
            _snapTargetPicker.Select(0);
            AddSelectedShopPart();
            if (_activeGraph.Components.Count != 11 || _activeGraph.ComponentMountConnections?.Count != 1)
                throw new InvalidDataException("Shop did not add exactly one mounted component and mount connection.");
            for (var axis = 0; axis < 3; axis++) _shopSizeInputs[axis].Value = axis == 1 ? 0.6 : 0.06;
            ApplySelectedShopSize();
            _shopMountMode.Select(2);
            UpdateSnapCandidates();
            var articulatedTarget = _snapCandidates.FindIndex(candidate => candidate.ParentComponentId == _selectedShopInstanceId
                && candidate.ParentPortId == "top" && candidate.ChildPortId == "bottom");
            if (articulatedTarget < 0) throw new InvalidOperationException("Resized root-mounted beam has no free top connector for the revolute child link.");
            _snapTargetPicker.Select(articulatedTarget);
            AddSelectedShopPart();
            if (!_activeGraph.Joints.Any(joint => joint.Type == "revolute" && joint.ChildLinkId == _activeGraph.Components.Single(component => component.Id == _selectedShopInstanceId).LinkId))
                throw new InvalidDataException("Interactive shop did not create the new revolute child link.");
            for (var axis = 0; axis < 3; axis++) _shopSizeInputs[axis].Value = axis == 1 ? 0.6 : 0.06;
            ApplySelectedShopSize();
            _shopMountMode.Select(1);
            UpdateSnapCandidates();
            var fixedTarget = _snapCandidates.FindIndex(candidate => candidate.ParentComponentId == _selectedShopInstanceId
                && candidate.ParentPortId == "top" && candidate.ChildPortId == "bottom");
            if (fixedTarget < 0) throw new InvalidOperationException("Revolute child beam has no free top connector for a fixed child link.");
            _snapTargetPicker.Select(fixedTarget);
            AddSelectedShopPart();
            if (!_activeGraph.Joints.Any(joint => joint.Type == "fixed" && joint.ChildLinkId == _activeGraph.Components.Single(component => component.Id == _selectedShopInstanceId).LinkId))
                throw new InvalidDataException("Interactive shop did not create the new fixed child link.");
            ProjectPackageStore.SavePackage(_project, packagePath, _activeGraph);
            reopened = ProjectPackageStore.OpenPackage(packagePath);
            var loaded = FixtureLoader.LoadRobot(reopened.RobotPath, reopened.ComponentDirectory);
            var beam = loaded.Components.Single(component => component.Id == "shop-rw-beam-structural");
            var articulatedBeam = loaded.Components.Single(component => loaded.Joints.Any(joint => joint.Type == "revolute" && joint.ChildLinkId == component.LinkId));
            var articulatedJoint = loaded.Joints.Single(joint => joint.ChildLinkId == articulatedBeam.LinkId);
            var fixedBeam = loaded.Components.Single(component => loaded.Joints.Any(joint => joint.Type == "fixed" && joint.ChildLinkId == component.LinkId));
            var fixedJoint = loaded.Joints.Single(joint => joint.ChildLinkId == fixedBeam.LinkId);
            if (Math.Abs((beam.SizeOverrideM ?? [])[1] - 0.6) > 1e-9 || loaded.ComponentMountConnections?.Single().ParentPortId != "top"
                || Math.Abs((articulatedBeam.SizeOverrideM ?? [])[1] - 0.6) > 1e-9 || Math.Abs(articulatedJoint.ChildFramePositionM[1] + 0.3) > 1e-9
                || fixedJoint.Type != "fixed" || fixedJoint.ChildComponentId != fixedBeam.Id)
                throw new InvalidDataException("Saved shop component size or mount connection did not survive package reopen.");
            Console.WriteLine($"SHOP_SNAP_PACKAGE_ROUNDTRIP id={beam.Id} parent_port=top child_port=bottom size_m={string.Join(',', beam.SizeOverrideM!.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} connections={loaded.ComponentMountConnections.Count}");
            Console.WriteLine($"SHOP_JOINT_PACKAGE_ROUNDTRIP component={articulatedBeam.Id} link={articulatedBeam.LinkId} joints={loaded.Joints.Count} child_port=bottom child_frame_y={articulatedJoint.ChildFramePositionM[1].ToString("F3", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"SHOP_FIXED_PACKAGE_ROUNDTRIP component={fixedBeam.Id} link={fixedBeam.LinkId} parent={fixedJoint.ParentComponentId} child_port={fixedJoint.ChildPortId}");

            ProjectPackageStore.DeleteTemporaryProject(reopened);
            reopened = null;
            _selectedShopInstanceId = "shop-rw-beam-structural";
            RemoveSelectedShopPart();
            if (_activeGraph.Components.Count != 10 || _activeGraph.Links.Count != 3 || _activeGraph.Joints.Count != 2
                || (_activeGraph.ComponentMountConnections ?? []).Count != 0)
                throw new InvalidDataException("Shop removal did not clear the selected component's dependent articulated assembly.");
            ProjectPackageStore.SavePackage(_project, packagePath, _activeGraph);
            reopened = ProjectPackageStore.OpenPackage(packagePath);
            var removedGraph = FixtureLoader.LoadRobot(reopened.RobotPath, reopened.ComponentDirectory);
            if (removedGraph.Components.Count != 10 || removedGraph.Components.Any(component => component.Id.StartsWith("shop-rw-beam-structural", StringComparison.Ordinal))
                || removedGraph.Joints.Count != 2 || (removedGraph.ComponentMountConnections ?? []).Count != 0)
                throw new InvalidDataException("Removed shop subassembly did not stay removed after project package reopen.");
            Console.WriteLine($"SHOP_REMOVE_PACKAGE_ROUNDTRIP components={removedGraph.Components.Count} links={removedGraph.Links.Count} joints={removedGraph.Joints.Count} mounts={(removedGraph.ComponentMountConnections ?? []).Count}");

            _selectedShopCatalogueId = "rw.sensor.distance";
            AddSelectedFreeShopPart();
            var freeSensorId = _selectedShopInstanceId ?? throw new InvalidDataException("Shop did not add a free sensor component.");
            _shopPosePositionInputs[0].Value = 0.42;
            _shopPosePositionInputs[1].Value = 0.36;
            _shopPosePositionInputs[2].Value = -0.27;
            _shopPoseRotationInputs[0].Value = 0;
            _shopPoseRotationInputs[1].Value = 0.3826834323650898;
            _shopPoseRotationInputs[2].Value = 0;
            _shopPoseRotationInputs[3].Value = 0.9238795325112867;
            ApplySelectedShopPose();
            var movedFreeSensor = _activeGraph.Components.Single(component => component.Id == freeSensorId);
            if (Math.Abs(movedFreeSensor.PositionM[0] - 0.42) > 1e-9 || Math.Abs(movedFreeSensor.PositionM[1] - 0.36) > 1e-9 || Math.Abs(movedFreeSensor.PositionM[2] + 0.27) > 1e-9
                || Math.Abs(Math.Sqrt(movedFreeSensor.RotationXyzw.Sum(value => value * value)) - 1) > 1e-9)
                throw new InvalidDataException("Free component pose editor did not store the requested SI position and normalized quaternion.");
            ProjectPackageStore.SavePackage(_project, packagePath, _activeGraph);
            reopened = ProjectPackageStore.OpenPackage(packagePath);
            var freePartGraph = FixtureLoader.LoadRobot(reopened.RobotPath, reopened.ComponentDirectory);
            var savedFreeSensor = freePartGraph.Components.Single(component => component.Id == freeSensorId);
            if (!savedFreeSensor.PositionM.SequenceEqual(movedFreeSensor.PositionM) || !savedFreeSensor.RotationXyzw.SequenceEqual(movedFreeSensor.RotationXyzw))
                throw new InvalidDataException("Free part position and rotation did not survive package save/reopen.");
            Console.WriteLine($"SHOP_FREE_COMPONENT_POSE_PACKAGE_ROUNDTRIP id={freeSensorId} link={savedFreeSensor.LinkId} position_m={string.Join(',', savedFreeSensor.PositionM.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} rotation_xyzw={string.Join(',', savedFreeSensor.RotationXyzw.Select(value => value.ToString("F4", CultureInfo.InvariantCulture)))}");
            ProjectPackageStore.DeleteTemporaryProject(reopened);
            reopened = null;

            _selectedShopCatalogueId = "rw.mcu.stm32f407vg";
            _shopMountMode.Select(0);
            UpdateSnapCandidates();
            var mcuMount = _snapCandidates.FindIndex(candidate => candidate.ParentComponentId == "chassis" && candidate.ParentPortId == "top");
            if (mcuMount < 0) throw new InvalidOperationException("Starter chassis has no free structural connector for a second MCU instance.");
            _snapTargetPicker.Select(mcuMount);
            AddSelectedShopPart();
            var secondMcuId = _selectedShopInstanceId ?? throw new InvalidDataException("Shop did not add the second MCU component.");
            SelectElectricalEndpoint(_electricalFromPicker, "mcu-board.uart");
            SelectElectricalEndpoint(_electricalToPicker, $"{secondMcuId}.uart");
            _electricalBusInput.Text = "shop_uart";
            AddSelectedElectricalConnection();
            var addedWire = _activeGraph.ElectricalConnections.SingleOrDefault(connection => connection.From == "mcu-board.uart" && connection.To == $"{secondMcuId}.uart");
            if (addedWire is null || addedWire.Interface != "uart" || addedWire.BusId != "shop_uart")
                throw new InvalidDataException("Electrical editor did not add the validated UART connection.");
            SelectElectricalConnection(addedWire.Id);
            RemoveSelectedElectricalConnection();
            if (_activeGraph.ElectricalConnections.Any(connection => connection.Id == addedWire.Id))
                throw new InvalidDataException("Electrical editor did not remove the selected UART connection.");
            AddSelectedElectricalConnection();
            addedWire = _activeGraph.ElectricalConnections.Single(connection => connection.From == "mcu-board.uart" && connection.To == $"{secondMcuId}.uart");
            ProjectPackageStore.SavePackage(_project, packagePath, _activeGraph);
            reopened = ProjectPackageStore.OpenPackage(packagePath);
            var wiredGraph = FixtureLoader.LoadRobot(reopened.RobotPath, reopened.ComponentDirectory);
            var savedWire = wiredGraph.ElectricalConnections.SingleOrDefault(connection => connection.Id == addedWire.Id);
            if (wiredGraph.Components.All(component => component.Id != secondMcuId) || savedWire is null
                || savedWire.From != "mcu-board.uart" || savedWire.To != $"{secondMcuId}.uart" || savedWire.BusId != "shop_uart")
                throw new InvalidDataException("Added shop electrical connection did not survive project package reopen.");
            Console.WriteLine($"SHOP_ELECTRICAL_PACKAGE_ROUNDTRIP components={wiredGraph.Components.Count} connection={savedWire.Id} from={savedWire.From} to={savedWire.To} interface={savedWire.Interface} bus={savedWire.BusId}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"SHOP_SNAP_SMOKE_FAILED {exception.Message}");
            GetTree().Quit(1);
            return;
        }
        finally
        {
            if (reopened is not null) ProjectPackageStore.DeleteTemporaryProject(reopened);
            if (File.Exists(packagePath)) File.Delete(packagePath);
        }
        GetTree().Quit();
    }

    private void SelectElectricalEndpoint(OptionButton picker, string endpoint)
    {
        for (var index = 0; index < picker.ItemCount; index++)
        {
            if (picker.GetItemMetadata(index).AsString() != endpoint) continue;
            picker.Select(index);
            if (picker == _electricalFromPicker) RefreshElectricalDestinationPicker();
            return;
        }
        throw new InvalidDataException($"Electrical editor does not offer endpoint {endpoint}.");
    }

    private void SelectElectricalConnection(string connectionId)
    {
        for (var item = _electricalConnectionTree.GetRoot()?.GetFirstChild(); item is not null; item = item.GetNext())
        {
            if (item.GetMetadata(0).AsString() != connectionId) continue;
            item.Select(0);
            return;
        }
        throw new InvalidDataException($"Electrical editor does not list connection {connectionId}.");
    }

    private void RunPhysicsCheckpointSmoke()
    {
        try
        {
            var checkpoint = _physicsAdapter.CaptureState(42, 350_000_000);
            if (!_linkBodies.TryGetValue(LinkBodyKey(_activeGraph.Id, _activeGraph.RootLinkId), out var rootBody))
                throw new InvalidOperationException("Root rigid body is not registered in the active physics graph.");
            rootBody.GlobalPosition += new Vector3(2.5f, 1.25f, -0.75f);
            rootBody.LinearVelocity = new Vector3(3.5f, -1.25f, 0.5f);
            rootBody.AngularVelocity = new Vector3(0.15f, -0.3f, 0.45f);

            _physicsAdapter.RestoreState(checkpoint);
            var restored = _physicsAdapter.CaptureState(checkpoint.PhysicsTick, checkpoint.SimTimeNs);
            foreach (var expected in checkpoint.Bodies)
            {
                var actual = restored.Bodies.SingleOrDefault(state => state.ComponentId == expected.ComponentId)
                    ?? throw new InvalidDataException($"Restored body {expected.ComponentId} is missing.");
                bool Close(double[] left, double[] right) => left.Length == right.Length
                    && left.Zip(right).All(pair => Math.Abs(pair.First - pair.Second) <= 1e-4);
                if (!Close(expected.PositionM, actual.PositionM) || !Close(expected.LinearVelocityMps, actual.LinearVelocityMps)
                    || !Close(expected.AngularVelocityRadps, actual.AngularVelocityRadps)
                    || Math.Abs(expected.RotationXyzw.Zip(actual.RotationXyzw).Sum(pair => pair.First * pair.Second)) < 0.9999)
                    throw new InvalidDataException($"Godot body state did not restore within tolerance for {expected.ComponentId}.");
            }
            Console.WriteLine($"PHYSICS_CHECKPOINT_RESTORE bodies={restored.Bodies.Count} tick={restored.PhysicsTick} sim_time_ns={restored.SimTimeNs} pose_velocity_match=PASS");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"PHYSICS_CHECKPOINT_RESTORE_FAILED {exception.Message}");
            GetTree().Quit(1);
            return;
        }
        GetTree().Quit();
    }

    private static CollisionShape3D MakeComponentCollisionShape(string id, ComponentDefinition definition, double[]? sizeOverrideM = null)
    {
        var sizeM = sizeOverrideM ?? definition.SizeM;
        Shape3D shape = definition.Model == "wheel" ? new CylinderShape3D
        {
            Radius = (float)sizeM[2] / 2,
            Height = (float)sizeM[1]
        } : definition.Primitive switch
        {
            "sphere" => new SphereShape3D { Radius = (float)(sizeOverrideM is null ? definition.RadiusM ?? sizeM[0] / 2 : sizeM[0] / 2) },
            "cylinder" => new CylinderShape3D
            {
                Radius = (float)(sizeOverrideM is null ? definition.RadiusM ?? sizeM[0] / 2 : sizeM[0] / 2),
                Height = (float)sizeM[1]
            },
            _ => new BoxShape3D { Size = ToVector(sizeM) }
        };
        var collision = new CollisionShape3D { Name = id + "Collision", Shape = shape };
        if (definition.Model == "wheel") collision.RotationDegrees = new Vector3(0, 0, 90);
        return collision;
    }

    private static Shape3D MakeComponentShape(ComponentDefinition definition, double[] sizeM)
    {
        return definition.Model == "wheel" ? new CylinderShape3D { Radius = (float)sizeM[2] / 2, Height = (float)sizeM[1] } : definition.Primitive switch
        {
            "sphere" => new SphereShape3D { Radius = (float)(sizeM[0] / 2) },
            "cylinder" => new CylinderShape3D { Radius = (float)(sizeM[0] / 2), Height = (float)sizeM[1] },
            _ => new BoxShape3D { Size = ToVector(sizeM) }
        };
    }

    private static string CreateShopInstanceId(string catalogueId, IEnumerable<string> existingIds)
    {
        var prefix = "shop-" + catalogueId.Replace('.', '-');
        var existing = existingIds.ToHashSet(StringComparer.Ordinal);
        if (!existing.Contains(prefix)) return prefix;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{prefix}-{suffix}";
            if (!existing.Contains(candidate)) return candidate;
        }
    }

    private static RobotAuthoringDraft AddRoverElectricalPreset(RobotAuthoringDraft draft,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        var graph = draft.RequireAssembly(catalogue);
        var controllers = graph.Components.Where(component => catalogue[component.CatalogueId].ElectricalPorts.Any(port => port.Id == "pwm1"))
            .OrderBy(component => component.Id, StringComparer.Ordinal).ToArray();
        var drivers = graph.Components.Where(component => catalogue[component.CatalogueId].Model == "dual_hbridge")
            .OrderBy(component => component.Id, StringComparer.Ordinal).ToArray();
        var motors = graph.Components.Where(component => catalogue[component.CatalogueId].Model == "dc_motor")
            .OrderBy(component => component.Id, StringComparer.Ordinal).ToArray();
        var sensors = graph.Components.Where(component => catalogue[component.CatalogueId].Model == "distance_raycast")
            .OrderBy(component => component.Id, StringComparer.Ordinal).ToArray();
        var batteries = graph.Components.Where(component => catalogue[component.CatalogueId].ElectricalPorts.Any(port => port.Id == "supply" && port.Interface == "power"))
            .OrderBy(component => component.Id, StringComparer.Ordinal).ToArray();
        if (controllers.Length != 1 || drivers.Length != 1 || motors.Length != 2 || sensors.Length != 1 || batteries.Length != 1)
            throw new InvalidDataException("The rover wiring preset needs one supported STM32 controller, one dual driver, two motors, one distance sensor and one battery.");
        var controller = controllers[0].Id;
        var driver = drivers[0].Id;
        var wires = new (string From, string To, string Interface)[]
        {
            ($"{controller}.pwm1", $"{driver}.pwm_a", "pwm"), ($"{controller}.dir1", $"{driver}.dir_a", "gpio"),
            ($"{driver}.out_a", $"{motors[0].Id}.drive", "motor_channel"),
            ($"{controller}.pwm2", $"{driver}.pwm_b", "pwm"), ($"{controller}.dir2", $"{driver}.dir_b", "gpio"),
            ($"{driver}.out_b", $"{motors[1].Id}.drive", "motor_channel"),
            ($"{sensors[0].Id}.echo", $"{controller}.sensor", "gpio"),
            ($"{batteries[0].Id}.supply", $"{driver}.vin", "power"),
            ($"{batteries[0].Id}.supply", $"{controller}.power_in", "power")
        };
        foreach (var wire in wires)
        {
            var existing = graph.ElectricalConnections.Where(connection => connection.From == wire.From || connection.To == wire.From
                || connection.From == wire.To || connection.To == wire.To).ToArray();
            if (existing.Any(connection => (connection.From == wire.From && connection.To == wire.To)
                || (connection.From == wire.To && connection.To == wire.From))) continue;
            if (wire.Interface != "power" && existing.Length > 0)
                throw new InvalidDataException($"Cannot apply preset: one of {wire.From} or {wire.To} is already wired differently.");
            draft = draft.AddElectricalConnection(catalogue, wire.From, wire.To, "");
            graph = draft.RequireAssembly(catalogue);
        }
        return draft;
    }

    private string CreateAuthoringInstanceId(ComponentDefinition definition, IEnumerable<string> existingIds)
    {
        _ = definition;
        return CreateShopInstanceId(definition.Id, existingIds);
    }

    private void BuildDiagnosticPanel(AssemblyGraph activeGraph, IReadOnlyList<AssemblyGraph> additionalRobots)
    {
        var layer = new CanvasLayer { Layer = 11 };
        var panel = new PanelContainer
        {
            AnchorLeft = 1,
            AnchorRight = 1,
            AnchorTop = 0,
            AnchorBottom = 0,
            OffsetLeft = -586,
            OffsetRight = -18,
            OffsetTop = 270,
            OffsetBottom = -18
        };
        var stack = new VBoxContainer();
        stack.AddChild(new Label { Text = "COMPONENT STATUS  ·  World › Robot › MCU › Device" });
        _diagnosticTree = new Tree { Columns = 3, HideRoot = true, CustomMinimumSize = new Vector2(536, 220), SizeFlagsVertical = Control.SizeFlags.ExpandFill };
        _diagnosticTree.SetColumnTitle(0, "Component");
        _diagnosticTree.SetColumnTitle(1, "Status");
        _diagnosticTree.SetColumnTitle(2, "Measurement / reason");
        _diagnosticTree.SetColumnExpand(0, true);
        _diagnosticTree.SetColumnExpand(1, false);
        _diagnosticTree.SetColumnExpand(2, true);
        _diagnosticTree.SetColumnCustomMinimumWidth(1, 96);
        _diagnosticTree.SetColumnCustomMinimumWidth(2, 180);
        _diagnosticTree.ItemSelected += OnDiagnosticItemSelected;
        var root = _diagnosticTree.CreateItem();
        var world = AddDiagnosticRow(root, "world", "World");
        var robot = AddDiagnosticRow(world, "robot", activeGraph.DisplayName);
        var controllerAssignments = _project.ControllersByRobotId.GetValueOrDefault(activeGraph.Id, []);
        foreach (var assignment in controllerAssignments)
        {
            var controller = activeGraph.Components.Single(component => component.Id == assignment.ComponentInstanceId);
            var controllerLabel = _catalogue[controller.CatalogueId].Id;
            AddDiagnosticRow(robot, controller.Id, controllerLabel);
            var suffix = controller.Id == "mcu-board" ? string.Empty : ":" + controller.Id;
            AddDiagnosticRow(_diagnosticRows[controller.Id], "firmware" + suffix, "Firmware image");
            AddDiagnosticRow(_diagnosticRows[controller.Id], "uart" + suffix, "UART endpoint");
        }
        foreach (var component in activeGraph.Components.Where(component => !_diagnosticRows.ContainsKey(component.Id)))
        {
            AddDiagnosticRow(robot, component.Id, _catalogue[component.CatalogueId].Id);
            SetDiagnostic(component.Id, "UNKNOWN", "Design component loaded; no firmware/device telemetry is assigned.");
        }
        foreach (var assignment in controllerAssignments)
        {
            var controller = activeGraph.Components.Single(component => component.Id == assignment.ComponentInstanceId);
            var suffix = controller.Id == "mcu-board" ? string.Empty : ":" + controller.Id;
            SetDiagnostic("firmware" + suffix, "UNKNOWN", assignment.FirmwareSha256[..Math.Min(12, assignment.FirmwareSha256.Length)]);
            SetDiagnostic("uart" + suffix, "UNKNOWN", "Waiting for a firmware endpoint.");
        }
        foreach (var assembly in additionalRobots)
        {
            var assemblyRow = AddDiagnosticRow(world, "robot-" + assembly.Id, assembly.DisplayName);
            SetDiagnostic("robot-" + assembly.Id, "OPERATIONAL", $"{assembly.Links.Count} links · {assembly.Joints.Count} joints · {assembly.Components.Count} components");
            foreach (var component in assembly.Components)
            {
                var row = AddDiagnosticRow(assemblyRow, component.Id, component.CatalogueId);
                SetDiagnostic(component.Id, "UNKNOWN", "Design component loaded; no firmware/device telemetry is assigned.");
            }
        }
        _diagnosticTree.SetColumnTitlesVisible(true);
        _diagnosticTree.SetColumnTitle(0, "Component");
        _diagnosticTree.SetColumnTitle(1, "Status");
        _diagnosticTree.SetColumnTitle(2, "Measurement / reason");
        stack.AddChild(_diagnosticTree);
        _diagnosticDetails = new Label { Text = "Select a component to inspect its firmware, clock, transport and last diagnostic." , AutowrapMode = TextServer.AutowrapMode.WordSmart };
        stack.AddChild(_diagnosticDetails);
        panel.AddChild(stack);
        layer.AddChild(panel);
        AddChild(layer);

        _saveProjectDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.SaveFile,
            Access = FileDialog.AccessEnum.Filesystem,
            CurrentFile = _project.ProjectId + ".roboworld",
            Filters = ["*.roboworld ; RoboWorld Project"]
        };
        _saveProjectDialog.FileSelected += SaveProjectToPath;
        _openProjectDialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Filters = ["*.roboworld ; RoboWorld Project"]
        };
        _openProjectDialog.FileSelected += RestartWithProject;
        AddChild(_saveProjectDialog);
        AddChild(_openProjectDialog);
    }

    private TreeItem AddDiagnosticRow(TreeItem parent, string id, string title)
    {
        var item = _diagnosticTree.CreateItem(parent);
        item.SetText(0, title);
        item.SetMetadata(0, id);
        _diagnosticRows.Add(id, item);
        return item;
    }

    private void OnDiagnosticItemSelected()
    {
        var item = _diagnosticTree.GetSelected();
        if (item is null) return;
        var id = item.GetMetadata(0).AsString();
        var selectedAssemblyComponent = _activeGraph.Components.SingleOrDefault(component => component.Id == id);
        if (selectedAssemblyComponent is null)
        {
            _selectedShopInstanceId = null;
            foreach (var input in _shopSizeInputs) input.Editable = false;
            _resizeShopButton.Disabled = true;
            _removeShopButton.Disabled = true;
        }
        else
        {
            _selectedShopInstanceId = id;
            var size = selectedAssemblyComponent.SizeOverrideM ?? _catalogue[selectedAssemblyComponent.CatalogueId].SizeM;
            for (var axis = 0; axis < 3; axis++) _shopSizeInputs[axis].Value = size[axis];
            foreach (var input in _shopSizeInputs) input.Editable = true;
            _resizeShopButton.Disabled = false;
            _removeShopButton.Disabled = !CanRemoveShopComponent(selectedAssemblyComponent);
        }
        if (_highlightedVisual is not null) _highlightedVisual.MaterialOverlay = null;
        _highlightedComponentId = id;
        _highlightedVisual = _componentVisuals.GetValueOrDefault(id) as GeometryInstance3D;
        if (_highlightedVisual is not null)
            _highlightedVisual.MaterialOverlay = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(0.2f, 0.9f, 1.0f, 0.45f), Transparency = BaseMaterial3D.TransparencyEnum.Alpha };
        _diagnosticDetails.Text = BuildDiagnosticDetails(id);
    }

    private string BuildDiagnosticDetails(string id)
    {
        if (id.StartsWith("firmware:", StringComparison.Ordinal) || id == "firmware")
            return $"Firmware SHA-256: {_firmwareSha256}\nRenode virtual time: {_renodeSimTimeNs} ns · RTF {_renodeMeasuredRtf:F3}\nSelected execution: {_activeControllerExecutionMode ?? _controllerExecutionMode} · {_activeControllerBackend}";
        if (id.StartsWith("uart:", StringComparison.Ordinal) || id == "uart")
            return $"Endpoint: {_uartEndpoint}\nRX {_uartRxBytes} B · TX {_uartTxBytes} B · Exact firmware UART";
        var component = _activeGraph?.Components.SingleOrDefault(item => item.Id == id);
        if (component is not null)
        {
            var definition = _catalogue[component.CatalogueId];
            if (definition.Model == "dc_motor") return GetMotorDetail(component.Id);
            if (definition.Model == "dual_hbridge") return $"Graph-routed motor channels · channel 1 duty {_motorLeftDuty:F3} · channel 2 duty {_motorRightDuty:F3}\nLast fault: {_lastDiagnosticComponent}/{_lastDiagnosticReason}";
            if (definition.Model == "distance_raycast")
                return $"Firmware measurement: {(_sensorReadingValid ? $"{_sensorDistanceMm} mm" : "no valid MCU reading yet")}\nLast sample sim ns: {_lastSensorSampleNs} · GPIOA0 echo / TIM2 pulse width";
            var firmware = (_authoringDraft?.FirmwareAssignments ?? []).SingleOrDefault(item => item.ControllerComponentId == id);
            var projectFirmware = _project?.ControllersByRobotId.GetValueOrDefault(_activeRobotId, [])
                .SingleOrDefault(item => item.ComponentInstanceId == id);
            if (firmware is not null || projectFirmware is not null)
                return $"Firmware SHA-256: {firmware?.FirmwareSha256 ?? projectFirmware!.FirmwareSha256}\nRenode virtual time: {_renodeSimTimeNs} ns · RTF {_renodeMeasuredRtf:F3}\nMCU state: {(_mcuHalted ? "CPU halted" : _serviceProcessFailed ? "service exited" : _eventBridgeConnected ? "running" : "starting")}";
        }
        return id switch
        {
            "world" => $"sim_time_ns={_simTimeNs}  physics={_measuredPhysicsHz:F1}/120 Hz  render={Engine.GetFramesPerSecond()} FPS  RTF={_metrics.Text}",
            "mcu-board" => $"Firmware SHA-256: {_firmwareSha256}\nRenode virtual time: {_renodeSimTimeNs} ns · RTF {_renodeMeasuredRtf:F3}",
            _ => $"Component id: {id}\nStatus: {_diagnosticRows[id].GetText(1)}\n{_diagnosticRows[id].GetText(2)}"
        };
    }

    private string GetMotorDetail(string componentId)
    {
        var joint = _activeGraph.Joints.SingleOrDefault(item => item.ActuatorComponentId == componentId);
        var wheel = joint is null ? null : _activeGraph.Components.SingleOrDefault(item => item.LinkId == joint.ChildLinkId
            && _catalogue[item.CatalogueId].Model == "wheel");
        var body = wheel is null ? null : _latestSnapshot?.Bodies.SingleOrDefault(item => item.ComponentId == wheel.Id);
        var rpm = body is null ? 0 : body.AngularVelocityRadps[0] * 60 / (2 * Math.PI);
        var duty = _motorDuties.GetValueOrDefault(componentId);
        return $"signed PWM duty: {duty:F3}\nMeasured wheel speed: {rpm:F1} RPM  torque/load model: versioned DC motor";
    }

    private void UpdateDiagnosticRows(PhysicalSnapshot snapshot)
    {
        if (_activeGraph is null) return;
        var rootStatus = _serviceProcessFailed || _mcuHalted ? "FAILED" : _driverRouteFailed || _haltedActuatorIds.Count > 0 ? "DEGRADED" : "OPERATIONAL";
        var elapsedSeconds = Math.Max((Stopwatch.GetTimestamp() - _sessionStarted) / (double)Stopwatch.Frequency, 0.001);
        SetDiagnostic("world", "OPERATIONAL", $"{_simTimeNs} ns · RTF {(_simTimeNs / 1_000_000_000d) / elapsedSeconds:F2}");
        SetDiagnostic("robot", rootStatus, rootStatus == "OPERATIONAL" ? _activeGraph.DisplayName : "Critical child fault; inspect MCU/driver");
        var mcuStatus = _serviceProcessFailed || _mcuHalted ? "FAILED" : _eventBridgeConnected ? "OPERATIONAL" : "UNKNOWN";
        var mcuReason = _mcuHalted ? "Renode CPU halted" : _serviceProcessFailed ? "Robot Service process exited" : _lastDiagnosticReason;
        foreach (var assignment in _project.ControllersByRobotId.GetValueOrDefault(_activeRobotId, []))
        {
            SetDiagnostic(assignment.ComponentInstanceId, mcuStatus, mcuReason);
            var firmwareId = assignment.ComponentInstanceId == "mcu-board" ? "firmware" : "firmware:" + assignment.ComponentInstanceId;
            var uartId = assignment.ComponentInstanceId == "mcu-board" ? "uart" : "uart:" + assignment.ComponentInstanceId;
            SetDiagnostic(firmwareId, _mcuHalted || _serviceProcessFailed ? "FAILED" : _firmwareSha256 == "unknown" ? "UNKNOWN" : "OPERATIONAL",
                assignment.FirmwareSha256[..Math.Min(12, assignment.FirmwareSha256.Length)]);
            SetDiagnostic(uartId, _mcuHalted || _serviceProcessFailed ? "INACTIVE" : _eventBridgeConnected ? "OPERATIONAL" : "UNKNOWN",
                $"RX {_uartRxBytes} / TX {_uartTxBytes} B");
        }
        foreach (var driver in _activeGraph.Components.Where(component => _catalogue[component.CatalogueId].Model == "dual_hbridge"))
        {
            var driverStatus = _driverRouteFailed ? "FAILED" : _haltedActuatorIds.Count > 0 || _mcuHalted || _serviceProcessFailed ? "DEGRADED" : _eventBridgeConnected ? "OPERATIONAL" : "UNKNOWN";
            SetDiagnostic(driver.Id, driverStatus, _driverRouteFailed ? _lastDiagnosticReason : $"PWM channel 1 {_motorLeftDuty:F2} / channel 2 {_motorRightDuty:F2}");
        }
        foreach (var motor in _activeGraph.Components.Where(component => _catalogue[component.CatalogueId].Model == "dc_motor"))
        {
            var joint = _activeGraph.Joints.SingleOrDefault(item => item.ActuatorComponentId == motor.Id);
            var wheel = joint is null ? null : _activeGraph.Components.SingleOrDefault(item => item.LinkId == joint.ChildLinkId
                && _catalogue[item.CatalogueId].Model == "wheel");
            var rpm = wheel is null ? 0 : GetMotorRpm(snapshot, wheel.Id);
            var duty = _motorDuties.GetValueOrDefault(motor.Id);
            var halted = _haltedActuatorIds.Contains(motor.Id);
            var motorStatus = halted ? "DEGRADED" : _mcuHalted || _serviceProcessFailed ? "INACTIVE" : Math.Abs(duty) > 0 ? "OPERATIONAL" : "INACTIVE";
            var detail = halted && motorStatus == "DEGRADED" ? $"PWM latched after its MCU halt · {rpm:F1} RPM" : $"{rpm:F1} RPM · duty {duty:F2}";
            SetDiagnostic(motor.Id, motorStatus, detail);
        }
        foreach (var sensor in _activeGraph.Components.Where(component => _catalogue[component.CatalogueId].Model == "distance_raycast"))
            SetDiagnostic(sensor.Id, _mcuHalted || _serviceProcessFailed ? "INACTIVE" : _sensorReadingValid ? "OPERATIONAL" : "UNKNOWN",
                _sensorReadingValid ? $"{_sensorDistanceMm} mm" : "awaiting MCU sample");
    }

    private static double GetMotorRpm(PhysicalSnapshot snapshot, string wheelId)
    {
        var wheel = snapshot.Bodies.SingleOrDefault(item => item.ComponentId == wheelId);
        return wheel is null ? 0 : wheel.AngularVelocityRadps[0] * 60 / (2 * Math.PI);
    }

    private void SetDiagnostic(string id, string status, string detail)
    {
        if (!_diagnosticRows.TryGetValue(id, out var item)) return;
        var (symbol, color) = status switch
        {
            "OPERATIONAL" => ("✓", new Color(0.3f, 0.88f, 0.55f)),
            "FAILED" => ("✖", new Color(1f, 0.28f, 0.3f)),
            "DEGRADED" => ("⚠", new Color(1f, 0.68f, 0.2f)),
            "INACTIVE" => ("—", new Color(0.64f, 0.69f, 0.75f)),
            _ => ("?", new Color(0.78f, 0.8f, 0.84f))
        };
        item.SetText(1, $"{symbol} {status}");
        item.SetText(2, detail);
        item.SetCustomColor(1, color);
        if (!_diagnosticLastStates.TryGetValue(id, out var previous) || previous != status)
        {
            _diagnosticLastStates[id] = status;
            Console.WriteLine($"DIAGNOSTIC_ROW id={id} status={status} detail={detail}");
        }
    }

    private void SaveProjectDialog() => _saveProjectDialog.PopupCentered(new Vector2I(680, 480));
    private void OpenProjectDialog() => _openProjectDialog.PopupCentered(new Vector2I(680, 480));

    private void SaveProjectToPath(string path)
    {
        try
        {
            var hash = ProjectPackageStore.SavePackage(_project, path, _activeGraph,
                new ProjectControllerExecutionSettings(_controllerExecutionMode, _project.ControllerExecutionSettings.ProfileReferences));
            _workerStatus.Text = $"Robot Service  READY · saved design {Path.GetFileName(path)}";
            Console.WriteLine($"PROJECT_SAVED id={_project.ProjectId} package={path} package_sha256={hash} firmware_sha256={_project.FirmwareSha256}");
        }
        catch (Exception exception)
        {
            _lastDiagnosticReason = "E_PROJECT_INVALID";
            _lastDiagnosticComponent = "project";
            _workerState = "FAILED · project save";
            _diagnosticDetails.Text = $"Project save failed: {exception.Message}";
            Console.Error.WriteLine($"PROJECT_SAVE_FAILED {exception.Message}");
        }
    }

    private void RestartWithProject(string path)
    {
        try
        {
            var executable = OS.GetExecutablePath();
            var arguments = new[] { "--path", ProjectSettings.GlobalizePath("res://"), "--", "--project=" + path };
            var pid = OS.CreateProcess(executable, arguments);
            if (pid <= 0) throw new IOException("Godot could not start a new project session.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            _diagnosticDetails.Text = $"Project reopen failed: {exception.Message}";
            Console.Error.WriteLine($"PROJECT_OPEN_FAILED {exception.Message}");
        }
    }

    private void RestartWithProjectRobot(string path, string robotId)
    {
        try
        {
            var executable = OS.GetExecutablePath();
            var arguments = new[] { "--path", ProjectSettings.GlobalizePath("res://"), "--", "--project=" + path,
                "--robot-id=" + robotId, "--controller-mode=exact" };
            var pid = OS.CreateProcess(executable, arguments);
            if (pid <= 0) throw new IOException("Godot could not reopen the saved robot project.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            GD.PushError($"Saved robot project reopen failed: {exception.Message}");
        }
    }

    private void StartEmptyAuthoring()
    {
        var arguments = new List<string> { "--path", ProjectSettings.GlobalizePath("res://"), "--" };
        if (_projectArgumentPath is not null) arguments.Add("--project=" + _projectArgumentPath);
        arguments.Add("--new-empty-robot");
        if (OS.CreateProcess(OS.GetExecutablePath(), arguments.ToArray()) <= 0)
        {
            _diagnosticDetails.Text = "Could not open the empty Robot Shop workspace.";
            return;
        }
        GetTree().Quit();
    }

    private void RestartWithRobot(string robotId)
    {
        try
        {
            var arguments = new List<string> { "--path", ProjectSettings.GlobalizePath("res://"), "--" };
            if (_projectArgumentPath is not null) arguments.Add("--project=" + _projectArgumentPath);
            if (_temporaryRunPackagePath is not null) arguments.Add("--temporary-run-package=" + _temporaryRunPackagePath);
            arguments.Add("--robot-id=" + robotId);
            arguments.Add("--controller-mode=" + _controllerExecutionMode);
            var pid = OS.CreateProcess(OS.GetExecutablePath(), arguments.ToArray());
            if (pid <= 0) throw new IOException("Godot could not start the selected robot session.");
            _handingOffTemporaryRunPackage = _temporaryRunPackagePath is not null;
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            _diagnosticDetails.Text = $"Robot reload failed: {exception.Message}";
            Console.Error.WriteLine($"ROBOT_OPEN_FAILED id={robotId} {exception.Message}");
        }
    }

    private Button MakeButton(string label, Action action)
    {
        var button = new Button { Text = label, FocusMode = Control.FocusModeEnum.None }; button.Pressed += action; return button;
    }

    private void SwitchToWorldView()
    {
        _robotViewActive = false;
        _worldCamera?.MakeCurrent();
        if (_viewModeStatus is not null) _viewModeStatus.Text = "World View · orbit: right-drag · pan: middle-drag · zoom: wheel";
        Console.WriteLine($"VIEW_MODE mode=world physics_tick={_physicsTicks} sim_time_ns={_simTimeNs} worker={_workerState}");
    }

    private void SwitchToRobotView()
    {
        if (_robotCameraPicker is null || _robotCameraPicker.ItemCount == 0)
        {
            if (_viewModeStatus is not null) _viewModeStatus.Text = "Robot View unavailable · add an enabled Robot Camera component to this robot.";
            return;
        }
        _robotViewActive = true;
        SetSelectedRobotCameraCurrent();
    }

    private void SetSelectedRobotCameraCurrent()
    {
        if (_robotCameraPicker.Selected < 0 || _worldCamera is null) return;
        var cameraKey = _robotCameraPicker.GetItemMetadata(_robotCameraPicker.Selected).AsString();
        if (!_robotCameras.TryGetValue(cameraKey, out var camera)) return;
        camera.MakeCurrent();
        var cameraViewport = camera.GetViewport();
        var activeCamera = cameraViewport?.GetCamera3D();
        var cameraForward = camera.GlobalBasis * Vector3.Forward;
        Console.WriteLine(FormattableString.Invariant($"CAMERA_VIEW_DIAGNOSTIC camera={camera.GetPath()} current={camera.Current} viewport_world_shared={camera.GetWorld3D() == GetWorld3D()} active_camera={activeCamera?.GetPath().ToString() ?? "none"} forward=({cameraForward.X:F3},{cameraForward.Y:F3},{cameraForward.Z:F3})"));
        if (_viewModeStatus is not null)
        {
            var cameraPosition = camera.GlobalPosition;
            _viewModeStatus.Text = $"Robot View · {_robotCameraPicker.GetItemText(_robotCameraPicker.Selected)} · mounted camera follows the robot · ({cameraPosition.X:F2}, {cameraPosition.Y:F2}, {cameraPosition.Z:F2}) m";
        }
        Console.WriteLine(FormattableString.Invariant($"VIEW_MODE mode=robot camera={cameraKey} physics_tick={_physicsTicks} sim_time_ns={_simTimeNs} worker={_workerState}"));
    }

    private void VerifyCameraFollowsRobot()
    {
        if (!_cameraFollowSmoke || _cameraFollowSmokeComplete || _cameraFollowProbe is null || _cameraFollowBody is null) return;
        var currentRelativeTransform = _cameraFollowBody.GlobalTransform.AffineInverse() * _cameraFollowProbe.GlobalTransform;
        var relativePositionError = currentRelativeTransform.Origin.DistanceTo(_cameraRelativeTransform.Origin);
        var relativeRotationError = (_cameraRelativeTransform.Basis.GetRotationQuaternion().Inverse()
            * currentRelativeTransform.Basis.GetRotationQuaternion()).GetAngle();
        if (relativePositionError > 0.003f || relativeRotationError > 0.01f)
            throw new InvalidDataException($"Mounted camera lost its rigid robot transform: position_error={relativePositionError:F4}m rotation_error={relativeRotationError:F4}rad.");
        var chassisMotion = _cameraFollowBody.GlobalPosition.DistanceTo(_cameraFollowInitialBodyPosition);
        var cameraMotion = _cameraFollowProbe.GlobalPosition.DistanceTo(_cameraFollowInitialWorldPosition);
        var cameraRotation = (_cameraFollowInitialWorldRotation.Inverse() * _cameraFollowProbe.GlobalBasis.GetRotationQuaternion()).GetAngle();
        if (_physicsTicks % 120 == 0) Console.WriteLine(FormattableString.Invariant($"CAMERA_FOLLOW_SMOKE_PROGRESS tick={_physicsTicks} chassis_motion_m={chassisMotion:F4} camera_motion_m={cameraMotion:F4} camera_rotation_rad={cameraRotation:F4} relative_position_error_m={relativePositionError:F5} relative_rotation_error_rad={relativeRotationError:F5}"));
        if (chassisMotion < 0.01f && cameraRotation < 0.05f) return;
        if (cameraMotion < 0.005f && cameraRotation < 0.05f)
            throw new InvalidDataException("Robot moved but its mounted camera world pose did not update.");
        _cameraFollowSmokeComplete = true;
        Console.WriteLine(FormattableString.Invariant($"CAMERA_FOLLOW_SMOKE status=PASS camera={_cameraFollowProbe.Name} chassis_motion_m={chassisMotion:F4} camera_motion_m={cameraMotion:F4} camera_rotation_rad={cameraRotation:F4} relative_position_error_m={relativePositionError:F5} relative_rotation_error_rad={relativeRotationError:F5} physics_tick={_physicsTicks} sim_time_ns={_simTimeNs}"));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_authoringMode || _worldCameraPivot is null || _worldCamera is null || _robotViewActive) return;
        if (@event is InputEventMouseButton button)
        {
            if (button.ButtonIndex == MouseButton.Right)
            {
                _worldOrbitDragging = button.Pressed;
                _worldCameraLastPointer = button.Position;
                GetViewport().SetInputAsHandled();
            }
            else if (button.ButtonIndex == MouseButton.Middle)
            {
                _worldPanDragging = button.Pressed;
                _worldCameraLastPointer = button.Position;
                GetViewport().SetInputAsHandled();
            }
            else if (button.Pressed && button.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
            {
                var direction = button.ButtonIndex == MouseButton.WheelUp ? -1f : 1f;
                var distance = Mathf.Clamp(_worldCamera.Position.Length() + direction * 0.65f, 2.2f, 30f);
                _worldCamera.Position = _worldCamera.Position.Normalized() * distance;
                GetViewport().SetInputAsHandled();
            }
            return;
        }
        if (@event is not InputEventMouseMotion motion || (!_worldOrbitDragging && !_worldPanDragging)) return;
        if (_worldOrbitDragging)
        {
            _worldCameraPivot.RotateY(-motion.Relative.X * 0.006f);
            var pitch = Mathf.Clamp(_worldCameraPivot.Rotation.X - motion.Relative.Y * 0.004f, -0.85f, 0.45f);
            _worldCameraPivot.Rotation = new Vector3(pitch, _worldCameraPivot.Rotation.Y, 0);
        }
        else
        {
            var scale = _worldCamera.Position.Length() * 0.0018f;
            _worldCameraPivot.Position += -_worldCamera.GlobalBasis.X * motion.Relative.X * scale + _worldCamera.GlobalBasis.Y * motion.Relative.Y * scale;
        }
        GetViewport().SetInputAsHandled();
    }

    private void StartWorker(string root)
    {
        try
        {
            _serviceProcessFailed = false;
            _activeControllerExecutionMode = _controllerExecutionMode;
            _activeControllerBackend = "preflight pending";
            _controllerExecutionReason = "Waiting for Robot Service preflight.";
            UpdateControllerModeStatus();
            _lastWorkerError = "none";
            _mcuHalted = false;
            _haltedActuatorIds.Clear();
            _actuatorEventTimes.Clear();
            _actuatorDuties.Clear();
            _motorDuties.Clear();
            _motorChannel1ActuatorId = null;
            _motorChannel2ActuatorId = null;
            _motorLeftDuty = 0;
            _motorRightDuty = 0;
            _driverRouteFailed = false;
            _lastDiagnosticComponent = "none";
            _lastDiagnosticReason = "none";
            var dll = Path.Combine(root, "src", "RoboWorld.RobotService", "bin", "Release", "net10.0", "RoboWorld.RobotService.dll");
            var dotnet = Path.Combine(root, ".toolchain", "dotnet", "dotnet.exe");
            var packagedService = Path.Combine(root, "runtime", "robot-service", "RoboWorld.RobotService.exe");
            var packaged = File.Exists(packagedService);
            if (!packaged && (!File.Exists(dll) || !File.Exists(dotnet))) { _workerState = "UNAVAILABLE · Robot Service runtime is missing"; return; }
            var assignments = _project.ControllersByRobotId.TryGetValue(_activeRobotId, out var configuredControllers) ? configuredControllers : [];
            _workerState = $"STARTING · controller mode {_controllerExecutionMode}";
            _workerStatus.Text = _workerState;
            Console.WriteLine($"CONTROLLER_EXECUTION_REQUESTED mode={_controllerExecutionMode} active_robot={_activeRobotId}");
            var start = new ProcessStartInfo(packaged ? packagedService : dotnet) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = root };
            if (!packaged) start.ArgumentList.Add(dll);
            if (_benchmarkRun) start.ArgumentList.Add("--demo-worker");
            else
            {
                if (assignments.Count == 0) { _workerState = "UNAVAILABLE · selected robot has no firmware assignment"; return; }
                start.ArgumentList.Add("--renode-motor-host");
                start.ArgumentList.Add($"--root={root}");
                start.ArgumentList.Add($"--firmware={assignments[0].FirmwarePath}");
                start.ArgumentList.Add($"--controller-profiles-root={Path.Combine(_project.RootDirectory, "controller-profiles")}");
                start.ArgumentList.Add($"--component-directory={_project.ComponentDirectory}");
                start.ArgumentList.Add($"--peripheral-map-source={Path.Combine(_projectRoot, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs")}");
                foreach (var assignment in assignments)
                {
                    var route1 = ResolveControllerMotorChannel(_activeGraph, _catalogue, assignment.ComponentInstanceId, "PA6", "pwm1");
                    var route2 = ResolveControllerMotorChannel(_activeGraph, _catalogue, assignment.ComponentInstanceId, "PA7", "pwm2");
                    if (_motorChannel1ActuatorId is null) _motorChannel1ActuatorId = route1.Length == 0 ? null : route1;
                    if (_motorChannel2ActuatorId is null) _motorChannel2ActuatorId = route2.Length == 0 ? null : route2;
                    if (route1.Length > 0) _motorDuties.TryAdd(route1, 0);
                    if (route2.Length > 0) _motorDuties.TryAdd(route2, 0);
                    var configuration = JsonSerializer.Serialize(new
                    {
                        id = assignment.Id,
                        componentInstanceId = assignment.ComponentInstanceId,
                        firmwarePath = assignment.FirmwarePath,
                        channel1ActuatorId = route1,
                        channel2ActuatorId = route2
                    });
                    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(configuration));
                    start.ArgumentList.Add($"--controller-config={encoded}");
                    var profileReferenceKey = assignment.RobotId + "/" + assignment.Id;
                    if (_project.ControllerExecutionSettings.ProfileReferences.TryGetValue(profileReferenceKey, out var profileReference))
                    {
                        var robotPath = _project.RobotPaths.Single(path => StringComparer.OrdinalIgnoreCase.Equals(
                            Path.GetFileName(path), assignment.RobotId + ".robot.json"));
                        var startupReference = JsonSerializer.Serialize(new
                        {
                            robotId = assignment.RobotId,
                            controllerId = assignment.Id,
                            profileId = profileReference.ProfileId,
                            profileSha256 = profileReference.ProfileSha256,
                            robotPath,
                            echoInputEndpointId = ResolveControllerEchoInput(_activeGraph, _catalogue, assignment.ComponentInstanceId)
                        });
                        var encodedReference = Convert.ToBase64String(Encoding.UTF8.GetBytes(startupReference));
                        start.ArgumentList.Add($"--controller-profile-reference={encodedReference}");
                    }
                }
            }
            if (_performanceTrace is not null)
            {
                start.ArgumentList.Add($"--perf-trace-dir={_performanceTrace.DirectoryPath}");
                start.ArgumentList.Add($"--perf-scenario={_performanceScenario}");
            }
            if (_characterizationTracePath is not null) start.ArgumentList.Add($"--characterization-trace={_characterizationTracePath}");
            start.ArgumentList.Add($"--controller-mode={_controllerExecutionMode}");
            if (_routeFaultSmoke)
                start.ArgumentList.Add(_routeFaultAtSimTimeNs is { } routeFaultNs
                    ? $"--test-route-fault-at-sim-time-ns={routeFaultNs}" : "--test-route-fault-after-ms=2500");
            if (_mcuHaltSmoke || _multiControllerIsolationSmoke)
                start.ArgumentList.Add(_mcuHaltAtSimTimeNs is { } mcuHaltNs
                    ? $"--test-mcu-halt-at-sim-time-ns={mcuHaltNs}" : "--test-mcu-halt-after-ms=2500");
            if (_multiControllerIsolationSmoke && assignments.Count > 1) start.ArgumentList.Add($"--test-mcu-halt-controller-id={assignments[^1].Id}");
            if (_injectWorkerFailure && !_workerRestarted) start.ArgumentList.Add("--crash-after-ms=500");
            _worker = new Process { StartInfo = start, EnableRaisingEvents = true };
            _worker.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CallDeferred(MethodName.WorkerOutput, e.Data); };
            _worker.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) CallDeferred(MethodName.WorkerError, e.Data); };
            _worker.Exited += (_, _) => CallDeferred(MethodName.WorkerExited);
            _worker.Start(); _worker.BeginOutputReadLine(); _worker.BeginErrorReadLine(); _workerState = "STARTING";
        }
        catch (Exception exception) { _workerState = "FAILED · " + exception.Message; }
    }

    private static string ResolveControllerMotorChannel(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string controllerComponentId, string pinName, string mcuPortId)
    {
        var expectedPin = mcuPortId switch { "pwm1" => "PA6", "pwm2" => "PA7", _ => string.Empty };
        if (expectedPin.Length == 0 || pinName != expectedPin) return string.Empty;
        var channel = mcuPortId == "pwm1" ? 1 : 2;
        return RobotElectricalRouter.ResolveMotorActuator(graph, catalogue, controllerComponentId, channel) ?? string.Empty;
    }

    private static string? ResolveControllerEchoInput(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string controllerComponentId)
    {
        var controller = graph.Components.SingleOrDefault(component => component.Id == controllerComponentId);
        if (controller is null || !catalogue.TryGetValue(controller.CatalogueId, out var controllerDefinition))
            throw new InvalidDataException($"Controller assignment references missing component {controllerComponentId}.");
        var sensorPin = controllerDefinition.ElectricalPorts.SingleOrDefault(port => port.Id == "sensor");
        if (sensorPin?.Pin != "PA0") return null;
        var mcuEndpoint = $"{controllerComponentId}.{sensorPin.Id}";
        var wire = graph.ElectricalConnections.SingleOrDefault(connection => connection.From == mcuEndpoint || connection.To == mcuEndpoint);
        if (wire is null) return null;
        var sensorEndpoint = wire.From == mcuEndpoint ? wire.To : wire.From;
        var separator = sensorEndpoint.LastIndexOf('.');
        if (separator <= 0 || sensorEndpoint[(separator + 1)..] != "echo") return null;
        var sensorComponent = graph.Components.SingleOrDefault(component => component.Id == sensorEndpoint[..separator]);
        if (sensorComponent is null || !catalogue.TryGetValue(sensorComponent.CatalogueId, out var sensorDefinition)
            || !sensorDefinition.ElectricalPorts.Any(port => port.Id == "echo" && port.Interface == "gpio")) return null;
        return RobotElectricalRouter.HasDistanceSensorRoute(graph, catalogue, controllerComponentId) ? "gpio.pa0.echo" : null;
    }

    private static string ResolveApplicationRoot()
    {
        var contentRoot = Path.GetFullPath(ProjectSettings.GlobalizePath("res://.."));
        if (File.Exists(Path.Combine(contentRoot, "examples", "project.json"))) return contentRoot;
        var executableRoot = Path.GetDirectoryName(OS.GetExecutablePath());
        if (executableRoot is not null)
        {
            executableRoot = Path.GetFullPath(executableRoot);
            if (File.Exists(Path.Combine(executableRoot, "examples", "project.json"))) return executableRoot;
        }
        return contentRoot;
    }

    private string GetEvidencePath(string task, string fileName)
    {
        var evidenceRoot = Directory.Exists(Path.Combine(_projectRoot, "src"))
            ? Path.Combine(_projectRoot, "artifacts", "evidence")
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "RoboWorld", "evidence");
        var path = Path.Combine(evidenceRoot, task, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private void WorkerOutput(string line)
    {
        if (line.StartsWith("CONTROLLER_EXECUTION_SELECTED", StringComparison.Ordinal))
        {
            var kind = System.Text.RegularExpressions.Regex.Match(line, @"kind=([^\s]+)");
            var reason = System.Text.RegularExpressions.Regex.Match(line, @"reason=(.*)$");
            if (kind.Success) _activeControllerBackend = kind.Groups[1].Value;
            if (reason.Success) _controllerExecutionReason = reason.Groups[1].Value;
            UpdateControllerModeStatus();
            Console.WriteLine($"ROBOT_SERVICE_MODE {line}");
        }
        else if (line.StartsWith("UART_ENDPOINT", StringComparison.Ordinal))
        {
            var endpoint = System.Text.RegularExpressions.Regex.Match(line, @"controller_id=([^\s]+).*?uart=tcp://([^\s]+)");
            if (endpoint.Success)
            {
                _uartEndpoints[endpoint.Groups[1].Value] = endpoint.Groups[2].Value;
                _uartEndpoint = "Python UARTs  " + string.Join("  ·  ", _uartEndpoints.Select(pair => $"{pair.Key} {pair.Value}"));
            }
            Console.WriteLine($"ROBOT_SERVICE_UART {line}");
        }
        else if (line.Contains("READY", StringComparison.OrdinalIgnoreCase))
        {
            var portMatch = System.Text.RegularExpressions.Regex.Match(line, @"events_port=(\d+)");
            var uartMatch = System.Text.RegularExpressions.Regex.Match(line, @"uart=tcp://([^\s]+)");
            if (uartMatch.Success) _uartEndpoint = "Python UART  " + uartMatch.Groups[1].Value;
            var firmwareMatch = System.Text.RegularExpressions.Regex.Match(line, @"firmware_sha256=([0-9a-f]{64})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (firmwareMatch.Success) _firmwareSha256 = firmwareMatch.Groups[1].Value;
            _workerState = portMatch.Success ? "READY · Renode and STM32" : "READY · child responded";
            Console.WriteLine($"ROBOT_SERVICE_ENDPOINT {line}");
            if (portMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port)) StartEventBridge(port);
        }
        else if (line.Contains("EVENT_STREAM_CONNECTED", StringComparison.Ordinal)) EventStreamConnected();
        else if (line.StartsWith("TEST_MCU_HALTED", StringComparison.Ordinal))
        {
            var componentMatch = System.Text.RegularExpressions.Regex.Match(line, @"component_id=([^\s]+)");
            var haltedComponentId = componentMatch.Success ? componentMatch.Groups[1].Value
                : _project.ControllersByRobotId.GetValueOrDefault(_activeRobotId, []).FirstOrDefault()?.ComponentInstanceId ?? "";
            var assignment = _project.ControllersByRobotId.GetValueOrDefault(_activeRobotId, []).SingleOrDefault(item => item.ComponentInstanceId == haltedComponentId);
            _mcuHalted = assignment is null || _project.ControllersByRobotId.GetValueOrDefault(_activeRobotId, []).FirstOrDefault()?.ComponentInstanceId == haltedComponentId;
            if (assignment is not null)
            {
                var channel1 = ResolveControllerMotorChannel(_activeGraph, _catalogue, haltedComponentId, "PA6", "pwm1");
                var channel2 = ResolveControllerMotorChannel(_activeGraph, _catalogue, haltedComponentId, "PA7", "pwm2");
                if (channel1.Length > 0) _haltedActuatorIds.Add(channel1);
                if (channel2.Length > 0) _haltedActuatorIds.Add(channel2);
            }
            _lastDiagnosticComponent = haltedComponentId;
            _lastDiagnosticReason = "E_MCU_HALTED";
            SetDiagnostic(haltedComponentId, "FAILED", "Renode CPU halted; its UART stopped answering.");
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
        }
        else if (line.StartsWith("UART_PROXY_CONNECTED", StringComparison.Ordinal))
        {
            var bootBytes = System.Text.RegularExpressions.Regex.Match(line, @"buffered_boot_bytes=(\d+)");
            if (bootBytes.Success) _uartTxBytes += long.Parse(bootBytes.Groups[1].Value, CultureInfo.InvariantCulture);
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
        }
        else if (line.StartsWith("UART_PROXY_TRANSFER", StringComparison.Ordinal))
        {
            var transfer = System.Text.RegularExpressions.Regex.Match(line, @"direction=([a-z_]+) bytes=(\d+)");
            if (transfer.Success)
            {
                var bytes = long.Parse(transfer.Groups[2].Value, CultureInfo.InvariantCulture);
                if (transfer.Groups[1].Value == "controller_to_mcu") _uartRxBytes += bytes;
                else if (transfer.Groups[1].Value == "mcu_to_controller") _uartTxBytes += bytes;
            }
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
        }
        else if (line.StartsWith("UART_SENSOR_TELEMETRY", StringComparison.Ordinal))
        {
            var telemetry = System.Text.RegularExpressions.Regex.Match(line, @"mm=(\d+) valid=([01])");
            if (telemetry.Success)
            {
                _sensorDistanceMm = uint.Parse(telemetry.Groups[1].Value, CultureInfo.InvariantCulture);
                _sensorReadingValid = telemetry.Groups[2].Value == "1";
            }
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
        }
        else if (line.StartsWith("CLOCK_VERIFIED", StringComparison.Ordinal))
        {
            var clock = System.Text.RegularExpressions.Regex.Match(line, @"sim_time_ns=(\d+) measured_rtf=([0-9.]+)");
            if (clock.Success)
            {
                _renodeSimTimeNs = long.Parse(clock.Groups[1].Value, CultureInfo.InvariantCulture);
                double.TryParse(clock.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _renodeMeasuredRtf);
            }
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
        }
        else if (line.StartsWith("DISTANCE_ECHO_RISE", StringComparison.Ordinal))
        {
            var sample = System.Text.RegularExpressions.Regex.Match(line, @"sample_ns=(\d+)");
            if (sample.Success) _lastSensorSampleNs = long.Parse(sample.Groups[1].Value, CultureInfo.InvariantCulture);
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
        }
        else if (line.StartsWith("UART_PROXY_BOUNDED", StringComparison.Ordinal) || line.StartsWith("DISTANCE_ECHO_", StringComparison.Ordinal)
            || line.StartsWith("UART_PROXY_INGRESS", StringComparison.Ordinal) || line.StartsWith("UART_MODEL_INPUT_BOUNDARY", StringComparison.Ordinal))
            Console.WriteLine($"ROBOT_SERVICE_LOG {line}");
    }
    private void WorkerError(string line)
    {
        if (!line.TrimStart().StartsWith("at ", StringComparison.Ordinal)) _lastWorkerError = line;
        _workerState = "DEGRADED · " + line;
        _lastDiagnosticReason = line;
        if (line.StartsWith("CONTROLLER_EXECUTION_REJECTED", StringComparison.Ordinal))
        {
            _activeControllerBackend = "rejected before run";
            _controllerExecutionReason = line;
            UpdateControllerModeStatus();
        }
        if (line.Contains("PWM_GPIO_AF_MISMATCH_PA6_PA7", StringComparison.Ordinal))
        {
            _driverRouteFailed = true;
            _lastDiagnosticComponent = _activeGraph.Components.FirstOrDefault(component => _catalogue[component.CatalogueId].Model == "dual_hbridge")?.Id ?? "robot";
        }
        Console.WriteLine($"ROBOT_SERVICE_ERROR {line}");
        if (line.Contains("PWM_GPIO_AF_MISMATCH_PA6_PA7", StringComparison.Ordinal))
            Console.WriteLine("ACTUATION_FAULT reason=PWM_GPIO_AF_MISMATCH_PA6_PA7 signed_duty=0.000");
    }

    private void HardwareEventError(string line)
    {
        StopActuation();
        WorkerError(line);
    }

    private void EventBridgeError(string line)
    {
        _eventBridgeConnected = false;
        StopActuation();
        _workerState = "DEGRADED · hardware stream " + line;
        _lastDiagnosticComponent = _project.ControllersByRobotId.GetValueOrDefault(_activeRobotId, []).FirstOrDefault()?.ComponentInstanceId ?? "robot";
        _lastDiagnosticReason = line;
    }

    private void StopActuation()
    {
        if (_physicsAdapter is null) return;
        _motorLeftDuty = 0;
        _motorRightDuty = 0;
        foreach (var motorId in _motorDuties.Keys.ToArray())
        {
            _motorDuties[motorId] = 0;
            try { _physicsAdapter.SetMotorDuty(motorId, 0); } catch (KeyNotFoundException) { }
        }
    }

    private void StartEventBridge(int port)
    {
        Console.WriteLine($"GODOT_EVENT_BRIDGE_START port={port}");
        StopEventBridge();
        _clockEpochReady = false;
        _eventBridgePending = false;
        _eventBridgeStop = new CancellationTokenSource();
        _ = ReceiveHardwareStreamAsync(port, _eventBridgeStop.Token);
    }

    private async Task ReceiveHardwareStreamAsync(int port, CancellationToken cancellationToken)
    {
        try
        {
            Console.WriteLine($"GODOT_EVENT_CONNECTING port={port}");
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(System.Net.IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            await using var stream = client.GetStream();
            Console.WriteLine($"GODOT_EVENT_CONNECTED port={port}");
            var serverHello = await ReadEnvelopeAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!_testClockEpochRace) CallDeferred(MethodName.SetClockEpoch, serverHello.SimTimeNs);
            Console.WriteLine($"GODOT_SERVER_HELLO payload={serverHello.PayloadCase} session={serverHello.SessionId}");
            if (serverHello.ProtocolMajor != 1 || serverHello.PayloadCase != Envelope.PayloadOneofCase.Hello)
                throw new InvalidDataException("RobotService protocol handshake was incompatible.");
            var clientHello = new Envelope
            {
                ProtocolMajor = 1,
                ProtocolMinor = 0,
                SessionId = serverHello.SessionId,
                WorldId = serverHello.WorldId,
                RobotId = serverHello.RobotId,
                Hello = new Hello { ClientKind = "godot", InstanceVersion = "4.7.2", Capabilities = { "fixed_physics", "pwm_output" } }
            };
            await WriteEnvelopeAsync(stream, clientHello, cancellationToken).ConfigureAwait(false);
            _eventStream = stream;
            _eventSessionId = serverHello.SessionId;
            Console.WriteLine("GODOT_EVENT_STREAM_HANDSHAKE_COMPLETE");
            CallDeferred(MethodName.EventStreamConnected);
            if (_testClockEpochRace)
            {
                Console.WriteLine("GODOT_CLOCK_EPOCH_TEST_REORDERED");
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                CallDeferred(MethodName.SetClockEpoch, serverHello.SimTimeNs);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await ReadEnvelopeAsync(stream, cancellationToken).ConfigureAwait(false);
                if (envelope.ProtocolMajor != 1 || !string.Equals(envelope.SessionId, serverHello.SessionId, StringComparison.Ordinal))
                    throw new InvalidDataException("RobotService stream session or protocol version changed.");
                if (envelope.PayloadCase == Envelope.PayloadOneofCase.Tick)
                {
                    var ackSource = Volatile.Read(ref _clockAckSource);
                    Interlocked.Exchange(ref _clockAckArrivalStopwatchTicks, Stopwatch.GetTimestamp());
                    if (ackSource is null || !ackSource.TrySetResult(envelope.Tick))
                        throw new InvalidDataException($"RobotService sent an unexpected or duplicate tick acknowledgment {envelope.Tick.TickIndex}.");
                }
                else if (envelope.PayloadCase is Envelope.PayloadOneofCase.HardwareEvent or Envelope.PayloadOneofCase.Diagnostic)
                    await _hardwareEvents.EnqueueAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { CallDeferred(MethodName.EventBridgeError, exception.Message); }
    }

    private void EventStreamConnected()
    {
        _eventBridgePending = true;
        _eventBridgeConnected = _clockEpochReady;
        ReleasePerformanceStartupPauseIfReady();
    }

    private async Task ResumePauseE2eAsync()
    {
        await Task.Delay(500);
        CallDeferred(MethodName.ResumePauseE2e);
    }

    private void ResumePauseE2e()
    {
        var heldNs = _simTimeNs - _pauseStartedSimTimeNs;
        Console.WriteLine($"SIM_RESUME sim_time_ns={_simTimeNs} physics_ticks={_physicsTicks} paused_sim_advance_ns={heldNs}");
        if (heldNs != 0) HardwareEventError("simulation time advanced while paused");
        _pauseResumed = true;
        _pauseResumePending = false;
        _pauseResumePhysicsTick = _physicsTicks;
        _paused = false;
        UpdateTreePauseState();
    }

    private void SetClockEpoch(long epochNs)
    {
        if (epochNs < 0) throw new InvalidDataException("RobotService supplied a negative clock epoch.");
        _simTimeNs = epochNs;
        _clockForwardTimeNs = epochNs;
        _clockRemainder = 0;
        _lastPhysicsDeltaNs = 0;
        _previousPhysicsDeltaNs = 0;
        _clockTickIndex = 0;
        _clockEpochReady = true;
        if (_eventBridgePending) _eventBridgeConnected = true;
        Console.WriteLine($"GODOT_CLOCK_EPOCH sim_time_ns={epochNs}");
        ReleasePerformanceStartupPauseIfReady();
    }

    private void ReleasePerformanceStartupPauseIfReady()
    {
        if (!_performanceStartupPauseActive || !_clockEpochReady || !_eventBridgeConnected) return;
        _performanceStartupPauseActive = false;
        _transportPauseActive = false;
        UpdateTreePauseState();
        Console.WriteLine($"PERF_STARTUP_BARRIER_RELEASE sim_time_ns={_simTimeNs} physics_ticks={_physicsTicks}");
    }

    private void DrainHardwareEvents()
    {
        while (_hardwareEvents.TryDequeue(out var envelope))
        {
            if (envelope.Sequence <= _lastEventSequence) { HardwareEventError($"replayed event #{envelope.Sequence}"); continue; }
            _lastEventSequence = envelope.Sequence;
            if (envelope.PayloadCase == Envelope.PayloadOneofCase.Diagnostic)
            {
                StopActuation();
                _lastDiagnosticComponent = envelope.Diagnostic.ComponentPath;
                _lastDiagnosticReason = envelope.Diagnostic.ReasonCode + ": " + envelope.Diagnostic.Reason;
                WorkerError($"{envelope.Diagnostic.ComponentPath}: {envelope.Diagnostic.ReasonCode}");
                continue;
            }
            var hardwareEvent = envelope.HardwareEvent;
            if (hardwareEvent.Kind != HardwareEvent.Types.Kind.PwmOutput || hardwareEvent.Unit != "fraction_signed" || !double.IsFinite(hardwareEvent.Scalar))
            {
                HardwareEventError("unsupported motor output event");
                continue;
            }
            if (hardwareEvent.EffectiveSimTimeNs < 0 || hardwareEvent.EffectiveSimTimeNs < envelope.SimTimeNs)
            {
                HardwareEventError("Renode event time moved backwards");
                continue;
            }
            try
            {
                if (_actuatorEventTimes.TryGetValue(envelope.ComponentId, out var previousActuatorEventNs) && hardwareEvent.EffectiveSimTimeNs < previousActuatorEventNs)
                {
                    HardwareEventError($"backward Renode time on {envelope.ComponentId}");
                    continue;
                }
                if (!_physicsAdapter.IsMotorBound(envelope.ComponentId)) { HardwareEventError($"unknown actuator {envelope.ComponentId}"); continue; }
                _physicsAdapter.SetMotorDuty(envelope.ComponentId, hardwareEvent.Scalar);
                _actuatorEventTimes[envelope.ComponentId] = hardwareEvent.EffectiveSimTimeNs;
                _actuatorDuties[envelope.ComponentId] = hardwareEvent.Scalar;
                _motorDuties[envelope.ComponentId] = hardwareEvent.Scalar;
                if (_armActuationSmoke && envelope.ComponentId == "arm-motor" && hardwareEvent.Scalar != 0 && _armActuationStartedPhysicsTick == 0)
                    _armActuationStartedPhysicsTick = _physicsTicks;
                if (_armActuationSmoke && envelope.ComponentId == "arm-motor") _armPeakDuty = Math.Max(_armPeakDuty, Math.Abs(hardwareEvent.Scalar));
                if (envelope.ComponentId == _motorChannel1ActuatorId) { _lastLeftEventSimTimeNs = hardwareEvent.EffectiveSimTimeNs; _motorLeftDuty = hardwareEvent.Scalar; }
                else if (envelope.ComponentId == _motorChannel2ActuatorId) { _lastRightEventSimTimeNs = hardwareEvent.EffectiveSimTimeNs; _motorRightDuty = hardwareEvent.Scalar; }
                if (_actuationSmoke && _motorLeftDuty != 0 && _motorRightDuty != 0 && _actuationSmokeStartedHostTicks == 0)
                {
                    _actuationSmokeStartedHostTicks = Stopwatch.GetTimestamp();
                    _actuationSmokeStartSimTimeNs = _clockForwardTimeNs;
                }
                Console.WriteLine($"ACTUATION_TRACE tick={_physicsTicks} godot_sim_time_ns={_simTimeNs} renode_sim_time_ns={hardwareEvent.EffectiveSimTimeNs} sequence={envelope.Sequence} actuator={envelope.ComponentId} duty={hardwareEvent.Scalar.ToString("F3", CultureInfo.InvariantCulture)} fidelity=sampled-output");
            }
            catch (Exception exception) { HardwareEventError(exception.Message); }
        }
    }

    private void RunEventQueueSmoke()
    {
        var queue = new BoundedInbox<int>(2);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        queue.EnqueueAsync(1, cancellation.Token).AsTask().GetAwaiter().GetResult();
        queue.EnqueueAsync(2, cancellation.Token).AsTask().GetAwaiter().GetResult();
        var waitingWriter = queue.EnqueueAsync(3, cancellation.Token).AsTask();
        if (waitingWriter.IsCompleted) throw new InvalidOperationException("Bounded inbox did not apply backpressure at capacity.");
        if (!queue.TryDequeue(out var first) || first != 1) throw new InvalidOperationException("Bounded inbox did not preserve the first queued event.");
        waitingWriter.GetAwaiter().GetResult();
        if (!queue.TryDequeue(out var second) || second != 2 || !queue.TryDequeue(out var third) || third != 3)
            throw new InvalidOperationException("Bounded inbox did not preserve queued event order after applying backpressure.");
        Console.WriteLine($"GODOT_EVENT_QUEUE_BOUNDED capacity=2 production_capacity={HardwareEventQueueCapacity} backpressure=PASS fifo=PASS dropped=0");
        GetTree().Quit();
    }

    private static async Task<Envelope> ReadEnvelopeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await ReadExactlyAsync(stream, prefix, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > 64 * 1024) throw new InvalidDataException($"Protobuf frame length {length} is outside the allowed range.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return Envelope.Parser.ParseFrom(payload);
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken);
            if (count == 0) throw new EndOfStreamException("RobotService closed the protobuf stream.");
            offset += count;
        }
    }

    private static async Task WriteEnvelopeAsync(NetworkStream stream, Envelope envelope, CancellationToken cancellationToken)
    {
        var payload = envelope.ToByteArray();
        if (payload.Length is <= 0 or > 64 * 1024) throw new InvalidDataException($"Protobuf frame length {payload.Length} is outside the allowed range.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void WriteEnvelopeSyncMeasured(NetworkStream stream, Envelope envelope, bool measure, out long serializationNs, out long transportNs)
    {
        var serializationStart = measure ? Stopwatch.GetTimestamp() : 0;
        var payload = envelope.ToByteArray();
        if (payload.Length is <= 0 or > 64 * 1024) throw new InvalidDataException($"Invalid protobuf frame length {payload.Length}.");
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        serializationNs = measure ? Stopwatch.GetElapsedTime(serializationStart).Ticks * 100L : 0;
        var transportStart = measure ? Stopwatch.GetTimestamp() : 0;
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
        transportNs = measure ? Stopwatch.GetElapsedTime(transportStart).Ticks * 100L : 0;
    }

    private void StopEventBridge()
    {
        if (_eventBridgeStop is null) return;
        _eventBridgeStop.Cancel();
        _eventBridgeStop.Dispose();
        _eventBridgeStop = null;
        _eventBridgeConnected = false;
    }
    private void WorkerExited()
    {
        _serviceProcessFailed = true;
        _lastDiagnosticComponent = "robot-service";
        _lastDiagnosticReason = "E_WORKER_CRASHED";
        StopActuation();
        _workerFailureSeen = true;
        _workerFailureAtMs = Time.GetTicksMsec();
        var exitCode = _worker is { HasExited: true } ? _worker.ExitCode.ToString(CultureInfo.InvariantCulture) : "unknown";
        _workerState = $"FAILED · worker exited ({exitCode}): {_lastWorkerError}";
    }
    private void UpdateControllerModeStatus()
    {
        if (_controllerModeStatus is null) return;
        var activeMode = _activeControllerExecutionMode ?? "not started";
        var reason = string.IsNullOrWhiteSpace(_controllerExecutionReason) ? "No preflight detail reported." : _controllerExecutionReason;
        var savedProfileCount = _project?.ControllerExecutionSettings.ProfileReferences.Count ?? 0;
        _controllerModePicker?.SetItemDisabled(2, savedProfileCount == 0);
        _controllerModePicker?.SetItemText(2, savedProfileCount == 0
            ? "Fast model · requires a qualified profile" : "Fast model · profile preflight required");
        var modelActive = StringComparer.Ordinal.Equals(_activeControllerBackend, "DeterministicModel");
        var fastProfileState = modelActive ? "VALIDATED profile active" : savedProfileCount == 0
            ? "unavailable · no project profile reference" : "not active · saved references are checked at run start";
        _controllerModeStatus.Text = $"Controller mode · active {_activeControllerBackend} ({activeMode}) · requested {_controllerExecutionMode}. Fast model: {fastProfileState}. Saved profile refs: {savedProfileCount}. {reason}";
        var restartRequired = _activeControllerExecutionMode is not null && _activeControllerExecutionMode != _controllerExecutionMode;
        Console.WriteLine($"CONTROLLER_MODE_UI active_backend={_activeControllerBackend} active_mode={activeMode} requested_mode={_controllerExecutionMode} fast_profile={(modelActive ? "VALIDATED_ACTIVE" : "NOT_ACTIVE")} saved_profile_refs={savedProfileCount} restart_required={restartRequired}");
    }

    private void RestartWithControllerMode()
    {
        try
        {
            var arguments = new List<string> { "--path", ProjectSettings.GlobalizePath("res://"), "--" };
            if (_projectArgumentPath is not null) arguments.Add("--project=" + _projectArgumentPath);
            arguments.Add("--robot-id=" + _activeRobotId);
            arguments.Add("--controller-mode=" + _controllerExecutionMode);
            var pid = OS.CreateProcess(OS.GetExecutablePath(), arguments.ToArray());
            if (pid <= 0) throw new IOException("Godot could not start the selected controller mode.");
            GetTree().Quit();
        }
        catch (Exception exception)
        {
            _controllerExecutionReason = "Restart failed: " + exception.Message;
            UpdateControllerModeStatus();
            Console.Error.WriteLine($"CONTROLLER_MODE_RESTART_FAILED {exception.Message}");
        }
    }

    private void RestartWorker()
    {
        if (_activeControllerExecutionMode is not null)
        {
            _controllerExecutionMode = _activeControllerExecutionMode;
            _controllerModePicker?.Select(_controllerExecutionMode switch { "auto" => 1, "fast" => 2, _ => 0 });
            UpdateControllerModeStatus();
        }
        StopWorker();
        StartWorker(_projectRoot);
    }
    private static List<(long SimTimeNs, double Left, double Right)> ReadActuationReplayEvents(string path)
    {
        var result = new List<(long SimTimeNs, double Left, double Right)>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("sim_time_ns,", StringComparison.Ordinal)) continue;
            var columns = line.Split(',');
            if (columns.Length != 3
                || !long.TryParse(columns[0], NumberStyles.None, CultureInfo.InvariantCulture, out var simTimeNs)
                || !double.TryParse(columns[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
                || !double.TryParse(columns[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var right)
                || simTimeNs < 0 || !double.IsFinite(left) || !double.IsFinite(right) || left is < -1 or > 1 || right is < -1 or > 1
                || result.Count > 0 && simTimeNs <= result[^1].SimTimeNs)
                throw new InvalidDataException("Actuation replay must contain strictly increasing simulation timestamps and signed duties in [-1,1].");
            result.Add((simTimeNs, left, right));
        }
        return result;
    }
    private void StopWorker()
    {
        if (_worker is null) return;
        StopEventBridge();
        try
        {
            // Closing the event stream is the worker's graceful shutdown signal.
            // Let its tick loop unwind and flush performance evidence before using
            // the process-tree kill fallback; the final ACK can arrive just before
            // Godot closes this process, so an immediate kill drops the last row.
            if (!_worker.HasExited && !_worker.WaitForExit(2000))
            {
                _worker.Kill(entireProcessTree: true);
                _worker.WaitForExit(2000);
            }
        }
        catch (InvalidOperationException) { }
        finally { _worker.Dispose(); _worker = null; _workerState = "STOPPED"; }
    }
    private void TogglePause() { _paused = !_paused; UpdateTreePauseState(); }
    private void UpdateTreePauseState() => GetTree().Paused = _paused || _transportPauseActive || _clockSyncFaulted;

    private ulong CapturePhysicsStateFingerprint()
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offsetBasis;
        static void Add(ref ulong value, float component)
        {
            const ulong fnvPrime = 1099511628211UL;
            value ^= unchecked((uint)BitConverter.SingleToInt32Bits(component));
            value *= fnvPrime;
        }
        foreach (var body in _bodies)
        {
            var transform = body.GlobalTransform;
            Add(ref hash, transform.Origin.X); Add(ref hash, transform.Origin.Y); Add(ref hash, transform.Origin.Z);
            Add(ref hash, transform.Basis.X.X); Add(ref hash, transform.Basis.X.Y); Add(ref hash, transform.Basis.X.Z);
            Add(ref hash, transform.Basis.Y.X); Add(ref hash, transform.Basis.Y.Y); Add(ref hash, transform.Basis.Y.Z);
            Add(ref hash, transform.Basis.Z.X); Add(ref hash, transform.Basis.Z.Y); Add(ref hash, transform.Basis.Z.Z);
            Add(ref hash, body.LinearVelocity.X); Add(ref hash, body.LinearVelocity.Y); Add(ref hash, body.LinearVelocity.Z);
            Add(ref hash, body.AngularVelocity.X); Add(ref hash, body.AngularVelocity.Y); Add(ref hash, body.AngularVelocity.Z);
        }
        return hash ^ ((ulong)_bodies.Count * prime);
    }
    private void ConfigureRenderCadence(int presentationCap)
    {
        _renderCap = presentationCap;
        // Godot's 60 Hz main-loop sleep phase-locks the single outstanding MCU
        // ACK just after its last fixed callback. Run the main loop slightly above
        // 2x the presentation cadence, keep engine physics independent, and request
        // root viewport draws on alternating loop iterations for presentation pacing.
        _presentationDecimationActive = presentationCap == 60 && !_benchmarkRun;
        Engine.MaxFps = _presentationDecimationActive ? presentationCap * 2 + 2 : presentationCap;
        if (IsInsideTree())
        {
            _presentationRequestOnNextLoop = true;
            RenderingServer.ViewportSetUpdateMode(GetViewport().GetViewportRid(),
                _presentationDecimationActive ? RenderingServer.ViewportUpdateMode.Once : RenderingServer.ViewportUpdateMode.Always);
        }
        Console.WriteLine($"PRESENTATION_CADENCE presentation_cap_fps={presentationCap} main_loop_cap_fps={Engine.MaxFps} viewport_decimation={_presentationDecimationActive}");
    }

    private void UpdatePresentationCadence()
    {
        if (!_presentationDecimationActive) return;
        var now = Stopwatch.GetTimestamp();
        var requestUpdate = _presentationRequestOnNextLoop;
        var mode = requestUpdate ? RenderingServer.ViewportUpdateMode.Once : RenderingServer.ViewportUpdateMode.Disabled;
        RenderingServer.ViewportSetUpdateMode(GetViewport().GetViewportRid(), mode);
        if (requestUpdate && _performanceTrace is not null)
            _performanceTrace.RecordViewportUpdateRequest(Stopwatch.GetTimestamp(), _presentationFrameIndex);
        _presentationRequestOnNextLoop = !_presentationRequestOnNextLoop;
        _presentationFrameIndex++;
    }

    private void SetRenderCap(int cap) => ConfigureRenderCadence(cap);
    private static StandardMaterial3D Material(Color color, bool transparent = false) => new() { AlbedoColor = color, Transparency = transparent ? BaseMaterial3D.TransparencyEnum.Alpha : BaseMaterial3D.TransparencyEnum.Disabled, Roughness = 0.82f };
    private static Vector3 ToVector(IReadOnlyList<double> values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Quaternion ToQuaternion(IReadOnlyList<double> values) => new Quaternion((float)values[0], (float)values[1], (float)values[2], (float)values[3]).Normalized();
    private static string LinkBodyKey(string robotId, string linkId) => robotId + "/" + linkId;
    private static string JointNodeKey(string robotId, string jointId) => robotId + "/" + jointId;
    private void AddLabel3D(string text, Vector3 at, Color color) => AddChild(new Label3D { Text = text, Position = at, PixelSize = 0.0025f, Modulate = color, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled });
}
