using System.Globalization;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Ipc;

public sealed record PassiveTelemetrySnapshot(
    int? ProfileId, string ServerInstanceId, string ProfileGeneration, string OwnershipEpoch,
    string State, string? Fault, bool Enabled, bool SafetyConfirmation = false,
    string Implementation = "offlinePassiveLifecyclePreparation");

/// <summary>Default-off capture lifecycle. Enabling is restricted to explicit simulation composition.</summary>
public sealed class PassiveTelemetryLifecycle : IAsyncDisposable
{
    private readonly bool simulationAllowed;
    private readonly IMonotonicClock clock;
    private readonly object gate = new();
    private readonly SemaphoreSlim reconfiguration = new(1, 1);
    private SimulatedBridgeReceiveObserver? observer;
    private ProfileConfig? profile;
    private CommanderBridgeState bridgeState;
    private string identity = Guid.NewGuid().ToString("N");
    private long generation;
    private long epoch;
    private string state = "Off";
    private string? fault;
    private bool disposed;

    internal PassiveTelemetryLifecycle(bool simulationAllowed, IMonotonicClock clock)
    {
        this.simulationAllowed = simulationAllowed;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool IsSimulation => simulationAllowed;

    public PassiveTelemetrySnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                var currentFault = state == "Observing" ? observer?.Journal.Fault ?? fault : fault;
                return new(profile?.Id, identity, Decimal(generation), Decimal(epoch),
                    currentFault is null ? state : "Faulted", currentFault,
                    state == "Observing" && currentFault is null);
            }
        }
    }

    public SimulatedReceiveJournal? SimulationJournal
    {
        get { lock (gate) return state == "Observing" ? observer?.Journal : null; }
    }

    internal ReceiveObservationBatch ReadObservations(int expectedProfile, string captureId,
        string profileGeneration, string ownershipEpoch, long afterId)
    {
        lock (gate)
        {
            ValidateCapture(expectedProfile, captureId, profileGeneration, ownershipEpoch);
            return observer!.Journal.ReadAfter(afterId);
        }
    }

    internal void ValidateCapture(int expectedProfile, string captureId,
        string profileGeneration, string ownershipEpoch)
    {
        lock (gate)
        {
            if (profile?.Id != expectedProfile || identity != captureId ||
                Decimal(generation) != profileGeneration || Decimal(epoch) != ownershipEpoch)
                throw new InvalidOperationException("IDENTITY_CHANGED: telemetry capture/profile changed.");
            if (state == "Faulted" && fault is not null)
                throw new InvalidDataException($"Receive journal is invalid: {fault}");
            if (state != "Observing" || observer is null)
                throw new InvalidOperationException("TELEMETRY_OFF: capture is not enabled.");
            if (observer.Journal.Fault is { } reason)
                throw new InvalidDataException($"Receive journal is invalid: {reason}");
        }
    }

    internal void BridgeStarted(ProfileConfig selected)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (bridgeState == CommanderBridgeState.Running)
                throw new InvalidOperationException("Stop the bridge before changing the telemetry profile.");
            profile = selected;
            bridgeState = CommanderBridgeState.Running;
            identity = Guid.NewGuid().ToString("N");
            generation = checked(generation + 1);
            epoch = checked(epoch + 1);
            observer?.Invalidate("Capture identity changed.");
            state = "Off";
            fault = null;
        }
    }

    public async Task EnableSimulationAsync()
    {
        await reconfiguration.WaitAsync().ConfigureAwait(false);
        try
        {
            SimulatedBridgeReceiveObserver? old;
            ProfileConfig selected;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!simulationAllowed)
                    throw new InvalidOperationException("Live telemetry enablement is unavailable; use an explicit simulation bridge.");
                if (bridgeState != CommanderBridgeState.Running || profile is null)
                    throw new InvalidOperationException("Telemetry requires an already-running simulation bridge.");
                if (state == "Observing")
                    throw new InvalidOperationException("Disable the previous capture before explicitly enabling another.");
                if (profile.Framing is not (FramingMode.Civ or FramingMode.KenwoodAscii))
                    throw new InvalidOperationException("A supported structural framing mode is required.");
                selected = profile;
                old = observer;
                observer = null;
            }
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (bridgeState != CommanderBridgeState.Running || !ReferenceEquals(profile, selected))
                    throw new InvalidOperationException("Bridge/profile changed while preparing capture; enablement was cancelled.");
                observer = new(selected.Framing, clock);
                identity = Guid.NewGuid().ToString("N");
                epoch = checked(epoch + 1);
                state = "Observing";
                fault = null;
            }
        }
        finally { reconfiguration.Release(); }
    }

    internal void Observe(ReadOnlySpan<byte> chunk)
    {
        lock (gate)
        {
            if (state == "Observing" && bridgeState == CommanderBridgeState.Running)
                observer?.Observe(chunk);
        }
    }

    internal void BridgeEnded(CommanderBridgeState endedState, string? reason)
    {
        lock (gate)
        {
            bridgeState = endedState;
            observer?.Invalidate(reason ?? "Bridge stopped; telemetry is off.");
            state = endedState == CommanderBridgeState.Faulted ? "Faulted" : "Off";
            fault = endedState == CommanderBridgeState.Faulted ? reason : null;
        }
    }

    public async Task DisableAsync()
    {
        await reconfiguration.WaitAsync().ConfigureAwait(false);
        try
        {
            SimulatedBridgeReceiveObserver? old;
            lock (gate)
            {
                old = observer;
                observer = null;
                old?.Invalidate("Telemetry explicitly disabled.");
                state = "Off";
                fault = null;
            }
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
        finally { reconfiguration.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate) disposed = true;
        await DisableAsync().ConfigureAwait(false);
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
}
