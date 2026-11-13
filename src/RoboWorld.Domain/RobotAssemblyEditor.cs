using System.Numerics;

namespace RoboWorld.Domain;

/// <summary>Pure graph operations used by the shop editor to attach versioned parts at compatible catalogue ports.</summary>
public static class RobotAssemblyEditor
{
    public static AssemblyGraph SetComponentDisplayName(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string componentId, string displayName)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Trim().Length > 80) throw new InvalidDataException("Component display name must contain 1 to 80 characters.");
        var components = graph.Components.ToArray();
        var index = Array.FindIndex(components, item => item.Id == componentId);
        if (index < 0) throw new InvalidDataException($"Cannot rename missing component {componentId}.");
        components[index] = components[index] with { DisplayName = displayName.Trim() };
        var updated = graph with { Components = components };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static AssemblyGraph SetCameraProperties(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string componentId, CameraProperties camera)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var components = graph.Components.ToArray();
        var index = Array.FindIndex(components, item => item.Id == componentId);
        if (index < 0 || catalogue[components[index].CatalogueId].Model != "rgb_camera") throw new InvalidDataException($"Component {componentId} is not a camera.");
        components[index] = components[index] with { Camera = camera };
        var updated = graph with { Components = components };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static AssemblyGraph RemoveComponent(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string componentId)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var component = graph.Components.SingleOrDefault(item => item.Id == componentId)
            ?? throw new InvalidDataException($"Cannot remove missing component {componentId}.");
        if (componentId == graph.Links.Single(link => link.Id == graph.RootLinkId).BaseComponentId)
            throw new InvalidDataException("The root-link base component cannot be removed.");

        var links = graph.Links.ToDictionary(link => link.Id, StringComparer.Ordinal);
        var removedComponents = new HashSet<string>(StringComparer.Ordinal);
        var removedLinks = new HashSet<string>(StringComparer.Ordinal);

        void RemoveLinkSubtree(string linkId)
        {
            if (linkId == graph.RootLinkId || !removedLinks.Add(linkId)) return;
            foreach (var linkComponent in graph.Components.Where(item => item.LinkId == linkId).ToArray())
                RemoveComponentTree(linkComponent.Id);
        }

        void RemoveComponentTree(string id)
        {
            if (!removedComponents.Add(id)) return;
            foreach (var connection in (graph.ComponentMountConnections ?? []).Where(item => item.ParentComponentId == id))
                RemoveComponentTree(connection.ChildComponentId);
            foreach (var joint in graph.Joints)
            {
                var parentId = joint.ParentComponentId ?? links[joint.ParentLinkId].BaseComponentId;
                var childId = joint.ChildComponentId ?? links[joint.ChildLinkId].BaseComponentId;
                if (parentId == id || childId == id) RemoveLinkSubtree(joint.ChildLinkId);
            }
            var instance = graph.Components.Single(item => item.Id == id);
            if (instance.Id == links[instance.LinkId].BaseComponentId) RemoveLinkSubtree(instance.LinkId);
        }

        RemoveComponentTree(component.Id);
        var joints = graph.Joints.Where(joint => !removedLinks.Contains(joint.ParentLinkId) && !removedLinks.Contains(joint.ChildLinkId)
            && (joint.ParentComponentId is null || !removedComponents.Contains(joint.ParentComponentId))
            && (joint.ChildComponentId is null || !removedComponents.Contains(joint.ChildComponentId))).ToArray();
        var electricalConnections = graph.ElectricalConnections.Where(connection =>
            !removedComponents.Any(id => connection.From.StartsWith(id + ".", StringComparison.Ordinal)
                || connection.To.StartsWith(id + ".", StringComparison.Ordinal))).ToArray();
        var updated = graph with
        {
            Links = graph.Links.Where(link => !removedLinks.Contains(link.Id)).ToArray(),
            Components = graph.Components.Where(item => !removedComponents.Contains(item.Id)).ToArray(),
            Joints = joints,
            ElectricalConnections = electricalConnections,
            ComponentMountConnections = (graph.ComponentMountConnections ?? []).Where(connection =>
                !removedComponents.Contains(connection.ParentComponentId) && !removedComponents.Contains(connection.ChildComponentId)).ToArray()
        };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static AssemblyGraph AddElectricalConnection(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string fromEndpoint, string toEndpoint, string? busId = null, string? connectionId = null)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        if (string.IsNullOrWhiteSpace(connectionId))
            connectionId = UniqueId($"wire-{fromEndpoint.Replace('.', '-')}-{toEndpoint.Replace('.', '-')}", graph.ElectricalConnections.Select(item => item.Id));
        if (graph.ElectricalConnections.Any(item => item.Id == connectionId))
            throw new InvalidDataException($"Electrical connection id already exists: {connectionId}.");

        var from = ResolveElectricalPort(graph, catalogue, fromEndpoint);
        var to = ResolveElectricalPort(graph, catalogue, toEndpoint);
        if (from.Port.Interface != to.Port.Interface)
            throw new InvalidDataException($"Electrical ports {fromEndpoint} and {toEndpoint} expose incompatible interfaces {from.Port.Interface} and {to.Port.Interface}.");
        var physicalPin = from.Port.Pin ?? to.Port.Pin;
        if (from.Port.Pin is not null && to.Port.Pin is not null && from.Port.Pin != to.Port.Pin)
            throw new InvalidDataException($"Electrical ports {fromEndpoint} and {toEndpoint} declare different physical pins.");

        var connection = new ElectricalConnection(connectionId, fromEndpoint, toEndpoint, from.Port.Interface,
            physicalPin, string.IsNullOrWhiteSpace(busId) ? null : busId.Trim());
        var updated = graph with { ElectricalConnections = graph.ElectricalConnections.Append(connection).ToArray() };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static AssemblyGraph RemoveElectricalConnection(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string connectionId)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        if (!graph.ElectricalConnections.Any(item => item.Id == connectionId))
            throw new InvalidDataException($"Cannot remove missing electrical connection {connectionId}.");
        var updated = graph with { ElectricalConnections = graph.ElectricalConnections.Where(item => item.Id != connectionId).ToArray() };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    private static (ComponentInstance Instance, ElectricalPortDefinition Port) ResolveElectricalPort(AssemblyGraph graph,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue, string endpoint)
    {
        var separator = endpoint.IndexOf('.');
        if (separator <= 0 || separator == endpoint.Length - 1 || endpoint.IndexOf('.', separator + 1) >= 0)
            throw new InvalidDataException($"Electrical endpoint '{endpoint}' must use component.port format.");
        var instanceId = endpoint[..separator];
        var portId = endpoint[(separator + 1)..];
        var instance = graph.Components.SingleOrDefault(item => item.Id == instanceId)
            ?? throw new InvalidDataException($"Electrical endpoint '{endpoint}' references missing component {instanceId}.");
        var definition = catalogue[instance.CatalogueId];
        var port = definition.ElectricalPorts.SingleOrDefault(item => item.Id == portId)
            ?? throw new InvalidDataException($"Electrical endpoint '{endpoint}' references missing port {portId}.");
        return (instance, port);
    }

    public static AssemblyGraph SetComponentSize(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string componentId, double[] sizeM)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var index = graph.Components.ToList().FindIndex(component => component.Id == componentId);
        if (index < 0) throw new InvalidDataException($"Cannot resize missing component {componentId}.");
        if (sizeM.Length != 3 || sizeM.Any(value => !double.IsFinite(value) || value <= 0 || value > 10))
            throw new InvalidDataException("Component size must contain three finite dimensions in the range (0, 10] m.");
        var components = graph.Components.ToArray();
        var resized = components[index] with { SizeOverrideM = [.. sizeM] };
        var connection = (graph.ComponentMountConnections ?? []).SingleOrDefault(item => item.ChildComponentId == componentId);
        if (connection is not null)
        {
            var parent = components.Single(item => item.Id == connection.ParentComponentId);
            var parentDefinition = catalogue[parent.CatalogueId];
            var parentPort = parentDefinition.MountPorts.Single(port => port.Id == connection.ParentPortId);
            var childDefinition = catalogue[resized.CatalogueId];
            var childPort = childDefinition.MountPorts.Single(port => port.Id == connection.ChildPortId);
            resized = SnapToPort(graph, parent, parentDefinition, parentPort, resized, childDefinition, childPort);
        }
        components[index] = resized;
        var links = graph.Links.ToArray();
        var joints = graph.Joints.ToArray();
        var resizedLink = links.Single(link => link.Id == resized.LinkId);
        var baseLinkComponentId = resizedLink.BaseComponentId;
        var resizedDefinition = catalogue[resized.CatalogueId];

        for (var jointIndex = 0; jointIndex < joints.Length; jointIndex++)
        {
            var joint = joints[jointIndex];
            if (joint.ChildLinkId == resized.LinkId && (joint.ChildComponentId ?? baseLinkComponentId) == resized.Id && joint.ChildPortId is not null)
            {
                var childPort = resizedDefinition.MountPorts.Single(port => port.Id == joint.ChildPortId);
                var childFrame = RobotGraphValidator.GetComponentMountFrame(resized, resizedDefinition, childPort);
                var parentLink = links.Single(link => link.Id == joint.ParentLinkId);
                var targetWorldFrame = Compose(parentLink.PositionM, parentLink.RotationXyzw,
                    joint.ParentFramePositionM, joint.ParentFrameRotationXyzw);
                links[Array.FindIndex(links, link => link.Id == joint.ChildLinkId)] = AlignLink(link: links.Single(link => link.Id == joint.ChildLinkId),
                    targetWorldFrame.Position, targetWorldFrame.Rotation, childFrame.PositionM, childFrame.RotationXyzw);
                joints[jointIndex] = joint with { ChildFramePositionM = childFrame.PositionM, ChildFrameRotationXyzw = childFrame.RotationXyzw };
            }
        }

        for (var jointIndex = 0; jointIndex < joints.Length; jointIndex++)
        {
            var joint = joints[jointIndex];
            if (joint.ParentLinkId != resized.LinkId || (joint.ParentComponentId ?? baseLinkComponentId) != resized.Id || joint.ParentPortId is null) continue;
            var parentPort = resizedDefinition.MountPorts.Single(port => port.Id == joint.ParentPortId);
            var parentPortFrame = RobotGraphValidator.GetComponentMountFrame(resized, resizedDefinition, parentPort);
            var parentFrame = Compose(resized.PositionM, resized.RotationXyzw, parentPortFrame.PositionM, parentPortFrame.RotationXyzw);
            var parentLink = links.Single(link => link.Id == joint.ParentLinkId);
            var targetWorldFrame = Compose(parentLink.PositionM, parentLink.RotationXyzw, ToArray(parentFrame.Position), ToArray(parentFrame.Rotation));
            var childLinkIndex = Array.FindIndex(links, link => link.Id == joint.ChildLinkId);
            var childLink = links[childLinkIndex];
            childLink = AlignLink(childLink, targetWorldFrame.Position, targetWorldFrame.Rotation, joint.ChildFramePositionM, joint.ChildFrameRotationXyzw);
            links[childLinkIndex] = childLink;
            joints[jointIndex] = joint with { ParentFramePositionM = ToArray(parentFrame.Position), ParentFrameRotationXyzw = ToArray(parentFrame.Rotation) };
        }

        var updated = graph with { Components = components, Links = links, Joints = joints };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static AssemblyGraph AddFreeComponent(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string catalogueId, string componentId, double[] positionM, double[] rotationXyzw)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        if (graph.Components.Any(component => component.Id == componentId))
            throw new InvalidDataException($"Component instance id already exists: {componentId}.");
        if (!catalogue.TryGetValue(catalogueId, out var definition))
            throw new InvalidDataException($"Missing catalogue component {catalogueId}.");
        ValidateComponentPose(positionM, rotationXyzw);
        var instance = new ComponentInstance(componentId, catalogueId, definition.Version, graph.RootLinkId, [.. positionM], NormalizeQuaternion(rotationXyzw));
        var updated = graph with { Components = graph.Components.Append(instance).ToArray() };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static AssemblyGraph SetComponentPose(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string componentId, double[] positionM, double[] rotationXyzw)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var componentIndex = graph.Components.ToList().FindIndex(component => component.Id == componentId);
        if (componentIndex < 0) throw new InvalidDataException($"Cannot reposition missing component {componentId}.");
        var component = graph.Components[componentIndex];
        var link = graph.Links.Single(item => item.Id == component.LinkId);
        if (component.Id == link.BaseComponentId
            || (graph.ComponentMountConnections ?? []).Any(connection => connection.ParentComponentId == componentId || connection.ChildComponentId == componentId)
            || graph.Joints.Any(joint => (joint.ParentComponentId ?? graph.Links.Single(item => item.Id == joint.ParentLinkId).BaseComponentId) == componentId
                || (joint.ChildComponentId ?? graph.Links.Single(item => item.Id == joint.ChildLinkId).BaseComponentId) == componentId))
            throw new InvalidDataException($"Component {componentId} is mechanically constrained; edit its compatible connector or joint instead of moving its pose.");
        ValidateComponentPose(positionM, rotationXyzw);
        var components = graph.Components.ToArray();
        components[componentIndex] = component with { PositionM = [.. positionM], RotationXyzw = NormalizeQuaternion(rotationXyzw) };
        var updated = graph with { Components = components };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    /// <summary>Moves the root link and all descendants as one rigid assembly, preserving every joint frame.</summary>
    public static AssemblyGraph SetRootPose(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        double[] positionM, double[] rotationXyzw)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        ValidateComponentPose(positionM, rotationXyzw);
        rotationXyzw = NormalizeQuaternion(rotationXyzw);
        var root = graph.Links.Single(link => link.Id == graph.RootLinkId);
        var oldRotation = ToQuaternion(root.RotationXyzw);
        var newRotation = ToQuaternion(rotationXyzw);
        var deltaRotation = Quaternion.Normalize(newRotation * Quaternion.Inverse(oldRotation));
        var oldPosition = ToVector(root.PositionM);
        var newPosition = ToVector(positionM);
        var links = graph.Links.Select(link =>
        {
            var worldPosition = newPosition + Vector3.Transform(ToVector(link.PositionM) - oldPosition, deltaRotation);
            var worldRotation = Quaternion.Normalize(deltaRotation * ToQuaternion(link.RotationXyzw));
            return link with { PositionM = ToArray(worldPosition), RotationXyzw = ToArray(worldRotation) };
        }).ToArray();
        var updated = graph with { Links = links };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    /// <summary>Associates a catalogue motor with an existing rotating wheel joint without depending on instance names.</summary>
    public static AssemblyGraph SetJointActuator(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string jointId, string motorComponentId)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var jointIndex = graph.Joints.ToList().FindIndex(joint => joint.Id == jointId);
        if (jointIndex < 0) throw new InvalidDataException($"Cannot bind actuator to missing joint {jointId}.");
        var joint = graph.Joints[jointIndex];
        if (joint.Type is not ("revolute" or "continuous"))
            throw new InvalidDataException($"Motor actuation requires a revolute or continuous joint; {jointId} is {joint.Type}.");
        var motor = graph.Components.SingleOrDefault(component => component.Id == motorComponentId)
            ?? throw new InvalidDataException($"Cannot bind missing motor component {motorComponentId}.");
        if (catalogue[motor.CatalogueId].Model != "dc_motor")
            throw new InvalidDataException($"Component {motorComponentId} is not a motor supported by the physics adapter.");
        if (!graph.Components.Any(component => component.LinkId == joint.ChildLinkId
                && catalogue[component.CatalogueId].Model == "wheel"))
            throw new InvalidDataException($"Joint {jointId} does not drive a wheel link.");
        if (graph.Joints.Where((candidate, index) => index != jointIndex).Any(candidate => candidate.ActuatorComponentId == motorComponentId))
            throw new InvalidDataException($"Motor {motorComponentId} is already assigned to another joint.");
        if (joint.ActuatorComponentId is not null && joint.ActuatorComponentId != motorComponentId)
            throw new InvalidDataException($"Joint {jointId} is already assigned to motor {joint.ActuatorComponentId}.");
        var joints = graph.Joints.ToArray();
        joints[jointIndex] = joint with { ActuatorComponentId = motorComponentId };
        var updated = graph with { Joints = joints };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    /// <summary>Attaches a currently free component to a compatible port without changing its instance identity or electrical wiring.</summary>
    public static AssemblyGraph AttachExistingComponent(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string parentComponentId, string parentPortId, string childComponentId, string childPortId,
        string jointType, string jointProfileId, string jointId, double[]? axisLocal = null)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var child = graph.Components.SingleOrDefault(component => component.Id == childComponentId)
            ?? throw new InvalidDataException($"Cannot attach missing component {childComponentId}.");
        var root = graph.Links.Single(link => link.Id == graph.RootLinkId);
        if (child.LinkId != graph.RootLinkId || child.Id == root.BaseComponentId
            || graph.Joints.Any(joint => joint.ParentComponentId == child.Id || joint.ChildComponentId == child.Id)
            || (graph.ComponentMountConnections ?? []).Any(connection => connection.ParentComponentId == child.Id || connection.ChildComponentId == child.Id))
            throw new InvalidDataException($"Component {child.Id} is not a free part; disconnect or remove its dependent assembly first.");
        var parent = graph.Components.SingleOrDefault(component => component.Id == parentComponentId)
            ?? throw new InvalidDataException($"Cannot attach to missing component {parentComponentId}.");
        var parentDefinition = catalogue[parent.CatalogueId];
        var childDefinition = catalogue[child.CatalogueId];
        var parentPort = parentDefinition.MountPorts.SingleOrDefault(port => port.Id == parentPortId)
            ?? throw new InvalidDataException($"Component {parent.Id} has no mount port {parentPortId}.");
        var childPort = childDefinition.MountPorts.SingleOrDefault(port => port.Id == childPortId)
            ?? throw new InvalidDataException($"Component {child.Id} has no mount port {childPortId}.");
        var jointProfile = ValidateJointDefinition(graph, catalogue, parent, parentPort, childDefinition, childPort, jointType, jointProfileId, jointId);
        EnsurePortAvailable(graph, parent.Id, parentPort.Id);
        EnsurePortAvailable(graph, child.Id, childPort.Id);
        var linkId = UniqueId(child.Id + "-link", graph.Links.Select(link => link.Id));
        var positionedChild = child with { LinkId = linkId, PositionM = [0, 0, 0], RotationXyzw = [0, 0, 0, 1] };
        var parentMount = RobotGraphValidator.GetComponentMountFrame(parent, parentDefinition, parentPort);
        var parentFrame = Compose(ToVector(parent.PositionM), ToQuaternion(parent.RotationXyzw),
            ToVector(parentMount.PositionM), ToQuaternion(parentMount.RotationXyzw));
        var parentLink = graph.Links.Single(link => link.Id == parent.LinkId);
        var parentWorldFrame = Compose(ToVector(parentLink.PositionM), ToQuaternion(parentLink.RotationXyzw), parentFrame.Position, parentFrame.Rotation);
        var childMount = RobotGraphValidator.GetComponentMountFrame(positionedChild, childDefinition, childPort);
        var childRotation = Quaternion.Normalize(parentWorldFrame.Rotation * Quaternion.Inverse(ToQuaternion(childMount.RotationXyzw)));
        var childPosition = parentWorldFrame.Position - Vector3.Transform(ToVector(childMount.PositionM), childRotation);
        var childLink = new RobotLink(linkId, child.Id, ToArray(childPosition), ToArray(childRotation), false);
        var jointAxis = NormalizeAxis(axisLocal ?? InferJointAxis(childDefinition, ToQuaternion(childLink.RotationXyzw), parentLink.RotationXyzw));
        var joint = new RobotJoint(jointId, jointType, parent.LinkId, linkId, jointAxis,
            ToArray(parentFrame.Position), ToArray(parentFrame.Rotation), childMount.PositionM, childMount.RotationXyzw,
            null, parentPort.Id, childPort.Id, jointProfile.Id, jointProfile.Version, parent.Id, child.Id);
        var updated = graph with
        {
            Links = graph.Links.Append(childLink).ToArray(),
            Components = graph.Components.Select(component => component.Id == child.Id ? positionedChild : component).ToArray(),
            Joints = graph.Joints.Append(joint).ToArray()
        };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    /// <summary>Disconnects a leaf jointed part and keeps it as a free part at the same world pose.</summary>
    public static AssemblyGraph DetachComponent(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue, string componentId)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        var component = graph.Components.SingleOrDefault(item => item.Id == componentId)
            ?? throw new InvalidDataException($"Cannot detach missing component {componentId}.");
        var link = graph.Links.Single(item => item.Id == component.LinkId);
        var root = graph.Links.Single(item => item.Id == graph.RootLinkId);
        if (link.Id == graph.RootLinkId || link.BaseComponentId != component.Id)
            throw new InvalidDataException("Select the base component of a connected child link to detach it.");
        if (graph.Components.Any(item => item.LinkId == link.Id && item.Id != component.Id)
            || graph.Joints.Any(joint => joint.ParentLinkId == link.Id))
            throw new InvalidDataException("Remove or detach dependent components before detaching this subassembly.");
        var incoming = graph.Joints.SingleOrDefault(joint => joint.ChildLinkId == link.Id)
            ?? throw new InvalidDataException($"Component {component.Id} is not attached by a joint.");
        var componentWorldPosition = ToVector(link.PositionM) + Vector3.Transform(ToVector(component.PositionM), ToQuaternion(link.RotationXyzw));
        var componentWorldRotation = Quaternion.Normalize(ToQuaternion(link.RotationXyzw) * ToQuaternion(component.RotationXyzw));
        var rootRotationInverse = Quaternion.Inverse(ToQuaternion(root.RotationXyzw));
        var rootLocalPosition = Vector3.Transform(componentWorldPosition - ToVector(root.PositionM), rootRotationInverse);
        var rootLocalRotation = Quaternion.Normalize(rootRotationInverse * componentWorldRotation);
        var updated = graph with
        {
            Links = graph.Links.Where(item => item.Id != link.Id).ToArray(),
            Components = graph.Components.Select(item => item.Id == component.Id
                ? item with { LinkId = graph.RootLinkId, PositionM = ToArray(rootLocalPosition), RotationXyzw = ToArray(rootLocalRotation) }
                : item).ToArray(),
            Joints = graph.Joints.Where(item => item.Id != incoming.Id).ToArray()
        };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    public static bool IsMountPortAvailable(AssemblyGraph graph, string componentId, string portId)
    {
        var usedByComponentMount = (graph.ComponentMountConnections ?? []).Any(connection =>
            (connection.ParentComponentId == componentId && connection.ParentPortId == portId)
            || (connection.ChildComponentId == componentId && connection.ChildPortId == portId));
        var usedByJoint = graph.Joints.Any(joint =>
        {
            var parentBase = joint.ParentComponentId ?? graph.Links.Single(link => link.Id == joint.ParentLinkId).BaseComponentId;
            var childBase = joint.ChildComponentId ?? graph.Links.Single(link => link.Id == joint.ChildLinkId).BaseComponentId;
            return (parentBase == componentId && joint.ParentPortId == portId)
                || (childBase == componentId && joint.ChildPortId == portId);
        });
        return !usedByComponentMount && !usedByJoint;
    }

    private static void ValidateComponentPose(double[] positionM, double[] rotationXyzw)
    {
        if (positionM.Length != 3 || positionM.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Component position must contain three finite metre values.");
        if (rotationXyzw.Length != 4 || rotationXyzw.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Component rotation must contain four finite quaternion values in xyzw order.");
        var length = Math.Sqrt(rotationXyzw.Sum(value => value * value));
        if (!double.IsFinite(length) || length < 1e-9) throw new InvalidDataException("Component rotation quaternion must be nonzero.");
    }

    private static double[] NormalizeQuaternion(double[] xyzw)
    {
        var length = Math.Sqrt(xyzw.Sum(value => value * value));
        return xyzw.Select(value => value / length).ToArray();
    }

    public static AssemblyGraph AddSnappedComponent(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string parentComponentId, string parentPortId, string childCatalogueId, string childPortId, string childComponentId)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        if (graph.Components.Any(component => component.Id == childComponentId))
            throw new InvalidDataException($"Component instance id already exists: {childComponentId}.");
        var parent = graph.Components.SingleOrDefault(component => component.Id == parentComponentId);
        if (parent is null)
            throw new InvalidDataException($"Cannot snap to missing component {parentComponentId}.");
        if (!catalogue.TryGetValue(childCatalogueId, out var childDefinition))
            throw new InvalidDataException($"Missing catalogue component {childCatalogueId}.");
        var parentDefinition = catalogue[parent.CatalogueId];
        var parentPort = parentDefinition.MountPorts.SingleOrDefault(port => port.Id == parentPortId)
            ?? throw new InvalidDataException($"Component {parent.Id} has no mount port {parentPortId}.");
        var childPort = childDefinition.MountPorts.SingleOrDefault(port => port.Id == childPortId)
            ?? throw new InvalidDataException($"Component {childCatalogueId} has no mount port {childPortId}.");
        if (!RobotGraphValidator.CanMate(parentPort, childPort))
            throw new InvalidDataException($"Incompatible mount ports: {parent.Id}.{parentPort.Id} cannot mate with {childCatalogueId}.{childPort.Id}.");

        var child = new ComponentInstance(childComponentId, childCatalogueId, childDefinition.Version, parent.LinkId, [0, 0, 0], [0, 0, 0, 1]);
        child = SnapToPort(graph, parent, parentDefinition, parentPort, child, childDefinition, childPort);
        var connection = new ComponentMountConnection(UniqueId("mount-" + childComponentId,
            (graph.ComponentMountConnections ?? []).Select(item => item.Id)), parent.Id, parentPort.Id, child.Id, childPort.Id);
        var updated = graph with
        {
            Components = graph.Components.Append(child).ToArray(),
            ComponentMountConnections = (graph.ComponentMountConnections ?? []).Append(connection).ToArray()
        };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    /// <summary>Adds a new rigid link at a component connector and joins it to the existing assembly.</summary>
    public static AssemblyGraph AddJointedComponent(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string parentComponentId, string parentPortId, string childCatalogueId, string childPortId,
        string childComponentId, string jointType, string jointProfileId, string jointId, double[]? axisLocal = null)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        if (graph.Components.Any(component => component.Id == childComponentId))
            throw new InvalidDataException($"Component instance id already exists: {childComponentId}.");
        if (graph.Joints.Any(joint => joint.Id == jointId)) throw new InvalidDataException($"Joint id already exists: {jointId}.");
        if (jointType is not ("fixed" or "revolute" or "continuous" or "prismatic"))
            throw new InvalidDataException($"Unsupported joint type: {jointType}.");
        if (!catalogue.TryGetValue(jointProfileId, out var jointProfile) || jointProfile.Model != "joint_" + jointType)
            throw new InvalidDataException($"Joint profile {jointProfileId} is missing or incompatible with {jointType}.");

        var parent = graph.Components.SingleOrDefault(component => component.Id == parentComponentId)
            ?? throw new InvalidDataException($"Cannot attach to missing component {parentComponentId}.");
        if (!catalogue.TryGetValue(childCatalogueId, out var childDefinition))
            throw new InvalidDataException($"Missing catalogue component {childCatalogueId}.");
        var parentDefinition = catalogue[parent.CatalogueId];
        var parentPort = parentDefinition.MountPorts.SingleOrDefault(port => port.Id == parentPortId)
            ?? throw new InvalidDataException($"Component {parent.Id} has no mount port {parentPortId}.");
        var childPort = childDefinition.MountPorts.SingleOrDefault(port => port.Id == childPortId)
            ?? throw new InvalidDataException($"Component {childCatalogueId} has no mount port {childPortId}.");
        if (!RobotGraphValidator.CanMate(parentPort, childPort))
            throw new InvalidDataException($"Incompatible mount ports: {parent.Id}.{parentPort.Id} cannot mate with {childCatalogueId}.{childPort.Id}.");
        EnsurePortAvailable(graph, parent.Id, parentPort.Id);

        var linkId = UniqueId(childComponentId + "-link", graph.Links.Select(link => link.Id));
        var childLink = new RobotLink(linkId, childComponentId, [0, 0, 0], [0, 0, 0, 1], false);
        var child = new ComponentInstance(childComponentId, childCatalogueId, childDefinition.Version, linkId, [0, 0, 0], [0, 0, 0, 1]);

        var parentLink = graph.Links.Single(link => link.Id == parent.LinkId);
        var parentMountFrame = RobotGraphValidator.GetComponentMountFrame(parent, parentDefinition, parentPort);
        var parentFrameLocal = Compose(ToVector(parent.PositionM), ToQuaternion(parent.RotationXyzw),
            ToVector(parentMountFrame.PositionM), ToQuaternion(parentMountFrame.RotationXyzw));
        var parentFrameWorld = Compose(ToVector(parentLink.PositionM), ToQuaternion(parentLink.RotationXyzw), parentFrameLocal.Position, parentFrameLocal.Rotation);
        var childMountFrame = RobotGraphValidator.GetComponentMountFrame(child, childDefinition, childPort);
        var childLinkRotation = Quaternion.Normalize(parentFrameWorld.Rotation * Quaternion.Inverse(ToQuaternion(childMountFrame.RotationXyzw)));
        var childLinkPosition = parentFrameWorld.Position - Vector3.Transform(ToVector(childMountFrame.PositionM), childLinkRotation);
        childLink = childLink with { PositionM = ToArray(childLinkPosition), RotationXyzw = ToArray(childLinkRotation) };

        var jointAxis = NormalizeAxis(axisLocal ?? InferJointAxis(childDefinition, childLinkRotation, parentLink.RotationXyzw));
        var joint = new RobotJoint(jointId, jointType, parent.LinkId, linkId, jointAxis,
            ToArray(parentFrameLocal.Position), ToArray(parentFrameLocal.Rotation), childMountFrame.PositionM, childMountFrame.RotationXyzw,
            null, parentPort.Id, childPort.Id, jointProfile.Id, jointProfile.Version, parent.Id, child.Id);
        var updated = graph with
        {
            Links = graph.Links.Append(childLink).ToArray(),
            Components = graph.Components.Append(child).ToArray(),
            Joints = graph.Joints.Append(joint).ToArray()
        };
        RobotGraphValidator.Validate(updated, catalogue);
        return updated;
    }

    private static ComponentDefinition ValidateJointDefinition(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        ComponentInstance parent, MountPortDefinition parentPort, ComponentDefinition child, MountPortDefinition childPort,
        string jointType, string jointProfileId, string jointId)
    {
        if (string.IsNullOrWhiteSpace(jointId) || graph.Joints.Any(joint => joint.Id == jointId))
            throw new InvalidDataException($"Joint id is missing or already exists: {jointId}.");
        if (jointType is not ("fixed" or "revolute" or "continuous" or "prismatic"))
            throw new InvalidDataException($"Unsupported joint type: {jointType}.");
        if (!catalogue.TryGetValue(jointProfileId, out var jointProfile) || jointProfile.Model != "joint_" + jointType)
            throw new InvalidDataException($"Joint profile {jointProfileId} is missing or incompatible with {jointType}.");
        if (!RobotGraphValidator.CanMate(parentPort, childPort))
            throw new InvalidDataException($"Incompatible mount ports: {parent.Id}.{parentPort.Id} cannot mate with {child.Id}.{childPort.Id}.");
        return jointProfile;
    }

    private static double[] NormalizeAxis(double[] axis)
    {
        if (axis.Length != 3 || axis.Any(value => !double.IsFinite(value)))
            throw new InvalidDataException("Joint axis must contain three finite values.");
        var length = Math.Sqrt(axis.Sum(value => value * value));
        if (length < 1e-9) throw new InvalidDataException("Joint axis must have nonzero length.");
        return axis.Select(value => value / length).ToArray();
    }

    private static double[] InferJointAxis(ComponentDefinition childDefinition, Quaternion childLinkRotation, double[] parentLinkRotationXyzw)
    {
        if (childDefinition.Model == "wheel") return [1, 0, 0];
        if (childDefinition.Primitive != "cylinder") return [0, 1, 0];
        // Primitive cylinders render along local Y; express that spindle in the parent link frame.
        var parentRotation = ToQuaternion(parentLinkRotationXyzw);
        var relativeRotation = Quaternion.Normalize(Quaternion.Inverse(parentRotation) * childLinkRotation);
        return ToArray(Vector3.Transform(Vector3.UnitY, relativeRotation));
    }

    private static ComponentInstance SnapToPort(AssemblyGraph graph, ComponentInstance parent, ComponentDefinition parentDefinition,
        MountPortDefinition parentPort, ComponentInstance child, ComponentDefinition childDefinition, MountPortDefinition childPort)
    {
        var parentLink = graph.Links.Single(link => link.Id == parent.LinkId);
        var childLink = graph.Links.Single(link => link.Id == child.LinkId);
        var parentPortFrame = RobotGraphValidator.GetComponentMountFrame(parent, parentDefinition, parentPort);
        var childPortFrame = RobotGraphValidator.GetComponentMountFrame(child, childDefinition, childPort);
        var parentLinkRotation = ToQuaternion(parentLink.RotationXyzw);
        var childLinkRotation = ToQuaternion(childLink.RotationXyzw);
        var parentComponentWorldPosition = ToVector(parentLink.PositionM) + Vector3.Transform(ToVector(parent.PositionM), parentLinkRotation);
        var parentComponentWorldRotation = Quaternion.Normalize(parentLinkRotation * ToQuaternion(parent.RotationXyzw));
        var targetPosition = parentComponentWorldPosition + Vector3.Transform(ToVector(parentPortFrame.PositionM), parentComponentWorldRotation);
        var targetRotation = Quaternion.Normalize(parentComponentWorldRotation * ToQuaternion(parentPortFrame.RotationXyzw));
        var childWorldRotation = Quaternion.Normalize(targetRotation * Quaternion.Inverse(ToQuaternion(childPortFrame.RotationXyzw)));
        var childWorldPosition = targetPosition - Vector3.Transform(ToVector(childPortFrame.PositionM), childWorldRotation);
        var childLocalRotation = Quaternion.Normalize(Quaternion.Inverse(childLinkRotation) * childWorldRotation);
        var childLocalPosition = Vector3.Transform(childWorldPosition - ToVector(childLink.PositionM), Quaternion.Inverse(childLinkRotation));
        return child with { PositionM = ToArray(childLocalPosition), RotationXyzw = ToArray(childLocalRotation) };
    }

    private static string UniqueId(string prefix, IEnumerable<string> existing)
    {
        var used = existing.ToHashSet(StringComparer.Ordinal);
        if (!used.Contains(prefix)) return prefix;
        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{prefix}-{suffix}";
            if (!used.Contains(candidate)) return candidate;
        }
    }

    private static void EnsurePortAvailable(AssemblyGraph graph, string componentId, string portId)
    {
        var usedByComponentMount = (graph.ComponentMountConnections ?? []).Any(connection =>
            (connection.ParentComponentId == componentId && connection.ParentPortId == portId)
            || (connection.ChildComponentId == componentId && connection.ChildPortId == portId));
        var usedByJoint = graph.Joints.Any(joint =>
        {
            var parentBase = joint.ParentComponentId ?? graph.Links.Single(link => link.Id == joint.ParentLinkId).BaseComponentId;
            var childBase = joint.ChildComponentId ?? graph.Links.Single(link => link.Id == joint.ChildLinkId).BaseComponentId;
            return (parentBase == componentId && joint.ParentPortId == portId)
                || (childBase == componentId && joint.ChildPortId == portId);
        });
        if (usedByComponentMount || usedByJoint) throw new InvalidDataException($"Connector port {componentId}.{portId} is already occupied.");
    }

    private static RobotLink AlignLink(RobotLink link, Vector3 targetWorldPosition, Quaternion targetWorldRotation,
        double[] childFramePosition, double[] childFrameRotation)
    {
        var linkRotation = Quaternion.Normalize(targetWorldRotation * Quaternion.Inverse(ToQuaternion(childFrameRotation)));
        var linkPosition = targetWorldPosition - Vector3.Transform(ToVector(childFramePosition), linkRotation);
        return link with { PositionM = ToArray(linkPosition), RotationXyzw = ToArray(linkRotation) };
    }

    private static Vector3 ToVector(double[] values) => new((float)values[0], (float)values[1], (float)values[2]);
    private static Quaternion ToQuaternion(double[] values) => Quaternion.Normalize(new Quaternion((float)values[0], (float)values[1], (float)values[2], (float)values[3]));
    private static double[] ToArray(Vector3 value) => [value.X, value.Y, value.Z];
    private static double[] ToArray(Quaternion value) => [value.X, value.Y, value.Z, value.W];
    private static (Vector3 Position, Quaternion Rotation) Compose(Vector3 parentPosition, Quaternion parentRotation,
        Vector3 localPosition, Quaternion localRotation) =>
        (parentPosition + Vector3.Transform(localPosition, parentRotation), Quaternion.Normalize(parentRotation * localRotation));

    private static (Vector3 Position, Quaternion Rotation) Compose(double[] parentPosition, double[] parentRotation,
        double[] localPosition, double[] localRotation)
    {
        var rotation = ToQuaternion(parentRotation);
        return (ToVector(parentPosition) + Vector3.Transform(ToVector(localPosition), rotation),
            Quaternion.Normalize(rotation * ToQuaternion(localRotation)));
    }
}
