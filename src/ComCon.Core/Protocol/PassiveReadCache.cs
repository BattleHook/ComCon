using ComCon.Core.Routing;

namespace ComCon.Core.Protocol;

public enum CacheReadStatus { Hit, Missing, Stale, Unsupported, ContextMismatch }
public sealed record CacheReadResult(CacheReadStatus Status, byte[]? Response = null,
    long? AgeMs = null, Guid? ObservationId = null);

// One PTT entry per profile; RF context dependence is not verified for emulation.
public sealed class PassiveReadCache
{
    public const int PttMaximumAgeMs = 750;
    private readonly IMonotonicClock clock;
    private CivObservationContext context;
    private HardwareReadObservation? ptt;
    private long lastNow;
    private long invalidatedAtMs = -1;
    private long latestObservationMs = -1;

    public PassiveReadCache(IMonotonicClock clock, CivObservationContext context)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(context);
        this.clock = clock;
        this.context = context;
        lastNow = clock.NowMs;
        if (lastNow < 0) throw new ArgumentOutOfRangeException(nameof(clock));
    }

    public string? LastInvalidationReason { get; private set; }

    public void Invalidate(CivObservationContext newContext, string reason)
    {
        ArgumentNullException.ThrowIfNull(newContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        invalidatedAtMs = Now();
        ptt = null;
        context = newContext;
        LastInvalidationReason = reason;
    }

    public CacheReadStatus Observe(HardwareReadObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var now = Now();
        if (observation.Contract.Kind != CivReadKind.Ptt) return CacheReadStatus.Unsupported;
        if (observation.Context != context) return CacheReadStatus.ContextMismatch;
        if (observation.ObservedMs > now)
            throw new ArgumentException("Observation cannot be from the future.", nameof(observation));
        if (observation.ObservedMs <= invalidatedAtMs)
            return CacheReadStatus.ContextMismatch;
        if (now - observation.ObservedMs >= PttMaximumAgeMs) return CacheReadStatus.Stale;
        if (observation.ObservedMs <= latestObservationMs)
            throw new ArgumentException("Observation must be newer than the retained observation.", nameof(observation));
        ptt = observation;
        latestObservationMs = observation.ObservedMs;
        LastInvalidationReason = null;
        return CacheReadStatus.Hit;
    }

    public CacheReadResult Read(Ic7600ReadContract request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = Now();
        if (request.Kind != CivReadKind.Ptt) return new(CacheReadStatus.Unsupported);
        if (ptt is null) return new(CacheReadStatus.Missing);
        if (ptt.Context != context || ptt.Contract.RadioAddress != request.RadioAddress)
            return new(CacheReadStatus.ContextMismatch);
        var age = now - ptt.ObservedMs;
        if (age >= PttMaximumAgeMs) return new(CacheReadStatus.Stale, AgeMs: age);
        var reply = ptt.CopyResponse();
        reply[2] = request.ControllerAddress;
        return new(CacheReadStatus.Hit, reply, age, ptt.TransactionId);
    }

    private long Now()
    {
        var now = clock.NowMs;
        if (now < lastNow)
        {
            ptt = null;
            LastInvalidationReason = "Monotonic clock moved backwards.";
            throw new InvalidOperationException(LastInvalidationReason);
        }
        lastNow = now;
        return now;
    }
}
