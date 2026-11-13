namespace RoboWorld.Domain;

/// <summary>Resolves the registered STM32F407/dual-H-bridge profile exclusively from authored electrical and mechanical edges.</summary>
public static class RobotElectricalRouter
{
    public static string? ResolveMotorActuator(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string controllerComponentId, int firmwareChannel)
    {
        if (firmwareChannel is not (1 or 2)) return null;
        var controller = graph.Components.SingleOrDefault(component => component.Id == controllerComponentId);
        if (controller is null || !catalogue.TryGetValue(controller.CatalogueId, out var controllerDefinition)) return null;
        var pwmPort = $"pwm{firmwareChannel}";
        var directionPort = $"dir{firmwareChannel}";
        var expectedPwmPin = firmwareChannel == 1 ? "PA6" : "PA7";
        var expectedDirectionPin = firmwareChannel == 1 ? "PD2" : "PD3";
        if (controllerDefinition.ElectricalPorts.SingleOrDefault(port => port.Id == pwmPort)?.Pin != expectedPwmPin
            || controllerDefinition.ElectricalPorts.SingleOrDefault(port => port.Id == directionPort)?.Pin != expectedDirectionPin)
            return null;

        var pwmEndpoint = $"{controllerComponentId}.{pwmPort}";
        if (!TryGetConnectedEndpoint(graph, pwmEndpoint, "pwm", out var driverPwmEndpoint)) return null;
        var separator = driverPwmEndpoint.LastIndexOf('.');
        if (separator <= 0 || driverPwmEndpoint[(separator + 1)..] is not ("pwm_a" or "pwm_b")) return null;
        var suffix = driverPwmEndpoint[^1];
        var driverId = driverPwmEndpoint[..separator];
        var driver = graph.Components.SingleOrDefault(component => component.Id == driverId);
        if (driver is null || !catalogue.TryGetValue(driver.CatalogueId, out var driverDefinition)
            || driverDefinition.Model != "dual_hbridge"
            || !driverDefinition.ElectricalPorts.Any(port => port.Id == $"dir_{suffix}" && port.Interface == "gpio")
            || !driverDefinition.ElectricalPorts.Any(port => port.Id == $"out_{suffix}" && port.Interface == "motor_channel")) return null;
        if (!TryGetConnectedEndpoint(graph, $"{controllerComponentId}.{directionPort}", "gpio", out var driverDirectionEndpoint)
            || driverDirectionEndpoint != $"{driverId}.dir_{suffix}") return null;
        if (!TryGetConnectedEndpoint(graph, $"{driverId}.out_{suffix}", "motor_channel", out var motorEndpoint)) return null;
        separator = motorEndpoint.LastIndexOf('.');
        if (separator <= 0 || motorEndpoint[(separator + 1)..] != "drive") return null;
        var motorId = motorEndpoint[..separator];
        var motor = graph.Components.SingleOrDefault(component => component.Id == motorId);
        if (motor is null || !catalogue.TryGetValue(motor.CatalogueId, out var motorDefinition) || motorDefinition.Model != "dc_motor") return null;
        var joint = graph.Joints.SingleOrDefault(candidate => candidate.ActuatorComponentId == motorId);
        if (joint is null || joint.Type is not ("revolute" or "continuous")
            || !graph.Links.Any(link => link.Id == joint.ChildLinkId)) return null;
        return motorId;
    }

    public static bool HasDistanceSensorRoute(AssemblyGraph graph, IReadOnlyDictionary<string, ComponentDefinition> catalogue,
        string controllerComponentId)
    {
        var controller = graph.Components.SingleOrDefault(component => component.Id == controllerComponentId);
        if (controller is null || !catalogue.TryGetValue(controller.CatalogueId, out var controllerDefinition)
            || controllerDefinition.ElectricalPorts.SingleOrDefault(port => port.Id == "sensor")?.Pin != "PA0") return false;
        var sensorEndpoint = $"{controllerComponentId}.sensor";
        if (!TryGetConnectedEndpoint(graph, sensorEndpoint, "gpio", out var otherEndpoint)) return false;
        var separator = otherEndpoint.LastIndexOf('.');
        if (separator <= 0 || otherEndpoint[(separator + 1)..] != "echo") return false;
        var sensor = graph.Components.SingleOrDefault(component => component.Id == otherEndpoint[..separator]);
        return sensor is not null && catalogue.TryGetValue(sensor.CatalogueId, out var sensorDefinition)
            && sensorDefinition.Model == "distance_raycast"
            && sensorDefinition.ElectricalPorts.Any(port => port.Id == "echo" && port.Interface == "gpio");
    }

    private static bool TryGetConnectedEndpoint(AssemblyGraph graph, string endpoint, string interfaceName, out string otherEndpoint)
    {
        otherEndpoint = string.Empty;
        var connections = graph.ElectricalConnections.Where(connection => connection.Interface == interfaceName
            && (connection.From == endpoint || connection.To == endpoint)).ToArray();
        if (connections.Length != 1) return false;
        otherEndpoint = connections[0].From == endpoint ? connections[0].To : connections[0].From;
        return true;
    }
}
