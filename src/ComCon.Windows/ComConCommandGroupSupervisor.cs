using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Windows;

public sealed record ComConCommandProfilePlan(
    int ProfileId,
    IByteBridgeEndpointFactory EndpointFactory,
    SharedRadioCoordinatorOptions CoordinatorOptions);

public sealed record ComConCommandGroupProfileStatus(
    int ProfileId,
    string Label,
    SharedRadioHostState State,
    string? Fault,
    LegacyConnectionStatus LegacyStatus)
{
    public ProfileConfig? ActiveConfiguration { get; init; }
    public bool CanRecoverLegacy =>
        State == SharedRadioHostState.Running && LegacyStatus.State == LegacyConnectionState.Unavailable;
}

[SupportedOSPlatform("windows")]
public sealed class ComConCommandGroupSupervisor : IAsyncDisposable
{
    private sealed class ProfileRuntime(
        ProfileConfig profile, SharedRadioHost host, ComConRigProfileMapping[] mappings)
    {
        public ProfileConfig Profile { get; } = profile;
        public SharedRadioHost Host { get; } = host;
        public ComConRigProfileMapping[] Mappings { get; } = mappings;
        public TaskCompletionSource<string> SessionStop { get; set; } = NewStopSignal();
        public Task? Close { get; set; }
        public bool UnknownStartupClosure { get; set; }
        public string? Fault;
    }

    private readonly AppSettings initialSettings;
    private readonly ComConCommandProfilePlan[] plans;
    private readonly ComConCommandPipeNames pipeNames;
    private readonly IMonotonicClock clock;
    private readonly TimeSpan setupTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly Action<int, Exception> reportFailure;
    private readonly Action<int, SharedRadioDiagnostic> reportDiagnostic;
    private readonly RawCatCapture? rawCatCapture;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<ProfileRuntime> runtimes = [];
    private readonly ConcurrentQueue<string> faults = new();
    private ProfileRuntime[] runtimeSnapshot = [];
    private ComConCommandTestPipeListener? listener;
    private CancellationTokenSource? serviceAbort;
    private Task? listenerCompletion;
    private bool started;
    private bool stopped;
    private bool ipcAvailable;
    private string? ipcFault;

    public ComConCommandGroupSupervisor(
        AppSettings settings,
        IEnumerable<ComConCommandProfilePlan> plans,
        ComConCommandPipeNames pipeNames,
        IMonotonicClock clock,
        TimeSpan setupTimeout,
        TimeSpan stopTimeout,
        Action<int, Exception> reportFailure,
        Action<int, SharedRadioDiagnostic> reportDiagnostic,
        RawCatCapture? rawCatCapture = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(pipeNames);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        ValidateSettings(settings);
        ValidateTimeout(setupTimeout, nameof(setupTimeout));
        ValidateTimeout(stopTimeout, nameof(stopTimeout));
        initialSettings = settings;
        this.plans = plans.Select(SnapshotPlan).ToArray();
        this.pipeNames = pipeNames;
        this.clock = clock;
        this.setupTimeout = setupTimeout;
        this.stopTimeout = stopTimeout;
        this.reportFailure = reportFailure;
        this.reportDiagnostic = reportDiagnostic;
        this.rawCatCapture = rawCatCapture;
        if (this.plans.Length > 0)
        {
            var optedIn = settings.Profiles.Where(profile => profile.Enabled && profile.CommandIpcEnabled)
                .Select(profile => profile.Id).Order().ToArray();
            if (!optedIn.SequenceEqual(this.plans.Select(plan => plan.ProfileId).Order()))
                throw new ArgumentException("Fixed plans must cover every enabled command-IPC profile exactly once.", nameof(plans));
            foreach (var plan in this.plans) ValidatePlan(settings, plan);
        }
        Sessions = new(clock, (long)setupTimeout.TotalMilliseconds, []);
        CloseAllAdmission();
    }

    public ComConCommandSessions Sessions { get; }
    public string CatPipeName => pipeNames.CatPipeName;
    public string ControlPipeName(int profileId) => pipeNames.ControlPipeName(profileId);
    public bool IpcAvailable => Volatile.Read(ref ipcAvailable);
    public string? IpcFault => Volatile.Read(ref ipcFault);
    public IReadOnlyList<ComConCommandGroupProfileStatus> Profiles => Array.AsReadOnly(
        Volatile.Read(ref runtimeSnapshot).Select(runtime =>
            new ComConCommandGroupProfileStatus(runtime.Profile.Id, runtime.Profile.Label,
                runtime.Fault is null ? runtime.Host.State : SharedRadioHostState.Faulted,
                runtime.Fault ?? runtime.Host.Fault, runtime.Host.LegacyStatus)
            { ActiveConfiguration = runtime.Profile }).ToArray());
    public IReadOnlyList<ComConRigProfileMapping> Mappings => Sessions.Mappings;
    public IReadOnlyList<string> Faults
    {
        get
        {
            var current = faults.ToList();
            if (IpcFault is { Length: > 0 } serviceFault)
                current.Add($"IPC service: {serviceFault}");
            return current.AsReadOnly();
        }
    }
    public IReadOnlyList<ComConCommandSession> ActiveSessions => Sessions.ActiveSessions;

    public async Task StartProfileAsync(
        AppSettings settings, ComConCommandProfilePlan plan, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            started = true;
            await StartProfileCoreAsync(settings, SnapshotPlan(plan), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task StartProfileCoreAsync(
        AppSettings settings, ComConCommandProfilePlan plan, CancellationToken cancellationToken)
    {
        var profile = ValidatePlan(settings, plan);
        if (runtimes.Any(runtime => runtime.Profile.Id == plan.ProfileId))
            throw new InvalidOperationException($"Profile {plan.ProfileId} already owns an active or retained runtime.");
        var requestedPorts = new[] { profile.PhysicalPort, profile.VirtualPort };
        if (runtimes.Any(runtime => new[] { runtime.Profile.PhysicalPort, runtime.Profile.VirtualPort }
            .Intersect(requestedPorts, StringComparer.OrdinalIgnoreCase).Any()))
            throw new InvalidOperationException($"Profile {plan.ProfileId} conflicts with a retained command runtime's ports.");
        if (IpcFault is not null)
            throw new InvalidOperationException($"IPC service is unavailable; explicit recovery is required: {IpcFault}");
        var profileMappings = settings.RigProfileMappings.Where(mapping => mapping.ProfileId == profile.Id).ToArray();
        if (runtimes.SelectMany(runtime => runtime.Mappings).Concat(profileMappings)
            .GroupBy(mapping => mapping.RigProfileId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Rig mappings conflict with an active configuration snapshot.");

        await StartServiceAsync(cancellationToken).ConfigureAwait(false);
        var host = new SharedRadioHost(plan.EndpointFactory, clock, diagnostic =>
        {
            if (diagnostic.Kind == SharedRadioDiagnosticKind.ProfileFaulted)
                Sessions.SetProfileAdmission(profile.Id, false);
            reportDiagnostic(profile.Id, diagnostic);
        }, rawCatCapture);
        var runtime = new ProfileRuntime(profile, host, profileMappings);
        runtimes.Add(runtime);
        PublishRuntimes();
        try
        {
            await host.StartAsync(profile, plan.CoordinatorOptions, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!IpcAvailable) throw new IOException($"IPC service failed during profile startup: {IpcFault}");
            if (host.State != SharedRadioHostState.Running)
                throw new IOException($"Profile faulted during startup: {host.Fault}");
            PublishMappings();
            Sessions.SetProfileAdmission(profile.Id, true);
            // A concurrent service or profile fault must win over a late successful host start.
            if (!IpcAvailable || host.State != SharedRadioHostState.Running)
                Sessions.SetProfileAdmission(profile.Id, false);
        }
        catch (Exception startFailure)
        {
            Sessions.SetProfileAdmission(profile.Id, false);
            try
            {
                if (host.State == SharedRadioHostState.Faulted)
                {
                    runtime.UnknownStartupClosure = true;
                    throw new IOException($"Host startup cleanup could not establish safe closure: {host.Fault}");
                }
                await CloseHostAsync(runtime, CancellationToken.None).ConfigureAwait(false);
                runtimes.Remove(runtime);
                PublishRuntimes();
                PublishMappings();
            }
            catch (Exception closeFailure)
            {
                runtime.Fault = $"Startup failed; cleanup outcome is retained: {closeFailure.Message}";
                throw new AggregateException("Profile startup and cleanup failed.", startFailure, closeFailure);
            }
            finally
            {
                if (runtimes.Count == 0) await StopServiceAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
    }

    public async Task StopProfileAsync(
        int profileId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var runtime = runtimes.SingleOrDefault(item => item.Profile.Id == profileId) ??
                throw new ArgumentOutOfRangeException(nameof(profileId), profileId, "Profile has no retained command runtime.");
            await StopProfileCoreAsync(runtime, reason, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task StopProfileCoreAsync(
        ProfileRuntime runtime, string reason, CancellationToken cancellationToken)
    {
        Sessions.SetProfileAdmission(runtime.Profile.Id, false);
        runtime.SessionStop.TrySetResult(reason);
        listener?.CancelProfileSetups(runtime.Profile.Id);
        try
        {
            await Sessions.WaitForProfileReleaseAsync(runtime.Profile.Id)
                .WaitAsync(stopTimeout, cancellationToken).ConfigureAwait(false);
            EndOfflineSessions(runtime.Profile.Id, reason);
            await CloseHostAsync(runtime, cancellationToken).ConfigureAwait(false);
            runtimes.Remove(runtime);
            PublishRuntimes();
            PublishMappings();
            if (runtimes.Count == 0) await StopServiceAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            runtime.Fault = $"Stop failed; runtime retained: {exception.Message}";
            faults.Enqueue($"Profile {runtime.Profile.Id}: {runtime.Fault}");
            reportFailure(runtime.Profile.Id, exception);
            throw;
        }
    }

    public async Task RecoverIpcServiceAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            CloseAllAdmission();
            foreach (var runtime in runtimes)
                runtime.SessionStop.TrySetResult("IPC service recovery; establish a fresh session.");
            try
            {
                await StopServiceAsync(cancellationToken).ConfigureAwait(false);
                foreach (var runtime in runtimes)
                {
                    await Sessions.WaitForProfileReleaseAsync(runtime.Profile.Id)
                        .WaitAsync(stopTimeout, cancellationToken).ConfigureAwait(false);
                    EndOfflineSessions(runtime.Profile.Id, "IPC service recovered; old identity revoked.");
                }
                Volatile.Write(ref ipcFault, null);
                foreach (var runtime in runtimes) runtime.SessionStop = NewStopSignal();
                if (runtimes.Count > 0)
                {
                    await StartServiceAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var runtime in runtimes)
                        if (runtime.Host.State == SharedRadioHostState.Running && runtime.Fault is null)
                            Sessions.SetProfileAdmission(runtime.Profile.Id, true);
                    if (!IpcAvailable) CloseAllAdmission();
                }
            }
            catch (Exception exception)
            {
                FaultService(exception);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task RecoverLegacyAsync(int profileId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started || stopped)
                throw new InvalidOperationException("Legacy recovery requires a running command supervisor.");
            var runtime = runtimes.SingleOrDefault(item => item.Profile.Id == profileId) ??
                throw new ArgumentOutOfRangeException(nameof(profileId));
            await runtime.Host.RecoverLegacyAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started || stopped) throw new InvalidOperationException("The fixed command group can start only once.");
            started = true;
            try
            {
                foreach (var plan in plans)
                    await StartProfileCoreAsync(initialSettings, plan, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception startupFailure)
            {
                var cleanup = await StopCoreAsync("Fixed group startup rollback.").ConfigureAwait(false);
                if (cleanup.Count > 0)
                    throw new AggregateException("Fixed group startup and rollback failed.", [startupFailure, .. cleanup]);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task StartServiceAsync(CancellationToken cancellationToken)
    {
        if (IpcAvailable) return;
        if (listenerCompletion is not null)
            throw new InvalidOperationException("Old IPC listener must quiesce before replacement.");
        CloseAllAdmission();
        serviceAbort = new();
        var current = new ComConCommandTestPipeListener(pipeNames, Sessions, setupTimeout, stopTimeout,
            exception => reportFailure(0, exception));
        listener = current;
        listenerCompletion = ObserveListenerAsync(current, serviceAbort.Token);
        try
        {
            await current.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (listenerCompletion.IsCompleted) await listenerCompletion.ConfigureAwait(false);
            Volatile.Write(ref ipcAvailable, true);
            if (listenerCompletion.IsCompleted)
            {
                Volatile.Write(ref ipcAvailable, false);
                await listenerCompletion.ConfigureAwait(false);
                throw new IOException("IPC listener ended before service activation.");
            }
        }
        catch (Exception exception)
        {
            FaultService(exception);
            serviceAbort.Cancel();
            try { await StopServiceAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception cleanup) { faults.Enqueue($"IPC startup cleanup: {cleanup.Message}"); }
            throw;
        }
    }

    private async Task ObserveListenerAsync(ComConCommandTestPipeListener current, CancellationToken token)
    {
        try
        {
            await current.RunAsync(ServeAsync, token).ConfigureAwait(false);
            if (!token.IsCancellationRequested && IpcAvailable)
                FaultService(new IOException("IPC listener ended unexpectedly."));
        }
        catch (Exception exception)
        {
            FaultService(exception);
            throw;
        }
    }

    private void FaultService(Exception exception)
    {
        Volatile.Write(ref ipcAvailable, false);
        Volatile.Write(ref ipcFault, exception.Message);
        CloseAllAdmission();
        reportFailure(0, exception);
    }

    private async Task ServeAsync(ComConAcceptedTestPipes pipes, CancellationToken token)
    {
        var runtime = Volatile.Read(ref runtimeSnapshot)
            .SingleOrDefault(item => item.Profile.Id == pipes.Session.ProfileId);
        if (runtime is null || !IpcAvailable || runtime.Host.State != SharedRadioHostState.Running ||
            runtime.SessionStop.Task.IsCompleted)
            throw new ComConCommandProtocolException("SETUP_NOT_READY", "Profile is no longer accepting attachment.");
        var stop = runtime.SessionStop.Task;
        ComConCommandLifecycle? lifecycle = null;
        try
        {
            var tracker = new ComConCommandNotifications(Sessions, pipes.Session, clock,
                ComConCommandBounds.MaximumPendingNotifications,
                ComConCommandBounds.MaximumPendingNotificationBytes,
                ComConCommandBounds.AcknowledgmentTimeoutMs,
                ComConCommandBounds.AcknowledgmentRetryIntervalMs);
            var endpoint = new ComConCommandEndpoint(pipes.CreateCatEndpoint(), Sessions, pipes.Session, tracker);
            var control = new ComConCommandControlLoop(pipes.Control, tracker,
                ComConCommandBounds.MaximumDeferredAcknowledgments,
                TimeSpan.FromMilliseconds(ComConCommandBounds.ControlPollIntervalMs));
            lifecycle = ComConCommandLifecycle.ForAttachedSession(
                runtime.Host, endpoint, tracker, control, stopTimeout);
            await lifecycle.StartAttachedAsync(token).ConfigureAwait(false);
            var completed = await Task.WhenAny(lifecycle.ControlCompletion, stop).WaitAsync(token).ConfigureAwait(false);
            if (completed == stop)
                await lifecycle.StopOrderlyAsync(await stop.ConfigureAwait(false), token).ConfigureAwait(false);
            else await lifecycle.ControlCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (IOException exception)
        {
            faults.Enqueue($"Profile {runtime.Profile.Id}: {exception.Message}");
            reportFailure(runtime.Profile.Id, exception);
        }
        finally
        {
            if (lifecycle is not null)
            {
                try { await lifecycle.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    runtime.Fault = $"IPC detach outcome is uncertain: {exception.Message}";
                    Sessions.SetProfileAdmission(runtime.Profile.Id, false);
                    throw;
                }
            }
        }
    }

    private async Task StopServiceAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref ipcAvailable, false);
        CloseAllAdmission();
        listener?.StopAccepting();
        serviceAbort?.Cancel();
        if (listenerCompletion is not null)
        {
            try { await listenerCompletion.WaitAsync(stopTimeout, cancellationToken).ConfigureAwait(false); }
            catch when (listenerCompletion.IsCompleted)
            {
                // Observed service failures do not prevent closing independent healthy Legacy paths.
            }
        }
        listenerCompletion = null;
        listener = null;
        serviceAbort?.Dispose();
        serviceAbort = null;
    }

    private async Task CloseHostAsync(ProfileRuntime runtime, CancellationToken cancellationToken)
    {
        if (runtime.UnknownStartupClosure)
            throw new IOException("Startup endpoint closure is uncertain; the runtime's reservations must remain retained.");
        runtime.Close ??= runtime.Host.DisposeAsync().AsTask();
        await runtime.Close.WaitAsync(stopTimeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopOrderlyAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started || stopped) throw new InvalidOperationException("Orderly shutdown requires a running supervisor.");
            var failures = await StopCoreAsync(reason).ConfigureAwait(false);
            if (failures.Count > 0) throw new AggregateException("Command-mode orderly shutdown failed.", failures);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var failures = await StopCoreAsync("Command supervisor stopped.").ConfigureAwait(false);
            if (failures.Count > 0) throw new AggregateException("Command supervisor cleanup failed.", failures);
        }
        finally { gate.Release(); }
    }

    private async Task<List<Exception>> StopCoreAsync(string reason)
    {
        var failures = new List<Exception>();
        stopped = true;
        CloseAllAdmission();
        foreach (var runtime in runtimes.ToArray())
        {
            try { await StopProfileCoreAsync(runtime, reason, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        try { await StopServiceAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) { failures.Add(exception); }
        return failures;
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void PublishRuntimes() => Volatile.Write(ref runtimeSnapshot, runtimes.ToArray());
    private void PublishMappings() => Sessions.ReplaceMappings(runtimes.SelectMany(runtime => runtime.Mappings));
    private void EndOfflineSessions(int profileId, string reason)
    {
        foreach (var session in Sessions.ActiveSessions.Where(session => session.ProfileId == profileId))
            Sessions.EndSession(session, reason);
    }
    private void CloseAllAdmission()
    {
        for (var id = 1; id <= 4; id++) Sessions.SetProfileAdmission(id, false);
    }
    private static TaskCompletionSource<string> NewStopSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void ValidateTimeout(TimeSpan value, string name)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(name);
    }
    private static void ValidateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0) throw new ConfigurationValidationException(errors);
    }
    private ProfileConfig ValidatePlan(AppSettings settings, ComConCommandProfilePlan plan)
    {
        ValidateSettings(settings);
        var profile = settings.Profiles.SingleOrDefault(item => item.Id == plan.ProfileId);
        if (profile is null || !profile.Enabled || !profile.CommandIpcEnabled)
            throw new ArgumentException("A command plan requires an enabled command-IPC profile.", nameof(plan));
        SharedRadioHost.ValidateProfile(profile, plan.CoordinatorOptions);
        if (plan.CoordinatorOptions.LegacyClientAddress != profile.LegacyClientAddress ||
            plan.CoordinatorOptions.IpcClientAddress != profile.IpcClientAddress)
            throw new ArgumentException($"Coordinator client addresses do not match profile {profile.Id}.", nameof(plan));
        _ = new SharedRadioCoordinator(plan.CoordinatorOptions, clock, _ => { });
        return profile with { SerialSettings = profile.SerialSettings with { } };
    }
    private static ComConCommandProfilePlan SnapshotPlan(ComConCommandProfilePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.EndpointFactory);
        ArgumentNullException.ThrowIfNull(plan.CoordinatorOptions);
        return plan with
        {
            CoordinatorOptions = plan.CoordinatorOptions with
            {
                AllowedUnmatchedAddresses = plan.CoordinatorOptions.AllowedUnmatchedAddresses is { } allowed
                    ? new HashSet<byte>(allowed) : null,
                DeniedUnmatchedAddresses = plan.CoordinatorOptions.DeniedUnmatchedAddresses is { } denied
                    ? new HashSet<byte>(denied) : null
            }
        };
    }
}
