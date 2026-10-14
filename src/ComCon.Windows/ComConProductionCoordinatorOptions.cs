using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Windows;

public static class ComConProductionCoordinatorOptions
{
    public static bool ProductionActivationEnabled => true;

    public const string OptionsUnavailableReason =
        "The selected profile cannot use the approved shared-radio coordinator options.";

    public static bool TryCreate(ProfileConfig profile, out SharedRadioCoordinatorOptions? options)
    {
        ArgumentNullException.ThrowIfNull(profile);
        options = null;
        if (!profile.Enabled || !profile.CommandIpcEnabled ||
            profile.Framing is not (FramingMode.Civ or FramingMode.KenwoodAscii) ||
            profile.LegacyClientAddress is < byte.MinValue or > byte.MaxValue ||
            profile.IpcClientAddress is < byte.MinValue or > byte.MaxValue ||
            profile.LegacyClientAddress == profile.IpcClientAddress ||
            profile.RadioAddress is < byte.MinValue or > byte.MaxValue)
            return false;
        if (profile.Framing == FramingMode.Civ &&
            (profile.RadioAddress is null or 0 or >= 0xF0 ||
             profile.LegacyClientAddress is 0 or >= 0xF0 ||
             profile.IpcClientAddress is 0 or >= 0xF0 ||
             profile.RadioAddress == profile.LegacyClientAddress ||
             profile.RadioAddress == profile.IpcClientAddress))
            return false;

        options = new(
            profile.Framing,
            (byte)profile.LegacyClientAddress,
            (byte)profile.IpcClientAddress,
            IngressChunksPerSource: 64,
            IngressBytesPerSource: 1024,
            TxQueueRecordsPerClient: 16,
            TxQueueBytesPerClient: 1024,
            TxQueueRecordsPerProfile: 32,
            ClientRxQueueRecordsPerClient: 16,
            ClientRxQueueBytesPerClient: 1024,
            QueueWaitLimitMs: profile.Framing == FramingMode.Civ ? 2500 : 500,
            InformationalRadioAddress: profile.RadioAddress is { } radioAddress
                ? (byte)radioAddress
                : null,
            CivExchangeGateEnabled: profile.Framing == FramingMode.Civ);
        return true;
    }
}
