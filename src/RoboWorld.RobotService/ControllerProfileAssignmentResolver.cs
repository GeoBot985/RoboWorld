using System.Text.Json;
using RoboWorld.Domain;

namespace RoboWorld.RobotService;

public sealed record ControllerProfileStartupReference(string RobotId, string ControllerId, string ProfileId,
    string ProfileSha256, string RobotPath, string? EchoInputEndpointId = null);

/// <summary>Resolves project-pinned, version-compatible, content-addressed models before backend selection.</summary>
public static class ControllerProfileAssignmentResolver
{
    private static readonly HashSet<(string Generator, string Evaluator)> SupportedRuntimeVersions =
    [
        ("goal03-bounded-rover-controller-2.0.0", "campaign-evaluator-2+uart-transducer-6+echo-pulse-state-4+sensor-envelope-800-2500-1+numeric-sensor-tolerance-2mm-1+noecho-retain-1+measured-mcu-halt-latch-2+fault-source-diagnostic-1+uart-response-timing-bracket-1+qualification-evidence-1"),
        ("goal03-motor-uart-echo-pulse-grammar-1.0.0", "campaign-evaluator-2+uart-transducer-6+exact-line-rule-2+echo-pulse-state-2+heldout-pulse-replay-1+line-bound-47+status-range-grammar-1+initial-uart-output-1"),
        ("goal03-motor-uart-echo-pulse-grammar-1.0.0", "campaign-evaluator-2+uart-transducer-6+exact-line-rule-2+echo-pulse-state-3+heldout-pulse-replay-1+line-bound-47+status-range-grammar-1+initial-uart-output-1+echo-noecho-contract-1"),
        ("goal03-motor-uart-echo-pulse-noecho-grammar-1.0.0", "campaign-evaluator-2+uart-transducer-6+exact-line-rule-2+echo-pulse-state-3+heldout-pulse-replay-1+line-bound-47+status-range-grammar-1+initial-uart-output-1+noecho-retain-1+invalid-initial-sensor-reply-1")
    ];
    private static readonly JsonSerializerOptions ProfileJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static IReadOnlyList<ValidatedModelAssignment> LoadAssignments(
        IReadOnlyList<RenodeControllerOptions> controllers,
        IReadOnlyList<ControllerProfileStartupReference> references,
        IReadOnlyDictionary<string, ProjectControllerProfileReference> projectReferences,
        string profilesRoot, string componentDirectory, string peripheralMapSourcePath)
    {
        try
        {
            return LoadAssignmentsCore(controllers, references, projectReferences, profilesRoot, componentDirectory, peripheralMapSourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            throw new InvalidDataException("Controller profile preflight could not read or validate its bounded artifacts: " + exception.Message, exception);
        }
    }

    private static IReadOnlyList<ValidatedModelAssignment> LoadAssignmentsCore(
        IReadOnlyList<RenodeControllerOptions> controllers,
        IReadOnlyList<ControllerProfileStartupReference> references,
        IReadOnlyDictionary<string, ProjectControllerProfileReference> projectReferences,
        string profilesRoot, string componentDirectory, string peripheralMapSourcePath)
    {
        ArgumentNullException.ThrowIfNull(controllers);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(projectReferences);
        var byController = references.ToDictionary(item => item.ControllerId, StringComparer.Ordinal);
        if (byController.Count != controllers.Count || controllers.Any(item => !byController.ContainsKey(item.Id)))
            throw new InvalidDataException("Every controller must have one startup profile reference.");
        var fullProfilesRoot = Path.GetFullPath(profilesRoot);
        var assignments = new List<ValidatedModelAssignment>(controllers.Count);
        foreach (var controller in controllers)
        {
            var reference = byController[controller.Id];
            if (!System.Text.RegularExpressions.Regex.IsMatch(reference.ProfileId, "^[A-Za-z][A-Za-z0-9_.-]{0,127}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                || !StringComparer.Ordinal.Equals(reference.ControllerId, controller.Id)
                || !System.Text.RegularExpressions.Regex.IsMatch(reference.ProfileSha256, "^[0-9a-fA-F]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidDataException($"Controller {controller.Id} has a malformed startup profile reference.");
            var projectReferenceKey = reference.RobotId + "/" + reference.ControllerId;
            if (!projectReferences.TryGetValue(projectReferenceKey, out var projectReference)
                || !StringComparer.Ordinal.Equals(projectReference.ProfileId, reference.ProfileId)
                || !ControllerProfileHashes.Matches(projectReference.ProfileSha256, reference.ProfileSha256))
                throw new InvalidDataException($"Controller {controller.Id} startup profile differs from the saved project reference.");
            var profileDirectory = Path.GetFullPath(Path.Combine(fullProfilesRoot, reference.ProfileId));
            if (!profileDirectory.StartsWith(fullProfilesRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException("Controller profile path escaped its configured content store.");
            var profilePath = Path.Combine(profileDirectory, "profile.json");
            var modelPath = Path.Combine(profileDirectory, "model.json");
            var profileBytes = ReadBoundedProfile(profilePath);
            var profile = JsonSerializer.Deserialize<DeterministicControllerProfile>(profileBytes, ProfileJson)
                ?? throw new InvalidDataException("Controller profile metadata is empty.");
            if (profile.Identity is null || profile.Envelope is null)
                throw new InvalidDataException("Controller profile metadata is missing its identity or input envelope.");
            if (!SupportedRuntimeVersions.Contains((profile.Identity.GeneratorVersion, profile.Identity.EvaluatorVersion)))
                throw new InvalidDataException($"Controller {controller.Id} profile generator/evaluator version is not supported by this runtime.");
            var currentIdentity = ControllerProfileIdentityFactory.CreateFromProjectFiles(reference.RobotPath,
                componentDirectory, controller.FirmwarePath, peripheralMapSourcePath, controller.Id, profile.Envelope,
                profile.Identity.GeneratorVersion, profile.Identity.EvaluatorVersion);
            var assignment = ControllerProfileArtifactLoader.LoadAssignment(controller.Id, projectReference,
                profilePath, modelPath, currentIdentity);
            if (!StringComparer.Ordinal.Equals(assignment.Model.EchoPulse?.InputEndpointId, reference.EchoInputEndpointId))
                throw new InvalidDataException($"Controller {controller.Id} model sensor endpoint does not match the current robot electrical graph route.");
            assignments.Add(assignment);
        }
        return assignments;
    }

    private static byte[] ReadBoundedProfile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > ControllerProfileArtifactLoader.MaximumArtifactBytes)
            throw new InvalidDataException("Controller profile metadata exceeds its bounded artifact size.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length) throw new InvalidDataException("Controller profile metadata changed while being read.");
        return bytes;
    }
}
