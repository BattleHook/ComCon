using ComCon.Core.Routing;

namespace ComCon.Core.Transport;

public enum TransportEventReceipt
{
    Accepted, FaultRecorded, Faulted, WrongProfile, InvalidContext,
    StaleGeneration, WrongEpoch, InvalidOrder, WrongEndpoint,
    WrongDispatch, DuplicateWrite, DeliveryFailed
}

/// <summary>
/// Hardware-free, actor-confined event boundary with synchronous bounded delivery.
/// This is not a serial host, response correlator or recovery/settlement mechanism.
/// </summary>
public sealed class TransportEventStream
{
    private readonly int actorThread = Environment.CurrentManagedThreadId;
    private readonly int profileId;
    private readonly IMonotonicClock clock;
    private readonly Func<TransportEvent, bool> sink;
    private readonly EndpointState[] endpoints;
    private DispatchAction? dispatch;
    private bool writeReported;
    private bool entered;
    private long lastNow;

    public TransportEventStream(
        int profileId, long physicalGeneration, long virtualGeneration,
        long ownershipEpoch, IMonotonicClock clock, Func<TransportEvent, bool> sink)
    {
        if (profileId is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(profileId));
        if (physicalGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(physicalGeneration));
        if (virtualGeneration <= 0) throw new ArgumentOutOfRangeException(nameof(virtualGeneration));
        if (ownershipEpoch <= 0) throw new ArgumentOutOfRangeException(nameof(ownershipEpoch));
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sink);
        this.profileId = profileId;
        this.clock = clock;
        this.sink = sink;
        OwnershipEpoch = ownershipEpoch;
        endpoints = [new(physicalGeneration), new(virtualGeneration)];
    }

    public long OwnershipEpoch { get; }
    public bool IsFaulted => Fault is not null;
    public string? Fault { get; private set; }
    public bool HasUnresolvedDispatch => dispatch is not null;

    public void RegisterDispatch(DispatchAction action)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(action);
            if (IsFaulted || dispatch is not null)
                throw new InvalidOperationException("Faulted stream or unresolved dispatch; no recovery boundary exists.");
            if (action.TransactionId == Guid.Empty || action.OwnershipEpoch != OwnershipEpoch ||
                action.Envelope is null || action.Identity is null || action.Payload is null)
                throw new ArgumentException("Invalid dispatch context.", nameof(action));
            if (!Enum.IsDefined(action.Identity.Origin) || action.Identity.RequestId <= 0 ||
                (action.Identity.Origin == RequestOrigin.DXLab
                    ? action.Identity.SessionId != Guid.Empty : action.Identity.SessionId == Guid.Empty) ||
                !Enum.IsDefined(action.CompletionMode) || action.DispatchedMs < 0 ||
                action.WriteDeadlineMs <= action.DispatchedMs ||
                action.TotalDeadlineMs < action.WriteDeadlineMs)
                throw new ArgumentException("Invalid authenticated dispatch envelope.", nameof(action));
            dispatch = action;
        }
        finally { entered = false; }
    }

    public TransportEventReceipt Report(TransportEvent observation)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(observation);
            if (IsFaulted) return TransportEventReceipt.Faulted;
            var context = observation.Context;
            if (context.ProfileId != profileId) return TransportEventReceipt.WrongProfile;
            if (!Enum.IsDefined(context.Endpoint) || context.Generation <= 0 ||
                context.Sequence <= 0 || context.ObservedMs < 0)
                return Reject(TransportEventReceipt.InvalidContext);
            var endpoint = endpoints[(int)context.Endpoint];
            if (context.Generation != endpoint.Generation) return TransportEventReceipt.StaleGeneration;
            if (context.OwnershipEpoch != OwnershipEpoch) return Reject(TransportEventReceipt.WrongEpoch);
            var now = clock.NowMs;
            if (now < lastNow || now < 0 || context.ObservedMs > now ||
                context.ObservedMs < endpoint.LastObservedMs)
                return Reject(TransportEventReceipt.InvalidContext);
            lastNow = now;
            if (endpoint.Sequence == long.MaxValue || context.Sequence != endpoint.Sequence + 1)
                return Reject(TransportEventReceipt.InvalidOrder);

            var isWrite = observation.Kind is TransportEventKind.WriteSettled or
                TransportEventKind.WriteProvenZero or TransportEventKind.WriteOutcomeUnknown;
            if (isWrite)
            {
                if (context.Endpoint != TransportEndpoint.Physical)
                    return Reject(TransportEventReceipt.WrongEndpoint);
                if (dispatch is null || observation.TransactionId != dispatch.TransactionId ||
                    observation.Identity != dispatch.Identity)
                    return Reject(TransportEventReceipt.WrongDispatch);
                if (context.ObservedMs < dispatch.DispatchedMs)
                    return Reject(TransportEventReceipt.InvalidContext);
                if (writeReported) return Reject(TransportEventReceipt.DuplicateWrite);
                writeReported = true;
            }

            endpoint.Sequence = context.Sequence;
            endpoint.LastObservedMs = context.ObservedMs;
            var faults = observation.Kind is TransportEventKind.WriteProvenZero or
                TransportEventKind.WriteOutcomeUnknown or TransportEventKind.FramingAnomaly or
                TransportEventKind.Overflow or TransportEventKind.EndpointDisconnected or
                TransportEventKind.IoFault or TransportEventKind.DeliveryFailed ||
                (observation.Kind == TransportEventKind.DeadlineElapsed &&
                    observation.Deadline != TransportDeadline.Queue);
            if (faults) Fault = $"Transport event requires blocked dispatch: {observation.Kind}.";
            try
            {
                if (!sink(observation))
                {
                    Fault = "Transport event delivery failed.";
                    return TransportEventReceipt.DeliveryFailed;
                }
            }
            catch
            {
                Fault = "Transport event delivery threw; dispatch is blocked.";
                throw;
            }
            return faults ? TransportEventReceipt.FaultRecorded : TransportEventReceipt.Accepted;
        }
        finally { entered = false; }
    }

    private TransportEventReceipt Reject(TransportEventReceipt receipt)
    {
        Fault = $"Invalid current-generation transport event: {receipt}.";
        return receipt;
    }

    private void Enter()
    {
        if (actorThread != Environment.CurrentManagedThreadId || entered)
            throw new InvalidOperationException("Transport event stream requires serialized non-reentrant actor access.");
        entered = true;
    }

    private sealed class EndpointState(long generation)
    {
        public long Generation { get; } = generation;
        public long Sequence { get; set; }
        public long LastObservedMs { get; set; }
    }
}
