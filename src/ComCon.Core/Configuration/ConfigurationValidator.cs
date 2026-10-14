using System.Globalization;
using ComCon.Core.Models;

namespace ComCon.Core.Configuration;

public static class ConfigurationValidator
{
    private const int MaximumProfiles = 4;
    private const int MaximumBaudRate = 4_000_000;
    private const int MaximumConnections = 16;
    private const int MaximumHelloDeadlineMs = 3_000;
    private const int MaximumPartialJsonDeadlineMs = 2_000;
    private const int MaximumLeaseDurationMs = 5_000;
    private const int MaximumNormalQueueCapacity = 32;
    private const int MaximumPendingRequestsPerClient = 4;
    private const int MaximumLeaseControlQueueCapacity = 16;
    private const int MaximumHardwareQueueWaitMs = 250;
    private const int MaximumCatSliceMs = 250;
    private const int MaximumCatTransactionTimeoutMs = 3_000;
    private const int MinimumIpcTimeoutMs = 100;
    private const int MaximumIpcTimeoutMs = 3_000;
    private const int MaximumOutboundQueueFrames = 32;
    private const int MaximumOutboundQueueBytes = 256 * 1_024;
    private const int MaximumBlockedPipeWriteTimeoutMs = 2_000;
    private const int MaximumShutdownGracePeriodMs = 3_000;
    private const int MaximumQuietGuardMs = 50;
    private const int MaximumTransitionTimeoutMs = 1_000;

    public static IReadOnlyList<ConfigurationError> Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var errors = new List<ConfigurationError>();
        if (settings.SchemaVersion != 1)
        {
            Add(errors, "schemaVersion", "Must be 1.");
        }

        ValidateRoutingLimits(settings.RoutingLimits, errors);

        var profiles = settings.Profiles;
        if (profiles.Count is < 1 or > MaximumProfiles)
        {
            Add(errors, "profiles", $"Must contain between 1 and {MaximumProfiles} profiles.");
        }

        var ids = new HashSet<int>();
        var ports = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var prefix = $"profiles[{index}]";
            if (profile is null)
            {
                Add(errors, prefix, "Profile entry cannot be null.");
                continue;
            }

            if (profile.Id is < 1 or > MaximumProfiles)
            {
                Add(errors, $"{prefix}.id", $"Must be between 1 and {MaximumProfiles}.");
            }
            else if (!ids.Add(profile.Id))
            {
                Add(errors, $"{prefix}.id", $"Profile ID {profile.Id} is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(profile.Label) || profile.Label.Length > 64)
            {
                Add(errors, $"{prefix}.label", "Must contain 1-64 non-whitespace characters.");
            }

            if (!Enum.IsDefined(profile.Adapter))
            {
                Add(errors, $"{prefix}.adapter", "Unsupported adapter value.");
            }

            if (!Enum.IsDefined(profile.Framing))
            {
                Add(errors, $"{prefix}.framing", "Unsupported framing mode.");
            }

            if (profile.AutoConnect && !profile.Enabled)
            {
                Add(errors, $"{prefix}.autoConnect", "Requires the profile to be enabled.");
            }

            if (profile.CommandIpcEnabled)
            {
                CheckRange(errors, $"{prefix}.legacyClientAddress", profile.LegacyClientAddress, 0, byte.MaxValue);
                CheckRange(errors, $"{prefix}.ipcClientAddress", profile.IpcClientAddress, 0, byte.MaxValue);
                if (profile.LegacyClientAddress == profile.IpcClientAddress)
                    Add(errors, $"{prefix}.ipcClientAddress", "Must differ from the LegacyClient address.");
                if (!profile.Enabled)
                    Add(errors, $"{prefix}.commandIpcEnabled", "Requires the profile to be enabled.");
                if (profile.Framing is not (FramingMode.Civ or FramingMode.KenwoodAscii))
                    Add(errors, $"{prefix}.framing", "Command IPC requires CI-V or Kenwood ASCII framing.");
            }

            var requiresIcomAddresses = profile.Enabled && profile.Adapter == AdapterKind.IcomIc7600;
            ValidateAddress(
                profile.RadioAddress,
                isRadioAddress: true,
                $"{prefix}.radioAddress",
                requiresIcomAddresses,
                errors);
            ValidateAddress(
                profile.ControllerAddress,
                isRadioAddress: false,
                $"{prefix}.controllerAddress",
                requiresIcomAddresses,
                errors);
            if (profile.RadioAddress.HasValue &&
                profile.ControllerAddress.HasValue &&
                profile.RadioAddress.Value == profile.ControllerAddress.Value)
            {
                Add(errors, $"{prefix}.controllerAddress", "Must differ from the radio address.");
            }

            ValidateSerialSettings(profile.SerialSettings, $"{prefix}.serialSettings", errors);

            ValidatePort(profile.PhysicalPort, $"{prefix}.physicalPort", profile.Enabled, ports, errors);
            ValidatePort(profile.VirtualPort, $"{prefix}.virtualPort", profile.Enabled, ports, errors);
            ValidatePort(profile.CommanderPort, $"{prefix}.commanderPort", profile.Enabled, ports, errors);
        }

        var rigIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < settings.RigProfileMappings.Count; index++)
        {
            var mapping = settings.RigProfileMappings[index];
            var prefix = $"rigProfileMappings[{index}]";
            if (mapping is null)
            {
                Add(errors, prefix, "Mapping entry cannot be null.");
                continue;
            }

            if (!Guid.TryParseExact(mapping.RigProfileId, "D", out var uuid) ||
                uuid.ToString("D") != mapping.RigProfileId)
            {
                Add(errors, $"{prefix}.rigProfileId", "Must be a canonical lowercase hyphenated UUID.");
            }
            else if (!rigIds.Add(mapping.RigProfileId))
            {
                Add(errors, $"{prefix}.rigProfileId", "Rig UUID is duplicated; each rig must have exactly one mapping.");
            }

            if (mapping.RigModelName is { } modelName &&
                (string.IsNullOrWhiteSpace(modelName) || modelName.Length > 128 || modelName.Any(char.IsControl)))
            {
                Add(errors, $"{prefix}.rigModelName",
                    "When provided, must contain 1-128 printable characters.");
            }

            if (mapping.CivAddress is { } civAddress &&
                (civAddress.Length != 2 ||
                 !byte.TryParse(civAddress, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)))
            {
                Add(errors, $"{prefix}.civAddress", "When provided, must be a two-digit hexadecimal CI-V address.");
            }
            else if (mapping.CivAddress is not null &&
                     profiles.FirstOrDefault(profile => profile?.Id == mapping.ProfileId)?.Framing != FramingMode.Civ)
            {
                Add(errors, $"{prefix}.civAddress", "A CI-V address is only valid for a CI-V-framed profile.");
            }

            if (!ids.Contains(mapping.ProfileId))
            {
                Add(errors, $"{prefix}.profileId", "Must reference an existing profile ID from 1 through 4.");
            }
        }

        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            if (profile is null || !profile.CommandIpcEnabled) continue;
            if (!settings.RigProfileMappings.Any(mapping => mapping is not null && mapping.ProfileId == profile.Id))
            {
                Add(errors, $"profiles[{index}].commandIpcEnabled",
                    "An opted-in command profile requires at least one persisted rig-profile mapping.");
            }
        }

        return Array.AsReadOnly(errors.ToArray());
    }

    private static void ValidateRoutingLimits(RoutingLimits? limits, List<ConfigurationError> errors)
    {
        if (limits is null)
        {
            Add(errors, "routingLimits", "Cannot be null.");
            return;
        }

        CheckRange(errors, "routingLimits.connectionsPerProfile", limits.ConnectionsPerProfile, 1, MaximumConnections);
        CheckRange(errors, "routingLimits.helloDeadlineMs", limits.HelloDeadlineMs, 1, MaximumHelloDeadlineMs);
        CheckRange(errors, "routingLimits.partialJsonDeadlineMs", limits.PartialJsonDeadlineMs, 1, MaximumPartialJsonDeadlineMs);
        CheckRange(errors, "routingLimits.leaseDurationMs", limits.LeaseDurationMs, MinimumIpcTimeoutMs, MaximumLeaseDurationMs);
        CheckRange(errors, "routingLimits.clientRenewalTargetMs", limits.ClientRenewalTargetMs, 1, MaximumLeaseDurationMs - 1);
        if (limits.ClientRenewalTargetMs >= limits.LeaseDurationMs)
        {
            Add(errors, "routingLimits.clientRenewalTargetMs", "Must be shorter than the lease duration.");
        }

        CheckRange(errors, "routingLimits.normalQueueCapacityPerProfile", limits.NormalQueueCapacityPerProfile, 1, MaximumNormalQueueCapacity);
        CheckRange(errors, "routingLimits.pendingRequestsPerClient", limits.PendingRequestsPerClient, 1, MaximumPendingRequestsPerClient);
        CheckRange(errors, "routingLimits.leaseControlQueueCapacityPerProfile", limits.LeaseControlQueueCapacityPerProfile, 1, MaximumLeaseControlQueueCapacity);
        CheckRange(errors, "routingLimits.hardwareQueueWaitMs", limits.HardwareQueueWaitMs, 1, MaximumHardwareQueueWaitMs);
        CheckRange(errors, "routingLimits.catTransactionTimeoutMs", limits.CatTransactionTimeoutMs, MinimumIpcTimeoutMs, MaximumCatTransactionTimeoutMs);
        CheckRange(errors, "routingLimits.catWriteTimeoutMs", limits.CatWriteTimeoutMs, 1, MaximumCatSliceMs);
        CheckRange(errors, "routingLimits.catReplyTimeoutMs", limits.CatReplyTimeoutMs, 1, MaximumCatSliceMs);
        if ((long)limits.CatWriteTimeoutMs + limits.CatReplyTimeoutMs > limits.CatTransactionTimeoutMs)
        {
            Add(errors, "routingLimits.catTransactionTimeoutMs", "Must cover both the write and reply timeout budgets.");
        }

        CheckRange(errors, "routingLimits.ipcTimeoutMinimumMs", limits.IpcTimeoutMinimumMs, MinimumIpcTimeoutMs, MaximumIpcTimeoutMs);
        CheckRange(errors, "routingLimits.ipcTimeoutMaximumMs", limits.IpcTimeoutMaximumMs, MinimumIpcTimeoutMs, MaximumIpcTimeoutMs);
        if (limits.IpcTimeoutMinimumMs > limits.IpcTimeoutMaximumMs)
        {
            Add(errors, "routingLimits.ipcTimeoutMinimumMs", "Cannot exceed the maximum IPC timeout.");
        }

        CheckRange(errors, "routingLimits.outboundQueueFrameCapacityPerClient", limits.OutboundQueueFrameCapacityPerClient, 1, MaximumOutboundQueueFrames);
        CheckRange(errors, "routingLimits.outboundQueueByteCapacityPerClient", limits.OutboundQueueByteCapacityPerClient, 1, MaximumOutboundQueueBytes);
        CheckRange(errors, "routingLimits.blockedPipeWriteTimeoutMs", limits.BlockedPipeWriteTimeoutMs, 1, MaximumBlockedPipeWriteTimeoutMs);
        CheckRange(errors, "routingLimits.shutdownGracePeriodMs", limits.ShutdownGracePeriodMs, 1, MaximumShutdownGracePeriodMs);
        CheckRange(errors, "routingLimits.quietGuardMs", limits.QuietGuardMs, 1, MaximumQuietGuardMs);
        CheckRange(errors, "routingLimits.transitionTimeoutMs", limits.TransitionTimeoutMs, 1, MaximumTransitionTimeoutMs);
    }

    private static void ValidateSerialSettings(
        SerialSettings? settings,
        string prefix,
        List<ConfigurationError> errors)
    {
        if (settings is null)
        {
            Add(errors, prefix, "Cannot be null.");
            return;
        }

        CheckRange(errors, $"{prefix}.baudRate", settings.BaudRate, 50, MaximumBaudRate);
        CheckRange(errors, $"{prefix}.dataBits", settings.DataBits, 5, 8);
        if (!Enum.IsDefined(settings.Parity))
        {
            Add(errors, $"{prefix}.parity", "Unsupported parity value.");
        }

        if (!Enum.IsDefined(settings.StopBits))
        {
            Add(errors, $"{prefix}.stopBits", "Unsupported stop-bits value.");
        }
        else if (settings.StopBits == SerialStopBits.OnePointFive && settings.DataBits != 5)
        {
            Add(errors, $"{prefix}.stopBits", "OnePointFive requires 5 data bits.");
        }

        if (!Enum.IsDefined(settings.Handshake))
        {
            Add(errors, $"{prefix}.handshake", "Unsupported handshake value.");
        }
    }

    private static void ValidateAddress(
        int? address,
        bool isRadioAddress,
        string path,
        bool required,
        List<ConfigurationError> errors)
    {
        if (!address.HasValue)
        {
            if (required)
            {
                Add(errors, path, "Required when an IC-7600 profile is enabled.");
            }

            return;
        }

        if (address.Value is < 0 or > byte.MaxValue)
        {
            Add(errors, path, "Must fit in one byte (0-255).");
            return;
        }

        var valid = isRadioAddress
            ? address.Value is >= 0x01 and <= 0xDF
            : address.Value is >= 0xE0 and <= 0xEF;
        if (!valid)
        {
            Add(errors, path, isRadioAddress
                ? "Reserved or invalid radio address; expected 0x01-0xDF."
                : "Reserved or invalid controller address; expected 0xE0-0xEF.");
        }
    }

    private static void ValidatePort(
        string? port,
        string path,
        bool required,
        Dictionary<string, string> knownPorts,
        List<ConfigurationError> errors)
    {
        if (string.IsNullOrEmpty(port))
        {
            if (required)
            {
                Add(errors, path, "Required when the profile is enabled.");
            }

            return;
        }

        if (!IsCanonicalComName(port))
        {
            Add(errors, path, "Must be a positive COM port name without leading zeroes (for example COM3).");
            return;
        }

        var normalized = port.ToUpperInvariant();
        if (knownPorts.TryGetValue(normalized, out var previousPath))
        {
            Add(errors, path, $"Overlaps the COM port assigned at {previousPath}.");
        }
        else
        {
            knownPorts.Add(normalized, path);
        }
    }

    private static bool IsCanonicalComName(string port)
    {
        if (port.Length < 4 || !port.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = port.AsSpan(3);
        if (suffix[0] is < '1' or > '9')
        {
            return false;
        }

        foreach (var character in suffix)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            && number > 0;
    }

    private static void CheckRange(
        List<ConfigurationError> errors,
        string path,
        int value,
        int minimum,
        int maximum)
    {
        if (value < minimum || value > maximum)
        {
            Add(errors, path, $"Must be between {minimum} and {maximum}.");
        }
    }

    private static void Add(List<ConfigurationError> errors, string path, string message) =>
        errors.Add(new ConfigurationError(path, message));
}
