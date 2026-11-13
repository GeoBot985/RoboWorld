namespace RoboWorld.Domain;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Derives the current project/firmware/hardware identity used by both profile induction and runtime preflight.</summary>
public static class ControllerProfileIdentityFactory
{
    private sealed record ComponentDefinitionFingerprint(string Id, string Version, string Sha256);
    private static readonly JsonSerializerOptions CanonicalJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static ControllerProfileIdentity CreateFromProjectFiles(string robotPath, string componentDirectory,
        string firmwarePath, string peripheralMapSourcePath, string controllerInstanceId, ControllerInputEnvelope inputEnvelope,
        string generatorVersion, string evaluatorVersion)
    {
        ArgumentNullException.ThrowIfNull(inputEnvelope);
        if (string.IsNullOrWhiteSpace(controllerInstanceId) || string.IsNullOrWhiteSpace(generatorVersion)
            || string.IsNullOrWhiteSpace(evaluatorVersion))
            throw new InvalidDataException("Controller profile identity inputs are incomplete.");
        var fullRobotPath = Path.GetFullPath(robotPath);
        var fullComponentDirectory = Path.GetFullPath(componentDirectory);
        var fullFirmwarePath = Path.GetFullPath(firmwarePath);
        var fullPeripheralSourcePath = Path.GetFullPath(peripheralMapSourcePath);
        using var robotDocument = JsonDocument.Parse(File.ReadAllBytes(fullRobotPath));
        var robot = robotDocument.RootElement;
        var controller = robot.GetProperty("controllers").EnumerateArray()
            .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.GetProperty("id").GetString(), controllerInstanceId));
        if (controller.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Robot graph has no controller assignment {controllerInstanceId}.");
        var componentId = controller.GetProperty("component_instance_id").GetString()
            ?? throw new InvalidDataException("Controller component instance ID is missing.");
        var component = robot.GetProperty("components").EnumerateArray()
            .SingleOrDefault(item => StringComparer.Ordinal.Equals(item.GetProperty("id").GetString(), componentId));
        if (component.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Robot graph has no controller component {componentId}.");
        var catalogueId = component.GetProperty("catalogue_id").GetString()
            ?? throw new InvalidDataException("Controller catalogue ID is missing.");
        var mcuDefinitionPath = Directory.GetFiles(fullComponentDirectory, "*.component.json")
            .SingleOrDefault(path => ReadCatalogueId(path) == catalogueId)
            ?? throw new FileNotFoundException($"Controller component definition {catalogueId} was not found.", fullComponentDirectory);
        using var mcuDefinition = JsonDocument.Parse(File.ReadAllBytes(mcuDefinitionPath));
        var firmware = controller.GetProperty("firmware");
        var definitions = robot.GetProperty("components").EnumerateArray()
            .Select(item => item.GetProperty("catalogue_id").GetString()!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id =>
            {
                var path = Directory.GetFiles(fullComponentDirectory, "*.component.json")
                    .SingleOrDefault(candidate => ReadCatalogueId(candidate) == id)
                    ?? throw new FileNotFoundException($"Component definition {id} was not found.", fullComponentDirectory);
                using var definition = JsonDocument.Parse(File.ReadAllBytes(path));
                return new ComponentDefinitionFingerprint(id, definition.RootElement.GetProperty("version").GetString()!, HashFile(path));
            }).ToArray();
        var peripheralContract = new
        {
            schemaVersion = 1,
            boardContractId = controller.GetProperty("board").GetString(),
            controllerComponentId = componentId,
            electricalPorts = mcuDefinition.RootElement.GetProperty("electrical_ports"),
            electricalConnections = robot.GetProperty("electrical_connections"),
            renodeRuntimeMapSourceSha256 = HashFile(fullPeripheralSourcePath)
        };
        var initialState = new
        {
            links = robot.GetProperty("links").EnumerateArray().Select(link => new
            {
                id = link.GetProperty("id").GetString(),
                initialTransform = link.GetProperty("initial_transform")
            }).ToArray(),
            joints = robot.GetProperty("joints").EnumerateArray().Select(joint => new
            {
                id = joint.GetProperty("id").GetString(),
                position = joint.TryGetProperty("initial_position", out var position) ? position.GetDouble() : 0d,
                velocity = joint.TryGetProperty("initial_velocity", out var velocity) ? velocity.GetDouble() : 0d
            }).ToArray()
        };
        return new ControllerProfileIdentity(1, HashFile(fullFirmwarePath),
            firmware.GetProperty("target").GetString()!, controller.GetProperty("board").GetString()!,
            mcuDefinition.RootElement.GetProperty("version").GetString()!, HashFile(fullRobotPath),
            HashCanonical(definitions), HashCanonical(peripheralContract), HashCanonical(initialState),
            "trace-schema-1", generatorVersion, "1", evaluatorVersion, ControllerProfileHashes.Compute(inputEnvelope));
    }

    private static string ReadCatalogueId(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("id").GetString()!;
    }

    private static string HashCanonical<T>(T value) => Convert.ToHexString(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, CanonicalJson)));

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
