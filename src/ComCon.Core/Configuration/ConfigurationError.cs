using System.Globalization;
using System.Text.RegularExpressions;

namespace ComCon.Core.Configuration;

public sealed record ConfigurationError(string Path, string Message);

public sealed partial class ConfigurationValidationException : Exception
{
    public ConfigurationValidationException(IReadOnlyList<ConfigurationError> errors)
        : base(CreateMessage(errors))
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    public IReadOnlyList<ConfigurationError> Errors { get; }

    private static string CreateMessage(IReadOnlyList<ConfigurationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            return "Configuration validation failed.";
        }

        var lines = new List<string>
        {
            $"Configuration validation failed with {errors.Count} error(s):",
            string.Empty
        };

        foreach (var error in errors)
        {
            var friendlyPath = FormatPath(error.Path);
            var friendlyMessage = FormatMessage(error.Message);
            lines.Add($"• {friendlyPath}: {friendlyMessage}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string FormatPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Configuration";
        }

        var profileMatch = ProfilePathRegex().Match(path);
        if (profileMatch.Success)
        {
            var index = int.Parse(profileMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var profileNum = index + 1;
            var subPath = profileMatch.Groups[2].Value;

            if (string.IsNullOrEmpty(subPath))
            {
                return $"Profile {profileNum}";
            }

            var friendlySub = subPath switch
            {
                "physicalPort" => "Physical Radio COM",
                "virtualPort" => "ComCon Virtual COM",
                "commanderPort" => "Legacy Client COM",
                "commandIpcEnabled" => "Command IPC",
                "autoConnect" => "Auto-connect",
                "enabled" => "Enabled",
                "label" => "Name / Label",
                "adapter" => "Adapter",
                "framing" => "Framing",
                "legacyClientAddress" => "Legacy Client Address",
                "ipcClientAddress" => "IPC Client Address",
                "radioAddress" => "Radio CI-V Address",
                "controllerAddress" => "Controller CI-V Address",
                "serialSettings.baudRate" => "Baud Rate",
                "serialSettings.dataBits" => "Data Bits",
                "serialSettings.parity" => "Parity",
                "serialSettings.stopBits" => "Stop Bits",
                "serialSettings.handshake" => "Handshake",
                "serialSettings" => "Serial Settings",
                "id" => "Profile ID",
                _ => subPath
            };

            return $"Profile {profileNum} - {friendlySub}";
        }

        var rigMatch = RigMappingPathRegex().Match(path);
        if (rigMatch.Success)
        {
            var index = int.Parse(rigMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            var mappingNum = index + 1;
            var subPath = rigMatch.Groups[2].Value;

            if (string.IsNullOrEmpty(subPath))
            {
                return $"Rig Profile Mapping {mappingNum}";
            }

            var friendlySub = subPath switch
            {
                "rigProfileId" => "Rig UUID",
                "rigModelName" => "Model Name",
                "civAddress" => "CI-V Address",
                "profileId" => "Profile Assignment",
                _ => subPath
            };

            return $"Rig Profile Mapping {mappingNum} - {friendlySub}";
        }

        return path switch
        {
            "schemaVersion" => "Schema Version",
            "profiles" => "Profiles",
            "routingLimits" => "Routing Limits",
            _ when path.StartsWith("routingLimits.", StringComparison.Ordinal) =>
                $"Routing Limits - {path["routingLimits.".Length..]}",
            _ => path
        };
    }

    public static string FormatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        return EmbeddedPathRegex().Replace(message, match => FormatPath(match.Value));
    }

    [GeneratedRegex(@"^profiles\[(\d+)\](?:\.(.+))?$")]
    private static partial Regex ProfilePathRegex();

    [GeneratedRegex(@"^rigProfileMappings\[(\d+)\](?:\.(.+))?$")]
    private static partial Regex RigMappingPathRegex();

    [GeneratedRegex(@"(profiles\[\d+\](?:\.[a-zA-Z0-9_]+)*|rigProfileMappings\[\d+\](?:\.[a-zA-Z0-9_]+)*)")]
    private static partial Regex EmbeddedPathRegex();
}

public sealed class ConfigurationSerializationException : Exception
{
    public ConfigurationSerializationException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
