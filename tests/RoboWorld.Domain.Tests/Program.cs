using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using RoboWorld.Domain;

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var robotPath = Path.Combine(root, "examples", "robots", "rover-01.robot.json");
var componentDirectory = Path.Combine(root, "examples", "components");
var catalogue = FixtureLoader.LoadCatalogue(componentDirectory);
var graph = FixtureLoader.LoadRobot(robotPath, componentDirectory);
var armGraph = FixtureLoader.LoadRobot(Path.Combine(root, "examples", "robots", "arm-01.robot.json"), componentDirectory);
var multiControllerGraph = FixtureLoader.LoadRobot(Path.Combine(root, "examples", "robots", "rover-multi-01.robot.json"), componentDirectory);
var cameraGraph = FixtureLoader.LoadRobot(Path.Combine(root, "examples", "robots", "rover-camera-01.robot.json"), componentDirectory);
var warehouse = FixtureLoader.LoadWorld(Path.Combine(root, "examples", "worlds", "room-20x20.world.json"));
var mountedCamera = cameraGraph.Components.Single(component => component.Id == "camera-front");
Require(catalogue[mountedCamera.CatalogueId].Camera == new CameraProperties(75, 640, 480, 0.05, 100, true)
    && cameraGraph.ComponentMountConnections?.Any(connection => connection.ParentComponentId == "chassis" && connection.ChildComponentId == mountedCamera.Id) == true,
    "The camera catalogue has versioned viewport defaults and a fixed, typed mount to the rover chassis.");
var configuredCameraGraph = RobotAssemblyEditor.SetCameraProperties(
    RobotAssemblyEditor.SetComponentDisplayName(cameraGraph, catalogue, mountedCamera.Id, "Warehouse Front Camera"), catalogue,
    mountedCamera.Id, new CameraProperties(82, 800, 600, 0.08, 55, true));
Require(configuredCameraGraph.Components.Single(component => component.Id == mountedCamera.Id).Camera?.HorizontalFovDeg == 82,
    "Camera name and optical viewport settings can be configured as engine-independent component instance data.");
ExpectInvalid(() => RobotAssemblyEditor.SetCameraProperties(cameraGraph, catalogue, mountedCamera.Id,
    new CameraProperties(82, 800, 600, 55, 0.08, true)), "Invalid camera near/far clipping settings are rejected.");
Require(warehouse.SchemaVersion == "1.1" && warehouse.Objects.Any(item => item.Id == "sensor-crate" && item.BodyType == "static"
        && item.PositionM.SequenceEqual(new[] { 0d, 0.3, -3.8 }))
    && warehouse.Objects.Count(item => item.Tags?.Contains("shelf", StringComparer.Ordinal) == true) >= 12
    && warehouse.Objects.Any(item => item.Tags?.Contains("visual_only", StringComparer.Ordinal) == true),
    "The versioned warehouse manifest persists static sensor obstacles, shelving and non-colliding lane markings.");

var emptyDraft = RobotAuthoringDraft.CreateEmpty("new-robot", "New Robot");
Require(emptyDraft.Assembly is null, "A new robot draft contains no hidden component or root link.");
ExpectInvalid(() => emptyDraft.RequireAssembly(catalogue), "An empty draft cannot be converted to an assembly.");
ExpectInvalid(() => emptyDraft.AddRoot(catalogue, "rw.wheel.standard", "wheel"), "A wheel cannot become the structural root of a draft.");
var rootedDraft = emptyDraft.AddRoot(catalogue, "rw.chassis.rect", "chassis");
var mechanicallyExtendedDraft = rootedDraft.AddSnappedComponent(catalogue, "chassis", "top", "rw.beam.structural", "bottom", "beam");
Require(mechanicallyExtendedDraft.RequireAssembly(catalogue).ComponentMountConnections?.Count == 1,
    "An authoring draft reuses the existing connector mating and graph validation path.");
var draftGraph = rootedDraft.RequireAssembly(catalogue);
Require(draftGraph.Components.Count == 1 && draftGraph.Links.Single().BaseComponentId == "chassis"
    && draftGraph.Components.Single().CatalogueVersion == catalogue["rw.chassis.rect"].Version,
    "The first structural part creates an explicit root with a versioned catalogue reference.");
ExpectInvalid(() => rootedDraft.AddRoot(catalogue, "rw.beam.structural", "second"), "A draft cannot have two roots.");
var emptyReadiness = RobotAuthoringReadiness.Validate(emptyDraft, catalogue, hasFirmwareAssignment: false);
Require(emptyReadiness.Any(issue => issue.Code == "E_ROBOT_GRAPH_INVALID" && issue.Message.Contains("structural component", StringComparison.Ordinal)),
    "Launch validation rejects an empty draft with a direct corrective message.");
var incompleteReadiness = RobotAuthoringReadiness.Validate(rootedDraft, catalogue, hasFirmwareAssignment: false);
Require(incompleteReadiness.Any(issue => issue.Code == "E_CONTROLLER_MISSING")
    && incompleteReadiness.Any(issue => issue.Code == "E_FIRMWARE_MISSING"),
    "Launch validation reports missing controller and firmware while preserving the editable draft.");
var emptyDraftPath = Path.Combine(Path.GetTempPath(), "roboworld-empty-" + Guid.NewGuid().ToString("N") + ".rwdraft");
var rootedDraftPath = Path.Combine(Path.GetTempPath(), "roboworld-rooted-" + Guid.NewGuid().ToString("N") + ".rwdraft");
try
{
    RobotAuthoringDraftStore.Save(emptyDraftPath, emptyDraft, catalogue);
    Require(RobotAuthoringDraftStore.Open(emptyDraftPath, catalogue).Assembly is null,
        "An empty authoring draft saves and reopens without adding a hidden root.");
    RobotAuthoringDraftStore.Save(rootedDraftPath, rootedDraft, catalogue);
    var reopenedDraft = RobotAuthoringDraftStore.Open(rootedDraftPath, catalogue);
    Require(reopenedDraft.RequireAssembly(catalogue).Components.Single().Id == "chassis",
        "A structurally valid but not necessarily runnable draft saves and reopens with its selected root.");
    var withFreePart = reopenedDraft.AddFreeComponent(catalogue, "rw.sensor.distance", "sensor-draft");
    withFreePart = withFreePart.AddFreeComponent(catalogue, "rw.mcu.stm32f407vg", "mcu-draft")
        .AddElectricalConnection(catalogue, "mcu-draft.sensor", "sensor-draft.echo", "sensor_bus");
    RobotAuthoringDraftStore.Save(rootedDraftPath, withFreePart, catalogue);
    var reopenedWithPart = RobotAuthoringDraftStore.Open(rootedDraftPath, catalogue);
    Require(reopenedWithPart.RequireAssembly(catalogue).Components.Count == 3
        && reopenedWithPart.Assembly!.ElectricalConnections.Single().BusId == "sensor_bus",
        "A partially assembled draft saves and reopens component identities and typed electrical connections.");
    var emptyAgain = reopenedWithPart.RemoveComponent(catalogue, "sensor-draft")
        .RemoveComponent(catalogue, "mcu-draft").RemoveComponent(catalogue, "chassis");
    Require(emptyAgain.Assembly is null, "Removing a part and then the final root returns to a true empty draft.");
    var cameraDraft = RobotAuthoringDraft.FromRunnableAssembly(configuredCameraGraph, catalogue);
    RobotAuthoringDraftStore.Save(rootedDraftPath, cameraDraft, catalogue);
    var reopenedCameraDraft = RobotAuthoringDraftStore.Open(rootedDraftPath, catalogue);
    Require(reopenedCameraDraft.RequireAssembly(catalogue).Components.Single(component => component.Id == mountedCamera.Id).Camera
            == new CameraProperties(82, 800, 600, 0.08, 55, true)
        && reopenedCameraDraft.Assembly!.Components.Single(component => component.Id == mountedCamera.Id).DisplayName == "Warehouse Front Camera",
        "Draft persistence preserves camera naming and viewport settings independently from runtime readiness.");
}
finally
{
    File.Delete(emptyDraftPath);
    File.Delete(rootedDraftPath);
}

Require(graph.Joints.All(joint => Math.Abs(Math.Sqrt(joint.AxisLocal.Sum(value => value * value)) - 1d) < 1e-9), "Loader normalizes every joint axis.");
Require(armGraph.Links.Count == 2 && armGraph.Joints.Single().Type == "revolute", "A second arm shape loads from the shared catalogue.");
Require(RobotElectricalRouter.ResolveMotorActuator(armGraph, catalogue, "arm-mcu", 1) == "arm-motor"
    && RobotElectricalRouter.ResolveMotorActuator(multiControllerGraph, catalogue, "mcu-board-aux", 1) == "motor-right",
    "Graph routing preserves existing one-channel arm and cross-wired auxiliary-controller motor assignments.");
Require(RobotGraphValidator.CanMate(catalogue["rw.chassis.rect"].MountPorts.Single(port => port.Id == "left"), catalogue["rw.wheel.standard"].MountPorts.Single(port => port.Id == "hub")), "Matching shaft mount ports mate.");
var snappedGraph = RobotAssemblyEditor.AddSnappedComponent(graph, catalogue, "chassis", "top", "rw.beam.structural", "bottom", "shop-beam");
var snappedBeam = snappedGraph.Components.Single(component => component.Id == "shop-beam");
Require(Math.Abs(snappedBeam.PositionM[1] - 0.25) < 1e-6 && snappedGraph.ComponentMountConnections?.Single().ChildPortId == "bottom",
    "Shop graph editor positions a beam so its bottom frame coincides with the chassis top port and records the connection.");
var sizedGraph = RobotAssemblyEditor.SetComponentSize(snappedGraph, catalogue, "shop-beam", [0.06, 0.6, 0.06]);
var sizedBeam = sizedGraph.Components.Single(component => component.Id == "shop-beam");
Require(Math.Abs(sizedBeam.SizeOverrideM![1] - 0.6) < 1e-9 && Math.Abs(sizedBeam.PositionM[1] - 0.35) < 1e-6,
    "Shop graph editor stores positive SI dimension overrides and keeps the resized connector snapped.");
var snappedBeamJointGraph = RobotAssemblyEditor.AddJointedComponent(sizedGraph, catalogue, "shop-beam", "top", "rw.beam.structural", "bottom",
    "shop-beam-joint-child", "revolute", "rw.mount.revolute", "shop-beam-joint");
Require(snappedBeamJointGraph.Joints.Any(joint => joint.Id == "shop-beam-joint"),
    "Shop graph editor accepts a revolute child on the resized free end of a rigidly snapped beam.");
var resizedSnappedBeamJointGraph = RobotAssemblyEditor.SetComponentSize(snappedBeamJointGraph, catalogue, "shop-beam-joint-child", [0.06, 0.6, 0.06]);
var removedAssembly = RobotAssemblyEditor.RemoveComponent(resizedSnappedBeamJointGraph, catalogue, "shop-beam");
Require(!removedAssembly.Components.Any(component => component.Id is "shop-beam" or "shop-beam-joint-child")
    && removedAssembly.Links.Count == graph.Links.Count && removedAssembly.Joints.Count == graph.Joints.Count
    && removedAssembly.ComponentMountConnections?.Count == 0,
    "Removing a rigid-mounted component also removes its dependent jointed subtree and connector records.");
ExpectInvalid(() => RobotAssemblyEditor.RemoveComponent(graph, catalogue, "chassis"),
    "The shop graph editor prevents deleting the root-link base component.");
var sensorRemovedGraph = RobotAssemblyEditor.RemoveComponent(graph, catalogue, "distance");
Require(!sensorRemovedGraph.Components.Any(component => component.Id == "distance")
    && sensorRemovedGraph.ElectricalConnections.All(connection => !connection.From.StartsWith("distance.", StringComparison.Ordinal)
        && !connection.To.StartsWith("distance.", StringComparison.Ordinal)),
    "Removing a component also removes its dangling electrical connections.");
var secondMcuGraph = RobotAssemblyEditor.AddSnappedComponent(graph, catalogue, "chassis", "top", "rw.mcu.stm32f407vg", "mount", "mcu-secondary");
var freePartGraph = RobotAssemblyEditor.AddFreeComponent(graph, catalogue, "rw.sensor.distance", "sensor-free", [0.4, 0.3, -0.2], [0, 0.3826834323650898, 0, 0.9238795325112867]);
var movedFreePartGraph = RobotAssemblyEditor.SetComponentPose(freePartGraph, catalogue, "sensor-free", [0.75, 0.41, -0.5], [0, 0.7071067811865475, 0, 0.7071067811865475]);
var movedFreePart = movedFreePartGraph.Components.Single(component => component.Id == "sensor-free");
Require(movedFreePart.LinkId == graph.RootLinkId && movedFreePart.PositionM.SequenceEqual([0.75, 0.41, -0.5])
    && Math.Abs(Math.Sqrt(movedFreePart.RotationXyzw.Sum(value => value * value)) - 1) < 1e-9,
    "Shop graph editor adds a free catalogue component and edits its SI position and normalized xyzw rotation.");
ExpectInvalid(() => RobotAssemblyEditor.SetComponentPose(graph, catalogue, "chassis", [1, 1, 1], [0, 0, 0, 1]),
    "The shop graph editor prevents repositioning the root-link base component.");
ExpectInvalid(() => RobotAssemblyEditor.SetComponentPose(snappedGraph, catalogue, "shop-beam", [1, 1, 1], [0, 0, 0, 1]),
    "The shop graph editor prevents repositioning a connector-constrained component.");
ExpectInvalid(() => RobotAssemblyEditor.SetComponentPose(graph, catalogue, "wheel-left", [1, 1, 1], [0, 0, 0, 1]),
    "The shop graph editor prevents repositioning a joint-constrained component.");
ExpectInvalid(() => RobotAssemblyEditor.AddFreeComponent(graph, catalogue, "rw.sensor.distance", "sensor-bad-pose", [0, double.NaN, 0], [0, 0, 0, 1]),
    "The shop graph editor rejects non-finite free-part poses.");
var wiredGraph = RobotAssemblyEditor.AddElectricalConnection(secondMcuGraph, catalogue, "mcu-board.uart", "mcu-secondary.uart", "uart_internal", "wire-secondary-uart");
Require(wiredGraph.ElectricalConnections.Single(connection => connection.Id == "wire-secondary-uart") is
    { Interface: "uart", BusId: "uart_internal", PhysicalPin: null }, "Shop graph editor creates a validated typed electrical connection and bus identifier.");
var unwiredGraph = RobotAssemblyEditor.RemoveElectricalConnection(wiredGraph, catalogue, "wire-secondary-uart");
Require(unwiredGraph.ElectricalConnections.Count == secondMcuGraph.ElectricalConnections.Count,
    "Shop graph editor removes an electrical connection without changing other wires.");
ExpectInvalid(() => RobotAssemblyEditor.AddElectricalConnection(graph, catalogue, "mcu-board.pwm1", "driver.dir_a"),
    "Shop graph editor rejects incompatible electrical interfaces.");
ExpectInvalid(() => RobotAssemblyEditor.AddElectricalConnection(secondMcuGraph, catalogue, "mcu-board.uart", "mcu-secondary.uart", "bad bus id"),
    "Shop graph editor rejects malformed electrical bus identifiers.");
ExpectInvalid(() => RobotAssemblyEditor.AddSnappedComponent(graph, catalogue, "chassis", "left", "rw.beam.structural", "bottom", "bad-snap"),
    "Shop graph editor rejects incompatible shaft/structural connector mating.");
ExpectInvalid(() => RobotAssemblyEditor.AddSnappedComponent(snappedGraph, catalogue, "chassis", "top", "rw.beam.structural", "bottom", "duplicate-port-snap"),
    "Shop graph editor prevents reusing an occupied connector port.");
var articulatedGraph = RobotAssemblyEditor.AddJointedComponent(graph, catalogue, "chassis", "top", "rw.beam.structural", "bottom",
    "shop-arm-beam", "revolute", "rw.mount.revolute", "shop-arm-shoulder");
Require(articulatedGraph.Links.Count == graph.Links.Count + 1 && articulatedGraph.Joints.Count == graph.Joints.Count + 1
    && articulatedGraph.Joints.Single(joint => joint.Id == "shop-arm-shoulder").ChildLinkId == "shop-arm-beam-link",
    "Shop graph editor adds a snapped structural component as a new revolute link.");
var fixedGraph = RobotAssemblyEditor.AddJointedComponent(graph, catalogue, "chassis", "top", "rw.beam.structural", "bottom",
    "shop-fixed-beam", "fixed", "rw.mount.fixed", "shop-fixed-joint");
Require(fixedGraph.Joints.Single(joint => joint.Id == "shop-fixed-joint").Type == "fixed",
    "Shop graph editor adds a snapped structural component as a new fixed link.");
var primitiveDraft = RobotAuthoringDraft.CreateEmpty("primitive-rover", "Primitive Rover")
    .AddRoot(catalogue, "rw.primitive.box", "primitive-chassis");
var primitiveAssembly = RobotAssemblyEditor.SetComponentSize(primitiveDraft.RequireAssembly(catalogue), catalogue,
    "primitive-chassis", [0.4, 0.05, 0.25]);
primitiveAssembly = RobotAssemblyEditor.AddFreeComponent(primitiveAssembly, catalogue, "rw.primitive.cylinder", "wheel-left", [-0.3, 0.06, 0], [0, 0, 0, 1]);
primitiveAssembly = RobotAssemblyEditor.AddFreeComponent(primitiveAssembly, catalogue, "rw.primitive.cylinder", "wheel-right", [0.3, 0.06, 0], [0, 0, 0, 1]);
primitiveAssembly = RobotAssemblyEditor.SetComponentSize(primitiveAssembly, catalogue, "wheel-left", [0.12, 0.03, 0.12]);
primitiveAssembly = RobotAssemblyEditor.SetComponentSize(primitiveAssembly, catalogue, "wheel-right", [0.12, 0.03, 0.12]);
primitiveAssembly = RobotAssemblyEditor.AttachExistingComponent(primitiveAssembly, catalogue, "primitive-chassis", "face_left",
    "wheel-left", "end_plus", "revolute", "rw.mount.revolute", "joint-wheel-left");
primitiveAssembly = RobotAssemblyEditor.AttachExistingComponent(primitiveAssembly, catalogue, "primitive-chassis", "face_right",
    "wheel-right", "end_plus", "revolute", "rw.mount.revolute", "joint-wheel-right");
Require(primitiveAssembly.Components.Count == 3 && primitiveAssembly.Links.Count == 3 && primitiveAssembly.Joints.Count == 2
    && primitiveAssembly.Joints.All(joint => joint.Type == "revolute"
        && (joint.ParentPortId is "face_left" or "face_right") && joint.ChildPortId == "end_plus")
    && primitiveAssembly.Joints.All(joint => Math.Abs(Math.Abs(joint.AxisLocal[0]) - 1) < 1e-5
        && Math.Abs(joint.AxisLocal[1]) < 1e-5 && Math.Abs(joint.AxisLocal[2]) < 1e-5)
    && primitiveAssembly.Components.Single(component => component.Id == "wheel-left").SizeOverrideM!.SequenceEqual([0.12, 0.03, 0.12]),
    "A 400×250×50 mm box accepts two identity-preserving, resized cylindrical wheels on separate revolute ports.");
var directlySnappedCylinder = RobotAssemblyEditor.AddJointedComponent(primitiveDraft.RequireAssembly(catalogue), catalogue,
    "primitive-chassis", "face_left", "rw.primitive.cylinder", "end_plus", "wheel-direct", "revolute", "rw.mount.revolute", "joint-wheel-direct");
Require(Math.Abs(Math.Abs(directlySnappedCylinder.Joints.Single().AxisLocal[0]) - 1) < 1e-5
    && Math.Abs(directlySnappedCylinder.Joints.Single().AxisLocal[1]) < 1e-5
    && Math.Abs(directlySnappedCylinder.Joints.Single().AxisLocal[2]) < 1e-5,
    "A newly snapped cylinder derives its revolute axis from the mounted spindle orientation.");
ExpectInvalid(() => RobotAssemblyEditor.AttachExistingComponent(primitiveAssembly, catalogue, "primitive-chassis", "face_right",
    "wheel-left", "end_plus", "revolute", "rw.mount.revolute", "joint-wheel-occupied"),
    "Assembly attachment rejects occupied ports without mutating the accepted graph.");
var detachedPrimitive = RobotAssemblyEditor.DetachComponent(primitiveAssembly, catalogue, "wheel-left");
var detachedWheel = detachedPrimitive.Components.Single(component => component.Id == "wheel-left");
var attachedWheelLink = primitiveAssembly.Links.Single(link => link.Id == "wheel-left-link");
Require(detachedPrimitive.Joints.Count == 1 && detachedWheel.LinkId == detachedPrimitive.RootLinkId
    && detachedWheel.PositionM.SequenceEqual(attachedWheelLink.PositionM) && detachedWheel.RotationXyzw.SequenceEqual(attachedWheelLink.RotationXyzw),
    "A leaf wheel can disconnect while remaining editable at its prior world pose.");
var reattachedPrimitive = RobotAssemblyEditor.AttachExistingComponent(detachedPrimitive, catalogue, "primitive-chassis", "face_left",
    "wheel-left", "end_plus", "revolute", "rw.mount.revolute", "joint-wheel-left-reconnected");
var movedPrimitive = RobotAssemblyEditor.SetRootPose(reattachedPrimitive, catalogue, [1, 2, 3], [0, 0, 0, 1]);
RobotGraphValidator.Validate(movedPrimitive, catalogue);
Require(movedPrimitive.Joints.Count == 2 && movedPrimitive.Links.Single(link => link.Id == "wheel-right-link").PositionM[0] > 1
    && movedPrimitive.Links.Single(link => link.Id == "wheel-left-link").PositionM[0] < 1,
    "Moving the chassis transforms both attached wheels while preserving valid joint frames.");
var primitiveDraftPath = Path.Combine(Path.GetTempPath(), "roboworld-rs01b-" + Guid.NewGuid().ToString("N") + ".rwdraft");
try
{
    RobotAuthoringDraftStore.Save(primitiveDraftPath, primitiveDraft with { Assembly = movedPrimitive }, catalogue);
    var reopenedPrimitive = RobotAuthoringDraftStore.Open(primitiveDraftPath, catalogue).RequireAssembly(catalogue);
    Require(reopenedPrimitive.Joints.Count == 2 && reopenedPrimitive.Joints.All(joint => joint.Type == "revolute"
            && (joint.ParentPortId is "face_left" or "face_right") && Math.Abs(Math.Abs(joint.AxisLocal[0]) - 1) < 1e-5)
        && reopenedPrimitive.Components.Single(component => component.Id == "wheel-right").SizeOverrideM!.SequenceEqual([0.12, 0.03, 0.12]),
        "The box-and-two-cylinder draft reopens with dimensions, identities and revolute joints intact.");
}
finally { File.Delete(primitiveDraftPath); }
var resizedArticulatedGraph = RobotAssemblyEditor.SetComponentSize(articulatedGraph, catalogue, "shop-arm-beam", [0.06, 0.6, 0.06]);
var resizedArticulatedBeam = resizedArticulatedGraph.Components.Single(component => component.Id == "shop-arm-beam");
Require(Math.Abs(resizedArticulatedBeam.SizeOverrideM![1] - 0.6) < 1e-9
    && Math.Abs(resizedArticulatedGraph.Joints.Single(joint => joint.Id == "shop-arm-shoulder").ChildFramePositionM[1] + 0.3) < 1e-9,
    "Resizing a revolute child link keeps the selected connector coincident with its parent joint.");
Require(!RobotGraphValidator.CanMate(
    new MountPortDefinition("a", "shaft", "M", [], [0, 0, 0], [0, 0, 0, 1]),
    new MountPortDefinition("b", "shaft", "L", [], [0, 0, 0], [0, 0, 0, 1])), "Different mount size classes do not mate.");
var fastMotor = catalogue["rw.motor.geared"];
var slowMotor = catalogue["rw.motor.geared.slow"];
var fastPoint = DcMotorModel.Evaluate(0.5, 0, fastMotor.StallTorqueNm!.Value, fastMotor.NoLoadRpm!.Value);
var slowPoint = DcMotorModel.Evaluate(0.5, 0, slowMotor.StallTorqueNm!.Value, slowMotor.NoLoadRpm!.Value);
Require(Math.Abs(fastPoint.TargetSpeedRadPerSecond - slowPoint.TargetSpeedRadPerSecond) > 1, "Different catalogue motor speeds produce different model targets.");
Require(fastPoint.TorqueNm != slowPoint.TorqueNm, "Different catalogue motor torque specifications produce different torque.");

var fixtureFirmwarePath = Path.Combine(root, "samples", "firmware", "stm32-drive", "build", "firmware.elf");
var packageSource = ProjectPackageStore.OpenDirectory(Path.Combine(root, "examples"), fixtureFirmwarePath);
var firmwareArtifacts = RobotFirmwareCatalog.FromProject(packageSource).ToDictionary(item => item.Id, StringComparer.Ordinal);
var authoredBuild = RobotAuthoringDraft.CreateEmpty("rs01c-domain-rover", "RS-01C Domain Rover")
    .AddRoot(catalogue, "rw.chassis.rect", "authored-chassis")
    .AddSnappedComponent(catalogue, "authored-chassis", "left", "rw.wheel.standard", "hub", "authored-wheel-left", "revolute")
    .AddSnappedComponent(catalogue, "authored-chassis", "right", "rw.wheel.standard", "hub", "authored-wheel-right", "revolute")
    .AddFreeComponent(catalogue, "rw.motor.geared", "authored-motor-left")
    .AddFreeComponent(catalogue, "rw.motor.geared", "authored-motor-right");
authoredBuild = authoredBuild.SetJointActuator(catalogue, "joint-authored-wheel-left", "authored-motor-left")
    .SetJointActuator(catalogue, "joint-authored-wheel-right", "authored-motor-right");
Require(authoredBuild.RequireAssembly(catalogue).Joints.All(joint => joint.ActuatorComponentId is not null),
    "A newly assembled chassis binds both authored wheel motors through explicit joint actuator identities.");
ExpectInvalid(() => authoredBuild.SetJointActuator(catalogue, "joint-authored-wheel-left", "authored-wheel-left"),
    "Only a catalogue motor can drive a wheel joint.");
var authoredDraftPath = Path.Combine(Path.GetTempPath(), "roboworld-rs01c-" + Guid.NewGuid().ToString("N") + ".rwdraft");
RobotAuthoringDraft? wired = null;
try
{
    var controllerAndElectronics = authoredBuild.AddFreeComponent(catalogue, "rw.driver.dual", "authored-driver")
        .AddFreeComponent(catalogue, "rw.mcu.stm32f407vg", "authored-controller")
        .AddFreeComponent(catalogue, "rw.sensor.distance", "authored-distance")
        .AddFreeComponent(catalogue, "rw.battery.pack12", "authored-battery");
    var artifact = firmwareArtifacts["stm32-drive"];
    controllerAndElectronics = controllerAndElectronics.AssignFirmware(catalogue, new RobotFirmwareAssignment(
        "authored-controller-firmware", "authored-controller", artifact.Id, artifact.Board, artifact.Target, artifact.RelativePath, artifact.Sha256));
    wired = AddTestRoverWiring(controllerAndElectronics, catalogue);
    var authoredRoutedGraph = wired.RequireAssembly(catalogue);
    Require(RobotElectricalRouter.ResolveMotorActuator(authoredRoutedGraph, catalogue, "authored-controller", 1) == "authored-motor-left"
        && RobotElectricalRouter.ResolveMotorActuator(authoredRoutedGraph, catalogue, "authored-controller", 2) == "authored-motor-right"
        && RobotElectricalRouter.HasDistanceSensorRoute(authoredRoutedGraph, catalogue, "authored-controller"),
        "Controller routes resolve through authored PWM, direction, dual-driver, motor, joint and sensor graph connections.");
    Require(RobotAuthoringReadiness.Validate(wired, catalogue, firmwareArtifacts).Count == 0,
        "A fully wired authored rover passes launch readiness with its registered firmware artifact.");
    var noFirmware = wired with { FirmwareAssignments = [] };
    Require(RobotAuthoringReadiness.Validate(noFirmware, catalogue, firmwareArtifacts).Any(issue => issue.Code == "E_FIRMWARE_MISSING"),
        "A graph without explicit firmware assignment is rejected with an actionable launch issue.");
    var wrongFirmwareIdentity = wired with { FirmwareAssignments = wired.FirmwareAssignments!.Select(item => item with { FirmwareSha256 = new string('0', 64) }).ToArray() };
    Require(RobotAuthoringReadiness.Validate(wrongFirmwareIdentity, catalogue, firmwareArtifacts).Any(issue => issue.Code == "E_FIRMWARE_IDENTITY"),
        "A firmware artifact checksum mismatch blocks launch without altering the draft graph.");
    RobotAuthoringDraftStore.Save(authoredDraftPath, wired, catalogue);
    var reopenedAuthoredDraft = RobotAuthoringDraftStore.Open(authoredDraftPath, catalogue);
    Require(reopenedAuthoredDraft.FirmwareAssignments?.Single().ControllerComponentId == "authored-controller"
        && reopenedAuthoredDraft.RequireAssembly(catalogue).Joints.Single(joint => joint.Id == "joint-authored-wheel-left").ActuatorComponentId == "authored-motor-left",
        "Draft persistence preserves explicit controller firmware identity and wheel actuator binding.");
    var brokenRoute = wired with { Assembly = RobotAssemblyEditor.RemoveElectricalConnection(authoredRoutedGraph, catalogue,
        authoredRoutedGraph.ElectricalConnections.Single(connection => connection.From == "authored-controller.dir1").Id) };
    Require(RobotElectricalRouter.ResolveMotorActuator(brokenRoute.RequireAssembly(catalogue), catalogue, "authored-controller", 1) is null
        && RobotAuthoringReadiness.Validate(brokenRoute, catalogue, firmwareArtifacts).Any(issue => issue.Code == "E_MOTOR_CONTROL_ROUTE" && issue.Message.Contains("authored-motor-left", StringComparison.Ordinal)),
        "Disconnecting one direction route blocks launch and identifies the affected motor without fallback mapping.");
    Require(RobotElectricalRouter.ResolveMotorActuator(wired.RequireAssembly(catalogue), catalogue, "authored-controller", 3) is null,
        "Unsupported controller channels resolve to no actuator instead of falling back to another graph route.");
}
finally { File.Delete(authoredDraftPath); }
var packagePath = Path.Combine(Path.GetTempPath(), "roboworld-assembly-" + Guid.NewGuid().ToString("N") + ".roboworld");
var snappedPackagePath = Path.Combine(Path.GetTempPath(), "roboworld-snapped-" + Guid.NewGuid().ToString("N") + ".roboworld");
var graphEditPackagePath = Path.Combine(Path.GetTempPath(), "roboworld-graph-edit-" + Guid.NewGuid().ToString("N") + ".roboworld");
var electricalPackagePath = Path.Combine(Path.GetTempPath(), "roboworld-electrical-edit-" + Guid.NewGuid().ToString("N") + ".roboworld");
var freePartPackagePath = Path.Combine(Path.GetTempPath(), "roboworld-free-part-" + Guid.NewGuid().ToString("N") + ".roboworld");
var controllerSettingsPackagePath = Path.Combine(Path.GetTempPath(), "roboworld-controller-settings-" + Guid.NewGuid().ToString("N") + ".roboworld");
var authoredPackagePath = Path.Combine(Path.GetTempPath(), "roboworld-authored-" + Guid.NewGuid().ToString("N") + ".roboworld");
ProjectPackageInfo? reopenedPackage = null;
ProjectPackageInfo? reopenedSnappedPackage = null;
ProjectPackageInfo? reopenedGraphEditPackage = null;
ProjectPackageInfo? reopenedElectricalPackage = null;
ProjectPackageInfo? reopenedFreePartPackage = null;
ProjectPackageInfo? reopenedControllerSettingsPackage = null;
ProjectPackageInfo? reopenedAuthoredPackage = null;
try
{
    ProjectPackageStore.SavePackage(packageSource, packagePath);
    reopenedPackage = ProjectPackageStore.OpenPackage(packagePath);
    Require(reopenedPackage.RobotPaths.Count == 5, "Design package preserves all five robot graph references including the camera rover.");
    var reopenedWarehouse = FixtureLoader.LoadWorld(reopenedPackage.WorldPath);
    Require(reopenedWarehouse.SchemaVersion == "1.1" && reopenedWarehouse.Objects.Count == warehouse.Objects.Count
        && reopenedWarehouse.Objects.Single(item => item.Id == "sensor-crate").AssetRef == "rw.world.crate.blue",
        "A saved/reopened project package preserves the versioned warehouse scenery and sensor target asset identity.");
    ProjectPackageStore.DeleteTemporaryProject(reopenedPackage);
    reopenedPackage = null;
    ProjectPackageStore.SavePackage(packageSource, packagePath, configuredCameraGraph);
    reopenedPackage = ProjectPackageStore.OpenPackage(packagePath);
    var cameraRoundTrip = FixtureLoader.LoadRobot(Path.Combine(reopenedPackage.RootDirectory, "robots", "rover-camera-01.robot.json"), reopenedPackage.ComponentDirectory);
    var roundTrippedCamera = cameraRoundTrip.Components.Single(component => component.Id == "camera-front");
    Require(roundTrippedCamera.DisplayName == "Warehouse Front Camera" && roundTrippedCamera.Camera == new CameraProperties(82, 800, 600, 0.08, 55, true)
        && cameraRoundTrip.ComponentMountConnections?.Any(connection => connection.ChildComponentId == "camera-front") == true,
        "A saved/reopened .roboworld project preserves camera identity, settings and fixed mechanical mounting.");
    var fullAuthoredDraft = wired!;
    ProjectPackageStore.SaveAuthoringPackage(packageSource, authoredPackagePath, fullAuthoredDraft, catalogue, firmwareArtifacts);
    reopenedAuthoredPackage = ProjectPackageStore.OpenPackage(authoredPackagePath);
    var packagedAuthoredGraph = FixtureLoader.LoadRobot(Path.Combine(reopenedAuthoredPackage.RootDirectory, "robots", "rs01c-domain-rover.robot.json"), reopenedAuthoredPackage.ComponentDirectory);
    Require(packagedAuthoredGraph.Id == "rs01c-domain-rover" && reopenedAuthoredPackage.RobotPaths.Count == packageSource.RobotPaths.Count + 1
        && reopenedAuthoredPackage.ControllersByRobotId[packagedAuthoredGraph.Id].Single().ComponentInstanceId == "authored-controller"
        && reopenedAuthoredPackage.ControllersByRobotId[packagedAuthoredGraph.Id].Single().FirmwareRelativePath == fullAuthoredDraft.FirmwareAssignments!.Single().RelativePath
        && reopenedAuthoredPackage.ControllerExecutionSettings.Mode == "exact" && reopenedAuthoredPackage.ControllerExecutionSettings.ProfileReferences.Count == 0,
        "An authored .roboworld package reopens its own graph and explicit controller firmware in Exact mode without inheriting template assignments.");
    var savedControllerSettings = new ProjectControllerExecutionSettings("auto",
        new Dictionary<string, ProjectControllerProfileReference>(StringComparer.Ordinal)
        {
            ["rover-01/mcu-drive"] = new ProjectControllerProfileReference("rover-profile-v1", new string('A', 64))
        });
    ProjectPackageStore.SavePackage(packageSource, controllerSettingsPackagePath, executionSettingsOverride: savedControllerSettings);
    reopenedControllerSettingsPackage = ProjectPackageStore.OpenPackage(controllerSettingsPackagePath);
    Require(reopenedControllerSettingsPackage.ControllerExecutionSettings.Mode == "auto"
        && reopenedControllerSettingsPackage.ControllerExecutionSettings.ProfileReferences.TryGetValue("rover-01/mcu-drive", out var savedProfileReference)
        && savedProfileReference.ProfileId == "rover-profile-v1" && savedProfileReference.ProfileSha256 == new string('a', 64),
        "Project save/reopen preserves controller mode and immutable profile reference identity without runtime state.");
    ExpectInvalid(() => ProjectPackageStore.SavePackage(packageSource, controllerSettingsPackagePath,
            executionSettingsOverride: new ProjectControllerExecutionSettings("auto",
                new Dictionary<string, ProjectControllerProfileReference>(StringComparer.Ordinal)
                {
                    ["unknown-robot/unknown-mcu"] = new ProjectControllerProfileReference("missing-profile", new string('1', 64))
                })),
        "Project packages reject profile references that do not identify a controller assignment.");
    ExpectInvalid(() => ProjectPackageStore.SavePackage(packageSource, controllerSettingsPackagePath,
            executionSettingsOverride: new ProjectControllerExecutionSettings("auto",
                new Dictionary<string, ProjectControllerProfileReference>(StringComparer.Ordinal)
                {
                    ["rover-01/mcu-drive"] = new ProjectControllerProfileReference("rover-profile-v1", "not-a-sha256")
                })),
        "Project packages reject malformed controller profile content hashes.");
    foreach (var robotRelativePath in new[] { "robots/rover-01.robot.json", "robots/arm-01.robot.json", "robots/rover-slow-01.robot.json", "robots/rover-multi-01.robot.json" })
    {
        var sourceJson = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "examples", robotRelativePath)));
        var reopenedJson = JsonNode.Parse(File.ReadAllText(Path.Combine(reopenedPackage.RootDirectory, robotRelativePath)));
        Require(JsonNode.DeepEquals(sourceJson, reopenedJson), $"Save/reload preserves every transform, mechanical joint and electrical connection in {Path.GetFileName(robotRelativePath)}.");
    }
    ProjectPackageStore.SavePackage(packageSource, snappedPackagePath, sizedGraph);
    reopenedSnappedPackage = ProjectPackageStore.OpenPackage(snappedPackagePath);
    var reopenedSnappedGraph = FixtureLoader.LoadRobot(reopenedSnappedPackage.RobotPath, reopenedSnappedPackage.ComponentDirectory);
    var reopenedBeam = reopenedSnappedGraph.Components.Single(component => component.Id == "shop-beam");
    Require(reopenedBeam.SizeOverrideM is { Length: 3 } && Math.Abs(reopenedBeam.SizeOverrideM[1] - 0.6) < 1e-9 && reopenedSnappedGraph.ComponentMountConnections?.Single().ParentPortId == "top",
        "Saved .roboworld package preserves a user-added snapped component, its editable SI dimensions and connector endpoints.");

    ProjectPackageStore.SavePackage(packageSource, graphEditPackagePath, resizedSnappedBeamJointGraph);
    reopenedGraphEditPackage = ProjectPackageStore.OpenPackage(graphEditPackagePath);
    var reopenedArticulatedGraph = FixtureLoader.LoadRobot(reopenedGraphEditPackage.RobotPath, reopenedGraphEditPackage.ComponentDirectory);
    Require(reopenedArticulatedGraph.Links.Any(link => link.Id == "shop-beam-joint-child-link")
        && reopenedArticulatedGraph.Joints.Any(joint => joint.Id == "shop-beam-joint" && joint.ParentComponentId == "shop-beam" && joint.ChildComponentId == "shop-beam-joint-child"
            && joint.ChildPortId == "bottom" && Math.Abs(joint.ChildFramePositionM[1] + 0.3) < 1e-9)
        && Math.Abs(reopenedArticulatedGraph.Components.Single(component => component.Id == "shop-beam-joint-child").SizeOverrideM![1] - 0.6) < 1e-9,
        "Saved .roboworld package preserves an assembled, resized child link and its revolute joint.");

    ProjectPackageStore.SavePackage(packageSource, electricalPackagePath, wiredGraph);
    reopenedElectricalPackage = ProjectPackageStore.OpenPackage(electricalPackagePath);
    var reopenedElectricalGraph = FixtureLoader.LoadRobot(reopenedElectricalPackage.RobotPath, reopenedElectricalPackage.ComponentDirectory);
    Require(reopenedElectricalGraph.Components.Any(component => component.Id == "mcu-secondary")
        && reopenedElectricalGraph.ElectricalConnections.Any(connection => connection.Id == "wire-secondary-uart"
            && connection.From == "mcu-board.uart" && connection.To == "mcu-secondary.uart" && connection.BusId == "uart_internal"),
        "Saved .roboworld package preserves added typed electrical connections and their endpoint component instances.");

    ProjectPackageStore.SavePackage(packageSource, freePartPackagePath, movedFreePartGraph);
    reopenedFreePartPackage = ProjectPackageStore.OpenPackage(freePartPackagePath);
    var reopenedFreePartGraph = FixtureLoader.LoadRobot(reopenedFreePartPackage.RobotPath, reopenedFreePartPackage.ComponentDirectory);
    var reopenedFreePart = reopenedFreePartGraph.Components.Single(component => component.Id == "sensor-free");
    Require(reopenedFreePart.PositionM.SequenceEqual(movedFreePart.PositionM) && reopenedFreePart.RotationXyzw.SequenceEqual(movedFreePart.RotationXyzw),
        "Saved .roboworld package preserves a free component's edited position and quaternion rotation.");
}
finally
{
    if (reopenedPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedPackage);
    if (reopenedSnappedPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedSnappedPackage);
    if (reopenedGraphEditPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedGraphEditPackage);
    if (reopenedElectricalPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedElectricalPackage);
    if (reopenedFreePartPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedFreePartPackage);
    if (reopenedControllerSettingsPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedControllerSettingsPackage);
    if (reopenedAuthoredPackage is not null) ProjectPackageStore.DeleteTemporaryProject(reopenedAuthoredPackage);
    if (File.Exists(authoredPackagePath)) File.Delete(authoredPackagePath);
    if (File.Exists(packagePath)) File.Delete(packagePath);
    if (File.Exists(snappedPackagePath)) File.Delete(snappedPackagePath);
    if (File.Exists(graphEditPackagePath)) File.Delete(graphEditPackagePath);
    if (File.Exists(electricalPackagePath)) File.Delete(electricalPackagePath);
    if (File.Exists(freePartPackagePath)) File.Delete(freePartPackagePath);
    if (File.Exists(controllerSettingsPackagePath)) File.Delete(controllerSettingsPackagePath);
}

ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    Joints = [graph.Joints[0] with { ParentLinkId = "wheel-right-link" }, graph.Joints[1] with { ParentLinkId = "wheel-left-link" }]
}, catalogue), "Mechanical cycles/disconnected root paths are rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    Joints = [graph.Joints[0] with { ParentPortId = "right" }, graph.Joints[1]]
}, catalogue), "Mechanical connector ports cannot be occupied by multiple joints.");
ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    Joints = [graph.Joints[0] with { ChildPortId = "missing-hub" }, graph.Joints[1]]
}, catalogue), "Missing mechanical connector ports are rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(armGraph with
{
    Joints = [armGraph.Joints[0] with { ChildFramePositionM = [0, -0.15, 0] }]
}, catalogue), "Joint connector positions that do not align with catalogue frames are rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(armGraph with
{
    Joints = [armGraph.Joints[0] with { JointProfileVersion = "9.9.9" }]
}, catalogue), "Stale versioned joint profiles are rejected.");
var badDimensionsCatalogue = catalogue.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
badDimensionsCatalogue["rw.beam.structural"] = badDimensionsCatalogue["rw.beam.structural"] with { SizeM = [0.06, -0.4, 0.06] };
ExpectInvalid(() => RobotGraphValidator.Validate(armGraph, badDimensionsCatalogue), "Negative component dimensions violate SI size constraints.");
ExpectInvalid(() => RobotAssemblyEditor.SetComponentSize(snappedGraph, catalogue, "shop-beam", [0.06, 0, 0.06]), "Zero-sized component overrides are rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    ElectricalConnections = [graph.ElectricalConnections[0] with { Interface = "uart" }, .. graph.ElectricalConnections.Skip(1)]
}, catalogue), "Electrical interface mismatches are rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    ElectricalConnections = [graph.ElectricalConnections[0] with { PhysicalPin = "PA7" }, .. graph.ElectricalConnections.Skip(1)]
}, catalogue), "Electrical wiring to the wrong physical MCU pin is rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    ElectricalConnections = [graph.ElectricalConnections[0] with { BusId = "bad/bus" }, .. graph.ElectricalConnections.Skip(1)]
}, catalogue), "Malformed electrical bus identifiers are rejected.");
ExpectInvalid(() => RobotGraphValidator.Validate(graph with
{
    ElectricalConnections = [graph.ElectricalConnections[0] with { From = "mcu-board.unwired" }, .. graph.ElectricalConnections.Skip(1)]
}, catalogue), "Unknown electrical ports are rejected.");

var normalizedPath = WriteTemporaryRobot(robotPath, node => node["joints"]![0]!["axis_local"] = new JsonArray(3, 0, 0));
try
{
    var normalized = FixtureLoader.LoadRobot(normalizedPath, componentDirectory);
    Require(normalized.Joints[0].AxisLocal.SequenceEqual(new[] { 1d, 0d, 0d }), "Non-unit joint axis is normalized on load.");
}
finally { File.Delete(normalizedPath); }

var stalePath = WriteTemporaryRobot(robotPath, node => node["components"]![0]!["catalogue_version"] = "9.9.9");
try { ExpectInvalid(() => FixtureLoader.LoadRobot(stalePath, componentDirectory), "Stale component versions are rejected."); }
finally { File.Delete(stalePath); }

Console.WriteLine("PASS: normalized axes, rooted acyclic graph, mount mating, electrical endpoint/interface and version validation.");

var controllerIdentity = new ControllerProfileIdentity(1, new string('a', 64), "stm32f407vg", "rw.board.stm32f407vg", "1.0.0",
    new string('b', 64), new string('c', 64), new string('d', 64), new string('e', 64), "1", "1.0.0", "1", "1.0.0", new string('f', 64));
var generatedCandidateProfilePath = Path.Combine(root, "tests", "fixtures", "controller-profiles", "fault-candidate", "profile.json");
var generatedCandidateProfile = JsonSerializer.Deserialize<DeterministicControllerProfile>(File.ReadAllBytes(generatedCandidateProfilePath),
    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Generated controller candidate profile is missing.");
var generatedCandidateModelPath = Path.Combine(root, "tests", "fixtures", "controller-profiles", "fault-candidate", "model.json");
var generatedCandidateModel = JsonSerializer.Deserialize<ControllerModelDocument>(File.ReadAllBytes(generatedCandidateModelPath),
    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Generated controller candidate model is missing.");
var generatedCandidateIdentity = ControllerProfileIdentityFactory.CreateFromProjectFiles(robotPath, componentDirectory,
    fixtureFirmwarePath,
    Path.Combine(root, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs"), "mcu-drive",
    generatedCandidateProfile.Envelope, generatedCandidateProfile.Identity.GeneratorVersion, generatedCandidateProfile.Identity.EvaluatorVersion);
var generatedCandidateValidation = ControllerProfileValidator.Validate(generatedCandidateProfile.Identity, generatedCandidateIdentity);
Require(!generatedCandidateValidation.IsValid && generatedCandidateValidation.MismatchReasons.Contains("peripheral_map_sha256"),
    "The historical unqualified fault candidate is rejected against the changed runtime peripheral map.");
var qualifiedProfilePath = Path.Combine(root, "tests", "fixtures", "controller-profiles", "validated-boundary", "profile.json");
var qualifiedProfile = JsonSerializer.Deserialize<DeterministicControllerProfile>(File.ReadAllBytes(qualifiedProfilePath),
    new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Qualified controller profile is missing.");
var qualifiedIdentity = ControllerProfileIdentityFactory.CreateFromProjectFiles(robotPath, componentDirectory,
    fixtureFirmwarePath,
    Path.Combine(root, "src", "RoboWorld.RobotService", "RenodeMotorEventHost.cs"), "mcu-drive",
    qualifiedProfile.Envelope, qualifiedProfile.Identity.GeneratorVersion, qualifiedProfile.Identity.EvaluatorVersion);
var qualifiedValidation = ControllerProfileValidator.Validate(qualifiedProfile.Identity, qualifiedIdentity);
Require(qualifiedProfile.FidelityLabel == "VALIDATED" && !qualifiedValidation.IsValid && qualifiedValidation.MismatchReasons.Contains("firmware_sha256"),
    "A historically qualified profile is rejected when a local firmware rebuild has a different byte identity.");
Require(generatedCandidateProfile.FidelityLabel == "UNQUALIFIED"
    && generatedCandidateModel.FaultRules?.SingleOrDefault()?.FaultCode == "MCU_HALTED"
    && generatedCandidateModel.FaultRules.Single().Behavior == ControllerModelFaultBehavior.HaltAndLatchOutputs,
    "The current generated candidate stays unqualified while retaining its trace-derived MCU-halt transition rule.");
var candidateBehaviorExercise = new ValidatedModelAssignment("mcu-drive",
    generatedCandidateProfile with { FidelityLabel = "VALIDATED" }, generatedCandidateProfile.Identity, generatedCandidateModel);
await using (var generatedCandidateBackend = new DeterministicModelBackend([candidateBehaviorExercise]))
{
    await generatedCandidateBackend.ResetAsync(0);
    generatedCandidateBackend.ScheduleInput(new ControllerInputEvent("mcu-drive", 1_000_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("MOTOR 50 50\n")));
    var motorBeforeHalt = await generatedCandidateBackend.AdvanceToAsync(1_000_000_000);
    Require(motorBeforeHalt.IsSupported && motorBeforeHalt.Outputs.Count == 5
        && motorBeforeHalt.Outputs.Where(output => output.Kind == ControllerSignalKind.PwmCapture).All(output => output.Scalar == 0.5),
        "The generated candidate applies its trace-fitted motor output before an MCU halt.");
    generatedCandidateBackend.ScheduleInput(new ControllerInputEvent("mcu-drive", 1_501_666_666, 2, "controller.fault", ControllerSignalKind.Fault,
        Unit: "MCU_HALTED"));
    var candidateHalt = await generatedCandidateBackend.AdvanceToAsync(1_501_666_666);
    Require(candidateHalt.IsSupported && candidateHalt.Outputs.Count == 0
        && candidateHalt.Diagnostics.Any(diagnostic => diagnostic.Code == "controller_mcu_halted_latched_outputs"),
        "The generated, still-unqualified candidate reproduces the measured MCU-halt transition without changing latched outputs.");
    generatedCandidateBackend.ScheduleInput(new ControllerInputEvent("mcu-drive", 1_510_000_000, 3, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("PING\n")));
    var candidateHaltedPing = await generatedCandidateBackend.AdvanceToAsync(1_510_000_000);
    Require(candidateHaltedPing.IsSupported && candidateHaltedPing.Outputs.Count == 0,
        "The generated candidate suppresses UART replies after the measured MCU halt.");
}
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { FirmwareSha256 = new string('A', 64) }).IsValid,
    "Controller profile firmware hashes compare independently of hex letter case.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { FirmwareSha256 = new string('0', 64) }).MismatchReasons.Contains("firmware_sha256"),
    "Controller profile invalidates when the firmware binary changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { RobotGraphSha256 = new string('0', 64) }).MismatchReasons.Contains("robot_graph_sha256"),
    "Controller profile invalidates when the assembled robot graph changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { ComponentDefinitionsSha256 = new string('0', 64) }).MismatchReasons.Contains("component_definitions_sha256"),
    "Controller profile invalidates when a used component definition changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { PeripheralMapSha256 = new string('0', 64) }).MismatchReasons.Contains("peripheral_map_sha256"),
    "Controller profile invalidates when the MCU peripheral map changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { InitialStateSha256 = new string('0', 64) }).MismatchReasons.Contains("initial_state_sha256"),
    "Controller profile invalidates when its initial simulation state changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { BoardContractVersion = "2.0.0" }).MismatchReasons.Contains("board_contract"),
    "Controller profile invalidates when its board contract version changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { CharacterizationSchemaVersion = "2" }).MismatchReasons.Contains("characterization_schema_version"),
    "Controller profile invalidates when the characterization trace schema changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { GeneratorVersion = "2.0.0" }).MismatchReasons.Contains("generator_version"),
    "Controller profile invalidates when the model generator changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { ModelSchemaVersion = "2" }).MismatchReasons.Contains("model_schema_version"),
    "Controller profile invalidates when the deterministic model schema changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { EvaluatorVersion = "2.0.0" }).MismatchReasons.Contains("evaluator_version"),
    "Controller profile invalidates when the deterministic evaluator changes.");
Require(ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { InputEnvelopeSha256 = new string('0', 64) }).MismatchReasons.Contains("input_envelope_sha256"),
    "Controller profile invalidates when the certified input envelope changes.");
Require(!ControllerProfileValidator.Validate(controllerIdentity, controllerIdentity with { FirmwareSha256 = "" }).IsValid,
    "Controller profile validation fails closed when a required digest is missing.");

var modelRule = new ControllerModelRule("uart.motor.left_percent", ControllerSignalKind.AnalogSample,
    "pwm.left", ControllerSignalKind.PwmCapture, ControllerModelOperator.Affine, 1, 0, -100, 100, "fraction_signed");
var model = new ControllerModelDocument(1, "fit-left-affine", 4, [modelRule]);
var modelProbe = new ControllerInputEvent("mcu-probe", 1, 1, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 25);
Require(ControllerModelEvaluator.Evaluate(model with { Rules = [modelRule with { Operator = ControllerModelOperator.Constant, Offset = 0.25 }] }, modelProbe, 4).Single().Scalar == 0.25,
    "Deterministic model evaluator supports constant candidates for complexity-aware selection.");
Require(ControllerModelEvaluator.Evaluate(model with { Rules = [modelRule with { Operator = ControllerModelOperator.PassThrough }] }, modelProbe, 4).Single().Scalar == 25,
    "Deterministic model evaluator supports pass-through candidates for complexity-aware selection.");
Require(ControllerModelEvaluator.Evaluate(model with { Rules = [modelRule with { Operator = ControllerModelOperator.MagnitudeAffine, Gain = 0.01 }] },
        modelProbe with { Scalar = -50 }, 4).Single().Scalar == 0.5,
    "Characterized motor duty magnitude is evaluated independently of direction.");
var directionRule = modelRule with { InputEndpointId = "uart.motor.direction", OutputEndpointId = "gpio.direction", OutputKind = ControllerSignalKind.GpioLevel,
    Operator = ControllerModelOperator.SignLevel, Unit = "level" };
Require(ControllerModelEvaluator.Evaluate(model with { Rules = [directionRule] }, modelProbe with { EndpointId = "uart.motor.direction", Scalar = 0 }, 4).Single().Scalar == 1,
    "Characterized nonnegative motor commands set the exact forward GPIO level, including zero.");
DeterministicControllerProfile MakeProfile(string controller, int maximumEventsPerAdvance = 8)
{
    var envelope = new ControllerInputEnvelope(["uart.motor.left_percent"], [ControllerSignalKind.AnalogSample], 0, 10_000, maximumEventsPerAdvance, 1000);
    var identity = controllerIdentity with { InputEnvelopeSha256 = ControllerProfileHashes.Compute(envelope) };
    return new DeterministicControllerProfile(1, "profile-" + controller, identity, envelope,
        ControllerProfileHashes.Compute(model), new string('2', 64), new string('3', 64), 2,
        new Dictionary<string, double>(), ["left-motor-affine"], [], "VALIDATED");
}
ValidatedModelAssignment MakeAssignment(string controller, DeterministicControllerProfile? profile = null)
{
    var selected = profile ?? MakeProfile(controller);
    return new ValidatedModelAssignment(controller, selected, selected.Identity, model);
}
ValidatedModelAssignment MakeUartAssignment(string controller, long maximumSimTimeNs = 10_000_000_000)
{
    var uartModel = new ControllerModelDocument(1, "uart-byte-probe", 1,
        [new ControllerModelRule("uart.rx", ControllerSignalKind.UartRxByte, "gpio.out", ControllerSignalKind.GpioLevel,
            ControllerModelOperator.Constant, 0, 1, 0, 1, "level")]);
    var uartEnvelope = new ControllerInputEnvelope(["uart.rx"], [ControllerSignalKind.UartRxByte], 0, maximumSimTimeNs, 4, 3);
    var uartIdentity = controllerIdentity with { InputEnvelopeSha256 = ControllerProfileHashes.Compute(uartEnvelope) };
    var uartProfile = new DeterministicControllerProfile(1, "profile-" + controller, uartIdentity, uartEnvelope,
        ControllerProfileHashes.Compute(uartModel), new string('2', 64), new string('3', 64), 2,
        new Dictionary<string, double>(), ["bounded uart input"], [], "VALIDATED");
    return new ValidatedModelAssignment(controller, uartProfile, uartIdentity, uartModel);
}
ValidatedModelAssignment MakeUartMotorAssignment(string controller)
{
    ControllerModelRule[] motorRules =
    [
        new("uart.motor.left_percent", ControllerSignalKind.AnalogSample, "tim3.ch1.duty_fraction", ControllerSignalKind.PwmCapture,
            ControllerModelOperator.MagnitudeAffine, 0.01, 0, -100, 100, "fraction"),
        new("uart.motor.left_percent", ControllerSignalKind.AnalogSample, "gpio.pd2.direction", ControllerSignalKind.GpioLevel,
            ControllerModelOperator.SignLevel, 0, 0, -100, 100, "level"),
        new("uart.motor.right_percent", ControllerSignalKind.AnalogSample, "tim3.ch2.duty_fraction", ControllerSignalKind.PwmCapture,
            ControllerModelOperator.MagnitudeAffine, 0.01, 0, -100, 100, "fraction"),
        new("uart.motor.right_percent", ControllerSignalKind.AnalogSample, "gpio.pd3.direction", ControllerSignalKind.GpioLevel,
            ControllerModelOperator.SignLevel, 0, 0, -100, 100, "level")
    ];
    ControllerUartCommandRule[] uartCommands =
    [
        new("PING", 0, -100, 100, "uart.tx", [], Encoding.ASCII.GetBytes("PONG 12345678\n")),
        new("MOTOR", 2, -100, 100, "uart.tx",
            [new ControllerUartOperandBinding(0, "uart.motor.left_percent"), new ControllerUartOperandBinding(1, "uart.motor.right_percent")], Encoding.ASCII.GetBytes("OK MOTOR\n")),
        new("MOVE", 2, 100, 100, "uart.tx", [], Encoding.ASCII.GetBytes("ERR CMD\n")),
        new("SENSOR?", 0, 0, 0, "uart.tx", [], null, IsUnsupported: true)
    ];
    var uartModel = new ControllerModelDocument(1, "measured-uart-motor-transducer", 8, motorRules, 64, uartCommands, "uart.rx", Encoding.ASCII.GetBytes("ERR LINE\n"));
    var uartEnvelope = new ControllerInputEnvelope(["uart.rx"], [ControllerSignalKind.UartRxByte], 0, 20_000_000, 8, 115200);
    var uartIdentity = controllerIdentity with { InputEnvelopeSha256 = ControllerProfileHashes.Compute(uartEnvelope) };
    var uartProfile = new DeterministicControllerProfile(1, "profile-uart-motor-" + controller, uartIdentity, uartEnvelope,
        ControllerProfileHashes.Compute(uartModel), new string('2', 64), new string('3', 64), 2,
        new Dictionary<string, double>(), ["observed PING reply", "observed MOTOR command duty and direction"], ["sensor and fault behavior"], "VALIDATED");
    return new ValidatedModelAssignment(controller, uartProfile, uartIdentity, uartModel);
}
await using (var uartMotorBackend = new DeterministicModelBackend([MakeUartMotorAssignment("mcu-motor")] ))
{
    await uartMotorBackend.ResetAsync(0);
    uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 5_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("MOT")));
    var partial = await uartMotorBackend.AdvanceToAsync(5_000_000);
    Require(partial.IsSupported && partial.Outputs.Count == 0, "UART transducer retains fragmented command bytes without early output.");
    uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 8_333_333, 2, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("OR -50 25\r\n")));
    var motor = await uartMotorBackend.AdvanceToAsync(8_333_334);
    Require(motor.IsSupported && motor.Outputs.Count == 5 && motor.Outputs.All(output => output.SimTimeNs == 8_333_334)
        && motor.Outputs.Single(output => output.EndpointId == "tim3.ch1.duty_fraction").Scalar == 0.5
        && motor.Outputs.Single(output => output.EndpointId == "gpio.pd2.direction").Scalar == 0
        && motor.Outputs.Single(output => output.EndpointId == "tim3.ch2.duty_fraction").Scalar == 0.25
        && motor.Outputs.Single(output => output.EndpointId == "gpio.pd3.direction").Scalar == 1
        && Encoding.ASCII.GetString(motor.Outputs.Single(output => output.Kind == ControllerSignalKind.UartTxByte).Bytes!) == "OK MOTOR\n",
        "UART transducer maps both motor operands and response at the authoritative advance boundary.");
    uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 8_500_000, 3, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("MOVE 100 100\n")));
    var moveRejected = await uartMotorBackend.AdvanceToAsync(8_500_000);
    Require(moveRejected.IsSupported && Encoding.ASCII.GetString(moveRejected.Outputs.Single().Bytes!) == "ERR CMD\n",
        "Training-derived observed MOVE rejection returns its exact UART error without actuator outputs.");
    uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 8_600_000, 4, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes(new string('X', 80))));
    var partialOverlength = await uartMotorBackend.AdvanceToAsync(8_600_000);
    Require(partialOverlength.IsSupported && partialOverlength.Outputs.Count == 0,
        "UART transducer bounds and retains overlength discard state until line termination.");
    uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 8_700_000, 5, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("\n")));
    var overlengthRejected = await uartMotorBackend.AdvanceToAsync(8_700_000);
    Require(overlengthRejected.IsSupported && Encoding.ASCII.GetString(overlengthRejected.Outputs.Single().Bytes!) == "ERR LINE\n"
        && overlengthRejected.Diagnostics.Any(diagnostic => diagnostic.Code == "uart_line_rejected_overlength" && !diagnostic.IsFault),
        "Training-derived overlength rejection returns its exact UART error while remaining inside the model domain.");
    uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 9_000_000, 6, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("STATUS?\n")));
    var unsupported = await uartMotorBackend.AdvanceToAsync(9_000_000);
    Require(!unsupported.IsSupported && unsupported.Outputs.Count == 0 && unsupported.Diagnostics.Any(diagnostic => diagnostic.IsFault),
        "Unknown UART commands fault closed and withhold outputs for the advance.");
    ExpectInvalid(() => uartMotorBackend.ScheduleInput(new ControllerInputEvent("mcu-motor", 10_000_000, 7, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("PING\n"))),
        "UART transducer remains disabled until explicit reset after unsupported behavior.");
    await uartMotorBackend.ResetAsync(0);
}
var measuredHaltBase = MakeUartMotorAssignment("mcu-measured-halt");
var measuredHaltEnvelope = measuredHaltBase.Profile.Envelope with
{
    SupportedEndpoints = ["uart.rx", "controller.fault"],
    SupportedSignals = [ControllerSignalKind.UartRxByte, ControllerSignalKind.Fault],
    MaximumEventsPerAdvance = measuredHaltBase.Profile.Envelope.MaximumEventsPerAdvance + 1
};
var measuredHaltModel = measuredHaltBase.Model with
{
    MaximumRules = measuredHaltBase.Model.MaximumRules + 1,
    FaultRules = [new ControllerModelFaultRule("controller.fault", "MCU_HALTED", ControllerModelFaultBehavior.HaltAndLatchOutputs)]
};
var measuredHaltIdentity = measuredHaltBase.CurrentIdentity with
{
    InputEnvelopeSha256 = ControllerProfileHashes.Compute(measuredHaltEnvelope)
};
var measuredHaltProfile = measuredHaltBase.Profile with
{
    Identity = measuredHaltIdentity,
    Envelope = measuredHaltEnvelope,
    ModelDocumentSha256 = ControllerProfileHashes.Compute(measuredHaltModel)
};
var measuredHaltAssignment = measuredHaltBase with
{
    Profile = measuredHaltProfile,
    CurrentIdentity = measuredHaltIdentity,
    Model = measuredHaltModel
};
await using (var measuredHaltBackend = new DeterministicModelBackend([measuredHaltAssignment]))
{
    await measuredHaltBackend.ResetAsync(0);
    ExpectInvalid(() => measuredHaltBackend.ScheduleInput(new ControllerInputEvent("mcu-measured-halt", 1, 1,
        "controller.fault", ControllerSignalKind.Fault, Unit: "PWM_GPIO_AF_MISMATCH_PA6_PA7")),
        "A device route fault cannot be misclassified as a controller firmware fault transition.");
    measuredHaltBackend.ScheduleInput(new ControllerInputEvent("mcu-measured-halt", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("MOTOR 50 50\n")));
    var beforeHalt = await measuredHaltBackend.AdvanceToAsync(1_000_000);
    Require(beforeHalt.IsSupported && beforeHalt.Outputs.Count == 5,
        $"A trained motor command establishes the peripheral state before the measured MCU halt (supported={beforeHalt.IsSupported}, outputs={beforeHalt.Outputs.Count}, diagnostics={string.Join(";", beforeHalt.Diagnostics.Select(item => item.Code + ":" + item.Message))}).");
    measuredHaltBackend.ScheduleInput(new ControllerInputEvent("mcu-measured-halt", 2_000_000, 2, "controller.fault", ControllerSignalKind.Fault,
        Unit: "MCU_HALTED"));
    var halted = await measuredHaltBackend.AdvanceToAsync(2_000_000);
    Require(halted.IsSupported && halted.Outputs.Count == 0
        && halted.Diagnostics.Any(diagnostic => diagnostic.Code == "controller_mcu_halted_latched_outputs" && diagnostic.IsFault),
        "The held-out MCU-halt transition remains supported, reports the fault, and preserves prior physical outputs.");
    measuredHaltBackend.ScheduleInput(new ControllerInputEvent("mcu-measured-halt", 3_000_000, 3, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("PING\n")));
    var noHaltReply = await measuredHaltBackend.AdvanceToAsync(3_000_000);
    Require(noHaltReply.IsSupported && noHaltReply.Outputs.Count == 0,
        "A halted controller produces no later firmware UART response while its outputs remain latched.");
    await measuredHaltBackend.ResetAsync(0);
    measuredHaltBackend.ScheduleInput(new ControllerInputEvent("mcu-measured-halt", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("PING\n")));
    var recovered = await measuredHaltBackend.AdvanceToAsync(1_000_000);
    Require(recovered.IsSupported && Encoding.ASCII.GetString(recovered.Outputs.Single().Bytes!) == "PONG 12345678\n",
        "An explicit session reset clears the measured halt state before a fresh controller run.");
}
await using (var boundedOutputBackend = new DeterministicModelBackend([MakeUartMotorAssignment("mcu-output-budget")]))
{
    await boundedOutputBackend.ResetAsync(0);
    boundedOutputBackend.ScheduleInput(new ControllerInputEvent("mcu-output-budget", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("MOTOR -50 25\nMOTOR 0 0\n")));
    var overBudget = await boundedOutputBackend.AdvanceToAsync(1_000_000);
    Require(!overBudget.IsSupported && overBudget.Outputs.Count == 0
        && overBudget.Diagnostics.Any(diagnostic => diagnostic.Code == "controller_model_output_budget_exceeded"),
        "UART output fan-out beyond the certified per-advance budget faults closed without returning partial outputs.");
    ExpectInvalid(() => boundedOutputBackend.ScheduleInput(new ControllerInputEvent("mcu-output-budget", 2_000_000, 2, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("PING\n"))),
        "Output-budget failure disables the whole model session until reset.");
    await boundedOutputBackend.ResetAsync(0);
}
await using (var explicitUnsupportedBackend = new DeterministicModelBackend([MakeUartMotorAssignment("mcu-sensor-unsupported")]))
{
    await explicitUnsupportedBackend.ResetAsync(0);
    explicitUnsupportedBackend.ScheduleInput(new ControllerInputEvent("mcu-sensor-unsupported", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("SENSOR?\n")));
    var sensorUnsupported = await explicitUnsupportedBackend.AdvanceToAsync(1_000_000);
    Require(!sensorUnsupported.IsSupported && sensorUnsupported.Outputs.Count == 0
        && sensorUnsupported.Diagnostics.Any(diagnostic => diagnostic.Code == "unsupported_uart_command"),
        "A model-declared unsupported SENSOR query faults closed without inventing a response.");
}
var pulseGain = 0.0001714872386075572;
var pulseOffset = -0.2792457027846922;
ControllerUartCommandRule[] pulseCommands =
[
    new("SENSOR?", 0, 0, 0, "uart.tx", [], null, NumericResponseCommandToken: "SENSOR?"),
    new("STATUS?", 0, 0, 0, "uart.tx", [], Encoding.ASCII.GetBytes("STATUS MCU=OK UART=OK\n"))
];
var pulseModel = new ControllerModelDocument(1, "echo-pulse-runtime-probe", 8, [], 48, pulseCommands, "uart.rx",
    EchoPulse: new ControllerEchoPulseRule("gpio.pa0.echo", pulseGain, pulseOffset, 5_819_302, 14_584_261,
        "SENSOR?", "DIST mm=", " valid=1\n"),
    ExactUartLines:
    [
        new(Encoding.ASCII.GetBytes("PING" + new string(' ', 43)), Encoding.ASCII.GetBytes("ERR CMD\n")),
        new(Encoding.ASCII.GetBytes("MOTOR 101 0"), Encoding.ASCII.GetBytes("ERR RANGE\n")),
        new(Encoding.ASCII.GetBytes("MOTOR -101 0"), Encoding.ASCII.GetBytes("ERR RANGE\n")),
        new(Encoding.ASCII.GetBytes("MOTOR 1"), Encoding.ASCII.GetBytes("ERR CMD\n")),
        new(Encoding.ASCII.GetBytes("ping"), Encoding.ASCII.GetBytes("ERR CMD\n"))
    ]);
var pulseEnvelope = new ControllerInputEnvelope(["uart.rx", "gpio.pa0.echo"],
    [ControllerSignalKind.UartRxByte, ControllerSignalKind.GpioLevel], 0, 20_000_000, 4, 115200);
var pulseIdentity = controllerIdentity with { InputEnvelopeSha256 = ControllerProfileHashes.Compute(pulseEnvelope) };
var pulseProfile = new DeterministicControllerProfile(1, "profile-echo-pulse-probe", pulseIdentity, pulseEnvelope,
    ControllerProfileHashes.Compute(pulseModel), new string('2', 64), new string('3', 64), 2,
    new Dictionary<string, double>(), ["test-only measured pulse mapping"], [], "VALIDATED");
var pulseAssignment = new ValidatedModelAssignment("mcu-echo", pulseProfile, pulseIdentity, pulseModel);
await using (var echoPulseBackend = new DeterministicModelBackend([
    pulseAssignment]))
{
    await echoPulseBackend.ResetAsync(0);
    echoPulseBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 1_000_000, 1, "gpio.pa0.echo", ControllerSignalKind.GpioLevel, 1));
    echoPulseBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 9_746_852, 2, "gpio.pa0.echo", ControllerSignalKind.GpioLevel, 0));
    var pulseCapture = await echoPulseBackend.AdvanceToAsync(9_746_852);
    Require(pulseCapture.IsSupported && pulseCapture.Outputs.Count == 0,
        "Stateful echo pulse capture retains measured controller input without writing physical or UI state.");
    echoPulseBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 10_000_000, 3, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes("SENSOR?\n")));
    var sensorResponse = await echoPulseBackend.AdvanceToAsync(10_000_000);
    Require(sensorResponse.IsSupported && sensorResponse.Outputs.Single().SimTimeNs == 10_000_000
        && Encoding.ASCII.GetString(sensorResponse.Outputs.Single().Bytes!) == "DIST mm=1499 valid=1\n",
        "SENSOR? formats its response from the prior causally completed echo pulse at the authoritative UART boundary.");
}
await using (var missingEchoBackend = new DeterministicModelBackend([pulseAssignment]))
{
    await missingEchoBackend.ResetAsync(0);
    missingEchoBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("SENSOR?\n")));
    var missingEchoResponse = await missingEchoBackend.AdvanceToAsync(1_000_000);
    Require(!missingEchoResponse.IsSupported && missingEchoResponse.Outputs.Count == 0
        && missingEchoResponse.Diagnostics.Any(diagnostic => diagnostic.Code == "unsupported_uart_command"),
        "Sensor query without a completed echo pulse fails closed and does not invent a valid-zero sensor result.");
}
var initialInvalidSensorReply = Encoding.ASCII.GetBytes("DIST mm=0 valid=0\n");
var retainingPulseModel = pulseModel with
{
    EchoPulse = pulseModel.EchoPulse! with
    {
        NoPulseBehavior = ControllerEchoNoPulseBehavior.RetainLastValidReading,
        InitialInvalidResponseBytes = initialInvalidSensorReply
    }
};
var retainingPulseProfile = pulseProfile with
{
    ProfileId = "profile-echo-pulse-noecho-probe",
    ModelDocumentSha256 = ControllerProfileHashes.Compute(retainingPulseModel),
    SupportedBehaviors = ["measured initial invalid reply", "measured retained no-echo value"]
};
var retainingPulseAssignment = new ValidatedModelAssignment("mcu-echo", retainingPulseProfile, pulseIdentity, retainingPulseModel);
await using (var retainingEchoBackend = new DeterministicModelBackend([retainingPulseAssignment]))
{
    await retainingEchoBackend.ResetAsync(0);
    initialInvalidSensorReply[0] = (byte)'X';
    retainingEchoBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("SENSOR?\n")));
    var initialInvalid = await retainingEchoBackend.AdvanceToAsync(1_000_000);
    Require(initialInvalid.IsSupported && Encoding.ASCII.GetString(initialInvalid.Outputs.Single().Bytes!) == "DIST mm=0 valid=0\n",
        "Measured initial invalid sensor response is emitted from an immutable training-derived byte template.");
    retainingEchoBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 2_000_000, 2, "gpio.pa0.echo", ControllerSignalKind.GpioLevel, 1));
    retainingEchoBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 9_746_852, 3, "gpio.pa0.echo", ControllerSignalKind.GpioLevel, 0));
    var validPulse = await retainingEchoBackend.AdvanceToAsync(9_746_852);
    retainingEchoBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 10_000_000, 4, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("SENSOR?\n")));
    var validReading = await retainingEchoBackend.AdvanceToAsync(10_000_000);
    await retainingEchoBackend.AdvanceToAsync(11_000_000); // A no-echo interval advances time without synthesizing a pulse.
    retainingEchoBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 12_000_000, 5, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("SENSOR?\n")));
    var retainedReading = await retainingEchoBackend.AdvanceToAsync(12_000_000);
    Require(validPulse.IsSupported && validReading.IsSupported && retainedReading.IsSupported
        && Encoding.ASCII.GetString(validReading.Outputs.Single().Bytes!) == "DIST mm=1328 valid=1\n"
        && Encoding.ASCII.GetString(retainedReading.Outputs.Single().Bytes!) == "DIST mm=1328 valid=1\n",
        $"Validated no-pulse behavior keeps the last measured distance and valid flag without inventing new sensor input (supported={validPulse.IsSupported}/{validReading.IsSupported}/{retainedReading.IsSupported}, valid='{Encoding.ASCII.GetString(validReading.Outputs.SingleOrDefault()?.Bytes ?? [])}', retained='{Encoding.ASCII.GetString(retainedReading.Outputs.SingleOrDefault()?.Bytes ?? [])}').");
}
await using (var exactMalformedLineBackend = new DeterministicModelBackend([pulseAssignment]))
{
    await exactMalformedLineBackend.ResetAsync(0);
    exactMalformedLineBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", 1_000_000, 1, "uart.rx", ControllerSignalKind.UartRxByte,
        Bytes: Encoding.ASCII.GetBytes("PING" + new string(' ', 43) + "\n")));
    var exactMalformedResponse = await exactMalformedLineBackend.AdvanceToAsync(1_000_000);
    Require(exactMalformedResponse.IsSupported && Encoding.ASCII.GetString(exactMalformedResponse.Outputs.Single().Bytes!) == "ERR CMD\n",
        "A training-derived exact malformed UART line produces its measured rejection without accepting other unknown grammar.");
}
await using (var uartGrammarBackend = new DeterministicModelBackend([pulseAssignment]))
{
    await uartGrammarBackend.ResetAsync(0);
    (string Request, string ExpectedReply)[] probes =
    [
        ("STATUS?", "STATUS MCU=OK UART=OK\n"),
        ("MOTOR 101 0", "ERR RANGE\n"),
        ("MOTOR -101 0", "ERR RANGE\n"),
        ("MOTOR 1", "ERR CMD\n"),
        ("ping", "ERR CMD\n")
    ];
    for (var index = 0; index < probes.Length; index++)
    {
        var timeNs = (index + 1) * 1_000_000L;
        uartGrammarBackend.ScheduleInput(new ControllerInputEvent("mcu-echo", timeNs, (ulong)(index + 1), "uart.rx",
            ControllerSignalKind.UartRxByte, Bytes: Encoding.ASCII.GetBytes(probes[index].Request + "\n")));
        var result = await uartGrammarBackend.AdvanceToAsync(timeNs);
        Require(result.IsSupported && result.Outputs.Count == 1
            && Encoding.ASCII.GetString(result.Outputs[0].Bytes!) == probes[index].ExpectedReply,
            $"Measured UART grammar response matches {probes[index].Request} without actuator side effects.");
    }
}
await using (var modelBackend = new DeterministicModelBackend([
    MakeAssignment("mcu-left"), MakeAssignment("mcu-right")]))
{
    await modelBackend.ResetAsync(0);
    modelBackend.ScheduleInput(new ControllerInputEvent("mcu-left", 10, 1, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 50));
    var firstAdvance = await modelBackend.AdvanceToAsync(10);
    Require(modelBackend.ControllerInstanceIds.Count == 2 && firstAdvance.Outputs.Single().ControllerInstanceId == "mcu-left"
        && firstAdvance.Outputs.Single().Scalar == 50 && modelBackend.CurrentSimTimeNs == 10,
        "Deterministic backend preserves per-controller outputs at one shared session time boundary.");
    ExpectInvalid(() => modelBackend.ScheduleInput(new ControllerInputEvent("mcu-left", 9, 2, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 1)),
        "Deterministic backend rejects stale timestamped inputs.");
    ExpectInvalid(() => modelBackend.ScheduleInput(new ControllerInputEvent("mcu-left", 11, 2, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 101)),
        "Deterministic backend rejects values outside the certified model domain.");
    modelBackend.ScheduleInput(new ControllerInputEvent("mcu-left", 20, 2, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 25));
    ExpectInvalid(() => modelBackend.ScheduleInput(new ControllerInputEvent("mcu-left", 15, 3, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 10)),
        "Deterministic backend rejects sequence-ordered inputs with regressing timestamps.");
}
ExpectInvalid(() => _ = new DeterministicModelBackend([
    MakeAssignment("mcu-bad", MakeProfile("mcu-bad") with { FidelityLabel = "UNQUALIFIED" })]),
    "Deterministic backend refuses to execute an unqualified profile.");
ExpectInvalid(() => _ = new DeterministicModelBackend([
    MakeAssignment("mcu-tampered", MakeProfile("mcu-tampered") with { ModelDocumentSha256 = new string('0', 64) })]),
    "Deterministic backend refuses a model document whose content hash does not match its profile.");
var artifactDirectory = Path.Combine(Path.GetTempPath(), "roboworld-profile-artifact-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(artifactDirectory);
var profileArtifactPath = Path.Combine(artifactDirectory, "profile.json");
var modelArtifactPath = Path.Combine(artifactDirectory, "model.json");
var artifactProfile = MakeProfile("loader");
var artifactJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
File.WriteAllBytes(profileArtifactPath, JsonSerializer.SerializeToUtf8Bytes(artifactProfile, artifactJsonOptions));
File.WriteAllBytes(modelArtifactPath, JsonSerializer.SerializeToUtf8Bytes(model, artifactJsonOptions));
var artifactProfileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(profileArtifactPath)));
var loadedAssignment = ControllerProfileArtifactLoader.LoadAssignment("mcu-loader",
    new ProjectControllerProfileReference(artifactProfile.ProfileId, artifactProfileHash), profileArtifactPath, modelArtifactPath, artifactProfile.Identity);
Require(loadedAssignment.ControllerInstanceId == "mcu-loader" && loadedAssignment.Profile.ProfileId == artifactProfile.ProfileId,
    "Profile artifact loader resolves the saved profile ID and content hash against current controller identity.");
ExpectInvalid(() => ControllerProfileArtifactLoader.LoadAssignment("mcu-loader",
        new ProjectControllerProfileReference(artifactProfile.ProfileId, new string('0', 64)), profileArtifactPath, modelArtifactPath, artifactProfile.Identity),
    "Profile artifact loader rejects content that differs from the project's immutable profile hash.");
var unqualifiedArtifactPath = Path.Combine(artifactDirectory, "unqualified-profile.json");
var unqualifiedArtifactBytes = JsonSerializer.SerializeToUtf8Bytes(artifactProfile with { FidelityLabel = "UNQUALIFIED" }, artifactJsonOptions);
File.WriteAllBytes(unqualifiedArtifactPath, unqualifiedArtifactBytes);
ExpectInvalid(() => ControllerProfileArtifactLoader.LoadAssignment("mcu-loader",
        new ProjectControllerProfileReference(artifactProfile.ProfileId, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(unqualifiedArtifactBytes))),
        unqualifiedArtifactPath, modelArtifactPath, artifactProfile.Identity),
    "Profile artifact loader rejects the current unqualified characterization candidate.");
ExpectInvalid(() => ControllerProfileArtifactLoader.LoadAssignment("mcu-loader",
        new ProjectControllerProfileReference(artifactProfile.ProfileId, artifactProfileHash), profileArtifactPath, modelArtifactPath,
        artifactProfile.Identity with { RobotGraphSha256 = new string('0', 64) }),
    "Profile artifact loader invalidates a project profile when the current robot graph has changed.");
try { Directory.Delete(artifactDirectory, recursive: true); } catch (IOException) { }
var exactSelection = ControllerBackendSelector.Resolve(ControllerExecutionMode.ExactFirmware,
    () => throw new InvalidOperationException("Exact mode must not invoke the model factory."));
Require(exactSelection.Kind == ControllerBackendKind.Renode && exactSelection.ModelBackend is null,
    "Exact selection preserves the Renode reference backend without probing a model.");
var autoFallback = ControllerBackendSelector.Resolve(ControllerExecutionMode.Automatic,
    () => throw new InvalidDataException("profile identity mismatch"));
Require(autoFallback.Kind == ControllerBackendKind.Renode && autoFallback.Reason.Contains("profile identity mismatch", StringComparison.Ordinal),
    "Automatic selection falls back to exact firmware before a run when model preflight fails.");
ExpectInvalid(() => ControllerBackendSelector.Resolve(ControllerExecutionMode.ValidatedModel,
        () => throw new InvalidDataException("profile identity mismatch")),
    "Explicit model selection fails closed when model preflight fails.");
await using (var boundedBackend = new DeterministicModelBackend([MakeAssignment("mcu-bounded")], maxPendingEvents: 1))
{
    await boundedBackend.ResetAsync(0);
    boundedBackend.ScheduleInput(new ControllerInputEvent("mcu-bounded", 1, 1, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 25));
    ExpectInvalid(() => boundedBackend.ScheduleInput(new ControllerInputEvent("mcu-bounded", 2, 2, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 50)),
        "Deterministic backend rejects inputs when its bounded queue is full.");
    var preserved = await boundedBackend.AdvanceToAsync(1);
    Require(preserved.Outputs.Single().Scalar == 25, "Queue overflow leaves accepted controller inputs intact and ordered.");
}
await using (var boundedUartBackend = new DeterministicModelBackend([MakeUartAssignment("mcu-uart")]))
{
    await boundedUartBackend.ResetAsync(0);
    boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 0, 1, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [1, 2]));
    ExpectInvalid(() => boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 100, 2, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [3, 4])),
        "Deterministic backend enforces the profile UART byte-rate envelope without dropping accepted data.");
    ExpectInvalid(() => boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 1_000_000_000, 2, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [3, 4])),
        "UART byte-rate accounting conservatively retains the cutoff millisecond bucket.");
    boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 1_001_000_000, 2, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [3, 4, 5]));
    ExpectInvalid(() => boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 1_001_000_001, 3, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [6])),
        "UART rate accounting retains bytes that are still inside the one-second simulation-time window.");
    boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 2_002_000_000, 3, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [6, 7, 8]));
    ExpectInvalid(() => boundedUartBackend.ScheduleInput(new ControllerInputEvent("mcu-uart", 2_002_000_001, 4, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [9])),
        "UART rate accounting expires bytes after the conservative cutoff bucket leaves the one-second window.");
}
await using (var boundedTimeBackend = new DeterministicModelBackend([MakeUartAssignment("mcu-time", maximumSimTimeNs: 10)]))
{
    ExpectInvalid(() => boundedTimeBackend.ResetAsync(11).AsTask().GetAwaiter().GetResult(),
        "Deterministic backend rejects reset times beyond the certified simulation-time envelope.");
    await boundedTimeBackend.ResetAsync(0);
    ExpectInvalid(() => boundedTimeBackend.ScheduleInput(new ControllerInputEvent("mcu-time", 11, 1, "uart.rx", ControllerSignalKind.UartRxByte, Bytes: [1])),
        "Deterministic backend rejects input timestamps outside the certified simulation-time envelope.");
    ExpectInvalid(() => boundedTimeBackend.AdvanceToAsync(11).AsTask().GetAwaiter().GetResult(),
        "Deterministic backend rejects advancement beyond the certified simulation-time envelope.");
}
await using (var perControllerBudgetBackend = new DeterministicModelBackend([
    MakeAssignment("mcu-budget", MakeProfile("mcu-budget", maximumEventsPerAdvance: 1))]))
{
    await perControllerBudgetBackend.ResetAsync(0);
    perControllerBudgetBackend.ScheduleInput(new ControllerInputEvent("mcu-budget", 1, 1, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 10));
    perControllerBudgetBackend.ScheduleInput(new ControllerInputEvent("mcu-budget", 1, 2, "uart.motor.left_percent", ControllerSignalKind.AnalogSample, 20));
    ExpectInvalid(() => perControllerBudgetBackend.AdvanceToAsync(1).AsTask().GetAwaiter().GetResult(),
        "Deterministic backend enforces each controller's profile-specific event budget.");
    Require(perControllerBudgetBackend.CurrentSimTimeNs == 0,
        "Per-controller event budget rejection does not advance shared simulation time.");
}

static RobotAuthoringDraft AddTestRoverWiring(RobotAuthoringDraft draft, IReadOnlyDictionary<string, ComponentDefinition> catalogue)
{
    var wires = new (string From, string To)[]
    {
        ("authored-controller.pwm1", "authored-driver.pwm_a"), ("authored-controller.dir1", "authored-driver.dir_a"),
        ("authored-driver.out_a", "authored-motor-left.drive"),
        ("authored-controller.pwm2", "authored-driver.pwm_b"), ("authored-controller.dir2", "authored-driver.dir_b"),
        ("authored-driver.out_b", "authored-motor-right.drive"),
        ("authored-distance.echo", "authored-controller.sensor"),
        ("authored-battery.supply", "authored-driver.vin"), ("authored-battery.supply", "authored-controller.power_in")
    };
    foreach (var (from, to) in wires) draft = draft.AddElectricalConnection(catalogue, from, to, "");
    return draft;
}

static string WriteTemporaryRobot(string sourcePath, Action<JsonObject> mutate)
{
    var node = JsonNode.Parse(File.ReadAllText(sourcePath))!.AsObject();
    mutate(node);
    var path = Path.Combine(Path.GetTempPath(), "roboworld-test-" + Guid.NewGuid().ToString("N") + ".json");
    File.WriteAllText(path, node.ToJsonString());
    return path;
}

static void ExpectInvalid(Action action, string message)
{
    try { action(); }
    catch (InvalidDataException) { Console.WriteLine("PASS: " + message); return; }
    throw new InvalidOperationException("FAIL: " + message);
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
