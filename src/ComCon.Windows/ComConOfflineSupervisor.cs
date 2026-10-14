using System.Runtime.Versioning;
using System.Collections.Concurrent;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Windows;

public sealed record ComConOfflineProfilePlan(
    int ProfileId, string TestPipeName, IByteBridgeEndpointFactory EndpointFactory,
    SharedRadioCoordinatorOptions CoordinatorOptions)
{
    internal bool CaptureCoordinatorMetrics { get; init; }
}

/// <summary>Explicit single-run test composition. Callers must supply fake endpoint factories.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComConOfflineSupervisor : IAsyncDisposable
{
    public sealed class ProfileRuntime
    {
        internal ProfileRuntime(
            ProfileConfig profile, SharedRadioHost host, ComConCommandTestPipeListener listener)
        {
            Profile = profile;
            Host = host;
            Listener = listener;
        }

        public ProfileConfig Profile { get; }
        public SharedRadioHost Host { get; }
        public ComConCommandTestPipeListener Listener { get; }
        public Task Completion { get; internal set; } = Task.CompletedTask;
        internal CancellationTokenSource Stop { get; } = new();
        internal TaskCompletionSource<string> OrderlyStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ConcurrentQueue<Exception> OrderlyFailures { get; } = new();
    }

    private readonly AppSettings settings;
    private readonly ComConOfflineProfilePlan[] plans;
    private readonly IMonotonicClock clock;
    private readonly Action<int, Exception> reportFailure;
    private readonly Action<int, SharedRadioDiagnostic> reportDiagnostic;
    private readonly TimeSpan setupTimeout;
    private readonly TimeSpan stopTimeout;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly List<ProfileRuntime> runtimes = [];
    private IReadOnlyList<ProfileRuntime> profiles = Array.AsReadOnly(Array.Empty<ProfileRuntime>());
    private bool started;
    private bool stopped;

    public ComConOfflineSupervisor(
        AppSettings settings, IEnumerable<ComConOfflineProfilePlan> plans,
        IMonotonicClock clock, TimeSpan setupTimeout, TimeSpan stopTimeout,
        Action<int, Exception> reportFailure, Action<int, SharedRadioDiagnostic> reportDiagnostic)
    {
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reportFailure);
        ArgumentNullException.ThrowIfNull(reportDiagnostic);
        var errors = ConfigurationValidator.Validate(settings);
        if (errors.Count > 0) throw new ConfigurationValidationException(errors);
        if (setupTimeout.TotalMilliseconds < 1 || setupTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(setupTimeout));
        if (stopTimeout <= TimeSpan.Zero || stopTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        this.settings = settings;
        this.plans = plans.Select(plan =>
        {
            ArgumentNullException.ThrowIfNull(plan);
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
        }).ToArray();
        if (this.plans.Length == 0) throw new ArgumentException("Select at least one profile.", nameof(plans));
        var ids = new HashSet<int>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Sessions = ComConCommandSessions.FromSettings(clock, (long)setupTimeout.TotalMilliseconds, settings);
        foreach (var plan in this.plans)
        {
            ArgumentNullException.ThrowIfNull(plan);
            ArgumentNullException.ThrowIfNull(plan.EndpointFactory);
            ArgumentNullException.ThrowIfNull(plan.CoordinatorOptions);
            if (!ids.Add(plan.ProfileId) ||
                !settings.Profiles.Any(profile => profile.Id == plan.ProfileId && profile.Enabled))
                throw new ArgumentException("Each selected profile must exist, be enabled and appear once.", nameof(plans));
            SharedRadioHost.ValidateProfile(
                settings.Profiles.Single(profile => profile.Id == plan.ProfileId), plan.CoordinatorOptions);
            // Validate protected test names before opening any endpoint.
            var listener = new ComConCommandTestPipeListener(plan.TestPipeName, Sessions, setupTimeout,
                stopTimeout, exception => reportFailure(plan.ProfileId, exception), plan.ProfileId);
            foreach (var name in Enumerable.Range(1, 4).Select(listener.ControlPipeName).Prepend(listener.CatPipeName))
                if (!names.Add(name))
                    throw new ArgumentException("Profile CAT/control pipe names must not overlap.", nameof(plans));
            _ = new SharedRadioCoordinator(plan.CoordinatorOptions, clock, _ => { });
        }
        this.clock = clock;
        this.setupTimeout = setupTimeout;
        this.stopTimeout = stopTimeout;
        this.reportFailure = reportFailure;
        this.reportDiagnostic = reportDiagnostic;
    }

    internal Func<int, Stream, ComConCommandNotifications, Stream>? WrapControlStream { get; init; }

    public ComConCommandSessions Sessions { get; }
    public IReadOnlyList<ProfileRuntime> Profiles => profiles;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (started || stopped) throw new InvalidOperationException("The supervisor can start only once.");
            started = true;
            try
            {
                foreach (var plan in plans)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var profile = settings.Profiles.Single(profile => profile.Id == plan.ProfileId);
                    var host = new SharedRadioHost(plan.EndpointFactory, clock,
                        diagnostic => reportDiagnostic(profile.Id, diagnostic));
                    if (plan.CaptureCoordinatorMetrics)
                        host.EnableCoordinatorMetricsCapture();
                    var listener = new ComConCommandTestPipeListener(plan.TestPipeName, Sessions,
                        setupTimeout, stopTimeout, exception => reportFailure(plan.ProfileId, exception), plan.ProfileId);
                    var runtime = new ProfileRuntime(profile, host, listener);
                    runtimes.Add(runtime);
                    await host.StartAsync(profile, plan.CoordinatorOptions, cancellationToken).ConfigureAwait(false);
                    runtime.Completion = ObserveListenerAsync(runtime);
                    await listener.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (runtime.Completion.IsCompleted)
                        await runtime.Completion.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var runtime in runtimes)
                    if (runtime.Completion.IsCompleted)
                        await runtime.Completion.ConfigureAwait(false);
                profiles = Array.AsReadOnly(runtimes.ToArray());
            }
            catch (Exception startupFailure)
            {
                var cleanup = await StopCoreAsync().ConfigureAwait(false);
                cleanup.RemoveAll(failure => ReferenceEquals(failure, startupFailure));
                if (cleanup.Count > 0)
                    throw new AggregateException("Offline startup failed; rollback also reported failures.",
                        new[] { startupFailure }.Concat(cleanup));
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private async Task ObserveListenerAsync(ProfileRuntime runtime)
    {
        try
        {
            await runtime.Listener.RunAsync((pipes, token) => ServeAsync(runtime, pipes, token),
                runtime.Stop.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            reportFailure(runtime.Profile.Id, exception);
            throw;
        }
    }

    private async Task ServeAsync(
        ProfileRuntime runtime, ComConAcceptedTestPipes pipes, CancellationToken token)
    {
        try
        {
            var tracker = new ComConCommandNotifications(Sessions, pipes.Session, clock,
                ComConCommandBounds.MaximumPendingNotifications,
                ComConCommandBounds.MaximumPendingNotificationBytes,
                ComConCommandBounds.AcknowledgmentTimeoutMs,
                ComConCommandBounds.AcknowledgmentRetryIntervalMs);
            var endpoint = new ComConCommandEndpoint(pipes.CreateCatEndpoint(), Sessions, pipes.Session, tracker);
            var controlStream = WrapControlStream?.Invoke(runtime.Profile.Id, pipes.Control, tracker) ?? pipes.Control;
            var control = new ComConCommandControlLoop(controlStream, tracker,
                ComConCommandBounds.MaximumDeferredAcknowledgments,
                TimeSpan.FromMilliseconds(ComConCommandBounds.ControlPollIntervalMs));
            await using var lifecycle = ComConCommandLifecycle.ForAttachedSession(
                runtime.Host, endpoint, tracker, control, stopTimeout);
            await lifecycle.StartAttachedAsync(token).ConfigureAwait(false);
            var requested = runtime.OrderlyStop.Task;
            var completed = await Task.WhenAny(lifecycle.ControlCompletion, requested)
                .WaitAsync(token).ConfigureAwait(false);
            if (completed == requested)
                await lifecycle.StopOrderlyAsync(await requested.ConfigureAwait(false), token).ConfigureAwait(false);
            else
                await lifecycle.ControlCompletion.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (runtime.OrderlyStop.Task.IsCompleted)
                runtime.OrderlyFailures.Enqueue(exception);
            reportFailure(runtime.Profile.Id, exception);
            // The listener owns session cleanup/reuse; a session fault is not a listener fault.
        }
    }

    public async Task StopOrderlyAsync(string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!started || stopped)
                throw new InvalidOperationException("Orderly shutdown requires a running supervisor.");
            var failures = new List<Exception>();
            try
            {
                foreach (var runtime in runtimes)
                {
                    runtime.Listener.StopAccepting();
                    runtime.OrderlyStop.TrySetResult(reason);
                }
                await Task.WhenAll(runtimes.Select(runtime => runtime.Completion))
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) { failures.Add(exception); }
            failures.AddRange(runtimes.SelectMany(runtime => runtime.OrderlyFailures));
            var cleanup = await StopCoreAsync().ConfigureAwait(false);
            foreach (var failure in cleanup)
                if (!failures.Any(existing => ReferenceEquals(existing, failure)))
                    failures.Add(failure);
            if (failures.Count > 0)
                throw new AggregateException("Offline orderly shutdown failed; abort cleanup was attempted.", failures);
        }
        finally { gate.Release(); }
    }

    public async Task StopAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var failures = await StopCoreAsync().ConfigureAwait(false);
            if (failures.Count > 0) throw new AggregateException("Offline supervisor cleanup failed.", failures);
        }
        finally { gate.Release(); }
    }

    private async Task<List<Exception>> StopCoreAsync()
    {
        var failures = new List<Exception>();
        if (stopped) return failures;
        stopped = true;
        foreach (var runtime in runtimes)
        {
            try { runtime.Stop.Cancel(); }
            catch (Exception exception) { failures.Add(exception); }
        }
        foreach (var runtime in runtimes)
        {
            try { await runtime.Completion.ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        var hostFailures = await Task.WhenAll(runtimes.Select(DisposeHostAsync)).ConfigureAwait(false);
        foreach (var failure in hostFailures)
        {
            if (failure is not null) failures.Add(failure);
        }
        return failures;
    }

    private static async Task<Exception?> DisposeHostAsync(ProfileRuntime runtime)
    {
        try
        {
            await runtime.Host.DisposeAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) { return exception; }
        finally { runtime.Stop.Dispose(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
