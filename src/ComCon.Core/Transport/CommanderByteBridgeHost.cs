using ComCon.Core.Configuration;
using ComCon.Core.Models;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;

namespace ComCon.Core.Transport;

public enum CommanderBridgeState
{
    Stopped,
    Starting,
    Running,
    Stopping,
    Faulted
}

/// <summary>Endpoint implementations must tolerate repeated DisposeAsync calls.</summary>
public interface IByteBridgeEndpoint : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
}

public interface IByteBridgeEndpointFactory
{
    ValueTask<IByteBridgeEndpoint> OpenAsync(
        string portName, SerialSettings settings, CancellationToken cancellationToken);
}

/// <summary>
/// Observes immediately readable local input without consuming it. Only the current
/// input reader may sample and consume bytes; the count does not describe peer queues.
/// </summary>
public interface IBufferedByteBridgeEndpoint : IByteBridgeEndpoint
{
    int BufferedInputBytes { get; }
}

/// <summary>
/// Explicit single-Commander byte relay. It does not parse CAT commands, gate
/// writes, support another owner, or establish request/reply correlation.
/// </summary>
public sealed class CommanderByteBridgeHost : IAsyncDisposable
{
    private const int BufferSize = 4096;
    private readonly IByteBridgeEndpointFactory endpointFactory;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object stateGate = new();
    private IByteBridgeEndpoint? physicalEndpoint;
    private IByteBridgeEndpoint? virtualEndpoint;
    private CancellationTokenSource? shutdown;
    private Task[] pumps = [];
    private Task<IReadOnlyList<Exception>>? endpointDisposal;
    private CommanderBridgeState state = CommanderBridgeState.Stopped;
    private string? fault;
    private int faultLatched;
    private SimulatedBridgeReceiveObserver? simulatedObserver;
    private readonly RawCatCapture? rawCatCapture;
    private RawCatFrameObserver? commanderCapture;
    private RawCatFrameObserver? radioCapture;
    private RawCatCapture.CaptureSession? captureSession;

    public static CommanderByteBridgeHost CreateSimulation(
        IByteBridgeEndpointFactory simulatedEndpoints, SimulatedBridgeReceiveObserver observer,
        RawCatCapture? rawCapture = null)
    {
        ArgumentNullException.ThrowIfNull(observer);
        return new(simulatedEndpoints, rawCapture) { simulatedObserver = observer };
    }

    public static CommanderByteBridgeHost CreateSimulationWithTelemetry(
        IByteBridgeEndpointFactory simulatedEndpoints, IMonotonicClock clock,
        RawCatCapture? rawCapture = null) =>
        new(simulatedEndpoints, new PassiveTelemetryLifecycle(true, clock), rawCapture);

    public PassiveTelemetryLifecycle Telemetry { get; }

    public CommanderByteBridgeHost(IByteBridgeEndpointFactory endpointFactory, RawCatCapture? rawCapture = null)
        : this(endpointFactory, new PassiveTelemetryLifecycle(false, new StopwatchClock()), rawCapture) { }

    private CommanderByteBridgeHost(IByteBridgeEndpointFactory endpointFactory,
        PassiveTelemetryLifecycle telemetry, RawCatCapture? rawCapture = null)
    {
        ArgumentNullException.ThrowIfNull(endpointFactory);
        this.endpointFactory = endpointFactory;
        Telemetry = telemetry;
        this.rawCatCapture = rawCapture;
    }

    public CommanderBridgeState State
    {
        get { lock (stateGate) return state; }
    }

    public string? Fault
    {
        get { lock (stateGate) return fault; }
    }

    public async Task StartAsync(ProfileConfig profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var errors = ConfigurationValidator.Validate(
            AppSettings.CreateDefault() with { Profiles = [profile] });
        if (errors.Count != 0) throw new ConfigurationValidationException(errors);
        if (!profile.Enabled) throw new InvalidOperationException("The selected profile is disabled.");
        if (string.IsNullOrWhiteSpace(profile.PhysicalPort) ||
            string.IsNullOrWhiteSpace(profile.VirtualPort))
            throw new InvalidOperationException("Both the physical and ComCon virtual ports are required.");

        await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State != CommanderBridgeState.Stopped)
                throw new InvalidOperationException($"Cannot start the bridge while it is {State}.");

            SetState(CommanderBridgeState.Starting);
            IByteBridgeEndpoint? openedVirtual = null;
            IByteBridgeEndpoint? openedPhysical = null;
            try
            {
                openedVirtual = await endpointFactory.OpenAsync(
                    profile.VirtualPort, profile.SerialSettings, cancellationToken).ConfigureAwait(false);
                openedPhysical = await endpointFactory.OpenAsync(
                    profile.PhysicalPort, profile.SerialSettings, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception openFailure)
            {
                var cleanupFailures = await DisposePairAsync(openedPhysical, openedVirtual).ConfigureAwait(false);
                if (cleanupFailures.Count != 0)
                {
                    var combined = new AggregateException(
                        "Bridge startup failed and one or more opened ports could not be closed.",
                        [openFailure, .. cleanupFailures]);
                    lock (stateGate)
                    {
                        physicalEndpoint = openedPhysical;
                        virtualEndpoint = openedVirtual;
                        endpointDisposal = null;
                    }
                    SetFault(combined);
                    throw combined;
                }

                SetState(CommanderBridgeState.Stopped);
                throw;
            }

            lock (stateGate)
            {
                virtualEndpoint = openedVirtual;
                physicalEndpoint = openedPhysical;
                shutdown = new CancellationTokenSource();
                endpointDisposal = null;
                fault = null;
                faultLatched = 0;
                state = CommanderBridgeState.Running;
            }

            Telemetry.BridgeStarted(profile);
            captureSession = rawCatCapture?.StartSession(profile.Id, profile.Label, profile.Framing.ToString());
            var token = shutdown.Token;
            commanderCapture = rawCatCapture is not null && profile.Framing != FramingMode.None
                ? new RawCatFrameObserver(profile.Framing, "Commander", "CommanderToRadio",
                    rawCatCapture, captureSession!)
                : null;
            radioCapture = rawCatCapture is not null && profile.Framing != FramingMode.None
                ? new RawCatFrameObserver(profile.Framing, "Radio", "RadioToCommander",
                    rawCatCapture, captureSession!)
                : null;
            pumps =
            [
                PumpAsync(virtualEndpoint, physicalEndpoint, token, captureObserver: commanderCapture),
                PumpAsync(physicalEndpoint, virtualEndpoint, token, observeReceive: true,
                    captureObserver: radioCapture)
            ];
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var currentState = State;
            if (currentState == CommanderBridgeState.Stopped) return;
            if (currentState != CommanderBridgeState.Faulted)
                SetState(CommanderBridgeState.Stopping);

            shutdown?.Cancel();
            Telemetry.BridgeEnded(currentState == CommanderBridgeState.Faulted
                ? CommanderBridgeState.Faulted : CommanderBridgeState.Stopped, Fault);
            CompleteRawCaptureObservers();
            if (captureSession is { } session) rawCatCapture?.StopSession(session);
            captureSession = null;
            simulatedObserver?.Invalidate("Commander bridge stopped.");
            var closeFailures = await DisposeEndpointsOnceAsync().ConfigureAwait(false);
            await Task.WhenAll(pumps).ConfigureAwait(false);
            if (closeFailures.Count != 0)
            {
                var closeFailure = new AggregateException(
                    "One or more bridge ports could not be closed.", closeFailures);
                lock (stateGate) endpointDisposal = null;
                SetFault(closeFailure);
                throw closeFailure;
            }

            shutdown?.Dispose();
            shutdown = null;
            if (State != CommanderBridgeState.Faulted)
                SetState(CommanderBridgeState.Stopped);
        }
        finally
        {
            lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await Telemetry.DisposeAsync().ConfigureAwait(false);
    }

    private async Task PumpAsync(
        IByteBridgeEndpoint source,
        IByteBridgeEndpoint destination,
        CancellationToken cancellationToken, bool observeReceive = false,
        RawCatFrameObserver? captureObserver = null)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new IOException("A bridge endpoint disconnected.");
                if (count < 0 || count > buffer.Length)
                    throw new IOException("A bridge endpoint returned an invalid byte count.");
                if (observeReceive) simulatedObserver?.Observe(buffer.AsSpan(0, count));
                if (observeReceive) Telemetry.Observe(buffer.AsSpan(0, count));
                captureObserver?.Observe(buffer.AsSpan(0, count));
                await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await LatchFaultAsync(exception).ConfigureAwait(false);
        }
    }

    private async Task LatchFaultAsync(Exception exception)
    {
        if (Interlocked.Exchange(ref faultLatched, 1) != 0) return;
        SetFault(exception);
        Telemetry.BridgeEnded(CommanderBridgeState.Faulted, exception.Message);
        CompleteRawCaptureObservers();
        if (captureSession is { } session) rawCatCapture?.StopSession(session);
        simulatedObserver?.Invalidate($"Commander bridge fault: {exception.Message}");
        shutdown?.Cancel();
        var closeFailures = await DisposeEndpointsOnceAsync().ConfigureAwait(false);
        if (closeFailures.Count != 0)
        {
            lock (stateGate)
            {
                endpointDisposal = null;
                fault = $"{fault} Port close failure: " +
                    string.Join("; ", closeFailures.Select(error => error.Message));
            }
        }
    }

    private Task<IReadOnlyList<Exception>> DisposeEndpointsOnceAsync()
    {
        lock (stateGate)
            return endpointDisposal ??= DisposeCurrentEndpointsAsync();
    }

    private async Task<IReadOnlyList<Exception>> DisposeCurrentEndpointsAsync()
    {
        IByteBridgeEndpoint? physical;
        IByteBridgeEndpoint? virtualPort;
        lock (stateGate)
        {
            physical = physicalEndpoint;
            virtualPort = virtualEndpoint;
        }

        return await DisposePairAsync(physical, virtualPort).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Exception>> DisposePairAsync(
        IByteBridgeEndpoint? physical,
        IByteBridgeEndpoint? virtualPort)
    {
        var failures = new List<Exception>();
        if (physical is not null)
        {
            try { await physical.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        if (virtualPort is not null)
        {
            try { await virtualPort.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { failures.Add(exception); }
        }
        return failures.AsReadOnly();
    }

    private void SetState(CommanderBridgeState value)
    {
        lock (stateGate) state = value;
    }

    private void SetFault(Exception exception)
    {
        lock (stateGate)
        {
            fault = $"{exception.GetType().Name}: {exception.Message}";
            state = CommanderBridgeState.Faulted;
        }
    }

    private void CompleteRawCaptureObservers()
    {
        try { commanderCapture?.Complete(); }
        finally
        {
            try { radioCapture?.Complete(); }
            finally
            {
                commanderCapture = null;
                radioCapture = null;
            }
        }
    }
}
