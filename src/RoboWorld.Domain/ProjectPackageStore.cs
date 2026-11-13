using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RoboWorld.Domain;

public sealed record ProjectControllerAssignment(string RobotId, string Id, string ComponentInstanceId, string Board, string FirmwarePath,
    string FirmwareSha256, string? FirmwareRelativePath = null, string? FirmwareTarget = null);
public sealed record ProjectControllerProfileReference(string ProfileId, string ProfileSha256);
public sealed record ProjectControllerExecutionSettings(string Mode, IReadOnlyDictionary<string, ProjectControllerProfileReference> ProfileReferences)
{
    public static ProjectControllerExecutionSettings Default { get; } = new("exact", new Dictionary<string, ProjectControllerProfileReference>(StringComparer.Ordinal));
}

public sealed record ProjectPackageInfo(
    string ProjectId,
    string Title,
    string RootDirectory,
    string ManifestPath,
    string WorldPath,
    string RobotPath,
    IReadOnlyList<string> RobotPaths,
    string ComponentDirectory,
    string FirmwarePath,
    string FirmwareSha256,
    string ManifestSha256,
    IReadOnlyDictionary<string, string> FileHashes,
    IReadOnlyDictionary<string, IReadOnlyList<ProjectControllerAssignment>> ControllersByRobotId,
    ProjectControllerExecutionSettings ControllerExecutionSettings,
    bool IsTemporary);

/// <summary>Validates canonical project references and writes a hash-locked .roboworld ZIP by atomic file replacement.</summary>
public static class ProjectPackageStore
{
    private const long MaxPackageBytes = 128L * 1024 * 1024;
    private const string ManifestName = "project.json";
    private const string LockName = "project.lock.json";

    public static ProjectPackageInfo OpenDirectory(string directory, string? firmwareFallbackPath = null) => LoadDirectory(Path.GetFullPath(directory), firmwareFallbackPath, false);

    public static ProjectPackageInfo OpenPackage(string packagePath)
    {
        var fullPackagePath = Path.GetFullPath(packagePath);
        if (!File.Exists(fullPackagePath)) throw new FileNotFoundException("RoboWorld project package does not exist.", fullPackagePath);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "roboworld-projects", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            using var archive = ZipFile.OpenRead(fullPackagePath);
            long expandedBytes = 0;
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;
                var relativePath = NormalizeRelativePath(entry.FullName);
                if (!seenPaths.Add(relativePath)) throw new InvalidDataException($"Duplicate project package entry: {relativePath}.");
                expandedBytes = checked(expandedBytes + entry.Length);
                if (expandedBytes > MaxPackageBytes) throw new InvalidDataException("Project package expands beyond the 128 MiB limit.");
                var destination = ResolveContainedPath(temporaryRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var input = entry.Open();
                using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
            }
            return LoadDirectory(temporaryRoot, null, true);
        }
        catch
        {
            TryDeleteDirectory(temporaryRoot);
            throw;
        }
    }

    public static string SavePackage(ProjectPackageInfo project, string destinationPath, AssemblyGraph? activeRobotOverride = null,
        ProjectControllerExecutionSettings? executionSettingsOverride = null)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(fullDestination), ".roboworld", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RoboWorld design packages must use the .roboworld extension.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        var source = LoadDirectory(project.RootDirectory, project.FirmwarePath, false);
        var references = GetReferences(source.RootDirectory, source.ManifestPath);
        var manifestContents = executionSettingsOverride is null
            ? File.ReadAllBytes(source.ManifestPath)
            : SerializeManifestWithControllerExecution(source.ManifestPath, executionSettingsOverride);
        using (var manifestDocument = JsonDocument.Parse(manifestContents))
            _ = ReadControllerExecutionSettings(manifestDocument.RootElement, source.ControllersByRobotId);
        var tempPackage = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var fileHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var archive = ZipFile.Open(tempPackage, ZipArchiveMode.Create))
            {
                AddBytes(archive, ManifestName, manifestContents, null);
                foreach (var reference in references)
                {
                    var sourcePath = ResolveSource(reference.RelativePath, source.RootDirectory, source.FirmwarePath);
                    if (activeRobotOverride is not null && project.RobotPaths.Contains(sourcePath, StringComparer.OrdinalIgnoreCase))
                    {
                        var sourceGraph = FixtureLoader.LoadRobot(sourcePath, project.ComponentDirectory);
                        if (sourceGraph.Id == activeRobotOverride.Id)
                            AddBytes(archive, reference.RelativePath, SerializeRobotOverride(sourcePath, activeRobotOverride), fileHashes);
                        else AddFile(archive, reference.RelativePath, sourcePath, fileHashes);
                    }
                    else AddFile(archive, reference.RelativePath, sourcePath, fileHashes);
                }
                var lockRecord = new ProjectLock(
                    "1.0",
                    source.ProjectId,
                    Convert.ToHexString(SHA256.HashData(manifestContents)).ToLowerInvariant(),
                    fileHashes);
                var lockEntry = archive.CreateEntry(LockName, CompressionLevel.Optimal);
                using var lockStream = lockEntry.Open();
                JsonSerializer.Serialize(lockStream, lockRecord, JsonOptions);
            }
            File.Move(tempPackage, fullDestination, true);
            return HashFile(fullDestination);
        }
        catch
        {
            if (File.Exists(tempPackage)) File.Delete(tempPackage);
            throw;
        }
    }

    /// <summary>Writes an authored graph and its explicit firmware selections as a runnable project, with no template controller inheritance.</summary>
    public static string SaveAuthoringPackage(ProjectPackageInfo project, string destinationPath, RobotAuthoringDraft draft,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue, IReadOnlyDictionary<string, RobotFirmwareArtifact> firmwareArtifacts)
    {
        var graph = draft.RequireAssembly(catalogue);
        var issues = RobotAuthoringReadiness.Validate(draft, catalogue, firmwareArtifacts);
        if (issues.Count > 0) throw new InvalidDataException(string.Join(" ", issues.Select(issue => issue.Message)));
        if (!System.Text.RegularExpressions.Regex.IsMatch(graph.Id, "^[A-Za-z][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Authored robot id must contain only letters, digits, dot, underscore or dash.");

        var fullDestination = Path.GetFullPath(destinationPath);
        if (!string.Equals(Path.GetExtension(fullDestination), ".roboworld", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("RoboWorld design packages must use the .roboworld extension.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);
        var source = LoadDirectory(project.RootDirectory, project.FirmwarePath, false);
        var existingRobotPath = source.RobotPaths.SingleOrDefault(path =>
            FixtureLoader.LoadRobot(path, source.ComponentDirectory).Id == graph.Id);
        var robotRelativePath = existingRobotPath is null ? $"robots/{graph.Id}.robot.json"
            : Path.GetRelativePath(source.RootDirectory, existingRobotPath).Replace('\\', '/');
        var templatePath = existingRobotPath ?? source.RobotPath;
        var assignments = (draft.FirmwareAssignments ?? []).OrderBy(item => item.ControllerComponentId, StringComparer.Ordinal).ToArray();
        var artifacts = assignments.Select(assignment => firmwareArtifacts[assignment.FirmwareArtifactId]).ToArray();
        var robotBytes = SerializeRobotOverride(templatePath, graph, assignments);

        var manifest = JsonNode.Parse(File.ReadAllText(source.ManifestPath))?.AsObject()
            ?? throw new InvalidDataException("Project manifest must be a JSON object.");
        var robotFiles = manifest["robot_files"]?.AsArray() ?? throw new InvalidDataException("Project robot_files must be an array.");
        if (existingRobotPath is null) robotFiles.Add(robotRelativePath);
        var manifestSettings = manifest["settings"]?.AsObject() ?? new JsonObject();
        manifestSettings["controller_execution"] = new JsonObject { ["mode"] = "exact", ["profiles"] = new JsonObject() };
        manifest["settings"] = manifestSettings;
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);

        var references = GetReferences(source.RootDirectory, source.ManifestPath).ToDictionary(item => item.RelativePath,
            item => item, StringComparer.Ordinal);
        references[robotRelativePath] = new ProjectReference(robotRelativePath);
        foreach (var artifact in artifacts) references[NormalizeRelativePath(artifact.RelativePath)] = new ProjectReference(artifact.RelativePath);

        var tempPackage = fullDestination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var fileHashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var archive = ZipFile.Open(tempPackage, ZipArchiveMode.Create))
            {
                AddBytes(archive, ManifestName, manifestBytes, null);
                foreach (var reference in references.Values.OrderBy(item => item.RelativePath, StringComparer.Ordinal))
                {
                    if (reference.RelativePath == robotRelativePath)
                    {
                        AddBytes(archive, robotRelativePath, robotBytes, fileHashes);
                        continue;
                    }
                    var selectedArtifact = artifacts.SingleOrDefault(item =>
                        StringComparer.Ordinal.Equals(NormalizeRelativePath(item.RelativePath), reference.RelativePath));
                    var sourcePath = selectedArtifact?.FullPath ?? ResolveSource(reference.RelativePath, source.RootDirectory, source.FirmwarePath);
                    if (!File.Exists(sourcePath)) throw new FileNotFoundException($"Project dependency '{reference.RelativePath}' is missing.", sourcePath);
                    AddFile(archive, reference.RelativePath, sourcePath, fileHashes);
                }
                var projectId = manifest["id"]?.GetValue<string>() ?? source.ProjectId;
                var lockRecord = new ProjectLock("1.0", projectId,
                    Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant(), fileHashes);
                var lockEntry = archive.CreateEntry(LockName, CompressionLevel.Optimal);
                using var lockStream = lockEntry.Open();
                JsonSerializer.Serialize(lockStream, lockRecord, JsonOptions);
            }
            File.Move(tempPackage, fullDestination, true);
            return HashFile(fullDestination);
        }
        catch
        {
            if (File.Exists(tempPackage)) File.Delete(tempPackage);
            throw;
        }
    }

    public static void DeleteTemporaryProject(ProjectPackageInfo project)
    {
        if (project.IsTemporary) TryDeleteDirectory(project.RootDirectory);
    }

    private static ProjectPackageInfo LoadDirectory(string root, string? firmwareFallbackPath, bool isTemporary)
    {
        var manifestPath = ResolveContainedPath(root, ManifestName);
        if (!File.Exists(manifestPath)) throw new FileNotFoundException("Project manifest project.json is missing.", manifestPath);
        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var manifest = document.RootElement;
        if (manifest.GetProperty("schema_version").GetString() != "1.0") throw new InvalidDataException("Unsupported project schema version; expected 1.0.");
        var projectId = manifest.GetProperty("id").GetString() ?? throw new InvalidDataException("Project id is missing.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(projectId, "^[A-Za-z][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Project id contains unsupported characters.");
        var title = manifest.GetProperty("title").GetString() ?? projectId;
        var robotFiles = manifest.GetProperty("robot_files").EnumerateArray().Select(item => item.GetString()!).ToArray();
        if (robotFiles.Length == 0) throw new InvalidDataException("A project must contain at least one robot design.");
        var worldRelative = NormalizeRelativePath(manifest.GetProperty("world_file").GetString()!);
        var robotRelatives = robotFiles.Select(NormalizeRelativePath).Distinct(StringComparer.Ordinal).ToArray();
        if (robotRelatives.Length != robotFiles.Length) throw new InvalidDataException("A project cannot list the same robot design more than once.");
        var robotRelative = robotRelatives[0];
        var componentRelatives = manifest.GetProperty("component_files").EnumerateArray().Select(item => NormalizeRelativePath(item.GetString()!)).ToArray();
        var robotPath = ResolveContainedPath(root, robotRelative);
        if (!File.Exists(robotPath)) throw new FileNotFoundException($"Project robot design '{robotRelative}' is missing.", robotPath);
        var robotPaths = robotRelatives.Select(relative => ResolveContainedPath(root, relative)).ToArray();
        var firmwareRelatives = new List<string>();
        var controllerRecords = new Dictionary<string, IReadOnlyList<ProjectControllerAssignment>>(StringComparer.Ordinal);
        foreach (var candidatePath in robotPaths)
        {
            if (!File.Exists(candidatePath)) throw new FileNotFoundException("A project robot design is missing.", candidatePath);
            using var robot = JsonDocument.Parse(File.ReadAllText(candidatePath));
            var robotId = robot.RootElement.GetProperty("id").GetString() ?? throw new InvalidDataException("Robot design id is missing.");
            var controllerIds = new HashSet<string>(StringComparer.Ordinal);
            var assignmentRecords = new List<(string Id, string ComponentId, string Board, string FirmwareRelative, string FirmwareTarget)>();
            var assignments = robot.RootElement.GetProperty("controllers").EnumerateArray().ToArray();
            foreach (var assignment in assignments)
            {
                var controllerId = assignment.GetProperty("id").GetString() ?? throw new InvalidDataException($"Robot {robotId} has a controller without an id.");
                if (!controllerIds.Add(controllerId)) throw new InvalidDataException($"Robot {robotId} has duplicate controller id {controllerId}.");
                var componentId = assignment.GetProperty("component_instance_id").GetString() ?? throw new InvalidDataException($"Controller {controllerId} has no component instance.");
                var board = assignment.GetProperty("board").GetString() ?? throw new InvalidDataException($"Controller {controllerId} has no board id.");
                var firmwareRelative = NormalizeRelativePath(assignment.GetProperty("firmware").GetProperty("relative_path").GetString()!);
                var firmwareTarget = assignment.GetProperty("firmware").GetProperty("target").GetString() ?? throw new InvalidDataException($"Controller {controllerId} has no firmware target.");
                firmwareRelatives.Add(firmwareRelative);
                assignmentRecords.Add((controllerId, componentId, board, firmwareRelative, firmwareTarget));
            }
            controllerRecords.Add(robotId, assignmentRecords.Select(assignment =>
            {
                var firmwarePath = ResolveSource(assignment.FirmwareRelative, root, firmwareFallbackPath);
                if (!File.Exists(firmwarePath)) throw new FileNotFoundException($"Firmware for controller {assignment.Id} is missing.", firmwarePath);
                return new ProjectControllerAssignment(robotId, assignment.Id, assignment.ComponentId, assignment.Board, firmwarePath,
                    HashFile(firmwarePath), assignment.FirmwareRelative, assignment.FirmwareTarget);
            }).ToArray());
        }
        var references = new[] { worldRelative }.Concat(robotRelatives).Concat(firmwareRelatives).Concat(componentRelatives).Distinct(StringComparer.Ordinal).ToArray();
        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var filePath = ResolveSource(reference, root, firmwareFallbackPath);
            if (!File.Exists(filePath)) throw new FileNotFoundException($"Project dependency '{reference}' is missing.", filePath);
            hashes.Add(reference, HashFile(filePath));
        }
        var worldPath = ResolveContainedPath(root, worldRelative);
        var componentDirectory = ResolveContainedPath(root, Path.GetDirectoryName(componentRelatives.FirstOrDefault() ?? "components/placeholder")!.Replace('\\', '/'));
        FixtureLoader.LoadWorld(worldPath);
        var catalogue = FixtureLoader.LoadCatalogue(componentDirectory);
        foreach (var candidatePath in robotPaths)
        {
            var assembly = FixtureLoader.LoadRobot(candidatePath, componentDirectory);
            foreach (var instance in assembly.Components)
                if (!catalogue.ContainsKey(instance.CatalogueId)) throw new InvalidDataException($"Missing component definition for {instance.Id}: {instance.CatalogueId}.");
            var robotId = assembly.Id;
            foreach (var controller in controllerRecords[robotId])
            {
                if (!assembly.Components.Any(instance => instance.Id == controller.ComponentInstanceId))
                    throw new InvalidDataException($"Controller {controller.Id} references missing component instance {controller.ComponentInstanceId} in {robotId}.");
            }
        }

        var primaryAssignments = controllerRecords.Values.FirstOrDefault()?.ToArray() ?? [];
        var controllerExecutionSettings = ReadControllerExecutionSettings(manifest, controllerRecords);
        var fallbackFirmware = firmwareFallbackPath is null ? string.Empty : Path.GetFullPath(firmwareFallbackPath);
        var firmwarePath = primaryAssignments.FirstOrDefault()?.FirmwarePath ?? fallbackFirmware;
        var manifestHash = HashFile(manifestPath);
        var lockPath = Path.Combine(root, LockName);
        if (File.Exists(lockPath)) ValidateLock(lockPath, projectId, manifestHash, hashes);
        return new ProjectPackageInfo(projectId, title, root, manifestPath, worldPath, robotPath, robotPaths, componentDirectory, firmwarePath,
            File.Exists(firmwarePath) ? HashFile(firmwarePath) : string.Empty, manifestHash, hashes, controllerRecords, controllerExecutionSettings, isTemporary);
    }

    private static ProjectControllerExecutionSettings ReadControllerExecutionSettings(JsonElement manifest,
        IReadOnlyDictionary<string, IReadOnlyList<ProjectControllerAssignment>> assignments)
    {
        var settings = ProjectControllerExecutionSettings.Default;
        if (!manifest.TryGetProperty("settings", out var settingsElement)) return settings;
        if (settingsElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Project settings must be a JSON object.");
        if (!settingsElement.TryGetProperty("controller_execution", out var executionElement)) return settings;
        if (executionElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Project controller_execution settings must be an object.");
        var mode = executionElement.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String
            ? modeElement.GetString() : null;
        if (mode is not ("exact" or "fast" or "auto")) throw new InvalidDataException("Project controller execution mode must be exact, fast, or auto.");
        var knownAssignments = assignments.Values.SelectMany(items => items).Select(item => item.RobotId + "/" + item.Id).ToHashSet(StringComparer.Ordinal);
        var profileReferences = new Dictionary<string, ProjectControllerProfileReference>(StringComparer.Ordinal);
        if (executionElement.TryGetProperty("profiles", out var profilesElement))
        {
            if (profilesElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Project controller profile references must be an object.");
            foreach (var property in profilesElement.EnumerateObject())
            {
                if (!knownAssignments.Contains(property.Name)) throw new InvalidDataException($"Controller profile reference '{property.Name}' does not identify a project controller.");
                if (property.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Controller profile references must be JSON objects.");
                var profileId = property.Value.GetProperty("profile_id").GetString();
                var profileHash = property.Value.GetProperty("profile_sha256").GetString();
                if (profileId is null || !System.Text.RegularExpressions.Regex.IsMatch(profileId, "^[A-Za-z][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                    || profileHash is null || profileHash.Length != 64 || !profileHash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Controller profile reference identity or SHA-256 is invalid.");
                profileReferences.Add(property.Name, new ProjectControllerProfileReference(profileId, profileHash.ToLowerInvariant()));
            }
        }
        return new ProjectControllerExecutionSettings(mode, profileReferences);
    }

    private static byte[] SerializeManifestWithControllerExecution(string manifestPath, ProjectControllerExecutionSettings executionSettings)
    {
        if (executionSettings.Mode is not ("exact" or "fast" or "auto"))
            throw new InvalidDataException("Project controller execution mode must be exact, fast, or auto.");
        var document = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject()
            ?? throw new InvalidDataException("Project manifest must be a JSON object.");
        var settings = document["settings"]?.AsObject() ?? new JsonObject();
        var profiles = new JsonObject();
        foreach (var item in executionSettings.ProfileReferences.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(item.Key) || !System.Text.RegularExpressions.Regex.IsMatch(item.Value.ProfileId, "^[A-Za-z][A-Za-z0-9_.-]{0,127}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || item.Value.ProfileSha256.Length != 64 || !item.Value.ProfileSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException("Controller profile reference identity or SHA-256 is invalid.");
            profiles[item.Key] = new JsonObject
            {
                ["profile_id"] = item.Value.ProfileId,
                ["profile_sha256"] = item.Value.ProfileSha256.ToLowerInvariant()
            };
        }
        settings["controller_execution"] = new JsonObject { ["mode"] = executionSettings.Mode, ["profiles"] = profiles };
        document["settings"] = settings;
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static IReadOnlyList<ProjectReference> GetReferences(string root, string manifestPath)
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var refs = new List<ProjectReference>
        {
            new(manifest.RootElement.GetProperty("world_file").GetString()!)
        };
        refs.AddRange(manifest.RootElement.GetProperty("robot_files").EnumerateArray().Select(item => new ProjectReference(item.GetString()!)));
        refs.AddRange(manifest.RootElement.GetProperty("component_files").EnumerateArray().Select(item => new ProjectReference(item.GetString()!)));
        foreach (var robotFile in manifest.RootElement.GetProperty("robot_files").EnumerateArray())
        {
            using var robot = JsonDocument.Parse(File.ReadAllText(ResolveContainedPath(root, robotFile.GetString()!)));
            foreach (var assignment in robot.RootElement.GetProperty("controllers").EnumerateArray())
                refs.Add(new ProjectReference(assignment.GetProperty("firmware").GetProperty("relative_path").GetString()!));
        }
        return refs.Select(item => new ProjectReference(NormalizeRelativePath(item.RelativePath))).DistinctBy(item => item.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static string ResolveSource(string relativePath, string root, string? firmwareFallbackPath)
    {
        var source = ResolveContainedPath(root, relativePath);
        if (File.Exists(source)) return source;
        if (relativePath.StartsWith("firmware/", StringComparison.OrdinalIgnoreCase))
        {
            var firmwareDirectoryName = Path.GetFileNameWithoutExtension(relativePath);
            for (var directory = new DirectoryInfo(Path.GetFullPath(root)); directory is not null; directory = directory.Parent)
            {
                var conventionalBuild = Path.Combine(directory.FullName, "samples", "firmware", firmwareDirectoryName, "build", "firmware.elf");
                if (File.Exists(conventionalBuild)) return conventionalBuild;
            }
            if (firmwareFallbackPath is not null && File.Exists(firmwareFallbackPath)) return Path.GetFullPath(firmwareFallbackPath);
        }
        return source;
    }

    private static void AddFile(ZipArchive archive, string relativePath, string sourcePath, IDictionary<string, string>? hashes)
    {
        var entry = archive.CreateEntry(relativePath.Replace('\\', '/'), CompressionLevel.Optimal);
        using (var input = File.OpenRead(sourcePath))
        using (var output = entry.Open()) input.CopyTo(output);
        hashes?.Add(relativePath.Replace('\\', '/'), HashFile(sourcePath));
    }

    private static void AddBytes(ZipArchive archive, string relativePath, byte[] contents, IDictionary<string, string>? hashes)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var entry = archive.CreateEntry(normalizedPath, CompressionLevel.Optimal);
        using (var output = entry.Open()) output.Write(contents);
        hashes?.Add(normalizedPath, Convert.ToHexString(SHA256.HashData(contents)).ToLowerInvariant());
    }

    private static byte[] SerializeRobotOverride(string sourcePath, AssemblyGraph graph,
        IReadOnlyList<RobotFirmwareAssignment>? firmwareAssignments = null)
    {
        var document = JsonNode.Parse(File.ReadAllText(sourcePath))?.AsObject()
            ?? throw new InvalidDataException("Robot design JSON must contain an object root.");
        document["id"] = graph.Id;
        document["display_name"] = graph.DisplayName;
        document["root_link_id"] = graph.RootLinkId;
        var originalLinks = document["links"]?.AsArray().ToDictionary(
            node => node?["id"]?.GetValue<string>() ?? throw new InvalidDataException("Robot link is missing an id."),
            node => node?.DeepClone().AsObject() ?? throw new InvalidDataException("Robot link must be an object."), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var links = new JsonArray();
        foreach (var link in graph.Links)
        {
            var node = originalLinks.TryGetValue(link.Id, out var existingLink) ? existingLink : new JsonObject();
            node["id"] = link.Id;
            node["base_component_id"] = link.BaseComponentId;
            node["initial_transform"] = SerializeTransform(link.PositionM, link.RotationXyzw);
            if (link.IsRoot) node["is_root"] = true;
            else node.Remove("is_root");
            links.Add(node);
        }
        document["links"] = links;

        var originalComponents = document["components"]?.AsArray().ToDictionary(
            node => node?["id"]?.GetValue<string>() ?? throw new InvalidDataException("Robot component is missing an id."),
            node => node?.DeepClone().AsObject() ?? throw new InvalidDataException("Robot component must be an object."), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var components = new JsonArray();
        foreach (var instance in graph.Components)
        {
            var component = originalComponents.TryGetValue(instance.Id, out var existing) ? existing : new JsonObject();
            component["id"] = instance.Id;
            component["catalogue_id"] = instance.CatalogueId;
            component["catalogue_version"] = instance.CatalogueVersion;
            component["link_id"] = instance.LinkId;
            component["transform"] = new JsonObject
            {
                ["position_m"] = JsonSerializer.SerializeToNode(instance.PositionM),
                ["rotation_xyzw"] = JsonSerializer.SerializeToNode(instance.RotationXyzw)
            };
            if (instance.SizeOverrideM is not null)
            {
                component["overrides"] ??= new JsonObject();
                component["overrides"]!["size_m"] = JsonSerializer.SerializeToNode(instance.SizeOverrideM);
            }
            else if (component["overrides"] is JsonObject sizeOverrides)
            {
                sizeOverrides.Remove("size_m");
            }
            if (instance.DisplayName is not null)
            {
                component["overrides"] ??= new JsonObject();
                component["overrides"]!["display_name"] = instance.DisplayName;
            }
            else if (component["overrides"] is JsonObject nameOverrides) nameOverrides.Remove("display_name");
            if (instance.Camera is not null)
            {
                component["overrides"] ??= new JsonObject();
                component["overrides"]!["camera"] = new JsonObject
                {
                    ["horizontal_fov_deg"] = instance.Camera.HorizontalFovDeg,
                    ["resolution_width_px"] = instance.Camera.ResolutionWidthPx,
                    ["resolution_height_px"] = instance.Camera.ResolutionHeightPx,
                    ["near_clip_m"] = instance.Camera.NearClipM,
                    ["far_clip_m"] = instance.Camera.FarClipM,
                    ["enabled"] = instance.Camera.Enabled
                };
            }
            else if (component["overrides"] is JsonObject cameraOverrides) cameraOverrides.Remove("camera");
            if (component["overrides"] is JsonObject remainingOverrides && remainingOverrides.Count == 0) component.Remove("overrides");
            components.Add(component);
        }
        document["components"] = components;

        var originalJoints = document["joints"]?.AsArray().ToDictionary(
            node => node?["id"]?.GetValue<string>() ?? throw new InvalidDataException("Robot joint is missing an id."),
            node => node?.DeepClone().AsObject() ?? throw new InvalidDataException("Robot joint must be an object."), StringComparer.Ordinal)
            ?? new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var joints = new JsonArray();
        foreach (var joint in graph.Joints)
        {
            var node = originalJoints.TryGetValue(joint.Id, out var existingJoint) ? existingJoint : new JsonObject();
            node["id"] = joint.Id;
            node["type"] = joint.Type;
            node["parent_link_id"] = joint.ParentLinkId;
            node["child_link_id"] = joint.ChildLinkId;
            node["axis_local"] = JsonSerializer.SerializeToNode(joint.AxisLocal);
            node["parent_frame"] = SerializeTransform(joint.ParentFramePositionM, joint.ParentFrameRotationXyzw);
            node["child_frame"] = SerializeTransform(joint.ChildFramePositionM, joint.ChildFrameRotationXyzw);
            SetOptionalString(node, "actuator_component_id", joint.ActuatorComponentId);
            SetOptionalString(node, "parent_port_id", joint.ParentPortId);
            SetOptionalString(node, "child_port_id", joint.ChildPortId);
            SetOptionalString(node, "joint_profile_id", joint.JointProfileId);
            SetOptionalString(node, "joint_profile_version", joint.JointProfileVersion);
            SetOptionalString(node, "parent_component_id", joint.ParentComponentId);
            SetOptionalString(node, "child_component_id", joint.ChildComponentId);
            joints.Add(node);
        }
        document["joints"] = joints;

        var electricalConnections = new JsonArray();
        foreach (var connection in graph.ElectricalConnections)
        {
            var node = new JsonObject
            {
                ["id"] = connection.Id,
                ["from"] = connection.From,
                ["to"] = connection.To,
                ["interface"] = connection.Interface
            };
            SetOptionalString(node, "physical_pin", connection.PhysicalPin);
            SetOptionalString(node, "bus_id", connection.BusId);
            electricalConnections.Add(node);
        }
        document["electrical_connections"] = electricalConnections;

        var mountConnections = new JsonArray();
        foreach (var connection in graph.ComponentMountConnections ?? [])
            mountConnections.Add(new JsonObject
            {
                ["id"] = connection.Id,
                ["parent_component_id"] = connection.ParentComponentId,
                ["parent_port_id"] = connection.ParentPortId,
                ["child_component_id"] = connection.ChildComponentId,
                ["child_port_id"] = connection.ChildPortId
            });
        document["component_mount_connections"] = mountConnections;
        if (firmwareAssignments is not null)
        {
            var controllers = new JsonArray();
            foreach (var assignment in firmwareAssignments)
                controllers.Add(new JsonObject
                {
                    ["id"] = assignment.Id,
                    ["component_instance_id"] = assignment.ControllerComponentId,
                    ["board"] = assignment.Board,
                    ["firmware"] = new JsonObject
                    {
                        ["relative_path"] = assignment.RelativePath,
                        ["target"] = assignment.Target
                    },
                    ["uart_channels"] = new JsonArray("uart")
                });
            document["controllers"] = controllers;
        }
        return JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
    }

    private static JsonObject SerializeTransform(double[] positionM, double[] rotationXyzw) => new()
    {
        ["position_m"] = JsonSerializer.SerializeToNode(positionM),
        ["rotation_xyzw"] = JsonSerializer.SerializeToNode(rotationXyzw)
    };

    private static void SetOptionalString(JsonObject node, string propertyName, string? value)
    {
        if (value is null) node.Remove(propertyName);
        else node[propertyName] = value;
    }

    private static void ValidateLock(string lockPath, string projectId, string manifestHash, IReadOnlyDictionary<string, string> actualHashes)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
        var root = document.RootElement;
        if (root.GetProperty("formatVersion").GetString() != "1.0" || root.GetProperty("projectId").GetString() != projectId)
            throw new InvalidDataException("Project lock file identifies an incompatible project or format version.");
        if (!string.Equals(root.GetProperty("projectJsonSha256").GetString(), manifestHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Project manifest hash does not match project.lock.json.");
        var expected = root.GetProperty("fileHashes");
        foreach (var item in actualHashes)
        {
            if (!expected.TryGetProperty(item.Key, out var expectedHash) || !string.Equals(expectedHash.GetString(), item.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Project dependency hash mismatch: {item.Key}.");
        }
        if (expected.EnumerateObject().Count() != actualHashes.Count) throw new InvalidDataException("Project lock file contains stale or missing dependency records.");
    }

    private static string NormalizeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) throw new InvalidDataException("Project references must be non-empty relative paths.");
        var normalized = value.Replace('\\', '/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or "..")) throw new InvalidDataException($"Unsafe project reference path: {value}.");
        return normalized;
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalized = NormalizeRelativePath(relativePath);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Project reference escapes its root: {relativePath}.");
        return fullPath;
    }

    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private sealed record ProjectReference(string RelativePath);
    private sealed record ProjectLock(string FormatVersion, string ProjectId, string ProjectJsonSha256, IReadOnlyDictionary<string, string> FileHashes);
}
