using ComCon.Core.Configuration;
using ComCon.Core.Models;

namespace ComCon.Core.Routing;

/// <summary>
/// Hardware-free, actor-confined router. Construct and call on one serialized
/// thread; cross-thread and reentrant calls throw. Tick regularly even when idle
/// so leases, queued buffers and adapter deadlines expire. TryDispatch registers
/// the transaction before returning the only write action.
/// Process currently available adapter input/disconnect facts before Tick or
/// TryDispatch, which may commit handover. RX callbacks themselves never grant
/// ownership before applying the newly reported activity/partial-frame fact.
///
/// Sessions are server-created connection identities, never client-chosen names.
/// The host supplies validated adapter RX/write facts, never raw client assertions.
/// Command request IDs must be fresh, positive and increasing per source/session. Full
/// duplicate attach/replay/conflict handling belongs to the future pipe boundary.
/// This includes control-operation deduplication. The core rejects old command IDs
/// without redispatch and retains no response history.
///
/// Faults are latched for this instance. Stop cannot certify resynchronization.
/// No adapter-verified recovery capability exists yet; replace only after a future
/// explicitly verified recovery procedure, not after quiet/flush/reopen alone.
/// </summary>
public sealed class ProfileRouter
{
    private readonly IMonotonicClock clock;
    private readonly RoutingLimits limits;
    private readonly RouterNotificationSink sink;
    private readonly BoundedRequestQueue<RouterRequest> queue;
    private readonly Dictionary<Guid, long> sessions = [];
    private readonly Dictionary<string, int> pendingCounts = [];
    private readonly int actorThread = Environment.CurrentManagedThreadId;
    private bool entered;
    private bool poisoned;
    private bool deliveryFailed;
    private bool partialRx;
    private bool overrideEnabled;
    private long lastNow;
    private long dxLabHighWater;
    private long quietSince;
    private long transitionStarted;
    private RouterState target;
    private Guid? pendingSession;
    private long pendingAcquireId;
    private LeaseCapability? lease;
    private Transaction? transaction;

    public ProfileRouter(IMonotonicClock clock, RoutingLimits limits, RouterNotificationSink sink)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(sink);
        var errors = ConfigurationValidator.Validate(AppSettings.CreateDefault() with { RoutingLimits = limits });
        if (errors.Count != 0)
        {
            throw new ConfigurationValidationException(errors);
        }
        if (limits.PendingRequestsPerClient > limits.NormalQueueCapacityPerProfile ||
            limits.CatTransactionTimeoutMs > 500 ||
            limits.TransitionTimeoutMs < limits.CatTransactionTimeoutMs + limits.QuietGuardMs)
        {
            throw new ArgumentException("Queue capacities and transaction/guard/transition budgets are incoherent.", nameof(limits));
        }
        this.clock = clock;
        this.limits = limits;
        this.sink = sink;
        queue = new(limits.NormalQueueCapacityPerProfile, limits.PendingRequestsPerClient);
    }

    public RouterState State { get; private set; } = RouterState.Stopped;
    public long OwnershipEpoch { get; private set; }
    public string? Fault { get; private set; }
    public int QueuedCount => queue.Count;
    public bool HasOutstandingTransaction => transaction is not null;

    public ControlResult Start(bool adapterVerifiedReady)
    {
        using var call = Enter();
        var now = Advance();
        if (poisoned) return Control(RouterCode.Faulted);
        if (State != RouterState.Stopped) return Control(RouterCode.Busy);
        if (!adapterVerifiedReady) return Control(RouterCode.NotReady);
        partialRx = false;
        quietSince = now;
        OwnershipEpoch++;
        SetState(RouterState.DXLabOwned);
        return Control(deliveryFailed ? RouterCode.DeliveryFailed : RouterCode.Accepted);
    }

    public Guid OpenSession()
    {
        using var call = Enter();
        Advance();
        if (State is RouterState.Stopped or RouterState.Faulted ||
            sessions.Count >= limits.ConnectionsPerProfile)
        {
            throw new InvalidOperationException("Profile unavailable or session capacity reached.");
        }
        var id = Guid.NewGuid();
        sessions.Add(id, 0);
        return id;
    }

    public void CloseSession(Guid sessionId)
    {
        using var call = Enter();
        var now = Advance();
        if (!sessions.Remove(sessionId)) return;
        RejectQueue(request => request.Value.Identity.SessionId == sessionId, RouterCode.SessionClosed);
        if (pendingSession == sessionId)
        {
            CancelAcquire(RouterCode.SessionClosed);
            if (State == RouterState.Transitioning) target = DesiredDefault;
        }
        if (lease?.SessionId == sessionId)
        {
            lease = null;
            RevokeOwner(now);
        }
    }

    public ControlResult Acquire(Guid sessionId, long requestId)
    {
        using var call = Enter();
        var now = Advance();
        if (!sessions.ContainsKey(sessionId) || requestId <= 0) return Control(RouterCode.Unauthorized);
        if (State is RouterState.Faulted or RouterState.Stopped) return Control(RouterCode.NotReady);
        if (pendingSession is not null || lease is not null || State != RouterState.DXLabOwned)
            return Control(RouterCode.Busy);
        pendingSession = sessionId;
        pendingAcquireId = requestId;
        BeginTransition(RouterState.ExternalOwned, now);
        return Control(deliveryFailed ? RouterCode.DeliveryFailed : RouterCode.Pending);
    }

    public ControlResult Renew(Guid sessionId, Guid leaseId)
    {
        using var call = Enter();
        var now = Advance();
        if (!IsHolder(sessionId, leaseId)) return Control(RouterCode.Unauthorized);
        lease = lease! with { ExpiresMs = now + limits.LeaseDurationMs, OwnershipEpoch = OwnershipEpoch };
        return new(RouterCode.Accepted, OwnershipEpoch, lease);
    }

    public ControlResult Release(Guid sessionId, Guid leaseId)
    {
        using var call = Enter();
        var now = Advance();
        if (!IsHolder(sessionId, leaseId)) return Control(RouterCode.Unauthorized);
        lease = null;
        RevokeOwner(now);
        return Control(deliveryFailed ? RouterCode.DeliveryFailed : RouterCode.Accepted);
    }

    /// <summary>Operator API only. Future UI/IPC host must authenticate permission before calling.</summary>
    public ControlResult SetOverride(bool enabled, bool operatorAuthorized)
    {
        using var call = Enter();
        var now = Advance();
        if (!operatorAuthorized) return Control(RouterCode.Unauthorized);
        if (State is RouterState.Faulted or RouterState.Stopped) return Control(RouterCode.NotReady);
        if (overrideEnabled == enabled) return Control(RouterCode.Accepted);
        overrideEnabled = enabled;
        CancelAcquire(RouterCode.OwnershipChanged);
        BeginTransition(enabled ? RouterState.OverrideDxLab :
            lease is not null ? RouterState.ExternalOwned : RouterState.DXLabOwned, now);
        return Control(deliveryFailed ? RouterCode.DeliveryFailed : RouterCode.Accepted);
    }

    public RouterCode Submit(RouterRequest request)
    {
        using var call = Enter();
        var now = Advance();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Identity);
        ArgumentNullException.ThrowIfNull(request.Payload);
        if (!Enum.IsDefined(request.CompletionMode))
            throw new ArgumentOutOfRangeException(nameof(request), "Invalid client completion mode.");
        if (!Enum.IsDefined(request.Identity.Origin) || request.Identity.RequestId <= 0 ||
            (request.Identity.Origin == RequestOrigin.DXLab && request.Identity.SessionId != Guid.Empty))
            throw new ArgumentException("Invalid request identity.", nameof(request));

        var code = ConsumeId(request.Identity);
        if (code == RouterCode.Accepted) code = Authority(request);
        if (code == RouterCode.Accepted)
        {
            var pendingForClient = PendingForClient(request.Identity);
            code = queue.Count + (transaction is null ? 0 : 1) >= limits.NormalQueueCapacityPerProfile ||
                pendingForClient >= limits.PendingRequestsPerClient
                ? RouterCode.Busy
                : queue.Enqueue(ClientKey(request.Identity), now, request) == QueueAdmission.Accepted
                    ? RouterCode.Accepted : RouterCode.Busy;
            if (code == RouterCode.Accepted)
            {
                var key = ClientKey(request.Identity);
                pendingCounts[key] = pendingCounts.GetValueOrDefault(key) + 1;
            }
        }
        if (code != RouterCode.Accepted) Complete(request, DispatchOutcome.NotDispatched, code);
        return deliveryFailed ? RouterCode.DeliveryFailed : code;
    }

    public DispatchAction? TryDispatch()
    {
        using var call = Enter();
        var now = Advance(commitTransition: true);
        if (transaction is not null || State is RouterState.Stopped or RouterState.Faulted or RouterState.Transitioning)
            return null;
        while (queue.Dequeue() is { } item)
        {
            RemovePending(item.ClientId);
            var code = Authority(item.Value);
            if (code != RouterCode.Accepted)
            {
                Complete(item.Value, DispatchOutcome.NotDispatched, code);
                if (State == RouterState.Faulted) return null;
                continue;
            }
            var action = new DispatchAction(Guid.NewGuid(), item.Value.Envelope,
                item.Value.OwnershipEpoch, now,
                now + limits.CatTransactionTimeoutMs, now + limits.CatWriteTimeoutMs);
            transaction = new(item.Value, action);
            return action;
        }
        return null;
    }

    /// <summary>
    /// Accepts a matching client event as an advisory fact only. The signal is not
    /// a transaction-settlement or ownership input; normal clock/deadline checks
    /// still run when the event is reported.
    /// </summary>
    public ClientSignalReceipt ReportClientSignal(ClientTransactionSignal signal)
    {
        using var call = Enter();
        Advance();
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(signal.Identity);
        if (!Enum.IsDefined(signal.Kind))
            throw new ArgumentOutOfRangeException(nameof(signal), "Invalid client transaction signal.");
        if (!Match(signal.TransactionId)) return ClientSignalReceipt.UnknownTransaction;
        if (transaction!.Request.Identity != signal.Identity)
            return ClientSignalReceipt.IdentityMismatch;
        return ClientSignalReceipt.AcceptedAdvisoryOnly;
    }

    public void WriteCompleted(Guid transactionId, AdapterWriteCompletion completion)
    {
        using var call = Enter();
        var now = Advance();
        if (!Enum.IsDefined(completion)) throw new ArgumentOutOfRangeException(nameof(completion));
        if (!Match(transactionId) || transaction!.WriteSettled)
        {
            Fail("Orphan, late or duplicate write completion.");
            return;
        }
        var active = transaction!;
        if (completion != AdapterWriteCompletion.Succeeded)
        {
            // Even a zero-byte write failure cannot make an already received reply attributable.
            Fail("Adapter write did not complete successfully.",
                completion == AdapterWriteCompletion.ProvenZeroBytes && active.Reply is null
                    ? DispatchOutcome.NotDispatched : DispatchOutcome.OutcomeUnknown);
            return;
        }
        active.WriteSettled = true;
        active.ReplyDeadline = Math.Min(active.Action.TotalDeadlineMs, now + limits.CatReplyTimeoutMs);
        Settle(now);
    }

    /// <summary>
    /// Simulated host fact only. Physical response correlation and model-specific
    /// validation are outside this router and remain unresolved for live hosting.
    /// </summary>
    public void ReplyReceived(Guid transactionId, ReadOnlySpan<byte> reply, bool radioRejected = false)
    {
        using var call = Enter();
        var now = Advance();
        if (!Match(transactionId) || transaction!.Reply is not null)
        {
            Fail("Orphan, late or duplicate terminal reply.");
            return;
        }
        if (transaction.Action.CompletionMode == ClientCompletionMode.NoReplyExpected)
        {
            Fail("A reply arrived for a no-reply transaction; attribution is ambiguous.");
            return;
        }
        if (reply.Length is < 1 or > 256)
        {
            Fail("Invalid terminal response size.");
            return;
        }
        quietSince = now;
        transaction!.Reply = Array.AsReadOnly(reply.ToArray());
        transaction.RadioRejected = radioRejected;
        Settle(now);
    }

    /// <summary>
    /// Report every physical RX activity and the adapter's persistent partial-frame state.
    /// Unrelated/echo/unsolicited bytes do not complete a transaction. Taint and orphan
    /// terminals must be reported through AdapterFault, not as harmless RX.
    /// </summary>
    public void ReceiveActivity(bool hasPartialFrame)
    {
        using var call = Enter();
        var now = Advance();
        quietSince = now;
        partialRx = hasPartialFrame;
    }

    public void AdapterFault(string reason)
    {
        using var call = Enter();
        Advance();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Fail(reason);
    }

    public void Tick()
    {
        using var call = Enter();
        Advance(commitTransition: true);
    }

    public void Stop()
    {
        using var call = Enter();
        Advance();
        if (transaction is not null) Fail("Stopped with an unsettled dispatched transaction.");
        CancelAcquire(RouterCode.Stopped);
        lease = null;
        sessions.Clear();
        overrideEnabled = false;
        OwnershipEpoch++;
        RejectQueue(_ => true, RouterCode.Stopped);
        SetState(RouterState.Stopped);
    }

    private RouterState DesiredDefault => overrideEnabled ? RouterState.OverrideDxLab : RouterState.DXLabOwned;
    private ControlResult Control(RouterCode code) => new(code, OwnershipEpoch);
    private bool IsHolder(Guid sessionId, Guid leaseId) =>
        sessions.ContainsKey(sessionId) && lease?.SessionId == sessionId && lease.LeaseId == leaseId;
    private bool Match(Guid id) => !poisoned && transaction?.Action.TransactionId == id;
    private static string ClientKey(RequestIdentity identity) =>
        identity.Origin == RequestOrigin.DXLab ? "DXLab" : identity.SessionId.ToString();

    private long Advance(bool commitTransition = false)
    {
        var now = clock.NowMs;
        if (now < lastNow || now < 0 || now > long.MaxValue - 10_000)
        {
            Fail("Invalid monotonic clock.");
            throw new InvalidOperationException("Clock must be nonnegative, monotonic and leave deadline headroom.");
        }
        lastNow = now;
        RejectQueue(_ => now - _.EnqueuedMs >= limits.HardwareQueueWaitMs, RouterCode.QueueExpired);
        if (lease is not null && now >= lease.ExpiresMs)
        {
            lease = null;
            RevokeOwner(now);
        }
        if (transaction is { } active &&
            (now >= active.Action.TotalDeadlineMs ||
             (!active.WriteSettled && now >= active.Action.WriteDeadlineMs) ||
             (active.WriteSettled && active.Reply is null && now >= active.ReplyDeadline)))
        {
            Fail("Dispatched transaction deadline expired.");
        }
        if (State == RouterState.Transitioning)
        {
            if (now - transitionStarted >= limits.TransitionTimeoutMs)
            {
                Fail("Ownership transition deadline expired.");
            }
            else if (commitTransition && transaction is null && !partialRx && now - quietSince >= limits.QuietGuardMs)
            {
                CommitTransition(now);
            }
        }
        return now;
    }

    private int PendingForClient(RequestIdentity identity) =>
        pendingCounts.GetValueOrDefault(ClientKey(identity)) +
        (transaction is { } active && ClientKey(active.Request.Identity) == ClientKey(identity) ? 1 : 0);

    private void RemovePending(string clientId)
    {
        if (pendingCounts[clientId] == 1) pendingCounts.Remove(clientId);
        else pendingCounts[clientId]--;
    }

    private RouterCode ConsumeId(RequestIdentity identity)
    {
        if (identity.Origin == RequestOrigin.External)
        {
            if (!sessions.TryGetValue(identity.SessionId, out var previous)) return RouterCode.Unauthorized;
            if (identity.RequestId <= previous) return RouterCode.DuplicateRequest;
            sessions[identity.SessionId] = identity.RequestId;
        }
        else
        {
            if (identity.RequestId <= dxLabHighWater) return RouterCode.DuplicateRequest;
            dxLabHighWater = identity.RequestId;
        }
        return RouterCode.Accepted;
    }

    private RouterCode Authority(RouterRequest request)
    {
        if (poisoned || State == RouterState.Faulted) return RouterCode.Faulted;
        if (State == RouterState.Stopped) return RouterCode.NotReady;
        if (request.OwnershipEpoch != OwnershipEpoch) return RouterCode.StaleEpoch;
        if (State == RouterState.Transitioning) return RouterCode.Busy;
        if (request.Identity.Origin == RequestOrigin.DXLab)
            return State is RouterState.DXLabOwned or RouterState.OverrideDxLab
                ? RouterCode.Accepted : RouterCode.Unauthorized;
        return State == RouterState.ExternalOwned && request.LeaseId is { } id &&
            IsHolder(request.Identity.SessionId, id) ? RouterCode.Accepted : RouterCode.Unauthorized;
    }

    private void RevokeOwner(long now)
    {
        if (State is RouterState.Faulted or RouterState.Stopped) return;
        if (overrideEnabled)
        {
            if (State == RouterState.Transitioning && target == RouterState.ExternalOwned)
                target = RouterState.OverrideDxLab;
            return;
        }
        BeginTransition(RouterState.DXLabOwned, now);
    }

    private void BeginTransition(RouterState newTarget, long now)
    {
        if (poisoned) return;
        if (State != RouterState.Transitioning)
        {
            transitionStarted = now;
            quietSince = now;
        }
        target = newTarget;
        OwnershipEpoch++;
        State = RouterState.Transitioning;
        RejectQueue(_ => true, RouterCode.OwnershipChanged);
        if (!poisoned) SetState(RouterState.Transitioning);
    }

    private void CommitTransition(long now)
    {
        if (target == RouterState.ExternalOwned && pendingSession is { } session)
        {
            if (!sessions.ContainsKey(session))
            {
                CancelAcquire(RouterCode.SessionClosed);
                target = DesiredDefault;
            }
            else
            {
                lease = new(session, Guid.NewGuid(), now + limits.LeaseDurationMs, OwnershipEpoch);
                var id = pendingAcquireId;
                pendingSession = null;
                State = RouterState.ExternalOwned;
                Emit(new AcquisitionCompleted(session, id, new(RouterCode.Granted, OwnershipEpoch, lease)));
            }
        }
        else if (target == RouterState.ExternalOwned && lease is null)
        {
            target = DesiredDefault;
        }
        if (!poisoned) SetState(target);
    }

    private void Settle(long now)
    {
        if (transaction is not { WriteSettled: true, Reply: not null } active) return;
        transaction = null;
        quietSince = now;
        Complete(active.Request, DispatchOutcome.ReplyReceived,
            active.RadioRejected ? RouterCode.RadioRejected : RouterCode.ReplyReceived, active.Reply);
    }

    private void CancelAcquire(RouterCode code)
    {
        if (pendingSession is not { } session) return;
        pendingSession = null;
        Emit(new AcquisitionCompleted(session, pendingAcquireId, Control(code)));
    }

    private void RejectQueue(Predicate<QueuedRequest<RouterRequest>> predicate, RouterCode code)
    {
        var removed = queue.RemoveWhere(predicate);
        foreach (var item in removed) RemovePending(item.ClientId);
        foreach (var item in removed)
            Complete(item.Value, DispatchOutcome.NotDispatched, code);
    }

    private void Complete(RouterRequest request, DispatchOutcome outcome, RouterCode code,
        IReadOnlyList<byte>? reply = null) =>
        Emit(new CommandCompleted(new(request.Identity, request.OwnershipEpoch, outcome, code,
            reply ?? Array.Empty<byte>())));

    private void SetState(RouterState state)
    {
        State = state;
        Emit(new RouterStatusChanged(State, OwnershipEpoch, Fault));
    }

    private void Fail(string reason, DispatchOutcome outcome = DispatchOutcome.OutcomeUnknown)
    {
        if (poisoned) return;
        poisoned = true;
        Fault = reason;
        State = RouterState.Faulted;
        OwnershipEpoch++;
        lease = null;
        var active = transaction;
        transaction = null;
        var cancelledSession = pendingSession;
        var cancelledId = pendingAcquireId;
        pendingSession = null;
        var rejected = queue.RemoveWhere(_ => true);
        foreach (var item in rejected) RemovePending(item.ClientId);
        sessions.Clear();
        // Release all engine-owned work before invoking a potentially failing sink.
        if (active is not null) Complete(active.Request, outcome, RouterCode.Faulted);
        if (cancelledSession is { } session)
            Emit(new AcquisitionCompleted(session, cancelledId, Control(RouterCode.Faulted)));
        foreach (var item in rejected)
            Complete(item.Value, DispatchOutcome.NotDispatched, RouterCode.Faulted);
        Emit(new RouterStatusChanged(State, OwnershipEpoch, Fault));
    }

    private void Emit(RouterNotification notification)
    {
        if (deliveryFailed) return;
        var delivered = false;
        try
        {
            delivered = sink(notification);
        }
        finally
        {
            if (!delivered)
            {
                deliveryFailed = true;
                Fail("Critical result/status delivery failed.");
            }
        }
    }

    private CallScope Enter()
    {
        if (Environment.CurrentManagedThreadId != actorThread || entered)
            throw new InvalidOperationException("Router calls must be serialized on its actor thread, without reentrancy.");
        entered = true;
        return new(this);
    }

    private readonly struct CallScope(ProfileRouter router) : IDisposable
    {
        public void Dispose() => router.entered = false;
    }

    private sealed class Transaction(RouterRequest request, DispatchAction action)
    {
        public RouterRequest Request { get; } = request;
        public DispatchAction Action { get; } = action;
        public bool WriteSettled { get; set; }
        public long ReplyDeadline { get; set; }
        public IReadOnlyList<byte>? Reply { get; set; }
        public bool RadioRejected { get; set; }
    }
}
