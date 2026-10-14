using System.Text.Json;
using System.Diagnostics;
using System.Threading.Tasks;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Transport;

public interface ISharedRadioIpcEndpoint : IByteBridgeEndpoint
{
    int ProfileId { get; }
    string? Fault { get; }
    bool Poll();
    void OnDiagnostic(SharedRadioDiagnostic diagnostic);
}

public enum SharedRadioHostState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

public enum LegacyConnectionState { Stopped, Initialising, Ready, PollingConfirmed, Finalising, Unavailable }

public sealed record LegacyConnectionStatus(
    LegacyConnectionState State, string? Reason = null, int DiscardedBytes = 0, long DrainElapsedMs = 0);

/// <summary>
/// Owns the shared Legacy/Radio endpoints and an optional transferred IPC endpoint.
/// No endpoint is opened until StartAsync.
/// </summary>
public sealed class SharedRadioHost : IAsyncDisposable
{
    private const int ReadBufferSize = 4096;
    private static readonly TimeSpan ActorPollInterval = TimeSpan.FromMilliseconds(2);

    private sealed class Reader
    {
        public required SharedRadioClient? Client { get; init; }
        public required IByteBridgeEndpoint Endpoint { get; init; }
        public required Task<Exception?> Task { get; init; }
        public required CancellationTokenSource Stop { get; init; }
        public bool CompletionHandled { get; set; }
        public Task<LegacyDrainResult>? Drain { get; init; }
        public TaskCompletionSource? Admission { get; init; }
        public TaskCompletionSource? Initialised { get; init; }
        public bool DrainHandled { get; set; }
        public CancellationToken OperationToken { get; init; }
        public CancellationTokenRegistration DrainCancellation { get; init; }
    }

    private readonly IByteBridgeEndpointFactory endpointFactory;
    private readonly IMonotonicClock clock;
    private readonly Action<SharedRadioDiagnostic> diagnosticSink;
    private readonly RawCatCapture? rawCatCapture;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object stateGate = new();
    private IByteBridgeEndpoint? legacyEndpoint;
    private IByteBridgeEndpoint? ipcEndpoint;
    private IByteBridgeEndpoint? radioEndpoint;
    private CancellationTokenSource? shutdown;
    private Task? actorTask;
    private SharedRadioHostState state = SharedRadioHostState.Stopped;
    private string? fault;
    private Exception? cleanupFailure;
    private int gracefulStop;
    private int profileId;
    private bool acceptingIpcChanges;
    private readonly Queue<IpcChange> ipcChanges = new();
    private int captureCoordinatorMetrics;
    private SharedRadioCoordinatorMetrics? coordinatorMetrics;
    private RawCatCapture.CaptureSession? captureSession;
    private LegacyConnectionStatus legacyStatus = new(LegacyConnectionState.Stopped);
    private ProfileConfig? activeProfile;
    private TaskCompletionSource? initialLegacy;
    private readonly Queue<LegacyChange> legacyChanges = new();
    private Task<Exception?>? legacyClose;
    private bool legacyAtBoundary = true;
    private CancellationToken initialStartCancellation;

    private sealed record LegacyChange(IByteBridgeEndpoint Endpoint, CancellationToken Token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record IpcChange(ISharedRadioIpcEndpoint Endpoint, bool Attach, CancellationToken Token)
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public SharedRadioHost(
        IByteBridgeEndpointFactory endpointFactory,
        IMonotonicClock clock,
        Action<SharedRadioDiagnostic> diagnosticSink,
        RawCatCapture? rawCatCapture = null)
    {
        ArgumentNullException.ThrowIfNull(endpointFactory);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        this.endpointFactory = endpointFactory;
        this.clock = clock;
        this.diagnosticSink = diagnosticSink;
        this.rawCatCapture = rawCatCapture;
    }

    public SharedRadioHostState State
    {
        get { lock (stateGate) return state; }
    }

    public string? Fault
    {
        get { lock (stateGate) return fault; }
    }

    internal SharedRadioCoordinatorMetrics? CoordinatorMetrics => Volatile.Read(ref coordinatorMetrics);

    public LegacyConnectionStatus LegacyStatus
    {
        get { lock (stateGate) return legacyStatus; }
    }

    internal void EnableCoordinatorMetricsCapture()
    {
        lock (stateGate)
        {
            if (state != SharedRadioHostState.Stopped || actorTask is not null)
                throw new InvalidOperationException("Coordinator metrics capture must be enabled before host startup.");
            Volatile.Write(ref captureCoordinatorMetrics, 1);
        }
    }

    public async Task StartAsync(
        ProfileConfig profile,
        SharedRadioCoordinatorOptions coordinatorOptions,
        IByteBridgeEndpoint ipcClientEndpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ipcClientEndpoint);
        await StartCoreAsync(profile, coordinatorOptions, ipcClientEndpoint, cancellationToken).ConfigureAwait(false);
    }

    public Task StartAsync(
        ProfileConfig profile, SharedRadioCoordinatorOptions coordinatorOptions,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(profile, coordinatorOptions, null, cancellationToken);

    private async Task StartCoreAsync(
        ProfileConfig profile, SharedRadioCoordinatorOptions coordinatorOptions,
        IByteBridgeEndpoint? ipcClientEndpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(coordinatorOptions);
        ValidateProfile(profile, coordinatorOptions);
        if (ipcClientEndpoint is ISharedRadioIpcEndpoint bound &&
            (bound.ProfileId != profile.Id || !bound.Poll()))
            throw new ArgumentException("IPC endpoint is inactive or bound to another profile.", nameof(ipcClientEndpoint));

        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != SharedRadioHostState.Stopped)
            {
                throw new InvalidOperationException($"Cannot start the shared host while it is {State}.");
            }

            SetState(SharedRadioHostState.Starting);
            IByteBridgeEndpoint? openedLegacy = null;
            IByteBridgeEndpoint? openedRadio = null;
            try
            {
                openedLegacy = await endpointFactory.OpenAsync(
                    profile.VirtualPort!, profile.SerialSettings, cancellationToken).ConfigureAwait(false);
                openedRadio = await endpointFactory.OpenAsync(
                    profile.PhysicalPort!, profile.SerialSettings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception openFailure)
            {
                var closeFailures = await DisposeEndpointsAsync(openedLegacy, ipcClientEndpoint, openedRadio)
                    .ConfigureAwait(false);
                if (closeFailures.Count != 0)
                {
                    var combined = new AggregateException(
                        "Shared host startup failed and an endpoint could not be closed.",
                        [openFailure, .. closeFailures]);
                    SetFault(combined);
                    throw combined;
                }

                SetState(SharedRadioHostState.Stopped);
                throw;
            }

            legacyEndpoint = openedLegacy;
            legacyClose = null;
            legacyAtBoundary = true;
            activeProfile = profile;
            initialLegacy = new(TaskCreationOptions.RunContinuationsAsynchronously);
            initialStartCancellation = cancellationToken;
            SetLegacyStatus(new(LegacyConnectionState.Initialising, "Legacy input forwarding is disabled during drain."));
            profileId = profile.Id;
            ipcEndpoint = ipcClientEndpoint;
            radioEndpoint = openedRadio;
            captureSession = rawCatCapture?.StartSession(
                profile.Id, profile.Label, profile.Framing.ToString());
            Volatile.Write(ref coordinatorMetrics, null);
            shutdown = new CancellationTokenSource();
            cleanupFailure = null;
            Volatile.Write(ref gracefulStop, 0);
            SetState(SharedRadioHostState.Running);
            lock (stateGate) acceptingIpcChanges = true;
            var token = shutdown.Token;
            actorTask = Task.Factory.StartNew(
                () => RunActor(coordinatorOptions, token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            await initialLegacy.Task.ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public Task StopAsync() => StopCoreAsync(false, CancellationToken.None);

    public Task AttachIpcAsync(ISharedRadioIpcEndpoint endpoint, CancellationToken cancellationToken = default) =>
        ChangeIpcAsync(endpoint, true, cancellationToken);

    public Task DetachIpcAsync(ISharedRadioIpcEndpoint endpoint, CancellationToken cancellationToken = default) =>
        ChangeIpcAsync(endpoint, false, cancellationToken);

    public async Task RecoverLegacyAsync(CancellationToken cancellationToken = default)
    {
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != SharedRadioHostState.Running || LegacyStatus.State != LegacyConnectionState.Unavailable ||
                cleanupFailure is not null || activeProfile is null)
                throw new InvalidOperationException("Legacy recovery requires a healthy running host and a closed unavailable Legacy endpoint.");
            if (legacyClose is { } closing)
            {
                var closeFailure = await closing.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (closeFailure is not null)
                    throw new IOException("Previous Legacy endpoint did not close successfully.", closeFailure);
            }
            var opened = await endpointFactory.OpenAsync(
                activeProfile.VirtualPort!, activeProfile.SerialSettings, cancellationToken).ConfigureAwait(false);
            var change = new LegacyChange(opened, cancellationToken);
            bool accepted;
            lock (stateGate)
            {
                accepted = acceptingIpcChanges;
                if (accepted) legacyChanges.Enqueue(change);
            }
            if (!accepted)
            {
                await opened.DisposeAsync().ConfigureAwait(false);
                throw new InvalidOperationException("Host stopped before Legacy recovery could be submitted.");
            }
            await change.Completion.Task.ConfigureAwait(false);
            if (LegacyStatus.State is not (LegacyConnectionState.Ready or LegacyConnectionState.PollingConfirmed))
                throw new IOException($"Legacy recovery failed: {LegacyStatus.Reason}");
        }
        finally { lifecycle.Release(); }
    }

    private async Task ChangeIpcAsync(
        ISharedRadioIpcEndpoint endpoint, bool attach, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var change = new IpcChange(endpoint, attach, cancellationToken);
            bool alreadyDetached;
            lock (stateGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                alreadyDetached = !attach && ipcEndpoint is null && !acceptingIpcChanges;
                if (!alreadyDetached)
                {
                    if (!acceptingIpcChanges ||
                        (state != SharedRadioHostState.Running && (attach || state != SharedRadioHostState.Faulted)))
                        throw new InvalidOperationException("IPC changes require a running profile host.");
                    ipcChanges.Enqueue(change);
                }
            }
            if (alreadyDetached)
            {
                // Join actor cleanup before observing its retained endpoint-close failures.
                if (actorTask is not null)
                    await actorTask.ConfigureAwait(false);
                if (cleanupFailure is not null)
                    throw new IOException("IPC endpoint cleanup failed.", cleanupFailure);
                return;
            }
            // Await the actor's decision even if cancellation races with ownership transfer.
            await change.Completion.Task.ConfigureAwait(false);
        }
        finally { lifecycle.Release(); }
    }

    public Task StopGracefullyAsync(CancellationToken cancellationToken) =>
        StopCoreAsync(true, cancellationToken);

    private async Task StopCoreAsync(bool graceful, CancellationToken cancellationToken)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State == SharedRadioHostState.Stopped)
            {
                return;
            }

            if (State != SharedRadioHostState.Faulted)
            {
                SetState(SharedRadioHostState.Stopping);
            }

            if (graceful)
                Volatile.Write(ref gracefulStop, 1);
            else
                shutdown?.Cancel();
            using var abort = cancellationToken.Register(() => shutdown?.Cancel());
            if (actorTask is not null)
            {
                await actorTask.ConfigureAwait(false);
            }

            if (captureSession is { } activeCaptureSession)
            {
                rawCatCapture!.StopSession(activeCaptureSession);
                captureSession = null;
            }
            shutdown?.Dispose();
            shutdown = null;
            actorTask = null;
            if (cleanupFailure is not null)
            {
                throw new AggregateException("One or more shared host endpoints could not be closed.",
                    cleanupFailure);
            }

            if (State != SharedRadioHostState.Faulted)
            {
                SetState(SharedRadioHostState.Stopped);
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void RunActor(SharedRadioCoordinatorOptions coordinatorOptions, CancellationToken cancellationToken)
    {
        SharedRadioCoordinator? coordinator = null;
        var readers = new List<Reader>();
        try
        {
            coordinator = new(coordinatorOptions, clock, diagnostic =>
            {
                diagnosticSink(diagnostic);
                CaptureCoordinatorDiagnostic(diagnostic);
                if (diagnostic.Kind == SharedRadioDiagnosticKind.LegacyPollingConfirmed)
                    SetLegacyStatus(LegacyStatus with { State = LegacyConnectionState.PollingConfirmed,
                        Reason = diagnostic.Message });
                (ipcEndpoint as ISharedRadioIpcEndpoint)?.OnDiagnostic(diagnostic);
            }, rawCatCapture is null ? null : byteCount => CaptureRadioIngressProcessed(byteCount));
            var commanderIngress = CreateCaptureObserver("Commander", "CommanderToComCon",
                captureIngressChunks: true);
            var radioIngress = CreateCaptureObserver("Radio", "RadioToComCon",
                captureIngressChunks: true);
            var radioEgress = CreateCaptureObserver("ComCon", "ComConToRadio", "WriteCompleted");
            var commanderEgress = CreateCaptureObserver("ComCon", "ComConToCommander", "WriteCompleted");
            coordinator.NotifyClientDisconnected(SharedRadioClient.LegacyClient, "Legacy input initialisation.");
            readers.Add(StartLegacyReader(legacyEndpoint!, coordinator, commanderIngress,
                initialLegacy!, coordinatorOptions.Framing, cancellationToken, initialStartCancellation));
            if (ipcEndpoint is not null)
                readers.Add(StartReader(SharedRadioClient.IpcClient, ipcEndpoint, coordinator, null, cancellationToken));
            else
                coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient, "No IPC session attached.");
            readers.Add(StartReader(null, radioEndpoint!, coordinator, radioIngress, cancellationToken));

            while (!cancellationToken.IsCancellationRequested && Volatile.Read(ref gracefulStop) == 0)
            {
                coordinator.ProcessAvailable();
                ProcessLegacyDrains(coordinator, readers);
                ProcessLegacyClose();
                ProcessReaderCompletions(coordinator, readers);
                PollIpcSession(coordinator);
                CloseDisconnectedClientEndpoints(coordinator, readers);
                ProcessIpcChanges(coordinator, readers, cancellationToken);
                ProcessLegacyChanges(coordinator, readers, coordinatorOptions.Framing, cancellationToken);
                DispatchRadioWrites(coordinator, radioEgress, cancellationToken);
                PollIpcSession(coordinator);
                CloseDisconnectedClientEndpoints(coordinator, readers);
                DeliverClientRx(coordinator, commanderEgress, cancellationToken);
                ReflectCoordinatorState(coordinator);
                if (Volatile.Read(ref captureCoordinatorMetrics) != 0)
                    Volatile.Write(ref coordinatorMetrics, coordinator.Metrics);
                cancellationToken.WaitHandle.WaitOne(ActorPollInterval);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetFault(exception);
        }
        finally
        {
            lock (stateGate)
            {
                acceptingIpcChanges = false;
                while (ipcChanges.TryDequeue(out var change))
                    change.Completion.TrySetException(new IOException("The profile host stopped before the IPC change."));
                while (legacyChanges.TryDequeue(out var change))
                {
                    try { change.Endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
                    catch (Exception exception) { SetFault(exception); }
                    change.Completion.TrySetException(new IOException("The profile host stopped before Legacy recovery."));
                }
            }
            if (coordinator is not null && Volatile.Read(ref captureCoordinatorMetrics) != 0)
            {
                try
                {
                    Volatile.Write(ref coordinatorMetrics, coordinator.Metrics);
                }
                catch (InvalidOperationException exception)
                {
                    SetFault(exception);
                }
            }
            if (coordinator is not null && coordinator.State != SharedRadioCoordinatorState.Stopped)
            {
                try
                {
                    coordinator.Stop();
                }
                catch (Exception exception)
                {
                    SetFault(exception);
                }
            }

            FinaliseLegacy(readers);
            foreach (var reader in readers)
                reader.Initialised?.TrySetException(new IOException("Host stopped before Legacy initialisation completed."));
            var closeFailure = StopReadersAndCloseEndpoints(readers);
            initialLegacy?.TrySetException(new IOException("Host stopped before Legacy initialisation completed."));
            if (cleanupFailure is not null && closeFailure is not null)
            {
                cleanupFailure = new AggregateException(cleanupFailure, closeFailure);
            }
            else
            {
                cleanupFailure ??= closeFailure;
            }
            if (cleanupFailure is not null)
            {
                SetFault(cleanupFailure);
            }
            else if (State != SharedRadioHostState.Faulted)
            {
                SetState(SharedRadioHostState.Stopped);
            }
        }
    }

    private Reader StartReader(
        SharedRadioClient? client,
        IByteBridgeEndpoint endpoint,
        SharedRadioCoordinator coordinator,
        RawCatFrameObserver? captureObserver,
        CancellationToken cancellationToken)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new()
        {
            Client = client,
            Endpoint = endpoint,
            Stop = stop,
            Task = ReadEndpointAsync(client, endpoint, coordinator, captureObserver, stop.Token)
        };
    }

    private Reader StartLegacyReader(
        IByteBridgeEndpoint endpoint, SharedRadioCoordinator coordinator, RawCatFrameObserver? captureObserver,
        TaskCompletionSource initialised, FramingMode framing, CancellationToken cancellationToken,
        CancellationToken operationToken)
    {
        SetLegacyStatus(new(LegacyConnectionState.Initialising, "Legacy input forwarding is disabled during drain."));
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var drainCancellation = operationToken.Register(stop.Cancel);
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = DrainLegacyAsync(endpoint, framing, stop.Token);
        return new()
        {
            Client = SharedRadioClient.LegacyClient, Endpoint = endpoint, Stop = stop,
            Drain = drain, Admission = admission, Initialised = initialised,
            OperationToken = operationToken, DrainCancellation = drainCancellation,
            Task = ReadInitialisedLegacyAsync(drain, admission.Task, endpoint, coordinator, captureObserver, stop.Token)
        };
    }

    private Task<LegacyDrainResult> DrainLegacyAsync(
        IByteBridgeEndpoint endpoint, FramingMode framing, CancellationToken cancellationToken,
        bool atBoundary = true) =>
        endpoint is IBufferedByteBridgeEndpoint buffered
            ? Task.Run(() => LegacyInputDrain.RunAsync(buffered, framing, clock, cancellationToken,
                atBoundary, boundary => legacyAtBoundary = boundary))
            : Task.FromException<LegacyDrainResult>(
                new NotSupportedException("Legacy endpoint does not support observed buffered-input drain."));

    private async Task<Exception?> ReadInitialisedLegacyAsync(
        Task<LegacyDrainResult> drain, Task admission, IByteBridgeEndpoint endpoint,
        SharedRadioCoordinator coordinator, RawCatFrameObserver? captureObserver, CancellationToken cancellationToken)
    {
        try
        {
            var result = await drain.ConfigureAwait(false);
            if (!result.Succeeded) return new IOException(result.Reason);
            await admission.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await ReadEndpointAsync(SharedRadioClient.LegacyClient, endpoint, coordinator,
                captureObserver, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return null; }
        catch (Exception exception) { return exception; }
    }

    private void ProcessLegacyDrains(SharedRadioCoordinator coordinator, List<Reader> readers)
    {
        foreach (var reader in readers.Where(reader => reader.Drain is not null && !reader.DrainHandled &&
            reader.Drain.IsCompleted))
        {
            reader.DrainHandled = true;
            reader.DrainCancellation.Dispose();
            try
            {
                var result = reader.Drain!.GetAwaiter().GetResult();
                if (!result.Succeeded) throw new IOException(result.Reason);
                reader.Stop.Token.ThrowIfCancellationRequested();
                coordinator.ReconnectLegacyClient();
                SetLegacyStatus(new(LegacyConnectionState.Ready, result.Reason,
                    result.DiscardedBytes, result.ElapsedMs));
                reader.Admission!.TrySetResult();
                reader.Initialised!.TrySetResult();
            }
            catch (Exception exception)
            {
                reader.Stop.Cancel();
                reader.Admission!.TrySetCanceled(reader.Stop.Token);
                var result = reader.Drain!.IsCompletedSuccessfully ? reader.Drain.Result : null;
                SetLegacyStatus(new(LegacyConnectionState.Unavailable, exception.Message,
                    result?.DiscardedBytes ?? 0, result?.ElapsedMs ?? 0));
                BeginLegacyClose(reader);
                if (reader.OperationToken.IsCancellationRequested)
                    reader.Initialised!.TrySetCanceled(reader.OperationToken);
                else
                    reader.Initialised!.TrySetResult();
            }
        }
    }

    private void ProcessLegacyChanges(
        SharedRadioCoordinator coordinator, List<Reader> readers, FramingMode framing, CancellationToken token)
    {
        LegacyChange? change;
        lock (stateGate)
            if (!legacyChanges.TryDequeue(out change)) return;
        try
        {
            change.Token.ThrowIfCancellationRequested();
            if (legacyEndpoint is not null || legacyClose is not null ||
                coordinator.IsConnected(SharedRadioClient.LegacyClient))
                throw new InvalidOperationException("Previous Legacy endpoint is not detached.");
            foreach (var reader in readers.Where(reader => reader.Client == SharedRadioClient.LegacyClient).ToArray())
            {
                reader.Stop.Cancel();
                reader.Task.GetAwaiter().GetResult();
                reader.DrainCancellation.Dispose();
                reader.Stop.Dispose();
                readers.Remove(reader);
            }
            legacyEndpoint = change.Endpoint;
            legacyAtBoundary = true;
            readers.Add(StartLegacyReader(change.Endpoint, coordinator,
                CreateCaptureObserver("Commander", "CommanderToComCon", captureIngressChunks: true),
                change.Completion, framing, token, change.Token));
        }
        catch (Exception exception)
        {
            try { change.Endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            catch (Exception closeException) { SetFault(closeException); }
            change.Completion.TrySetException(exception);
        }
    }

    private void FinaliseLegacy(List<Reader> readers)
    {
        if (legacyClose is not null)
        {
            ProcessLegacyClose();
            if (legacyClose is not null) return;
        }
        if (legacyEndpoint is null || activeProfile is null) return;
        var reader = readers.SingleOrDefault(reader => reader.Client == SharedRadioClient.LegacyClient);
        if (reader?.Admission is { Task.IsCompletedSuccessfully: false })
        {
            SetLegacyStatus(LegacyStatus with { State = LegacyConnectionState.Unavailable,
                Reason = "Finalisation incomplete: Legacy initialisation was not admitted; closing without a competing input read." });
            return;
        }
        SetLegacyStatus(new(LegacyConnectionState.Finalising, "Legacy forwarding stopped; input is being discarded."));
        try
        {
            if (reader is not null)
            {
                reader.Stop.Cancel();
                reader.Task.WaitAsync(TimeSpan.FromMilliseconds(LegacyInputDrain.MaximumMs)).GetAwaiter().GetResult();
            }
            var result = DrainLegacyAsync(legacyEndpoint, activeProfile.Framing, CancellationToken.None, legacyAtBoundary)
                .GetAwaiter().GetResult();
            SetLegacyStatus(new(LegacyConnectionState.Stopped,
                result.Succeeded ? "Finalisation completed at an observed boundary." : $"Finalisation incomplete: {result.Reason}",
                result.DiscardedBytes, result.ElapsedMs));
        }
        catch (Exception exception)
        {
            SetLegacyStatus(new(LegacyConnectionState.Unavailable, $"Finalisation failed: {exception.Message}"));
            SetFault(exception);
        }
    }

    private void SetLegacyStatus(LegacyConnectionStatus value)
    {
        lock (stateGate) legacyStatus = value;
        var diagnostic = new SharedRadioDiagnostic(SharedRadioDiagnosticKind.LegacyLifecycle,
            $"{value.State}: {value.Reason} Discarded {value.DiscardedBytes} byte(s) in {value.DrainElapsedMs} ms.",
            SharedRadioClient.LegacyClient, ByteCount: value.DiscardedBytes, MonotonicTimestampMs: clock.NowMs);
        diagnosticSink(diagnostic);
        CaptureCoordinatorDiagnostic(diagnostic);
    }

    private void BeginLegacyClose(Reader? reader)
    {
        if (legacyClose is not null || legacyEndpoint is null) return;
        var endpoint = legacyEndpoint;
        reader?.Stop.Cancel();
        legacyClose = Task.Run(async () =>
        {
            try
            {
                await endpoint.DisposeAsync().ConfigureAwait(false);
                if (reader is not null)
                    await reader.Task.WaitAsync(TimeSpan.FromMilliseconds(LegacyInputDrain.MaximumMs)).ConfigureAwait(false);
                return (Exception?)null;
            }
            catch (Exception exception) { return exception; }
        });
    }

    private void ProcessLegacyClose()
    {
        if (legacyClose is not { IsCompleted: true }) return;
        var failure = legacyClose.GetAwaiter().GetResult();
        legacyClose = null;
        legacyEndpoint = null;
        if (failure is not null)
        {
            cleanupFailure = new AggregateException("Legacy endpoint close failed.",
                cleanupFailure is null ? [failure] : [cleanupFailure, failure]);
            SetLegacyStatus(LegacyStatus with { State = LegacyConnectionState.Unavailable,
                Reason = $"Legacy endpoint close failed: {failure.Message}" });
        }
    }

    private async Task<Exception?> ReadEndpointAsync(
        SharedRadioClient? client,
        IByteBridgeEndpoint endpoint,
        SharedRadioCoordinator coordinator,
        RawCatFrameObserver? captureObserver,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        try
        {
            while (true)
            {
                var readBuffer = buffer.AsMemory();
                if (client == SharedRadioClient.LegacyClient && endpoint is IBufferedByteBridgeEndpoint buffered)
                {
                    var available = buffered.BufferedInputBytes;
                    if (available < 0) throw new IOException("Negative buffered-input count.");
                    if (available == 0)
                    {
                        await Task.Delay(2, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    readBuffer = buffer.AsMemory(0, Math.Min(buffer.Length, available));
                }
                var count = await endpoint.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    throw new IOException("Endpoint disconnected.");
                }
                if (count < 0 || count > readBuffer.Length)
                {
                    throw new IOException("Endpoint returned an invalid byte count.");
                }
                if (client == SharedRadioClient.LegacyClient)
                    legacyAtBoundary = activeProfile!.Framing == FramingMode.Civ
                        ? buffer[count - 1] == 0xFD : buffer[count - 1] == (byte)';';

                var readTimestampMs = clock.NowMs;
                var readTimestampTicks = Stopwatch.GetTimestamp();
                captureObserver?.Observe(buffer.AsSpan(0, count), readTimestampMs, readTimestampTicks);
                var accepted = client is { } source
                    ? coordinator.TryPostClientBytes(source, buffer.AsSpan(0, count))
                    : coordinator.TryPostRadioBytes(buffer.AsSpan(0, count));
                if (!accepted)
                {
                    throw new IOException("Coordinator rejected bytes at bounded ingress.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
        finally
        {
            captureObserver?.Complete();
        }
    }

    private void ProcessReaderCompletions(
        SharedRadioCoordinator coordinator,
        IEnumerable<Reader> readers)
    {
        foreach (var reader in readers)
        {
            if (reader.CompletionHandled || !reader.Task.IsCompleted)
            {
                continue;
            }

            reader.CompletionHandled = true;
            if (reader.Drain is not null && !reader.Admission!.Task.IsCompletedSuccessfully)
                continue;
            var exception = reader.Task.GetAwaiter().GetResult();
            if (exception is null)
            {
                continue;
            }

            if (reader.Client is { } client)
            {
                coordinator.NotifyClientDisconnected(client,
                    $"{client} endpoint failed: {exception.Message}");
                if (client == SharedRadioClient.LegacyClient)
                    SetLegacyStatus(new(LegacyConnectionState.Unavailable, exception.Message));
            }
            else
            {
                coordinator.NotifyRadioFailure($"Radio RX endpoint failed: {exception.Message}");
                CloseEndpoint(ref radioEndpoint);
            }
        }
    }

    private void CloseDisconnectedClientEndpoints(SharedRadioCoordinator coordinator, List<Reader> readers)
    {
        if (!coordinator.IsConnected(SharedRadioClient.LegacyClient))
        {
            var reader = readers.SingleOrDefault(reader => reader.Client == SharedRadioClient.LegacyClient);
            if (reader is null || reader.DrainHandled)
            {
                if (legacyEndpoint is not null && legacyClose is null &&
                    LegacyStatus.State != LegacyConnectionState.Unavailable)
                    SetLegacyStatus(LegacyStatus with { State = LegacyConnectionState.Unavailable,
                        Reason = "Legacy endpoint disconnected; explicit recovery required." });
                BeginLegacyClose(reader);
            }
        }
        if (!coordinator.IsConnected(SharedRadioClient.IpcClient))
        {
            var reader = readers.SingleOrDefault(value => value.Client == SharedRadioClient.IpcClient);
            reader?.Stop.Cancel();
            CloseEndpoint(ref ipcEndpoint);
            if (reader is not null)
            {
                reader.Task.GetAwaiter().GetResult();
                reader.Stop.Dispose();
                readers.Remove(reader);
            }
        }
    }

    private void ProcessIpcChanges(
        SharedRadioCoordinator coordinator, List<Reader> readers, CancellationToken cancellationToken)
    {
        IpcChange? change;
        lock (stateGate)
            if (!ipcChanges.TryDequeue(out change)) return;
        try
        {
            change.Token.ThrowIfCancellationRequested();
            if (change.Attach)
            {
                if (cleanupFailure is not null)
                    throw new IOException("Previous IPC endpoint cleanup failed.", cleanupFailure);
                if (ipcEndpoint is not null)
                    throw new InvalidOperationException("A profile IPC endpoint is already attached.");
                if (change.Endpoint.ProfileId != profileId || !change.Endpoint.Poll())
                    throw new ArgumentException("IPC endpoint is inactive or bound to another profile.");
                coordinator.ReconnectIpcClient();
                ipcEndpoint = change.Endpoint;
                readers.Add(StartReader(SharedRadioClient.IpcClient, ipcEndpoint, coordinator,
                    null, cancellationToken));
            }
            else if (ReferenceEquals(ipcEndpoint, change.Endpoint))
            {
                coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient, "IPC session detached.");
                CloseDisconnectedClientEndpoints(coordinator, readers);
                if (cleanupFailure is not null)
                    throw new IOException("IPC endpoint cleanup failed.", cleanupFailure);
            }
            else if (ipcEndpoint is not null)
                throw new InvalidOperationException("A different IPC session owns this profile.");
            else if (cleanupFailure is not null)
                throw new IOException("IPC endpoint cleanup failed.", cleanupFailure);
            change.Completion.TrySetResult();
        }
        catch (OperationCanceledException) when (change.Token.IsCancellationRequested)
        {
            change.Completion.TrySetCanceled(change.Token);
        }
        catch (Exception exception)
        {
            change.Completion.TrySetException(exception);
        }
    }

    private void DispatchRadioWrites(
        SharedRadioCoordinator coordinator,
        RawCatFrameObserver? captureObserver,
        CancellationToken cancellationToken)
    {
        while (Volatile.Read(ref gracefulStop) == 0 && !HasPendingIpcChange() && coordinator.TryTakeRadioWrite(out var write))
        {
            if (write!.Client == SharedRadioClient.IpcClient &&
                ipcEndpoint is ISharedRadioIpcEndpoint bound && !bound.Poll())
            {
                coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.ProvenZeroBytes);
                PollIpcSession(coordinator);
                continue;
            }

            try
            {
                radioEndpoint!.WriteAsync(write!.Bytes, cancellationToken)
                    .AsTask().GetAwaiter().GetResult();
                captureObserver?.Observe(write.Bytes);
                coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Unknown);
                SetFault(new IOException(
                    $"Radio write failed; delivery outcome is unknown: {exception.Message}", exception));
            }
        }
    }

    private bool HasPendingIpcChange()
    {
        lock (stateGate) return ipcChanges.Count != 0;
    }

    private void PollIpcSession(SharedRadioCoordinator coordinator)
    {
        if (ipcEndpoint is ISharedRadioIpcEndpoint bound &&
            coordinator.IsConnected(SharedRadioClient.IpcClient) && !bound.Poll())
            coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient,
                $"IPC session terminated: {bound.Fault ?? "endpoint unavailable"}");
    }

    private void DeliverClientRx(
        SharedRadioCoordinator coordinator,
        RawCatFrameObserver? legacyCaptureObserver,
        CancellationToken cancellationToken)
    {
        foreach (var client in Enum.GetValues<SharedRadioClient>())
        {
            var endpoint = client == SharedRadioClient.LegacyClient ? legacyEndpoint : ipcEndpoint;
            while (endpoint is not null && coordinator.TryTakeClientDelivery(client, out var delivery))
            {
                try
                {
                    endpoint.WriteAsync(delivery!.Bytes, cancellationToken)
                        .AsTask().GetAwaiter().GetResult();
                    if (client == SharedRadioClient.LegacyClient)
                        legacyCaptureObserver?.Observe(delivery.Bytes);
                    coordinator.CompleteClientDelivery(delivery.Id, delivered: true);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    coordinator.CompleteClientDelivery(delivery!.Id, delivered: false,
                        $"{client} RX delivery failed: {exception.Message}");
                    break;
                }
            }

        }
    }

    private RawCatFrameObserver? CreateCaptureObserver(
        string source, string direction, string completeOutcome = "CompleteRecord",
        bool captureIngressChunks = false) =>
        rawCatCapture is not null && captureSession is { FramingSupported: true }
            ? new RawCatFrameObserver(
                Enum.Parse<FramingMode>(captureSession.Framing), source, direction,
                rawCatCapture, captureSession, completeOutcome, captureIngressChunks, clock)
            : null;

    private void CaptureCoordinatorDiagnostic(SharedRadioDiagnostic diagnostic)
    {
        if (rawCatCapture is null || captureSession is not { } session) return;
        if (!rawCatCapture.CanCapture(session)) return;
        var diagnosticTimestampTicks = Stopwatch.GetTimestamp();
        var details = JsonSerializer.SerializeToUtf8Bytes(new
        {
            kind = diagnostic.Kind.ToString(),
            message = diagnostic.Message,
            client = diagnostic.Client?.ToString(),
            outcome = diagnostic.Outcome?.ToString(),
            address = diagnostic.Address,
            byteCount = diagnostic.ByteCount,
            recordSequence = diagnostic.RecordSequence,
            dispatched = diagnostic.Dispatched,
            monotonicTimestampMs = diagnostic.MonotonicTimestampMs,
            monotonicTimestampTicks = diagnosticTimestampTicks,
            monotonicTimestampFrequency = Stopwatch.Frequency,
            parserIdleElapsedMs = diagnostic.ParserIdleElapsedMs,
            parserTotalElapsedMs = diagnostic.ParserTotalElapsedMs
        });
        rawCatCapture.TryCapture(session, "Coordinator", "CoordinatorDiagnostic",
            diagnostic.Kind.ToString(), details, diagnostic.MonotonicTimestampMs, diagnosticTimestampTicks);
    }

    private void CaptureRadioIngressProcessed(int byteCount) =>
        CaptureCoordinatorDiagnostic(new(
            SharedRadioDiagnosticKind.RadioIngressProcessed,
            "Coordinator processed a radio ingress chunk.",
            ByteCount: byteCount,
            MonotonicTimestampMs: clock.NowMs));

    private void ReflectCoordinatorState(SharedRadioCoordinator coordinator)
    {
        if (coordinator.State == SharedRadioCoordinatorState.Faulted)
        {
            lock (stateGate)
            {
                state = SharedRadioHostState.Faulted;
                fault = coordinator.Fault;
            }
        }
    }

    private Exception? StopReadersAndCloseEndpoints(IReadOnlyCollection<Reader> readers)
    {
        var failures = new List<Exception>();
        foreach (var reader in readers) reader.Stop.Cancel();
        BeginLegacyClose(readers.SingleOrDefault(reader => reader.Client == SharedRadioClient.LegacyClient));
        if (legacyClose is { } closing)
        {
            try
            {
                var failure = closing.WaitAsync(TimeSpan.FromMilliseconds(LegacyInputDrain.MaximumMs))
                    .GetAwaiter().GetResult();
                if (failure is not null) failures.Add(failure);
                ProcessLegacyClose();
            }
            catch (TimeoutException exception)
            {
                failures.Add(new IOException("Legacy endpoint close exceeded 3000 ms; close outcome is unknown.", exception));
                SetLegacyStatus(LegacyStatus with { State = LegacyConnectionState.Unavailable,
                    Reason = "Legacy endpoint close exceeded 3000 ms; close outcome is unknown." });
            }
        }
        foreach (var endpoint in new[] { ipcEndpoint, radioEndpoint })
        {
            if (endpoint is null)
            {
                continue;
            }
            try
            {
                endpoint.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        foreach (var reader in readers)
        {
            try
            {
                reader.Task.WaitAsync(TimeSpan.FromMilliseconds(LegacyInputDrain.MaximumMs)).GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
            finally
            {
                reader.DrainCancellation.Dispose();
                reader.Stop.Dispose();
            }
        }

        legacyEndpoint = null;
        ipcEndpoint = null;
        radioEndpoint = null;
        return failures.Count == 0 ? null : new AggregateException(failures);
    }

    private void CloseEndpoint(ref IByteBridgeEndpoint? endpoint)
    {
        if (endpoint is null)
        {
            return;
        }

        var closing = endpoint;
        endpoint = null;
        try
        {
            closing.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            cleanupFailure = new AggregateException(
                "A disconnected shared client endpoint could not be closed.",
                cleanupFailure is null ? [exception] : [cleanupFailure, exception]);
        }
    }

    private static async Task<IReadOnlyList<Exception>> DisposeEndpointsAsync(
        params IByteBridgeEndpoint?[] endpoints)
    {
        var failures = new List<Exception>();
        foreach (var endpoint in endpoints)
        {
            if (endpoint is null)
            {
                continue;
            }
            try
            {
                await endpoint.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        return failures.AsReadOnly();
    }

    public static void ValidateProfile(
        ProfileConfig profile,
        SharedRadioCoordinatorOptions coordinatorOptions)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(coordinatorOptions);
        // This validates a host's local wiring; group mapping coverage is checked by the supervisor.
        var profileMappings = profile.CommandIpcEnabled
            ? new[] { new ComConRigProfileMapping("00000000-0000-0000-0000-000000000000", profile.Id) }
            : Array.Empty<ComConRigProfileMapping>();
        var errors = ConfigurationValidator.Validate(
            AppSettings.CreateDefault() with { Profiles = [profile], RigProfileMappings = profileMappings });
        if (errors.Count != 0)
        {
            throw new ConfigurationValidationException(errors);
        }
        if (!profile.Enabled)
        {
            throw new InvalidOperationException("The selected profile is disabled.");
        }
        if (profile.Framing is not (FramingMode.Civ or FramingMode.KenwoodAscii) ||
            profile.Framing != coordinatorOptions.Framing)
        {
            throw new InvalidOperationException(
                "Shared host framing must be CI-V or Kenwood ASCII and match the selected profile.");
        }
        if (string.IsNullOrWhiteSpace(profile.PhysicalPort) ||
            string.IsNullOrWhiteSpace(profile.VirtualPort))
        {
            throw new InvalidOperationException("Both the Radio and LegacyClient ports are required.");
        }
    }

    private void SetState(SharedRadioHostState value)
    {
        lock (stateGate)
        {
            state = value;
        }
    }

    private void SetFault(Exception exception)
    {
        lock (stateGate)
        {
            fault = $"{exception.GetType().Name}: {exception.Message}";
            state = SharedRadioHostState.Faulted;
        }
    }
}
