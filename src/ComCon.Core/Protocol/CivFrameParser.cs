using System.Collections.Immutable;
using ComCon.Core.Routing;

namespace ComCon.Core.Protocol;

public abstract record CivParserEvent(long TimestampMs);

public sealed record CivReceiveActivity(long TimestampMs, int ByteCount) : CivParserEvent(TimestampMs);

public sealed record CivFrameReceived(long TimestampMs, ImmutableArray<byte> Frame) : CivParserEvent(TimestampMs);

public sealed record CivParserAnomaly(
    long TimestampMs,
    CivParserAnomalyKind Kind,
    long ObservedCount,
    ImmutableArray<byte> Sample) : CivParserEvent(TimestampMs)
{
    public ImmutableArray<byte> RawBytes { get; init; } = Sample;
    public long? CandidateIdleElapsedMs { get; init; }
    public long? CandidateTotalElapsedMs { get; init; }
}

public sealed record CivUnframedByte(long TimestampMs, byte Value) : CivParserEvent(TimestampMs);

public enum CivParserAnomalyKind
{
    Noise,
    Collision,
    LonePreamble,
    PreambleNormalized,
    PreambleTooLong,
    InteriorFrameDelimiter,
    InteriorCollision,
    DamagedCandidateResynchronized,
    CandidateTooShort,
    CandidateTooLong,
    CandidateExpiredIdle,
    CandidateExpiredTotal,
    IngressOverflow
}

public sealed class CivFrameParser
{
    public const int MinimumFrameLength = 6;
    public const int MaximumFrameLength = 64;
    public const int MaximumIngressBytes = 4096;
    public const int MaximumDiagnosticSampleLength = 16;
    public const long CandidateIdleTimeoutMs = 250;
    public const long CandidateTotalTimeoutMs = 1000;

    private readonly IMonotonicClock clock;
    private readonly Action<CivParserEvent> eventSink;
    private readonly byte[] candidate = new byte[MaximumFrameLength];
    private readonly byte[] noiseSample = new byte[MaximumDiagnosticSampleLength];
    private int candidateLength;
    private bool inCandidate;
    private bool pendingInteriorFe;
    private long pendingInteriorFeMs;
    private long candidateStartedMs;
    private long candidateLastByteMs;
    private int leadingFeCount;
    private long leadingStartedMs;
    private long leadingLastByteMs;
    private long noiseCount;
    private int noiseSampleLength;
    private bool isProcessing;

    public CivFrameParser(IMonotonicClock clock, Action<CivParserEvent> eventSink)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    public bool HasPartialFrame => inCandidate || leadingFeCount > 0;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        EnterProcessing();
        try
        {
            var now = clock.NowMs;
            var events = new List<CivParserEvent>();
            if (bytes.IsEmpty)
            {
                return;
            }

            events.Add(new CivReceiveActivity(now, bytes.Length));
            ExpireCandidate(now, events);
            if (bytes.Length > MaximumIngressBytes)
            {
                FlushNoise(now, events);
                AddAnomaly(
                    now,
                    CivParserAnomalyKind.IngressOverflow,
                    bytes.Length,
                    CopySample(bytes, MaximumDiagnosticSampleLength),
                    events,
                    ImmutableArray.CreateRange(bytes.ToArray()));
                Dispatch(events);
                return;
            }

            foreach (var value in bytes)
            {
                ProcessByte(value, now, events);
            }

            Dispatch(events);
        }
        finally
        {
            isProcessing = false;
        }
    }

    public void Tick()
    {
        EnterProcessing();
        try
        {
            var now = clock.NowMs;
            var events = new List<CivParserEvent>();
            FlushNoise(now, events);
            ExpireCandidate(now, events);

            Dispatch(events);
        }
        finally
        {
            isProcessing = false;
        }
    }

    private long CandidateStartMs => inCandidate ? candidateStartedMs : leadingStartedMs;

    private void ProcessByte(byte value, long now, List<CivParserEvent> events)
    {
        if (inCandidate)
        {
            ProcessCandidateByte(value, now, events);
            return;
        }

        ProcessIdleByte(value, now, events);
    }

    private void ProcessIdleByte(byte value, long now, List<CivParserEvent> events)
    {
        if (leadingFeCount > 0)
        {
            if (value == 0xFE)
            {
                leadingFeCount++;
                leadingLastByteMs = now;
                if (leadingFeCount > MaximumFrameLength)
                {
                    var count = leadingFeCount;
                    var sample = CreateRepeatedSample(0xFE, MaximumDiagnosticSampleLength);
                    ClearLeadingPreamble();
                    AddAnomaly(now, CivParserAnomalyKind.PreambleTooLong, count, sample, events,
                        ImmutableArray.CreateRange(Enumerable.Repeat((byte)0xFE, count)));
                }

                return;
            }

            if (leadingFeCount == 1)
            {
                ClearLeadingPreamble();
                AddAnomaly(
                    now,
                    CivParserAnomalyKind.LonePreamble,
                    1,
                    ImmutableArray.Create((byte)0xFE),
                    events,
                    ImmutableArray.Create((byte)0xFE));
                ProcessIdleByte(value, now, events);
                return;
            }

            var extraPreambleBytes = leadingFeCount - 2;
            var candidateStart = leadingStartedMs;
            var lastPrefixByte = leadingLastByteMs;
            ClearLeadingPreamble();
            StartCandidate(candidateStart, lastPrefixByte);
            if (extraPreambleBytes > 0)
            {
                AddAnomaly(
                    now,
                    CivParserAnomalyKind.PreambleNormalized,
                    extraPreambleBytes,
                    ImmutableArray.Create((byte)0xFE, (byte)0xFE),
                    events,
                    ImmutableArray.CreateRange(Enumerable.Repeat((byte)0xFE, extraPreambleBytes)));
            }

            ProcessCandidateByte(value, now, events);
            return;
        }

        if (value == 0xFE)
        {
            FlushNoise(now, events);
            leadingFeCount = 1;
            leadingStartedMs = now;
            leadingLastByteMs = now;
            return;
        }

        if (value == 0xFC)
        {
            FlushNoise(now, events);
            AddAnomaly(
                now,
                CivParserAnomalyKind.Collision,
                1,
                ImmutableArray.Create(value),
                events);
            return;
        }

        AddNoise(value);
        events.Add(new CivUnframedByte(now, value));
    }

    private void ProcessCandidateByte(byte value, long now, List<CivParserEvent> events)
    {
        if (pendingInteriorFe)
        {
            if (value == 0xFE)
            {
                var sample = GetCandidateSample(0xFE, 0xFE);
                var count = candidateLength + 2;
                var resyncStartedMs = pendingInteriorFeMs;
                ClearCandidate();
                AddAnomaly(
                    now,
                    CivParserAnomalyKind.DamagedCandidateResynchronized,
                    count,
                    sample,
                    events,
                    GetCandidateRaw(0xFE, 0xFE));
                StartCandidate(resyncStartedMs, now);
                return;
            }

            var malformedSample = GetCandidateSample(0xFE);
            var malformedCount = candidateLength + 1;
            ClearCandidate();
            AddAnomaly(
                now,
                CivParserAnomalyKind.InteriorFrameDelimiter,
                malformedCount,
                malformedSample,
                events,
                GetCandidateRaw(0xFE));
            ProcessIdleByte(value, now, events);
            return;
        }

        if (value == 0xFE)
        {
            pendingInteriorFe = true;
            pendingInteriorFeMs = now;
            candidateLastByteMs = now;
            return;
        }

        if (value == 0xFC)
        {
            var sample = GetCandidateSample(value);
            var count = candidateLength + 1;
            ClearCandidate();
            AddAnomaly(now, CivParserAnomalyKind.InteriorCollision, count, sample, events,
                GetCandidateRaw(value));
            return;
        }

        if (value == 0xFD)
        {
            if (candidateLength == MaximumFrameLength)
            {
                var sample = GetCandidateSample(value);
                var count = candidateLength + 1;
                ClearCandidate();
                AddAnomaly(now, CivParserAnomalyKind.CandidateTooLong, count, sample, events,
                    GetCandidateRaw(value));
                return;
            }

            candidate[candidateLength++] = value;
            candidateLastByteMs = now;
            if (candidateLength < MinimumFrameLength)
            {
                var sample = GetCandidateSample();
                var raw = GetCandidateRaw();
                var count = candidateLength;
                ClearCandidate();
                AddAnomaly(now, CivParserAnomalyKind.CandidateTooShort, count, sample, events, raw);
                return;
            }

            var frame = ImmutableArray.CreateRange(candidate.AsSpan(0, candidateLength).ToArray());
            ClearCandidate();
            events.Add(new CivFrameReceived(now, frame));
            return;
        }

        if (candidateLength == MaximumFrameLength)
        {
            var sample = GetCandidateSample(value);
            var count = candidateLength + 1;
            ClearCandidate();
            AddAnomaly(now, CivParserAnomalyKind.CandidateTooLong, count, sample, events,
                GetCandidateRaw(value));
            ProcessIdleByte(value, now, events);
            return;
        }

        candidate[candidateLength++] = value;
        candidateLastByteMs = now;
    }

    private void StartCandidate(long startedMs, long lastByteMs)
    {
        inCandidate = true;
        candidateLength = 2;
        candidate[0] = 0xFE;
        candidate[1] = 0xFE;
        candidateStartedMs = startedMs;
        candidateLastByteMs = lastByteMs;
        pendingInteriorFe = false;
    }

    private void AddNoise(byte value)
    {
        if (noiseCount < long.MaxValue)
        {
            noiseCount++;
        }

        if (noiseSampleLength < noiseSample.Length)
        {
            noiseSample[noiseSampleLength++] = value;
        }
    }

    private void FlushNoise(long now, List<CivParserEvent> events)
    {
        if (noiseCount == 0)
        {
            return;
        }

        var sample = ImmutableArray.CreateRange(noiseSample.AsSpan(0, noiseSampleLength).ToArray());
        AddAnomaly(now, CivParserAnomalyKind.Noise, noiseCount, sample, events);
        noiseCount = 0;
        noiseSampleLength = 0;
    }

    private void ExpireCandidate(long now, List<CivParserEvent> events)
    {
        if (!HasPartialFrame)
        {
            return;
        }

        var lastByteMs = inCandidate ? candidateLastByteMs : leadingLastByteMs;
        var idleExpired = HasElapsed(now, lastByteMs, CandidateIdleTimeoutMs);
        var totalExpired = HasElapsed(now, CandidateStartMs, CandidateTotalTimeoutMs);
        if (!idleExpired && !totalExpired)
        {
            return;
        }

        var kind = totalExpired &&
            (!idleExpired || CandidateStartMs + CandidateTotalTimeoutMs <
                lastByteMs + CandidateIdleTimeoutMs)
            ? CivParserAnomalyKind.CandidateExpiredTotal
            : CivParserAnomalyKind.CandidateExpiredIdle;
        var sample = GetPartialFrameSample();
        var count = GetPartialFrameLength();
        var raw = GetPartialFrameRaw();
        var idleElapsedMs = Math.Max(0, now - lastByteMs);
        var totalElapsedMs = Math.Max(0, now - CandidateStartMs);
        ClearPartialFrame();
        events.Add(new CivParserAnomaly(now, kind, count, sample)
        {
            RawBytes = raw,
            CandidateIdleElapsedMs = idleElapsedMs,
            CandidateTotalElapsedMs = totalElapsedMs
        });
    }

    private ImmutableArray<byte> GetPartialFrameSample()
    {
        if (inCandidate)
        {
            return GetCandidateSample(pendingInteriorFe ? new byte[] { 0xFE } : []);
        }

        return CreateRepeatedSample(0xFE, Math.Min(leadingFeCount, MaximumDiagnosticSampleLength));
    }

    private ImmutableArray<byte> GetPartialFrameRaw() =>
        inCandidate
            ? GetCandidateRaw(pendingInteriorFe ? new byte[] { 0xFE } : [])
            : ImmutableArray.CreateRange(Enumerable.Repeat((byte)0xFE, leadingFeCount));

    private int GetPartialFrameLength() =>
        inCandidate ? candidateLength + (pendingInteriorFe ? 1 : 0) : leadingFeCount;

    private ImmutableArray<byte> GetCandidateSample(params byte[] suffix)
    {
        var sampleLength = Math.Min(candidateLength + suffix.Length, MaximumFrameLength);
        var sample = new byte[sampleLength];
        var candidateCopyLength = Math.Min(candidateLength, sampleLength);
        candidate.AsSpan(0, candidateCopyLength).CopyTo(sample);
        var suffixCopyLength = sampleLength - candidateCopyLength;
        if (suffixCopyLength > 0)
        {
            suffix.AsSpan(0, suffixCopyLength).CopyTo(sample.AsSpan(candidateCopyLength));
        }

        return ImmutableArray.CreateRange(sample);
    }

    private ImmutableArray<byte> GetCandidateRaw(params byte[] suffix)
    {
        var raw = new byte[candidateLength + suffix.Length];
        candidate.AsSpan(0, candidateLength).CopyTo(raw);
        suffix.AsSpan().CopyTo(raw.AsSpan(candidateLength));
        return ImmutableArray.CreateRange(raw);
    }

    private static ImmutableArray<byte> CopySample(ReadOnlySpan<byte> bytes, int maximumLength) =>
        ImmutableArray.CreateRange(bytes[..Math.Min(bytes.Length, maximumLength)].ToArray());

    private static ImmutableArray<byte> CreateRepeatedSample(byte value, int length)
    {
        var sample = new byte[length];
        Array.Fill(sample, value);
        return ImmutableArray.CreateRange(sample);
    }

    private static void AddAnomaly(
        long now,
        CivParserAnomalyKind kind,
        long observedCount,
        ImmutableArray<byte> sample,
        List<CivParserEvent> events,
        ImmutableArray<byte> rawBytes = default) =>
        events.Add(new CivParserAnomaly(now, kind, observedCount, sample)
        {
            RawBytes = rawBytes.IsDefault ? sample : rawBytes
        });

    private void ClearPartialFrame()
    {
        ClearCandidate();
        ClearLeadingPreamble();
    }

    private void ClearCandidate()
    {
        inCandidate = false;
        candidateLength = 0;
        pendingInteriorFe = false;
    }

    private void ClearLeadingPreamble()
    {
        leadingFeCount = 0;
    }

    private void Dispatch(List<CivParserEvent> events)
    {
        foreach (var parserEvent in events)
        {
            eventSink(parserEvent);
        }
    }

    private void EnterProcessing()
    {
        if (isProcessing)
        {
            throw new InvalidOperationException("CivFrameParser does not allow reentrant calls.");
        }

        isProcessing = true;
    }

    private static bool HasElapsed(long now, long startedMs, long thresholdMs) =>
        now >= startedMs && now - startedMs >= thresholdMs;
}
