namespace ComCon.Core.Routing;

public enum RouterState { DXLabOwned, ExternalOwned, OverrideDxLab, Transitioning, Faulted, Stopped }
public enum RequestOrigin { DXLab, External }
public enum DispatchOutcome { NotDispatched, ReplyReceived, OutcomeUnknown }
/// <summary>Client expectation only; neither value proves physical transaction completion.</summary>
public enum ClientCompletionMode { ReplyExpected, NoReplyExpected }
public enum ClientTransactionSignalKind { Completed, Aborted }
/// <summary>Accepted client signals are advisory and never settle a dispatched transaction.</summary>
public enum ClientSignalReceipt { AcceptedAdvisoryOnly, UnknownTransaction, IdentityMismatch }
public enum RouterCode
{
    Accepted, Pending, Granted, Busy, NotReady, Unauthorized, StaleEpoch,
    QueueExpired, OwnershipChanged, SessionClosed, Stopped, Faulted,
    ReplyReceived, RadioRejected, DuplicateRequest, DeliveryFailed
}
public enum AdapterWriteCompletion { Succeeded, ProvenZeroBytes, Unknown }

/// <summary>
/// Trusted host identity: external input cannot choose DXLab origin. DXLab uses
/// Guid.Empty; external input uses a live OpenSession identity bound by the host
/// to its authenticated connection. No caller-provided process/name is authority.
/// </summary>
public sealed record RequestIdentity(RequestOrigin Origin, Guid SessionId, long RequestId);

/// <summary>
/// A bounded, immutable opaque byte payload. The router does not validate protocol
/// framing, command semantics, or response expectations.
/// </summary>
public sealed class BrokerPayload
{
    private readonly byte[] bytes;

    private BrokerPayload(byte[] bytes) => this.bytes = bytes;

    public static BrokerPayload Create(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 1 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes));
        }

        return new BrokerPayload(bytes.ToArray());
    }

    public byte[] CopyBytes() => bytes.ToArray();
}

public sealed record ClientTransactionEnvelope(
    RequestIdentity Identity, BrokerPayload Payload, ClientCompletionMode CompletionMode);

/// <summary>A client-reported event, distinct from trusted transport completion facts.</summary>
public sealed record ClientTransactionSignal(
    Guid TransactionId, RequestIdentity Identity, ClientTransactionSignalKind Kind);

public sealed record RouterRequest(
    RequestIdentity Identity, BrokerPayload Payload, long OwnershipEpoch,
    ClientCompletionMode CompletionMode, Guid? LeaseId = null)
{
    public ClientTransactionEnvelope Envelope => new(Identity, Payload, CompletionMode);
}

public sealed record DispatchAction(
    Guid TransactionId, ClientTransactionEnvelope Envelope, long OwnershipEpoch,
    long DispatchedMs, long TotalDeadlineMs, long WriteDeadlineMs)
{
    public RequestIdentity Identity => Envelope.Identity;
    public BrokerPayload Payload => Envelope.Payload;
    public ClientCompletionMode CompletionMode => Envelope.CompletionMode;
}

public sealed record CommandResult(
    RequestIdentity Identity, long DispatchedEpoch, DispatchOutcome Outcome,
    RouterCode Code, IReadOnlyList<byte> Reply);

public sealed record LeaseCapability(Guid SessionId, Guid LeaseId, long ExpiresMs, long OwnershipEpoch);
public sealed record ControlResult(RouterCode Code, long OwnershipEpoch, LeaseCapability? Lease = null);

public abstract record RouterNotification;
public sealed record CommandCompleted(CommandResult Result) : RouterNotification;
public sealed record AcquisitionCompleted(Guid SessionId, long RequestId, ControlResult Result) : RouterNotification;
public sealed record RouterStatusChanged(RouterState State, long OwnershipEpoch, string? Fault) : RouterNotification;

/// <summary>
/// A synchronous, non-reentrant sink must reserve bounded delivery capacity.
/// Returning false means critical delivery failed: dispatch is permanently faulted.
/// Exceptions are propagated and also latch the fault. The host must disconnect
/// affected clients and report any undeliverable results; there is no hidden backlog.
/// </summary>
public delegate bool RouterNotificationSink(RouterNotification notification);
