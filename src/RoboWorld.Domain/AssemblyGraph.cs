using System.Text.Json;

namespace RoboWorld.Domain;

public sealed record MountPortDefinition(string Id, string Kind, string SizeClass, IReadOnlyList<string> MateKinds, double[] FramePositionM, double[] FrameRotationXyzw);
public sealed record ElectricalPortDefinition(string Id, string Interface, string? Role, string? Pin);
public sealed record CameraProperties(double HorizontalFovDeg, int ResolutionWidthPx, int ResolutionHeightPx, double NearClipM, double FarClipM, bool Enabled);
public sealed record ComponentDefinition(string Id, string Version, string Category, double MassKg, double[] SizeM, string Primitive, string Model, double? RadiusM, double? StallTorqueNm, double? NoLoadRpm, IReadOnlyList<MountPortDefinition> MountPorts, IReadOnlyList<ElectricalPortDefinition> ElectricalPorts, CameraProperties? Camera = null, double? FrictionCoefficient = null);
public sealed record ComponentInstance(string Id, string CatalogueId, string CatalogueVersion, string LinkId, double[] PositionM, double[] RotationXyzw, double[]? SizeOverrideM = null, string? DisplayName = null, CameraProperties? Camera = null);
public sealed record RobotLink(string Id, string BaseComponentId, double[] PositionM, double[] RotationXyzw, bool IsRoot);
public sealed record RobotJoint(string Id, string Type, string ParentLinkId, string ChildLinkId, double[] AxisLocal, double[] ParentFramePositionM, double[] ParentFrameRotationXyzw, double[] ChildFramePositionM, double[] ChildFrameRotationXyzw, string? ActuatorComponentId, string? ParentPortId, string? ChildPortId, string? JointProfileId, string? JointProfileVersion, string? ParentComponentId = null, string? ChildComponentId = null);
public sealed record ElectricalConnection(string Id, string From, string To, string Interface, string? PhysicalPin, string? BusId);
public sealed record ComponentMountConnection(string Id, string ParentComponentId, string ParentPortId, string ChildComponentId, string ChildPortId);
public sealed record AssemblyGraph(string Id, string DisplayName, string RootLinkId, IReadOnlyList<RobotLink> Links, IReadOnlyList<ComponentInstance> Components, IReadOnlyList<RobotJoint> Joints, IReadOnlyList<ElectricalConnection> ElectricalConnections, IReadOnlyList<ComponentMountConnection>? ComponentMountConnections = null);
public sealed record WorldObject(string Id, string Primitive, double[] PositionM, double[] SizeM, double? RadiusM, double MassKg,
    string AssetRef = "", string DisplayName = "", double[]? RotationXyzw = null, string BodyType = "static", double FrictionCoefficient = 0.5, IReadOnlyList<string>? Tags = null);
public sealed record WorldDefinition(string Id, double[] BoundsM, double[] GravityMps2, IReadOnlyList<WorldObject> Objects, string DisplayName = "", string SchemaVersion = "1.0");
public sealed record BodyState(string ComponentId, double[] PositionM, double[] RotationXyzw, double[] LinearVelocityMps, double[] AngularVelocityRadps);
public sealed record PhysicalSnapshot(long PhysicsTick, long SimTimeNs, IReadOnlyList<BodyState> Bodies);
public sealed record ActuatorForce(string ComponentId, double[] ForceN, double[] ApplicationPointM);

/// <summary>Engine-neutral authority seam. Godot invokes RecordEnginePhysicsStep from its fixed physics callback; it does not expose a manual stepping API.</summary>
public interface IPhysicsWorldAdapter
{
    void RegisterBody(string componentId, double massKg);
    void ApplyActuatorForces(IReadOnlyList<ActuatorForce> forces, long simTimeNs);
    PhysicalSnapshot CaptureState(long physicsTick, long simTimeNs);
    void RestoreState(PhysicalSnapshot snapshot);
}

/// <summary>Loads engine-neutral, versioned JSON fixtures; no Godot types cross this boundary.</summary>
public static class FixtureLoader
{
    public static AssemblyGraph LoadRobot(string robotPath, string componentDirectory)
    {
        using var robot = JsonDocument.Parse(File.ReadAllText(robotPath));
        var root = robot.RootElement;
        var components = new Dictionary<string, ComponentDefinition>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(componentDirectory, "*.component.json"))
        {
            var definition = ParseComponent(path);
            components.Add(definition.Id, definition);
        }

        var links = root.GetProperty("links").EnumerateArray().Select(link => new RobotLink(
            link.GetProperty("id").GetString()!, link.GetProperty("base_component_id").GetString()!,
            link.GetProperty("initial_transform").GetProperty("position_m").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            link.GetProperty("initial_transform").GetProperty("rotation_xyzw").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            link.TryGetProperty("is_root", out var isRoot) && isRoot.GetBoolean())).ToArray();
        var instances = root.GetProperty("components").EnumerateArray().Select(component => new ComponentInstance(
            component.GetProperty("id").GetString()!, component.GetProperty("catalogue_id").GetString()!,
            component.GetProperty("catalogue_version").GetString()!, component.GetProperty("link_id").GetString()!,
            component.GetProperty("transform").GetProperty("position_m").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            component.GetProperty("transform").GetProperty("rotation_xyzw").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            component.TryGetProperty("overrides", out var overrides) && overrides.TryGetProperty("size_m", out var sizeOverride)
                ? sizeOverride.EnumerateArray().Select(x => x.GetDouble()).ToArray()
                : null,
            component.TryGetProperty("overrides", out overrides) && overrides.TryGetProperty("display_name", out var instanceName)
                ? instanceName.GetString()
                : null,
            component.TryGetProperty("overrides", out overrides) && overrides.TryGetProperty("camera", out var cameraOverride)
                ? ParseCameraProperties(cameraOverride)
                : null)).ToArray();

        foreach (var instance in instances)
        {
            if (!components.TryGetValue(instance.CatalogueId, out var definition))
                throw new InvalidDataException($"Component {instance.Id} references missing catalogue id {instance.CatalogueId}.");
            if (definition.Version != instance.CatalogueVersion)
                throw new InvalidDataException($"Component {instance.Id} requires {instance.CatalogueId}@{instance.CatalogueVersion}, found {definition.Version}.");
        }

        var joints = root.GetProperty("joints").EnumerateArray().Select(joint => new RobotJoint(
            joint.GetProperty("id").GetString()!, joint.GetProperty("type").GetString()!,
            joint.GetProperty("parent_link_id").GetString()!, joint.GetProperty("child_link_id").GetString()!,
            NormalizeAxis(joint.GetProperty("axis_local").EnumerateArray().Select(x => x.GetDouble()).ToArray(), joint.GetProperty("id").GetString()!),
            joint.GetProperty("parent_frame").GetProperty("position_m").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            joint.GetProperty("parent_frame").GetProperty("rotation_xyzw").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            joint.GetProperty("child_frame").GetProperty("position_m").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            joint.GetProperty("child_frame").GetProperty("rotation_xyzw").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            joint.TryGetProperty("actuator_component_id", out var actuator) ? actuator.GetString() : null,
            joint.TryGetProperty("parent_port_id", out var parentPort) ? parentPort.GetString() : null,
            joint.TryGetProperty("child_port_id", out var childPort) ? childPort.GetString() : null,
            joint.TryGetProperty("joint_profile_id", out var jointProfile) ? jointProfile.GetString() : null,
            joint.TryGetProperty("joint_profile_version", out var jointProfileVersion) ? jointProfileVersion.GetString() : null,
            joint.TryGetProperty("parent_component_id", out var parentComponent) ? parentComponent.GetString() : null,
            joint.TryGetProperty("child_component_id", out var childComponent) ? childComponent.GetString() : null)).ToArray();
        var electricalConnections = root.GetProperty("electrical_connections").EnumerateArray().Select(connection => new ElectricalConnection(
            connection.GetProperty("id").GetString()!, connection.GetProperty("from").GetString()!, connection.GetProperty("to").GetString()!, connection.GetProperty("interface").GetString()!,
            connection.TryGetProperty("physical_pin", out var physicalPin) ? physicalPin.GetString() : null,
            connection.TryGetProperty("bus_id", out var busId) ? busId.GetString() : null)).ToArray();
        var mountConnections = root.TryGetProperty("component_mount_connections", out var mountConnectionArray)
            ? mountConnectionArray.EnumerateArray().Select(connection => new ComponentMountConnection(
                connection.GetProperty("id").GetString()!, connection.GetProperty("parent_component_id").GetString()!,
                connection.GetProperty("parent_port_id").GetString()!, connection.GetProperty("child_component_id").GetString()!,
                connection.GetProperty("child_port_id").GetString()!)).ToArray()
            : [];
        var rootLink = root.GetProperty("root_link_id").GetString()!;
        var graph = new AssemblyGraph(root.GetProperty("id").GetString()!, root.GetProperty("display_name").GetString()!, rootLink, links, instances, joints, electricalConnections, mountConnections);
        RobotGraphValidator.Validate(graph, components);
        return graph;
    }

    public static IReadOnlyDictionary<string, ComponentDefinition> LoadCatalogue(string componentDirectory) =>
        Directory.EnumerateFiles(componentDirectory, "*.component.json").Select(ParseComponent).ToDictionary(file => file.Id, StringComparer.Ordinal);

    private static ComponentDefinition ParseComponent(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var mechanical = root.GetProperty("mechanical");
        var simulation = root.GetProperty("simulation");
        var parameters = simulation.GetProperty("parameters");
        double? radius = parameters.TryGetProperty("radius_m", out var radiusElement) ? radiusElement.GetDouble() : null;
        double? friction = parameters.TryGetProperty("friction", out var frictionElement) ? frictionElement.GetDouble() : null;
        double? stallTorque = parameters.TryGetProperty("stall_torque_nm", out var stallTorqueElement) ? stallTorqueElement.GetDouble() : null;
        double? noLoadRpm = parameters.TryGetProperty("no_load_rpm", out var noLoadRpmElement) ? noLoadRpmElement.GetDouble() : null;
        var camera = root.GetProperty("simulation").GetProperty("model").GetString() == "rgb_camera"
            ? ParseCameraProperties(parameters)
            : null;
        var mountPorts = root.GetProperty("mount_ports").EnumerateArray().Select(port => new MountPortDefinition(
            port.GetProperty("id").GetString()!, port.GetProperty("kind").GetString()!,
            port.TryGetProperty("size_class", out var sizeClass) ? sizeClass.GetString() ?? "" : "",
            port.TryGetProperty("mate_kinds", out var mateKinds) ? mateKinds.EnumerateArray().Select(value => value.GetString()!).ToArray() : [],
            port.GetProperty("frame").GetProperty("position_m").EnumerateArray().Select(value => value.GetDouble()).ToArray(),
            port.GetProperty("frame").GetProperty("rotation_xyzw").EnumerateArray().Select(value => value.GetDouble()).ToArray())).ToArray();
        var electricalPorts = root.GetProperty("electrical_ports").EnumerateArray().Select(port => new ElectricalPortDefinition(
            port.GetProperty("id").GetString()!, port.GetProperty("interface").GetString()!,
            port.TryGetProperty("role", out var role) ? role.GetString() : null,
            port.TryGetProperty("pin", out var pin) ? pin.GetString() : null)).ToArray();
        return new ComponentDefinition(root.GetProperty("id").GetString()!, root.GetProperty("version").GetString()!,
            root.GetProperty("category").GetString()!, mechanical.GetProperty("mass_kg").GetDouble(),
            mechanical.GetProperty("size_m").EnumerateArray().Select(x => x.GetDouble()).ToArray(),
            root.GetProperty("geometry").GetProperty("primitive").GetString()!, simulation.GetProperty("model").GetString()!, radius, stallTorque, noLoadRpm, mountPorts, electricalPorts, camera, friction);
    }

    private static double[] NormalizeAxis(double[] axis, string jointId)
    {
        if (axis.Length != 3 || axis.Any(value => !double.IsFinite(value))) throw new InvalidDataException($"Joint {jointId} axis must contain three finite values.");
        var length = Math.Sqrt(axis.Sum(value => value * value));
        if (length < 1e-9) throw new InvalidDataException($"Joint {jointId} axis must have nonzero length.");
        return axis.Select(value => value / length).ToArray();
    }

    public static WorldDefinition LoadWorld(string worldPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(worldPath));
        var root = doc.RootElement;
        var schemaVersion = root.TryGetProperty("schema_version", out var versionElement) ? versionElement.GetString() ?? "1.0" : "1.0";
        if (schemaVersion is not ("1.0" or "1.1")) throw new InvalidDataException($"Unsupported world schema version '{schemaVersion}'.");
        var objects = root.GetProperty("objects").EnumerateArray().Select(item =>
        {
            var geometry = item.GetProperty("geometry");
            var position = item.GetProperty("transform").GetProperty("position_m").EnumerateArray().Select(x => x.GetDouble()).ToArray();
            double? radius = geometry.TryGetProperty("radius_m", out var radiusElement) ? radiusElement.GetDouble() : null;
            var size = geometry.TryGetProperty("size_m", out var sizeElement)
                ? sizeElement.EnumerateArray().Select(x => x.GetDouble()).ToArray()
                : item.GetProperty("geometry").GetProperty("primitive").GetString() == "cylinder"
                    ? new[] { (radius ?? 0.04) * 2, geometry.GetProperty("height_m").GetDouble(), (radius ?? 0.04) * 2 }
                    : new[] { 0d, 0d, 0d };
            var mass = item.TryGetProperty("physics", out var physics) && physics.TryGetProperty("mass_kg", out var massElement) ? massElement.GetDouble() : 0d;
            var rotation = item.GetProperty("transform").TryGetProperty("rotation_xyzw", out var rotationElement)
                ? rotationElement.EnumerateArray().Select(x => x.GetDouble()).ToArray() : new[] { 0d, 0d, 0d, 1d };
            var bodyType = item.TryGetProperty("physics", out physics) && physics.TryGetProperty("body_type", out var bodyTypeElement) ? bodyTypeElement.GetString() ?? "static" : "static";
            var friction = item.TryGetProperty("physics", out physics) && physics.TryGetProperty("friction_coefficient", out var frictionElement) ? frictionElement.GetDouble() : 0.5;
            var tags = item.TryGetProperty("tags", out var tagArray) ? tagArray.EnumerateArray().Select(x => x.GetString()!).ToArray() : [];
            return new WorldObject(item.GetProperty("id").GetString()!, geometry.GetProperty("primitive").GetString()!, position, size, radius, mass,
                item.TryGetProperty("asset_ref", out var assetRef) ? assetRef.GetString() ?? "" : "",
                item.TryGetProperty("display_name", out var displayName) ? displayName.GetString() ?? "" : "", rotation, bodyType, friction, tags);
        }).ToArray();
        if (objects.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != objects.Length)
            throw new InvalidDataException("World object identities must be unique.");
        foreach (var item in objects)
        {
            if (item.PositionM.Length != 3 || item.PositionM.Any(value => !double.IsFinite(value))) throw new InvalidDataException($"World object {item.Id} position must contain three finite metres values.");
            var rotation = item.RotationXyzw ?? [0, 0, 0, 1];
            if (rotation.Length != 4 || rotation.Any(value => !double.IsFinite(value)) || Math.Abs(Math.Sqrt(rotation.Sum(value => value * value)) - 1) > 1e-6)
                throw new InvalidDataException($"World object {item.Id} rotation must be a finite unit xyzw quaternion.");
            if (item.BodyType is not ("static" or "dynamic" or "kinematic")) throw new InvalidDataException($"World object {item.Id} has unsupported body type '{item.BodyType}'.");
            if (!double.IsFinite(item.FrictionCoefficient) || item.FrictionCoefficient < 0 || !double.IsFinite(item.MassKg) || item.MassKg < 0)
                throw new InvalidDataException($"World object {item.Id} has invalid mass or friction values.");
            if (item.Primitive is not ("room" or "box" or "cylinder" or "sphere" or "mesh")) throw new InvalidDataException($"World object {item.Id} has unsupported primitive '{item.Primitive}'.");
        }
        var bounds = root.GetProperty("bounds_m").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        var gravity = root.GetProperty("gravity_mps2").EnumerateArray().Select(x => x.GetDouble()).ToArray();
        if (bounds.Length != 3 || bounds.Any(value => !double.IsFinite(value) || value <= 0)) throw new InvalidDataException("World bounds must contain three positive finite metre values.");
        if (gravity.Length != 3 || gravity.Any(value => !double.IsFinite(value))) throw new InvalidDataException("World gravity must contain three finite SI values.");
        return new WorldDefinition(root.GetProperty("id").GetString()!,
            bounds, gravity, objects,
            root.TryGetProperty("display_name", out var worldName) ? worldName.GetString() ?? "" : "",
            schemaVersion);
    }

    private static CameraProperties ParseCameraProperties(JsonElement element) => new(
        element.TryGetProperty("horizontal_fov_deg", out var fov) ? fov.GetDouble() : 75,
        element.TryGetProperty("resolution_width_px", out var width) ? width.GetInt32() : 640,
        element.TryGetProperty("resolution_height_px", out var height) ? height.GetInt32() : 480,
        element.TryGetProperty("near_clip_m", out var near) ? near.GetDouble() : 0.05,
        element.TryGetProperty("far_clip_m", out var far) ? far.GetDouble() : 100,
        !element.TryGetProperty("enabled", out var enabled) || enabled.GetBoolean());
}
