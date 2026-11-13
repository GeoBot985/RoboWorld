namespace RoboWorld.Domain;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Loads one project-referenced, content-addressed controller profile without trusting paths or labels alone.</summary>
public static class ControllerProfileArtifactLoader
{
    public const int MaximumArtifactBytes = 1_048_576;

    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// Validates an on-disk profile/model pair against the immutable project reference and current hardware identity.
    /// The returned assignment is still validated again by <see cref="DeterministicModelBackend"/> at construction.
    /// </summary>
    public static ValidatedModelAssignment LoadAssignment(string controllerInstanceId,
        ProjectControllerProfileReference projectReference, string profilePath, string modelPath,
        ControllerProfileIdentity currentIdentity)
    {
        if (string.IsNullOrWhiteSpace(controllerInstanceId)) throw new InvalidDataException("Controller instance ID is required.");
        ArgumentNullException.ThrowIfNull(projectReference);
        ArgumentNullException.ThrowIfNull(currentIdentity);
        if (!IsSha256(projectReference.ProfileSha256)) throw new InvalidDataException("Project profile reference has an invalid SHA-256 digest.");

        var profileBytes = ReadBounded(profilePath, "profile");
        var actualProfileHash = Convert.ToHexString(SHA256.HashData(profileBytes));
        if (!ControllerProfileHashes.Matches(projectReference.ProfileSha256, actualProfileHash))
            throw new InvalidDataException("Controller profile artifact hash does not match the saved project reference.");

        var profile = Deserialize<DeterministicControllerProfile>(profileBytes, "profile");
        if (profile.SchemaVersion != 1 || !StringComparer.Ordinal.Equals(profile.ProfileId, projectReference.ProfileId))
            throw new InvalidDataException("Controller profile identity does not match the saved project reference.");
        if (!StringComparer.Ordinal.Equals(profile.FidelityLabel, "VALIDATED"))
            throw new InvalidDataException("Only profiles explicitly labeled VALIDATED may be loaded for runtime execution.");
        var identity = ControllerProfileValidator.Validate(profile.Identity, currentIdentity);
        if (!identity.IsValid) throw new InvalidDataException("Controller profile identity mismatch: " + string.Join(",", identity.MismatchReasons));

        var modelBytes = ReadBounded(modelPath, "model");
        var model = Deserialize<ControllerModelDocument>(modelBytes, "model");
        if (!ControllerProfileHashes.Matches(profile.ModelDocumentSha256, ControllerProfileHashes.Compute(model)))
            throw new InvalidDataException("Controller model document hash does not match its profile.");
        if (!ControllerProfileHashes.Matches(profile.Identity.InputEnvelopeSha256, ControllerProfileHashes.Compute(profile.Envelope)))
            throw new InvalidDataException("Controller input envelope hash does not match its profile identity.");

        // Run bounded schema validation before returning the assignment; the backend repeats it during construction.
        ControllerModelEvaluator.ValidateDocument(model, model.MaximumRules);
        return new ValidatedModelAssignment(controllerInstanceId, profile, currentIdentity, model);
    }

    private static byte[] ReadBounded(string path, string artifactName)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException($"Controller {artifactName} artifact path is required.");
        var fullPath = Path.GetFullPath(path);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        if (stream.Length is <= 0 or > MaximumArtifactBytes)
            throw new InvalidDataException($"Controller {artifactName} artifact exceeds the bounded {MaximumArtifactBytes}-byte file limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.Position != stream.Length)
            throw new InvalidDataException($"Controller {artifactName} artifact changed while it was being read or exceeded its size limit.");
        return bytes;
    }

    private static T Deserialize<T>(byte[] bytes, string artifactName) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, ArtifactJson)
                ?? throw new InvalidDataException($"Controller {artifactName} artifact is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Controller {artifactName} artifact is not valid schema JSON.", exception);
        }
    }

    private static bool IsSha256(string value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
