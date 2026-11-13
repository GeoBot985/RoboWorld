namespace RoboWorld.Domain;

public sealed record AuthoringIssue(string Code, string Message);

/// <summary>Launch gate layered on top of, and never weakening, canonical graph validation.</summary>
public static class RobotAuthoringReadiness
{
    /// <summary>Compatibility overload for existing lifecycle checks that only need to establish firmware presence.</summary>
    public static IReadOnlyList<AuthoringIssue> Validate(RobotAuthoringDraft draft,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue, bool hasFirmwareAssignment)
    {
        var issues = ValidateStructure(draft, catalogue);
        if (issues.Count > 0) return issues;
        var graph = draft.Assembly!;
        if (!HasController(catalogue, graph))
            issues.Add(new("E_CONTROLLER_MISSING", "Add a controller with a UART interface before running this robot."));
        if (!hasFirmwareAssignment)
            issues.Add(new("E_FIRMWARE_MISSING", "Assign compatible firmware to a controller before running this robot."));
        ValidateActuatedMotors(graph, catalogue, issues);
        foreach (var connection in graph.ElectricalConnections)
            if (connection.Interface == "uart" && !hasFirmwareAssignment)
                issues.Add(new("E_CONTROLLER_LINK_REQUIRED", $"UART connection '{connection.Id}' cannot run until controller firmware is assigned."));
        return issues;
    }

    /// <summary>Validates explicit author selections against registered, hash-verified artifacts and required graph routes.</summary>
    public static IReadOnlyList<AuthoringIssue> Validate(RobotAuthoringDraft draft,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        IReadOnlyDictionary<string, RobotFirmwareArtifact> firmwareArtifacts)
    {
        var issues = ValidateStructure(draft, catalogue);
        if (issues.Count > 0) return issues;
        var graph = draft.Assembly!;
        if (!HasController(catalogue, graph))
            issues.Add(new("E_CONTROLLER_MISSING", "Add a controller with a UART interface before running this robot."));

        var assignments = draft.FirmwareAssignments ?? [];
        if (assignments.Count == 0)
            issues.Add(new("E_FIRMWARE_MISSING", "Select a controller and assign compatible installed firmware before running this robot."));
        foreach (var assignment in assignments)
        {
            var controller = graph.Components.SingleOrDefault(component => component.Id == assignment.ControllerComponentId);
            if (controller is null)
            {
                issues.Add(new("E_FIRMWARE_CONTROLLER_MISSING", $"Firmware assignment '{assignment.Id}' references missing controller '{assignment.ControllerComponentId}'."));
                continue;
            }
            var definition = catalogue[controller.CatalogueId];
            var expectedTarget = definition.Model.StartsWith("renode_", StringComparison.Ordinal) ? definition.Model["renode_".Length..] : string.Empty;
            if (!definition.ElectricalPorts.Any(port => port.Interface == "uart") || definition.Category != "electronics")
                issues.Add(new("E_FIRMWARE_CONTROLLER_INCOMPATIBLE", $"Component '{controller.Id}' is not a supported UART controller."));
            if (!firmwareArtifacts.TryGetValue(assignment.FirmwareArtifactId, out var artifact))
            {
                issues.Add(new("E_FIRMWARE_NOT_REGISTERED", $"Firmware artifact '{assignment.FirmwareArtifactId}' is not installed or registered in this project."));
                continue;
            }
            if (!string.Equals(assignment.FirmwareSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase)
                || assignment.RelativePath != artifact.RelativePath)
                issues.Add(new("E_FIRMWARE_IDENTITY", $"Firmware artifact '{assignment.Id}' changed or no longer matches its saved checksum."));
            if (assignment.Target != expectedTarget || artifact.Target != expectedTarget
                || assignment.Board != artifact.Board || artifact.Board is not ("stm32f4_discovery_stm32f407vg"))
                issues.Add(new("E_FIRMWARE_TARGET", $"Firmware '{assignment.Id}' targets {assignment.Target}/{assignment.Board}, which is incompatible with controller '{controller.Id}' ({expectedTarget})."));
        }
        if (assignments.Select(item => item.ControllerComponentId).Distinct(StringComparer.Ordinal).Count() != assignments.Count)
            issues.Add(new("E_FIRMWARE_DUPLICATE", "A controller can have only one firmware assignment."));

        ValidateActuatedMotors(graph, catalogue, issues);
        ValidateMotorElectricalRoutes(graph, catalogue, assignments, issues);
        ValidateSensorElectricalRoutes(graph, catalogue, assignments, issues);
        return issues;
    }

    private static List<AuthoringIssue> ValidateStructure(RobotAuthoringDraft draft,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        try { _ = draft.RequireAssembly(catalogue); }
        catch (InvalidDataException exception) { return [new("E_ROBOT_GRAPH_INVALID", exception.Message)]; }
        return [];
    }

    private static bool HasController(IReadOnlyDictionary<string, ComponentDefinition> catalogue, AssemblyGraph graph) =>
        graph.Components.Any(component => catalogue[component.CatalogueId].ElectricalPorts.Any(port => port.Interface == "uart"));

    private static void ValidateActuatedMotors(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        List<AuthoringIssue> issues)
    {
        foreach (var motor in graph.Components.Where(component => catalogue[component.CatalogueId].Model == "dc_motor"))
        {
            var joint = graph.Joints.SingleOrDefault(candidate => candidate.ActuatorComponentId == motor.Id);
            if (joint is null)
                issues.Add(new("E_MOTOR_UNBOUND", $"Motor '{motor.Id}' is not bound to a rotating wheel joint."));
            else if (!graph.Components.Any(component => component.LinkId == joint.ChildLinkId && catalogue[component.CatalogueId].Model == "wheel"))
                issues.Add(new("E_MOTOR_NOT_WHEEL", $"Motor '{motor.Id}' is not bound to a rotating wheel joint."));
        }
    }

    private static void ValidateMotorElectricalRoutes(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        IReadOnlyList<RobotFirmwareAssignment> assignments, List<AuthoringIssue> issues)
    {
        var routedMotors = assignments.SelectMany(assignment => new[] { 1, 2 }
                .Select(channel => RobotElectricalRouter.ResolveMotorActuator(graph, catalogue, assignment.ControllerComponentId, channel)))
            .Where(motorId => motorId is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var motor in graph.Components.Where(component => catalogue[component.CatalogueId].Model == "dc_motor"))
        {
            var output = graph.ElectricalConnections.Any(connection => connection.Interface == "motor_channel"
                && (connection.From == motor.Id + ".drive" || connection.To == motor.Id + ".drive"));
            if (!output) issues.Add(new("E_MOTOR_DRIVER_ROUTE", $"Motor '{motor.Id}' is not connected to a motor-driver output."));
            if (!routedMotors.Contains(motor.Id))
                issues.Add(new("E_MOTOR_CONTROL_ROUTE", $"Motor '{motor.Id}' is missing a complete assigned-controller PWM, direction, driver and joint route."));
        }
    }

    private static void ValidateSensorElectricalRoutes(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        IReadOnlyList<RobotFirmwareAssignment> assignments, List<AuthoringIssue> issues)
    {
        foreach (var sensor in graph.Components.Where(component => catalogue[component.CatalogueId].Model == "distance_raycast"))
        {
            var connected = assignments.Any(assignment => RobotElectricalRouter.HasDistanceSensorRoute(graph, catalogue, assignment.ControllerComponentId)
                && HasWire(graph, $"{sensor.Id}.echo", $"{assignment.ControllerComponentId}.sensor", "gpio"));
            if (!connected)
                issues.Add(new("E_SENSOR_ROUTE", $"Distance sensor '{sensor.Id}' is not connected to an assigned controller sensor input."));
        }
    }

    private static bool HasWire(AssemblyGraph graph, string first, string second, string interfaceName) =>
        graph.ElectricalConnections.Any(connection => connection.Interface == interfaceName
            && ((connection.From == first && connection.To == second) || (connection.From == second && connection.To == first)));
}
