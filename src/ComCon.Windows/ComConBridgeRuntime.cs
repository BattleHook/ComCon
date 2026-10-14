using System.Runtime.Versioning;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Windows;

public sealed record ComConBridgeProfileStatus(
    ProfileConfig ActiveConfiguration, bool IsRaw, string State, string? Fault,
    LegacyConnectionStatus? LegacyStatus, bool IpcAvailable)
{
    public int ProfileId => ActiveConfiguration.Id;
    public string ActualState => State == "Running" ? IsRaw ? "Running RAW" : "Running IPC" : State;
}

[SupportedOSPlatform("windows")]
public sealed class ComConBridgeRuntime : IAsyncDisposable
{
    private sealed record RawRuntime(ProfileConfig Profile, CommanderByteBridgeHost Host);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, RawRuntime> raw = [];
    private readonly Dictionary<int, ProfileConfig> reserved = [];
    private readonly Func<int, IByteBridgeEndpointFactory> factories;
    private readonly Action<int, Exception> reportFailure;
    private readonly RawCatCapture? capture;
    private readonly ComConCommandPipeNames names;
    private ComConBridgeProfileStatus[] snapshot = [];

    public ComConBridgeRuntime(Func<int, IByteBridgeEndpointFactory> factories,
        Action<int, Exception> reportFailure, RawCatCapture? capture = null,
        ComConCommandPipeNames? names = null)
    {
        this.factories = factories;
        this.reportFailure = reportFailure;
        this.capture = capture;
        this.names = names ?? ComConCommandPipeNames.Production();
    }

    public ComConCommandGroupSupervisor? CommandSupervisor { get; private set; }
    public IReadOnlyList<ComConBridgeProfileStatus> Profiles
    {
        get
        {
            var current = Volatile.Read(ref snapshot);
            var ipc = CommandSupervisor;
            return current.Select(item =>
            {
                if (item.IsRaw) return item;
                var status = ipc?.Profiles.SingleOrDefault(profile => profile.ProfileId == item.ProfileId);
                return status is null ? item : item with
                {
                    State = status.State.ToString(), Fault = status.Fault,
                    LegacyStatus = status.LegacyStatus, IpcAvailable = ipc!.IpcAvailable
                };
            }).ToArray();
        }
    }

    private void Publish(int? transitionProfile = null, string? transition = null)
    {
        var ipc = CommandSupervisor;
        var next = reserved.Values.OrderBy(profile => profile.Id).Select(profile =>
        {
            if (raw.TryGetValue(profile.Id, out var relay))
                return new ComConBridgeProfileStatus(profile, true, relay.Host.State.ToString(),
                    relay.Host.Fault, null, false);
            var status = ipc?.Profiles.SingleOrDefault(item => item.ProfileId == profile.Id);
            return new ComConBridgeProfileStatus(profile, false, status?.State.ToString() ?? "Starting",
                status?.Fault, status?.LegacyStatus, ipc?.IpcAvailable == true);
        }).Select(status => status.ProfileId == transitionProfile
            ? status with { State = transition! } : status).ToArray();
        Volatile.Write(ref snapshot, next);
    }

    public string? Conflict(ProfileConfig profile) =>
        Profiles.Where(item => item.ProfileId != profile.Id).Select(item =>
        {
            var other = item.ActiveConfiguration;
            var overlap = new[] { profile.PhysicalPort, profile.VirtualPort }
                .FirstOrDefault(port => port is not null &&
                    new[] { other.PhysicalPort, other.VirtualPort }.Contains(port, StringComparer.OrdinalIgnoreCase));
            return overlap is null ? null : $"{overlap} is reserved by profile {other.Id} '{other.Label}'.";
        }).FirstOrDefault(message => message is not null);

    public async Task StartProfileAsync(AppSettings settings, int profileId,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var errors = ConfigurationValidator.Validate(settings);
            if (errors.Count != 0) throw new ConfigurationValidationException(errors);
            var profile = settings.Profiles.Single(item => item.Id == profileId);
            if (!profile.Enabled) throw new InvalidOperationException("The selected profile is disabled.");
            if (reserved.ContainsKey(profileId))
                throw new InvalidOperationException($"Profile {profileId} must stop successfully before starting again.");
            if (Conflict(profile) is { } conflict) throw new InvalidOperationException(conflict);
            reserved.Add(profileId, profile);
            if (!profile.CommandIpcEnabled)
            {
                var host = new CommanderByteBridgeHost(factories(profileId), capture);
                raw.Add(profileId, new(profile, host));
                Publish(profileId, "Starting");
                try { await host.StartAsync(profile, cancellationToken).ConfigureAwait(false); }
                catch (Exception startFailure) when (IsLifecycleFailure(startFailure))
                {
                    try
                    {
                        await host.DisposeAsync().ConfigureAwait(false);
                        raw.Remove(profileId);
                        reserved.Remove(profileId);
                    }
                    catch (Exception cleanup) when (IsLifecycleFailure(cleanup))
                    {
                        throw new AggregateException("RAW startup failed; ports remain reserved after cleanup failure.",
                            startFailure, cleanup);
                    }
                    throw;
                }
            }
            else
            {
                try
                {
                    if (!ComConProductionCoordinatorOptions.TryCreate(profile, out var options) || options is null)
                        throw new InvalidOperationException(ComConProductionCoordinatorOptions.OptionsUnavailableReason);
                    CommandSupervisor ??= new(settings, [], names, new StopwatchClock(),
                        TimeSpan.FromMilliseconds(ComConCommandBounds.SetupTimeoutMs), TimeSpan.FromSeconds(3),
                        reportFailure, (_, _) => { }, capture);
                    Publish(profileId, "Starting");
                    await CommandSupervisor.StartProfileAsync(settings,
                        new(profile.Id, factories(profileId), options), cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsLifecycleFailure(exception))
                {
                    if (CommandSupervisor?.Profiles.Any(item => item.ProfileId == profileId) != true)
                        reserved.Remove(profileId);
                    throw;
                }
            }
        }
        finally { Publish(); gate.Release(); }
    }

    public async Task StopProfileAsync(int profileId, string reason,
        CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!reserved.ContainsKey(profileId)) return;
            Publish(profileId, "Stopping");
            if (raw.TryGetValue(profileId, out var relay))
            {
                await relay.Host.DisposeAsync().ConfigureAwait(false);
                raw.Remove(profileId);
            }
            else if (CommandSupervisor is { } ipc)
                await ipc.StopProfileAsync(profileId, reason, cancellationToken).ConfigureAwait(false);
            reserved.Remove(profileId);
        }
        finally { Publish(); gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> StopAllAsync(string reason)
    {
        var errors = new List<string>();
        foreach (var profile in Profiles)
        {
            try { await StopProfileAsync(profile.ProfileId, reason).ConfigureAwait(false); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                AggregateException or InvalidOperationException or TimeoutException or
                System.ComponentModel.Win32Exception)
            {
                errors.Add($"Profile {profile.ProfileId}: {exception.Message}");
                reportFailure(profile.ProfileId, exception);
            }
        }
        return errors;
    }

    private static bool IsLifecycleFailure(Exception exception) => exception is IOException or
        UnauthorizedAccessException or AggregateException or InvalidOperationException or TimeoutException or
        System.ComponentModel.Win32Exception or ConfigurationValidationException or ArgumentException or
        OperationCanceledException or System.Security.SecurityException;

    public void Refresh()
    {
        if (!gate.Wait(0)) return;
        try { Publish(); }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        var failures = await StopAllAsync("Application runtime disposed.").ConfigureAwait(false);
        if (failures.Count != 0) throw new AggregateException(
            failures.Select(message => new IOException(message)));
        if (CommandSupervisor is { } ipc) await ipc.DisposeAsync().ConfigureAwait(false);
    }
}
