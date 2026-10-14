using ComCon.Core.Routing;

namespace ComCon.Core.Protocol;

public enum CivReadKind { Ptt, RfSetting }
public enum CivReplyKind { Echo, Unrelated, Value, RadioRejected, Anomaly }
public sealed record CivReply(CivReplyKind Kind, int? Value = null, string? Error = null);

// Only the two fixture-backed read contracts are enabled. No controls or generic ACKs.
public sealed class Ic7600ReadContract
{
    private readonly byte[] request;

    internal Ic7600ReadContract(byte[] request, CivReadKind kind)
    {
        this.request = request.ToArray();
        Kind = kind;
    }

    public CivReadKind Kind { get; }
    public byte RadioAddress => request[2];
    public byte ControllerAddress => request[3];
    public byte[] CopyRequest() => request.ToArray();
    public BrokerPayload ToPayload() => BrokerPayload.Create(request);

    public CivReply Classify(ReadOnlySpan<byte> frame)
    {
        if (frame.SequenceEqual(request)) return new(CivReplyKind.Echo);
        if (frame.Length < 6 || frame.Length > 64 || frame[0] != 0xFE ||
            frame[1] != 0xFE || frame[^1] != 0xFD)
            return new(CivReplyKind.Anomaly, Error: "Malformed short CI-V frame.");
        for (var i = 2; i < frame.Length - 1; i++)
        {
            if (frame[i] is 0xFE or 0xFC or 0xFD)
                return new(CivReplyKind.Anomaly, Error: "Interior delimiter or collision.");
        }
        if (frame[2] == RadioAddress && frame[3] == ControllerAddress)
            return new(CivReplyKind.Anomaly, Error: "Unwritten request-direction frame.");
        if (frame[2] != ControllerAddress || frame[3] != RadioAddress)
            return new(CivReplyKind.Unrelated);
        if (frame[4] == 0xFB)
            return new(CivReplyKind.Anomaly, Error: "ACK cannot complete a value read.");
        if (frame[4] == 0xFA)
            return frame.Length == 6
                ? new(CivReplyKind.RadioRejected)
                : new(CivReplyKind.Anomaly, Error: "Invalid rejection shape.");
        if (frame[4] != request[4] || frame[5] != request[5])
            return new(CivReplyKind.Unrelated);
        if (Kind == CivReadKind.Ptt)
            return frame.Length == 8 && frame[6] <= 1
                ? new(CivReplyKind.Value, frame[6])
                : new(CivReplyKind.Anomaly, Error: "PTT must be exactly one 00/01 byte.");
        if (frame.Length != 9 || !TryBcd(frame[6], out var high) ||
            !TryBcd(frame[7], out var low) || high * 100 + low > 255)
            return new(CivReplyKind.Anomaly, Error: "RF setting must be two BCD bytes in 0-255.");
        return new(CivReplyKind.Value, high * 100 + low);
    }

    private static bool TryBcd(byte value, out int decoded)
    {
        decoded = (value >> 4) * 10 + (value & 15);
        return (value >> 4) <= 9 && (value & 15) <= 9;
    }
}

public static class Ic7600ReadPolicy
{
    public static Ic7600ReadContract ValidateRequest(
        ReadOnlySpan<byte> frame, byte radioAddress, byte controllerAddress)
    {
        ValidateAddresses(radioAddress, controllerAddress);
        if (frame.Length != 7 || frame[0] != 0xFE || frame[1] != 0xFE ||
            frame[2] != radioAddress || frame[3] != controllerAddress || frame[6] != 0xFD)
            throw new ArgumentException("Expected an exact addressed seven-byte read.", nameof(frame));
        var kind = (frame[4], frame[5]) switch
        {
            (0x1C, 0x00) => CivReadKind.Ptt,
            (0x14, 0x0A) => CivReadKind.RfSetting,
            _ => throw new ArgumentException("Unsupported IC-7600 managed command.", nameof(frame))
        };
        return new(frame.ToArray(), kind);
    }

    internal static void ValidateAddresses(byte radio, byte controller)
    {
        if (radio is 0 or >= 0xF0 || controller is 0 or >= 0xF0 || radio == controller)
            throw new ArgumentException("Distinct nonbroadcast short-frame radio/controller addresses are required.");
    }
}

public sealed record CivObservationContext(
    long ProfileGeneration, long ContextEpoch, string Model, string? Firmware);

public sealed class HardwareReadObservation
{
    private readonly byte[] response;
    internal HardwareReadObservation(Guid transactionId, Ic7600ReadContract contract,
        CivObservationContext context, long observedMs, int value, byte[] response)
    {
        TransactionId = transactionId;
        Contract = contract;
        Context = context;
        ObservedMs = observedMs;
        Value = value;
        this.response = response.ToArray();
    }
    public Guid TransactionId { get; }
    public Ic7600ReadContract Contract { get; }
    public CivObservationContext Context { get; }
    public long ObservedMs { get; }
    public int Value { get; }
    public byte[] CopyResponse() => response.ToArray();
}

// Host supplies physical RX and write facts, never client assertions. This is not a parser.
public sealed class CivReadExchange
{
    private readonly DispatchAction action;
    private readonly Ic7600ReadContract contract;
    private readonly CivObservationContext context;
    private readonly IMonotonicClock clock;
    private bool writeSucceeded;
    private bool writeReported;
    private bool terminal;
    private bool taken;
    private HardwareReadObservation? observation;
    private long lastNow;
    private long? replyDeadline;

    public CivReadExchange(DispatchAction action, Ic7600ReadContract contract,
        CivObservationContext context, IMonotonicClock clock)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        if (action.TransactionId == Guid.Empty ||
            !action.Payload.CopyBytes().AsSpan().SequenceEqual(contract.CopyRequest()) ||
            context.ProfileGeneration < 0 || context.ContextEpoch < 0 ||
            context.Model != "IC-7600")
            throw new ArgumentException("Dispatch, contract and observation context must match.");
        this.action = action;
        this.contract = contract;
        this.context = context;
        this.clock = clock;
        lastNow = action.DispatchedMs;
    }

    public bool IsTainted { get; private set; }
    public string? Fault { get; private set; }
    public bool IsResolved => writeSucceeded && terminal && !IsTainted;

    public CivReply Receive(ReadOnlySpan<byte> frame)
    {
        var now = Now();
        if (IsTainted) return new(CivReplyKind.Anomaly, Error: Fault);
        if (!terminal && (now >= action.TotalDeadlineMs ||
            (replyDeadline is { } deadline && now >= deadline)))
        {
            Taint("Reply reached an expired transaction.");
            return new(CivReplyKind.Anomaly, Error: Fault);
        }
        var result = contract.Classify(frame);
        if (result.Kind == CivReplyKind.Anomaly) Taint(result.Error!);
        if (result.Kind is CivReplyKind.Value or CivReplyKind.RadioRejected)
        {
            if (terminal)
            {
                Taint("Duplicate terminal reply.");
                return new(CivReplyKind.Anomaly, Error: Fault);
            }
            terminal = true;
            if (result.Kind == CivReplyKind.Value)
                observation = new(action.TransactionId, contract, context, now,
                    result.Value!.Value, frame.ToArray());
        }
        return result;
    }

    public void CompleteWrite(AdapterWriteCompletion completion)
    {
        var now = Now();
        if (writeReported || now >= action.WriteDeadlineMs ||
            now >= action.TotalDeadlineMs || completion != AdapterWriteCompletion.Succeeded)
            Taint("Duplicate, expired or unsuccessful hardware write.");
        writeReported = true;
        writeSucceeded = !IsTainted && completion == AdapterWriteCompletion.Succeeded;
        if (writeSucceeded) replyDeadline = Math.Min(action.TotalDeadlineMs, checked(now + 250));
    }

    public void Tick()
    {
        var now = Now();
        if (IsResolved || IsTainted) return;
        if (now >= action.TotalDeadlineMs ||
            (!writeReported && now >= action.WriteDeadlineMs) ||
            (!terminal && replyDeadline is { } deadline && now >= deadline))
            Taint("Hardware transaction deadline expired.");
    }

    public void Taint(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        IsTainted = true;
        Fault ??= reason;
        observation = null;
    }

    public HardwareReadObservation? TakeObservation()
    {
        if (!IsResolved || taken || observation is null) return null;
        taken = true;
        return observation;
    }

    private long Now()
    {
        var now = clock.NowMs;
        if (now < lastNow)
        {
            Taint("Monotonic clock moved backwards.");
            throw new InvalidOperationException(Fault);
        }
        lastNow = now;
        return now;
    }
}
