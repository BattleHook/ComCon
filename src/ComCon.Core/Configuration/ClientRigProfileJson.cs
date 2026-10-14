using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Models;

namespace ComCon.Core.Configuration;

public sealed record ClientRigProfile(ComConRigProfileMapping Mapping, FramingMode Framing);

public static class ClientRigProfileJson
{
    public static ClientRigProfile Load(string path, int profileId)
    {
        using var stream = File.OpenRead(path);
        var bytes = new byte[ConfigurationFileStore.MaxFileSizeBytes + 1];
        var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (count > ConfigurationFileStore.MaxFileSizeBytes)
            throw new InvalidDataException("Client rig-profile JSON exceeds the 1 MiB import limit.");
        return Parse(bytes.AsMemory(0, count), profileId);
    }

    public static ClientRigProfile Parse(ReadOnlyMemory<byte> json, int profileId)
    {
        if (json.Length > ConfigurationFileStore.MaxFileSizeBytes)
            throw new InvalidDataException("Client rig-profile JSON exceeds the 1 MiB import limit.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Client rig-profile JSON must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name))
                throw new InvalidDataException($"Duplicate client rig-profile property '{property.Name}'.");

        if (RequiredString(root, "schema") != "ham-shack-console/rig-profile" ||
            !root.TryGetProperty("schema_version", out var version) ||
            version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var number) || number != 1)
            throw new InvalidDataException("Supported client rig-profile format is ham-shack-console/rig-profile version 1.");

        var framing = RequiredString(root, "transport") switch
        {
            "CIV" => FramingMode.Civ,
            "KENWOOD_ASCII" => FramingMode.KenwoodAscii,
            var transport => throw new InvalidDataException(
                $"Client rig transport '{transport}' is not supported by ComCon command IPC in this build.")
        };
        var mapping = new ComConRigProfileMapping(RequiredString(root, "rig_profile_uuid"), profileId)
        {
            RigModelName = RequiredString(root, "display_name"),
            CivAddress = framing == FramingMode.Civ ? RequiredString(root, "civ_address") : null
        };
        var defaults = AppSettings.CreateDefault();
        var candidate = defaults with
        {
            Profiles = defaults.Profiles.Select(profile => profile with { Framing = framing }).ToArray(),
            RigProfileMappings = [mapping]
        };
        var errors = ConfigurationValidator.Validate(candidate);
        if (errors.Count != 0) throw new ConfigurationValidationException(errors);
        return new(mapping, framing);
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Client rig-profile property '{name}' must be a non-empty string.");
        return value.GetString()!;
    }
}
