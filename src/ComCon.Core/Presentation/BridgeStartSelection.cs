using System.ComponentModel;
using ComCon.Core.Models;

namespace ComCon.Core.Presentation;

public sealed record BridgeActiveProfile(ProfileConfig Configuration, bool IsRaw,
    string State, string? Reason, bool IpcAvailable, bool IpcConnected);

public sealed class BridgeStartRow : INotifyPropertyChanged
{
    private bool selected;
    public required int ProfileId { get; init; }
    public required string Profile { get; init; }
    public required string SavedMode { get; init; }
    public required string ActualState { get; init; }
    public required string LegacyClientCom { get; init; }
    public required string ComConVirtualCom { get; init; }
    public required string TransceiverCom { get; init; }
    public required string IpcDetails { get; init; }
    public required string Reason { get; init; }
    public required bool CanStart { get; init; }
    public bool IsRunning { get; init; }
    public bool IsFaulted { get; init; }
    public bool IsSelected
    {
        get => selected;
        set
        {
            var next = value && CanStart;
            if (selected == next) return;
            selected = next;
            PropertyChanged?.Invoke(this, new(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public static class BridgeStartSelection
{
    public static IReadOnlyList<BridgeStartRow> Create(AppSettings saved,
        IEnumerable<BridgeActiveProfile> activeProfiles, int? selectedId,
        bool autoconnectOnly, string catPipeName, string? ipcFault = null)
    {
        var active = activeProfiles.ToArray();
        return saved.Profiles.OrderBy(profile => profile.Id).Select(profile =>
        {
            var runtime = active.SingleOrDefault(item => item.Configuration.Id == profile.Id);
            var shown = runtime?.Configuration ?? profile;
            var overlap = active.Where(item => item.Configuration.Id != profile.Id)
                .Select(item => new
                {
                    item.Configuration.Id,
                    Port = new[] { profile.PhysicalPort, profile.VirtualPort }.FirstOrDefault(port =>
                        port is not null && new[] { item.Configuration.PhysicalPort, item.Configuration.VirtualPort }
                            .Contains(port, StringComparer.OrdinalIgnoreCase))
                }).FirstOrDefault(item => item.Port is not null);
            var reason = runtime is not null
                ? runtime.Reason ?? (runtime.IsRaw ? "Existing bridge stays unchanged."
                    : !runtime.IpcAvailable ? "IPC unavailable; Legacy path retained."
                    : runtime.IpcConnected ? "IPC client connected." : "No IPC client connected.")
                : !profile.Enabled ? "Profile disabled."
                : overlap is not null ? $"{overlap.Port} reserved by profile {overlap.Id}."
                : profile.CommandIpcEnabled && ipcFault is not null
                    ? $"IPC unavailable: {ipcFault}. Recover IPC service manually."
                : autoconnectOnly && (!profile.AutoConnect || !profile.CommandIpcEnabled)
                    ? "Not eligible for IPC Autoconnect; manual Start remains available."
                : "Ready to start using saved settings.";
            var canStart = runtime is null && profile.Enabled && overlap is null &&
                (!profile.CommandIpcEnabled || ipcFault is null) &&
                (!autoconnectOnly || (profile.AutoConnect && profile.CommandIpcEnabled));
            var isFaulted = runtime is not null && (string.Equals(runtime.State, "Faulted", StringComparison.OrdinalIgnoreCase) ||
                (runtime.Reason is not null && (runtime.Reason.Contains("fault", StringComparison.OrdinalIgnoreCase) || runtime.Reason.Contains("error", StringComparison.OrdinalIgnoreCase))));
            var isRunning = runtime is not null && !isFaulted &&
                (string.Equals(runtime.State, "Running", StringComparison.OrdinalIgnoreCase) ||
                 runtime.State.StartsWith("Running", StringComparison.OrdinalIgnoreCase));
            var rigNames = saved.RigProfileMappings.Where(mapping => mapping.ProfileId == profile.Id)
                .Select(mapping => mapping.RigModelName ?? mapping.RigProfileId);
            return new BridgeStartRow
            {
                ProfileId = profile.Id,
                Profile = $"Profile {profile.Id} / {shown.Label}",
                SavedMode = profile.CommandIpcEnabled ? "IPC" : "RAW",
                ActualState = runtime?.State ?? "Stopped",
                LegacyClientCom = shown.CommanderPort ?? "(none)",
                ComConVirtualCom = shown.VirtualPort ?? "(none)",
                TransceiverCom = shown.PhysicalPort ?? "(none)",
                IpcDetails = runtime?.IsRaw == true || (runtime is null && !profile.CommandIpcEnabled)
                    ? "Not used" : $"{string.Join(", ", rigNames)} / {catPipeName}",
                Reason = reason,
                CanStart = canStart,
                IsRunning = isRunning,
                IsFaulted = isFaulted,
                IsSelected = canStart && profile.Id == selectedId
            };
        }).ToArray();
    }

    public static void ValidateSelectedPorts(AppSettings saved, IEnumerable<int> ids)
    {
        var ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var profile = saved.Profiles.Single(item => item.Id == id);
            foreach (var port in new[] { profile.PhysicalPort, profile.VirtualPort })
            {
                if (port is null) throw new InvalidOperationException($"Profile {id} has a missing endpoint.");
                if (ports.TryGetValue(port, out var owner))
                    throw new InvalidOperationException($"{port} conflicts between selected profiles {owner} and {id}.");
                ports.Add(port, id);
            }
        }
    }
}
