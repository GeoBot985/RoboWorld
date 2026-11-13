using System.Security.Cryptography;
using System.Text.Json;

namespace RoboWorld.Domain;

/// <summary>Versioned persistence for incomplete authoring state, separate from runnable project packages.</summary>
public static class RobotAuthoringDraftStore
{
    private const string FormatVersion = "1.0";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static string Save(string path, RobotAuthoringDraft draft,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        Validate(draft, catalogue);
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".rwdraft", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Robot authoring drafts must use the .rwdraft extension.");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DraftFile(FormatVersion, draft), Options);
        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(payload);
                stream.Flush(true);
            }
            File.Move(tempPath, fullPath, true);
            return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }
    }

    public static RobotAuthoringDraft Open(string path,
        IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        var file = JsonSerializer.Deserialize<DraftFile>(File.ReadAllBytes(path), Options)
            ?? throw new InvalidDataException("Robot draft file is empty.");
        if (file.SchemaVersion != FormatVersion) throw new InvalidDataException($"Unsupported robot draft schema version '{file.SchemaVersion}'.");
        Validate(file.Draft, catalogue);
        return file.Draft;
    }

    private static void Validate(RobotAuthoringDraft draft, IReadOnlyDictionary<string, ComponentDefinition> catalogue)
    {
        if (string.IsNullOrWhiteSpace(draft.Id) || string.IsNullOrWhiteSpace(draft.DisplayName))
            throw new InvalidDataException("A robot draft needs an id and display name.");
        if (draft.Assembly is null)
        {
            if ((draft.FirmwareAssignments ?? []).Count != 0)
                throw new InvalidDataException("An empty draft cannot contain firmware assignments.");
            return;
        }
        if (draft.Assembly.Id != draft.Id || draft.Assembly.DisplayName != draft.DisplayName)
            throw new InvalidDataException("Draft and assembly identities do not match.");
        RobotGraphValidator.Validate(draft.Assembly, catalogue);
        var assignments = draft.FirmwareAssignments ?? [];
        if (assignments.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != assignments.Count
            || assignments.Select(item => item.ControllerComponentId).Distinct(StringComparer.Ordinal).Count() != assignments.Count)
            throw new InvalidDataException("A draft cannot contain duplicate firmware assignment or controller identities.");
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Id) || string.IsNullOrWhiteSpace(assignment.Board) || string.IsNullOrWhiteSpace(assignment.Target)
                || string.IsNullOrWhiteSpace(assignment.ControllerComponentId))
                throw new InvalidDataException("A firmware assignment needs an id, controller, board and target.");
            if (string.IsNullOrWhiteSpace(assignment.RelativePath) || Path.IsPathRooted(assignment.RelativePath)
                || assignment.RelativePath.Replace('\\', '/').Split('/').Any(segment => segment is "" or "." or ".."))
                throw new InvalidDataException($"Firmware assignment {assignment.Id} must reference a safe relative artifact path.");
            if (assignment.FirmwareSha256.Length != 64 || !assignment.FirmwareSha256.All(Uri.IsHexDigit))
                throw new InvalidDataException($"Firmware assignment {assignment.Id} needs a valid SHA-256 checksum.");
            var controller = draft.Assembly.Components.SingleOrDefault(component => component.Id == assignment.ControllerComponentId)
                ?? throw new InvalidDataException($"Firmware assignment {assignment.Id} references missing controller {assignment.ControllerComponentId}.");
            if (!catalogue[controller.CatalogueId].ElectricalPorts.Any(port => port.Interface == "uart"))
                throw new InvalidDataException($"Firmware assignment {assignment.Id} references a component without a UART controller interface.");
        }
    }

    private sealed record DraftFile(string SchemaVersion, RobotAuthoringDraft Draft);
}
