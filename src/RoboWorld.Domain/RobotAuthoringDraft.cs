namespace RoboWorld.Domain;

/// <summary>Portable reference to a registered firmware artifact assigned by the author to a controller component.</summary>
public sealed record RobotFirmwareAssignment(string Id, string ControllerComponentId, string FirmwareArtifactId, string Board, string Target,
    string RelativePath, string FirmwareSha256);

/// <summary>An unsimulated authoring state. An empty draft has no artificial root or component.</summary>
public sealed record RobotAuthoringDraft(string Id, string DisplayName, AssemblyGraph? Assembly,
    IReadOnlyList<RobotFirmwareAssignment>? FirmwareAssignments = null)
{
    public static RobotAuthoringDraft FromRunnableAssembly(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        RobotGraphValidator.Validate(graph, catalogue);
        return new RobotAuthoringDraft(graph.Id, graph.DisplayName, graph);
    }

    public static RobotAuthoringDraft CreateEmpty(string id, string displayName)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName))
            throw new InvalidDataException("A robot draft needs an id and display name.");
        return new RobotAuthoringDraft(id, displayName, null);
    }

    public RobotAuthoringDraft AddRoot(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string catalogueId, string componentId)
    {
        if (Assembly is not null) throw new InvalidDataException("This draft already has a root component.");
        if (!catalogue.TryGetValue(catalogueId, out var definition))
            throw new InvalidDataException($"Missing catalogue component {catalogueId}.");
        if (definition.Category is not ("body" or "structure"))
            throw new InvalidDataException("The first component must be a body or structural part.");
        if (string.IsNullOrWhiteSpace(componentId)) throw new InvalidDataException("The root component needs an id.");
        const string rootLinkId = "root";
        var graph = new AssemblyGraph(Id, DisplayName, rootLinkId,
            [new RobotLink(rootLinkId, componentId, [0, 0, 0], [0, 0, 0, 1], true)],
            [new ComponentInstance(componentId, definition.Id, definition.Version, rootLinkId, [0, 0, 0], [0, 0, 0, 1])],
            [], [], []);
        RobotGraphValidator.Validate(graph, catalogue);
        return this with { Assembly = graph };
    }

    /// <summary>Returns a structurally validated graph; this alone does not establish simulation readiness.</summary>
    public AssemblyGraph RequireAssembly(IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        var graph = Assembly ?? throw new InvalidDataException("Add a structural component before validating the robot assembly.");
        if (graph.Id != Id || graph.DisplayName != DisplayName) throw new InvalidDataException("Draft and assembly identities do not match.");
        RobotGraphValidator.Validate(graph, catalogue);
        return graph;
    }

    public RobotAuthoringDraft AddFreeComponent(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string catalogueId, string componentId)
    {
        if (Assembly is null) return AddRoot(catalogue, catalogueId, componentId);
        return this with { Assembly = RobotAssemblyEditor.AddFreeComponent(Assembly, catalogue, catalogueId,
            componentId, [0, 0.3, 0], [0, 0, 0, 1]) };
    }

    public RobotAuthoringDraft AddSnappedComponent(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string parentId, string parentPort, string childCatalogueId, string childPort, string childId, string? jointType = null,
        double[]? jointAxisLocal = null)
    {
        var graph = RequireAssembly(catalogue);
        var updated = jointType is null
            ? RobotAssemblyEditor.AddSnappedComponent(graph, catalogue, parentId, parentPort, childCatalogueId, childPort, childId)
            : RobotAssemblyEditor.AddJointedComponent(graph, catalogue, parentId, parentPort, childCatalogueId, childPort,
                childId, jointType, jointType == "fixed" ? "rw.mount.fixed" : "rw.mount.revolute", "joint-" + childId, jointAxisLocal);
        return this with { Assembly = updated };
    }

    public RobotAuthoringDraft AttachExistingComponent(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string parentId, string parentPort, string childId, string childPort, string jointType, double[]? jointAxisLocal = null)
    {
        var graph = RequireAssembly(catalogue);
        var profileId = jointType == "fixed" ? "rw.mount.fixed" : "rw.mount.revolute";
        var updated = RobotAssemblyEditor.AttachExistingComponent(graph, catalogue, parentId, parentPort, childId, childPort,
            jointType, profileId, "joint-" + childId, jointAxisLocal);
        return this with { Assembly = updated };
    }

    public RobotAuthoringDraft DetachComponent(IReadOnlyDictionary<string, ComponentDefinition> catalogue, string componentId)
    {
        var graph = RequireAssembly(catalogue);
        return this with { Assembly = RobotAssemblyEditor.DetachComponent(graph, catalogue, componentId) };
    }

    public RobotAuthoringDraft SetComponentPose(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string componentId, double[] positionM, double[] rotationXyzw)
    {
        var graph = RequireAssembly(catalogue);
        return this with { Assembly = RobotAssemblyEditor.SetComponentPose(graph, catalogue, componentId, positionM, rotationXyzw) };
    }

    public RobotAuthoringDraft SetRootPose(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        double[] positionM, double[] rotationXyzw)
    {
        var graph = RequireAssembly(catalogue);
        return this with { Assembly = RobotAssemblyEditor.SetRootPose(graph, catalogue, positionM, rotationXyzw) };
    }

    public RobotAuthoringDraft SetJointActuator(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string jointId, string motorComponentId)
    {
        var graph = RequireAssembly(catalogue);
        return this with { Assembly = RobotAssemblyEditor.SetJointActuator(graph, catalogue, jointId, motorComponentId) };
    }

    public RobotAuthoringDraft AssignFirmware(IReadOnlyDictionary<string, ComponentDefinition> catalogue, RobotFirmwareAssignment assignment)
    {
        var graph = RequireAssembly(catalogue);
        if (!graph.Components.Any(component => component.Id == assignment.ControllerComponentId))
            throw new InvalidDataException($"Firmware assignment references missing controller component {assignment.ControllerComponentId}.");
        var previous = FirmwareAssignments ?? [];
        return this with { FirmwareAssignments = previous.Where(item => item.ControllerComponentId != assignment.ControllerComponentId)
            .Append(assignment).OrderBy(item => item.ControllerComponentId, StringComparer.Ordinal).ToArray() };
    }

    public RobotAuthoringDraft RemoveFirmwareAssignment(string controllerComponentId) => this with
    {
        FirmwareAssignments = (FirmwareAssignments ?? []).Where(item => item.ControllerComponentId != controllerComponentId).ToArray()
    };

    public RobotAuthoringDraft AddElectricalConnection(IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string from, string to, string busId)
    {
        var graph = RequireAssembly(catalogue);
        return this with { Assembly = RobotAssemblyEditor.AddElectricalConnection(graph, catalogue, from, to, busId) };
    }

    public RobotAuthoringDraft RemoveComponent(IReadOnlyDictionary<string, ComponentDefinition> catalogue, string componentId)
    {
        if (Assembly is null) throw new InvalidDataException("The empty draft has no components to remove.");
        var instance = Assembly.Components.SingleOrDefault(component => component.Id == componentId)
            ?? throw new InvalidDataException($"Draft component '{componentId}' does not exist.");
        if (instance.Id == Assembly.Links.Single(link => link.Id == Assembly.RootLinkId).BaseComponentId)
        {
            if (Assembly.Components.Count != 1 || Assembly.Links.Count != 1 || Assembly.Joints.Count != 0)
                throw new InvalidDataException("Remove dependent components before removing the root component.");
            return this with { Assembly = null, FirmwareAssignments = [] };
        }
        return this with
        {
            Assembly = RobotAssemblyEditor.RemoveComponent(Assembly, catalogue, componentId),
            FirmwareAssignments = (FirmwareAssignments ?? []).Where(assignment => assignment.ControllerComponentId != componentId).ToArray()
        };
    }
}
