using ComCon.Core.Routing;

namespace ComCon.Core.Transport;

public enum TransportEndpoint { Physical, VirtualClient }
public enum TransportEventKind
{
    WriteSettled, WriteProvenZero, WriteOutcomeUnknown, BytesObserved,
    FramingProgress, RecordObserved, FramingAnomaly, Overflow,
    DeadlineElapsed, EndpointDisconnected, IoFault, DeliveryFailed
}
public enum TransportDeadline { Queue, Write, Transaction, Transition }
public enum TransportFaultCategory { Framing, Overflow, Disconnected, Io, Delivery }

public sealed record TransportEventContext(
    int ProfileId, TransportEndpoint Endpoint, long Generation,
    long Sequence, long OwnershipEpoch, long ObservedMs);

/// <summary>Immutable host observation. No event asserts radio completion or RX attribution.</summary>
public sealed class TransportEvent
{
    private readonly byte[] bytes;

    private TransportEvent(
        TransportEventContext context, TransportEventKind kind, byte[] bytes,
        Guid? transactionId = null, RequestIdentity? identity = null,
        TransportDeadline? deadline = null, TransportFaultCategory? faultCategory = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Kind = kind;
        this.bytes = bytes;
        TransactionId = transactionId;
        Identity = identity;
        Deadline = deadline;
        FaultCategory = faultCategory;
    }

    public TransportEventContext Context { get; }
    public TransportEventKind Kind { get; }
    public Guid? TransactionId { get; }
    public RequestIdentity? Identity { get; }
    public TransportDeadline? Deadline { get; }
    public TransportFaultCategory? FaultCategory { get; }
    public int ByteCount => bytes.Length;
    public byte[] CopyBytes() => bytes.ToArray();

    public static TransportEvent Write(
        TransportEventContext context, Guid transactionId,
        RequestIdentity identity, AdapterWriteCompletion completion)
    {
        if (transactionId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(transactionId));
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(identity.Origin) || identity.RequestId <= 0 ||
            (identity.Origin == RequestOrigin.DXLab ? identity.SessionId != Guid.Empty : identity.SessionId == Guid.Empty))
            throw new ArgumentException("Invalid authenticated request identity.", nameof(identity));
        var kind = completion switch
        {
            AdapterWriteCompletion.Succeeded => TransportEventKind.WriteSettled,
            AdapterWriteCompletion.ProvenZeroBytes => TransportEventKind.WriteProvenZero,
            AdapterWriteCompletion.Unknown => TransportEventKind.WriteOutcomeUnknown,
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        return new(context, kind, [], transactionId, identity);
    }

    public static TransportEvent Observe(
        TransportEventContext context, TransportEventKind kind, ReadOnlySpan<byte> bytes)
    {
        var maximum = kind switch
        {
            TransportEventKind.BytesObserved => 4096,
            TransportEventKind.FramingProgress or TransportEventKind.RecordObserved => 256,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        if (bytes.Length < 1 || bytes.Length > maximum)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        return new(context, kind, bytes.ToArray());
    }

    public static TransportEvent DeadlineExpired(TransportEventContext context, TransportDeadline deadline)
    {
        if (!Enum.IsDefined(deadline)) throw new ArgumentOutOfRangeException(nameof(deadline));
        return new(context, TransportEventKind.DeadlineElapsed, [], deadline: deadline);
    }

    public static TransportEvent Fault(TransportEventContext context, TransportFaultCategory category)
    {
        var kind = category switch
        {
            TransportFaultCategory.Framing => TransportEventKind.FramingAnomaly,
            TransportFaultCategory.Overflow => TransportEventKind.Overflow,
            TransportFaultCategory.Disconnected => TransportEventKind.EndpointDisconnected,
            TransportFaultCategory.Io => TransportEventKind.IoFault,
            TransportFaultCategory.Delivery => TransportEventKind.DeliveryFailed,
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
        return new(context, kind, [], faultCategory: category);
    }
}
