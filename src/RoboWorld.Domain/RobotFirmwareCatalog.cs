using System.Security.Cryptography;

namespace RoboWorld.Domain;

/// <summary>A registered compiled firmware artifact usable by a compatible catalogue controller.</summary>
public sealed record RobotFirmwareArtifact(string Id, string DisplayName, string Board, string Target,
    string RelativePath, string FullPath, string Sha256);

/// <summary>Discovers only firmware artifacts already registered by a project controller assignment.</summary>
public static class RobotFirmwareCatalog
{
    public static IReadOnlyList<RobotFirmwareArtifact> FromProject(ProjectPackageInfo project)
    {
        var artifacts = new Dictionary<string, RobotFirmwareArtifact>(StringComparer.Ordinal);
        foreach (var assignment in project.ControllersByRobotId.Values.SelectMany(items => items))
        {
            if (!File.Exists(assignment.FirmwarePath)) continue;
            var artifactDirectory = assignment.FirmwareRelativePath is null
                ? Directory.GetParent(Path.GetDirectoryName(assignment.FirmwarePath)!)?.Name
                : Path.GetFileNameWithoutExtension(assignment.FirmwareRelativePath);
            if (string.IsNullOrWhiteSpace(artifactDirectory)
                || !System.Text.RegularExpressions.Regex.IsMatch(artifactDirectory, "^[A-Za-z0-9_.-]{1,64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                continue;
            var sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assignment.FirmwarePath))).ToLowerInvariant();
            var target = assignment.FirmwareTarget
                ?? (assignment.Board.EndsWith("stm32f407vg", StringComparison.OrdinalIgnoreCase) ? "stm32f407vg" : string.Empty);
            if (target.Length == 0) continue;
            var artifact = new RobotFirmwareArtifact(artifactDirectory, $"STM32F407VG · {artifactDirectory}", assignment.Board,
                target, assignment.FirmwareRelativePath ?? $"firmware/{artifactDirectory}.elf", Path.GetFullPath(assignment.FirmwarePath), sha256);
            if (artifacts.TryGetValue(artifact.Id, out var existing)
                && (!StringComparer.OrdinalIgnoreCase.Equals(existing.Sha256, artifact.Sha256) || existing.Board != artifact.Board))
                throw new InvalidDataException($"Registered firmware id {artifact.Id} resolves to conflicting artifacts.");
            artifacts[artifact.Id] = artifact;
        }
        return artifacts.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray();
    }
}
