using System.Numerics;

namespace RoboWorld.Domain;

/// <summary>Engine-neutral semantic validation shared by the shop, project loader and simulation adapter.</summary>
public static class RobotGraphValidator
{
    public static bool CanMate(MountPortDefinition first, MountPortDefinition second)
    {
        var kindCompatible = first.MateKinds.Contains(second.Kind, StringComparer.Ordinal)
            || second.MateKinds.Contains(first.Kind, StringComparer.Ordinal)
            || string.Equals(first.Kind, second.Kind, StringComparison.Ordinal);
        var sizeCompatible = string.IsNullOrEmpty(first.SizeClass) || string.IsNullOrEmpty(second.SizeClass)
            || string.Equals(first.SizeClass, second.SizeClass, StringComparison.Ordinal);
        return kindCompatible && sizeCompatible;
    }

    public static (double[] PositionM, double[] RotationXyzw) GetComponentMountFrame(ComponentInstance instance,
        ComponentDefinition definition, MountPortDefinition port)
    {
        var size = instance.SizeOverrideM ?? definition.SizeM;
        return (port.FramePositionM.Select((value, axis) => value * size[axis] / definition.SizeM[axis]).ToArray(), [.. port.FrameRotationXyzw]);
    }

    public static void Validate(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        EnsureUnique(graph.Links.Select(link => link.Id), "link");
        EnsureUnique(graph.Components.Select(component => component.Id), "component instance");
        EnsureUnique(graph.Joints.Select(joint => joint.Id), "joint");
        EnsureUnique(graph.ElectricalConnections.Select(connection => connection.Id), "electrical connection");

        var links = graph.Links.ToDictionary(link => link.Id, StringComparer.Ordinal);
        if (!links.TryGetValue(graph.RootLinkId, out var root) || !root.IsRoot || graph.Links.Count(link => link.IsRoot) != 1)
            throw new InvalidDataException("Assembly must declare exactly one valid root link.");
        var instances = graph.Components.ToDictionary(component => component.Id, StringComparer.Ordinal);
        foreach (var link in graph.Links) ValidatePose(link.PositionM, link.RotationXyzw, $"Link {link.Id}");
        foreach (var link in graph.Links)
        {
            if (!instances.TryGetValue(link.BaseComponentId, out var baseComponent) || baseComponent.LinkId != link.Id)
                throw new InvalidDataException($"Link {link.Id} must be based on a component instance attached to that link.");
        }
        foreach (var component in graph.Components)
        {
            if (!links.ContainsKey(component.LinkId)) throw new InvalidDataException($"Component {component.Id} references missing link {component.LinkId}.");
            if (!catalogue.TryGetValue(component.CatalogueId, out var definition)) throw new InvalidDataException($"Component {component.Id} references missing catalogue id {component.CatalogueId}.");
            if (definition.Version != component.CatalogueVersion) throw new InvalidDataException($"Component {component.Id} requires {component.CatalogueId}@{component.CatalogueVersion}, found {definition.Version}.");
            if (!double.IsFinite(definition.MassKg) || definition.MassKg < 0 || definition.SizeM.Length != 3 || definition.SizeM.Any(value => !double.IsFinite(value) || value <= 0))
                throw new InvalidDataException($"Component profile {definition.Id} must use a finite nonnegative mass in kg and three positive dimensions in m.");
            if (component.SizeOverrideM is { } sizeOverride && (sizeOverride.Length != 3 || sizeOverride.Any(value => !double.IsFinite(value) || value <= 0 || value > 10)))
                throw new InvalidDataException($"Component {component.Id} size override must contain three finite dimensions in the range (0, 10] m.");
            if (component.DisplayName is { } displayName && (string.IsNullOrWhiteSpace(displayName) || displayName.Length > 80))
                throw new InvalidDataException($"Component {component.Id} display name must contain 1 to 80 characters.");
            if (component.Camera is { } camera)
            {
                if (catalogue[component.CatalogueId].Model != "rgb_camera") throw new InvalidDataException($"Component {component.Id} cannot store camera settings because it is not a camera.");
                if (!double.IsFinite(camera.HorizontalFovDeg) || camera.HorizontalFovDeg is < 10 or > 170
                    || camera.ResolutionWidthPx is < 16 or > 4096 || camera.ResolutionHeightPx is < 16 or > 4096
                    || !double.IsFinite(camera.NearClipM) || !double.IsFinite(camera.FarClipM)
                    || camera.NearClipM <= 0 || camera.FarClipM <= camera.NearClipM)
                    throw new InvalidDataException($"Camera {component.Id} settings are outside supported FOV, resolution or clipping ranges.");
            }
            ValidatePose(component.PositionM, component.RotationXyzw, $"Component {component.Id}");
            foreach (var port in definition.MountPorts)
                ValidatePose(port.FramePositionM, port.FrameRotationXyzw, $"Mount port {definition.Id}.{port.Id}");
        }

        var incoming = new Dictionary<string, string>(StringComparer.Ordinal);
        var usedMechanicalPorts = new HashSet<(string ComponentId, string PortId)>();
        foreach (var joint in graph.Joints)
        {
            if (!links.ContainsKey(joint.ParentLinkId) || !links.ContainsKey(joint.ChildLinkId)) throw new InvalidDataException($"Joint {joint.Id} references a missing link.");
            if (joint.ParentLinkId == joint.ChildLinkId) throw new InvalidDataException($"Joint {joint.Id} cannot connect a link to itself.");
            if (joint.AxisLocal.Length != 3 || joint.AxisLocal.Any(value => !double.IsFinite(value))) throw new InvalidDataException($"Joint {joint.Id} axis must contain three finite values.");
            var axisLength = Math.Sqrt(joint.AxisLocal.Sum(value => value * value));
            if (Math.Abs(axisLength - 1d) > 1e-9) throw new InvalidDataException($"Joint {joint.Id} axis was not normalized by the loader.");
            if ((joint.JointProfileId is null) != (joint.JointProfileVersion is null))
                throw new InvalidDataException($"Joint {joint.Id} must pin both a joint profile id and version or neither.");
            if (joint.JointProfileId is not null)
            {
                if (!catalogue.TryGetValue(joint.JointProfileId, out var profile)) throw new InvalidDataException($"Joint {joint.Id} references missing profile {joint.JointProfileId}.");
                if (profile.Version != joint.JointProfileVersion) throw new InvalidDataException($"Joint {joint.Id} requires {joint.JointProfileId}@{joint.JointProfileVersion}, found {profile.Version}.");
                if (profile.Model != "joint_" + joint.Type) throw new InvalidDataException($"Joint {joint.Id} type {joint.Type} is incompatible with profile {joint.JointProfileId} model {profile.Model}.");
            }
            if (!incoming.TryAdd(joint.ChildLinkId, joint.ParentLinkId)) throw new InvalidDataException($"Link {joint.ChildLinkId} has multiple parent joints.");
            ValidatePose(joint.ParentFramePositionM, joint.ParentFrameRotationXyzw, $"Joint {joint.Id} parent frame");
            ValidatePose(joint.ChildFramePositionM, joint.ChildFrameRotationXyzw, $"Joint {joint.Id} child frame");
            ValidateJointPorts(joint, links, instances, catalogue, usedMechanicalPorts);
        }
        if (incoming.ContainsKey(graph.RootLinkId)) throw new InvalidDataException("The root link cannot have a parent joint.");
        foreach (var link in graph.Links.Where(link => link.Id != graph.RootLinkId))
            if (!incoming.ContainsKey(link.Id)) throw new InvalidDataException($"Link {link.Id} is mechanically disconnected from the root.");
        foreach (var link in graph.Links) Visit(link.Id, graph.RootLinkId, incoming, new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));

        ValidateComponentMountConnections(graph.ComponentMountConnections ?? [], links, instances, catalogue, usedMechanicalPorts);
        ValidateElectricalConnections(graph.ElectricalConnections, instances, catalogue);
    }

    private static void ValidateComponentMountConnections(IReadOnlyList<ComponentMountConnection> connections,
        IReadOnlyDictionary<string, RobotLink> links, IReadOnlyDictionary<string, ComponentInstance> instances,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue, HashSet<(string ComponentId, string PortId)> usedPorts)
    {
        EnsureUnique(connections.Select(connection => connection.Id), "component mount connection");
        foreach (var connection in connections)
        {
            if (!usedPorts.Add((connection.ParentComponentId, connection.ParentPortId))
                || !usedPorts.Add((connection.ChildComponentId, connection.ChildPortId)))
                throw new InvalidDataException($"Component mount {connection.Id} reuses an already occupied connector port.");
            if (connection.ParentComponentId == connection.ChildComponentId)
                throw new InvalidDataException($"Component mount {connection.Id} cannot connect a component to itself.");
            if (!instances.TryGetValue(connection.ParentComponentId, out var parent) || !instances.TryGetValue(connection.ChildComponentId, out var child))
                throw new InvalidDataException($"Component mount {connection.Id} references a missing component instance.");
            var parentDefinition = catalogue[parent.CatalogueId];
            var childDefinition = catalogue[child.CatalogueId];
            var parentPort = parentDefinition.MountPorts.SingleOrDefault(port => port.Id == connection.ParentPortId)
                ?? throw new InvalidDataException($"Component mount {connection.Id} cannot find {parent.Id}.{connection.ParentPortId}.");
            var childPort = childDefinition.MountPorts.SingleOrDefault(port => port.Id == connection.ChildPortId)
                ?? throw new InvalidDataException($"Component mount {connection.Id} cannot find {child.Id}.{connection.ChildPortId}.");
            if (!CanMate(parentPort, childPort))
                throw new InvalidDataException($"Incompatible component mount ports: {parent.Id}.{parentPort.Id} ({parentPort.Kind}/{parentPort.SizeClass}) cannot mate with {child.Id}.{childPort.Id} ({childPort.Kind}/{childPort.SizeClass}).");

            var parentLink = links[parent.LinkId];
            var childLink = links[child.LinkId];
            var parentPortFrame = GetComponentMountFrame(parent, parentDefinition, parentPort);
            var childPortFrame = GetComponentMountFrame(child, childDefinition, childPort);
            var parentTransform = Compose(parentLink.PositionM, parentLink.RotationXyzw, parent.PositionM, parent.RotationXyzw);
            var parentFrame = Compose(parentTransform.Position, parentTransform.Rotation, parentPortFrame.PositionM, parentPortFrame.RotationXyzw);
            var childTransform = Compose(childLink.PositionM, childLink.RotationXyzw, child.PositionM, child.RotationXyzw);
            var childFrame = Compose(childTransform.Position, childTransform.Rotation, childPortFrame.PositionM, childPortFrame.RotationXyzw);
            if (Vector3.Distance(parentFrame.Position, childFrame.Position) > 1e-4f || Math.Abs(Quaternion.Dot(parentFrame.Rotation, childFrame.Rotation)) < 0.9999f)
                throw new InvalidDataException($"Component mount {connection.Id} connector frames do not coincide; check component transforms.");
        }
    }

    private static void ValidateJointPorts(RobotJoint joint, IReadOnlyDictionary<string, RobotLink> links,
        IReadOnlyDictionary<string, ComponentInstance> instances, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        HashSet<(string ComponentId, string PortId)> usedPorts)
    {
        if (joint.ParentPortId is null && joint.ChildPortId is null) return;
        if (joint.ParentPortId is null || joint.ChildPortId is null) throw new InvalidDataException($"Joint {joint.Id} must name both mating ports or neither.");
        var parentComponentId = joint.ParentComponentId ?? links[joint.ParentLinkId].BaseComponentId;
        var childComponentId = joint.ChildComponentId ?? links[joint.ChildLinkId].BaseComponentId;
        if (!usedPorts.Add((parentComponentId, joint.ParentPortId)) || !usedPorts.Add((childComponentId, joint.ChildPortId)))
            throw new InvalidDataException($"Joint {joint.Id} reuses an already occupied connector port.");
        if (!instances.TryGetValue(parentComponentId, out var parentComponent) || parentComponent.LinkId != joint.ParentLinkId)
            throw new InvalidDataException($"Joint {joint.Id} parent component {parentComponentId} is not attached to link {joint.ParentLinkId}.");
        if (!instances.TryGetValue(childComponentId, out var childComponent) || childComponent.LinkId != joint.ChildLinkId)
            throw new InvalidDataException($"Joint {joint.Id} child component {childComponentId} is not attached to link {joint.ChildLinkId}.");
        var parentDefinition = catalogue[parentComponent.CatalogueId];
        var childDefinition = catalogue[childComponent.CatalogueId];
        var parentPort = parentDefinition.MountPorts.SingleOrDefault(port => port.Id == joint.ParentPortId)
            ?? throw new InvalidDataException($"Joint {joint.Id} cannot find {parentComponent.Id}.{joint.ParentPortId}.");
        var childPort = childDefinition.MountPorts.SingleOrDefault(port => port.Id == joint.ChildPortId)
            ?? throw new InvalidDataException($"Joint {joint.Id} cannot find {childComponent.Id}.{joint.ChildPortId}.");
        if (!CanMate(parentPort, childPort)) throw new InvalidDataException($"Incompatible mount ports: {parentComponent.Id}.{parentPort.Id} ({parentPort.Kind}/{parentPort.SizeClass}) cannot mate with {childComponent.Id}.{childPort.Id} ({childPort.Kind}/{childPort.SizeClass}).");

        var parentPortFrame = GetComponentMountFrame(parentComponent, parentDefinition, parentPort);
        var childPortFrame = GetComponentMountFrame(childComponent, childDefinition, childPort);
        var parentFrame = Compose(parentComponent.PositionM, parentComponent.RotationXyzw, parentPortFrame.PositionM, parentPortFrame.RotationXyzw);
        var childFrame = Compose(childComponent.PositionM, childComponent.RotationXyzw, childPortFrame.PositionM, childPortFrame.RotationXyzw);
        RequireFrameMatches(parentFrame, joint.ParentFramePositionM, joint.ParentFrameRotationXyzw, $"Joint {joint.Id} parent mount");
        RequireFrameMatches(childFrame, joint.ChildFramePositionM, joint.ChildFrameRotationXyzw, $"Joint {joint.Id} child mount");
        var parentLink = links[joint.ParentLinkId];
        var childLink = links[joint.ChildLinkId];
        var parentWorld = Compose(parentLink.PositionM, parentLink.RotationXyzw, joint.ParentFramePositionM, joint.ParentFrameRotationXyzw);
        var childWorld = Compose(childLink.PositionM, childLink.RotationXyzw, joint.ChildFramePositionM, joint.ChildFrameRotationXyzw);
        if (Vector3.Distance(parentWorld.Position, childWorld.Position) > 1e-4f || Math.Abs(Quaternion.Dot(parentWorld.Rotation, childWorld.Rotation)) < 0.9999f)
            throw new InvalidDataException($"Joint {joint.Id} mount frames do not coincide in world space; check connector positions and rotations.");
    }

    private static void ValidatePose(double[] position, double[] rotation, string label)
    {
        if (position.Length != 3 || position.Any(value => !double.IsFinite(value))) throw new InvalidDataException($"{label} position must contain three finite values in m.");
        if (rotation.Length != 4 || rotation.Any(value => !double.IsFinite(value))) throw new InvalidDataException($"{label} rotation must contain four finite quaternion values.");
        var norm = Math.Sqrt(rotation.Sum(value => value * value));
        if (Math.Abs(norm - 1d) > 1e-6) throw new InvalidDataException($"{label} rotation quaternion must be normalized.");
    }

    private static (Vector3 Position, Quaternion Rotation) Compose(ComponentInstance instance, MountPortDefinition port) =>
        Compose(instance.PositionM, instance.RotationXyzw, port.FramePositionM, port.FrameRotationXyzw);

    private static (Vector3 Position, Quaternion Rotation) Compose(Vector3 parentPosition, Quaternion parentRotation, double[] localPosition, double[] localRotation)
    {
        var localQuaternion = ToQuaternion(localRotation);
        return (parentPosition + Vector3.Transform(ToVector(localPosition), parentRotation), Quaternion.Normalize(parentRotation * localQuaternion));
    }

    private static (Vector3 Position, Quaternion Rotation) Compose(double[] parentPosition, double[] parentRotation, double[] localPosition, double[] localRotation)
    {
        var parentPositionVector = ToVector(parentPosition);
        var parentQuaternion = ToQuaternion(parentRotation);
        var localPositionVector = ToVector(localPosition);
        var localQuaternion = ToQuaternion(localRotation);
        return (parentPositionVector + Vector3.Transform(localPositionVector, parentQuaternion), Quaternion.Normalize(parentQuaternion * localQuaternion));
    }

    private static void RequireFrameMatches((Vector3 Position, Quaternion Rotation) actual, double[] expectedPosition, double[] expectedRotation, string label)
    {
        if (Vector3.Distance(actual.Position, ToVector(expectedPosition)) > 1e-4f || Math.Abs(Quaternion.Dot(actual.Rotation, ToQuaternion(expectedRotation))) < 0.9999f)
            throw new InvalidDataException($"{label} does not match its catalogue connector frame (actual position {actual.Position}, expected position {ToVector(expectedPosition)}).");
    }

    private static Vector3 ToVector(double[] values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Quaternion ToQuaternion(double[] values) => Quaternion.Normalize(new Quaternion((float)values[0], (float)values[1], (float)values[2], (float)values[3]));

    private static void ValidateElectricalConnections(IReadOnlyList<ElectricalConnection> connections,
        IReadOnlyDictionary<string, ComponentInstance> instances, IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        foreach (var connection in connections)
        {
            var from = ResolvePort(connection.From);
            var to = ResolvePort(connection.To);
            var fromType = catalogue[instances[from.Instance].CatalogueId].ElectricalPorts.SingleOrDefault(port => port.Id == from.Port)
                ?? throw new InvalidDataException($"Electrical connection {connection.Id} references missing port {connection.From}.");
            var toType = catalogue[instances[to.Instance].CatalogueId].ElectricalPorts.SingleOrDefault(port => port.Id == to.Port)
                ?? throw new InvalidDataException($"Electrical connection {connection.Id} references missing port {connection.To}.");
            if (connection.Interface != fromType.Interface || connection.Interface != toType.Interface)
                throw new InvalidDataException($"Electrical connection {connection.Id} declares {connection.Interface}, but {connection.From} and {connection.To} expose {fromType.Interface} and {toType.Interface}.");
            var declaredPins = new[] { fromType.Pin, toType.Pin }.Where(pin => pin is not null).ToArray();
            if (declaredPins.Length > 0 && (connection.PhysicalPin is null || !declaredPins.Contains(connection.PhysicalPin, StringComparer.Ordinal)))
                throw new InvalidDataException($"Electrical connection {connection.Id} must select one of its endpoint's declared physical pins ({string.Join(", ", declaredPins)}).");
            if (connection.PhysicalPin is not null && declaredPins.Length == 0)
                throw new InvalidDataException($"Electrical connection {connection.Id} assigns physical pin {connection.PhysicalPin} to endpoints without a pin-capable component port.");
            if (connection.BusId is not null && !System.Text.RegularExpressions.Regex.IsMatch(connection.BusId, "^[A-Za-z][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException($"Electrical connection {connection.Id} has an invalid bus id.");
            if (connection.From == connection.To) throw new InvalidDataException($"Electrical connection {connection.Id} cannot connect a port to itself.");
        }
    }

    private static (string Instance, string Port) ResolvePort(string endpoint)
    {
        var separator = endpoint.IndexOf('.');
        if (separator <= 0 || separator == endpoint.Length - 1) throw new InvalidDataException($"Electrical endpoint '{endpoint}' must use component.port format.");
        var instance = endpoint[..separator];
        if (endpoint.IndexOf('.', separator + 1) < 0) return (instance, endpoint[(separator + 1)..]);
        throw new InvalidDataException($"Electrical endpoint '{endpoint}' has more than one separator.");
    }

    private static void Visit(string linkId, string rootId, IReadOnlyDictionary<string, string> incoming, HashSet<string> path, HashSet<string> complete)
    {
        if (complete.Contains(linkId)) return;
        if (!path.Add(linkId)) throw new InvalidDataException($"Mechanical graph contains a cycle at link {linkId}.");
        if (linkId != rootId)
        {
            if (!incoming.TryGetValue(linkId, out var parent)) throw new InvalidDataException($"Link {linkId} is disconnected from root {rootId}.");
            Visit(parent, rootId, incoming, path, complete);
        }
        path.Remove(linkId);
        complete.Add(linkId);
    }

    private static void EnsureUnique(IEnumerable<string> ids, string kind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            if (!seen.Add(id)) throw new InvalidDataException($"Duplicate {kind} id: {id}.");
    }
}
