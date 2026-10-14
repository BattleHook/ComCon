using System.Collections.Immutable;
using ComCon.Core.Routing;

namespace ComCon.Core.Protocol;

public abstract record KenwoodAsciiParserEvent(long TimestampMs);

public sealed record KenwoodAsciiReceiveActivity(long TimestampMs, int ByteCount)
    : KenwoodAsciiParserEvent(TimestampMs);

public sealed record KenwoodAsciiRecordReceived(long TimestampMs, ImmutableArray<byte> Record)
    : KenwoodAsciiParserEvent(TimestampMs);

public sealed record KenwoodAsciiParserAnomaly(
    long TimestampMs,
    KenwoodAsciiParserAnomalyKind Kind,
    long ObservedCount,
    ImmutableArray<byte> Sample) : KenwoodAsciiParserEvent(TimestampMs)
{
    public ImmutableArray<byte> RawBytes { get; init; } = Sample;
}

public sealed record KenwoodRejectedByte(long TimestampMs, byte Value) : KenwoodAsciiParserEvent(TimestampMs);

public enum KenwoodAsciiParserAnomalyKind
{
    InvalidByte,
    RecordTooLong,
    PartialRecordExpiredIdle,
    PartialRecordExpiredTotal,
    IngressOverflow
}

public delegate bool KenwoodAsciiParserSink(KenwoodAsciiParserEvent parserEvent);

public sealed class KenwoodAsciiFrameParser
{
    public const int MaximumIngressBytes = 4096;
    public const int MaximumRecordLength = 256;
    public const int MaximumDiagnosticSampleLength = 16;
    public const long PartialRecordIdleTimeoutMs = 500;
    public const long PartialRecordTotalTimeoutMs = 2000;

    private readonly IMonotonicClock clock;
    private readonly KenwoodAsciiParserSink eventSink;
    private readonly byte[] record = new byte[MaximumRecordLength - 1];
    private readonly byte[] diagnosticSample = new byte[MaximumDiagnosticSampleLength];
    private int recordLength;
    private int diagnosticSampleLength;
    private long recordByteCount;
    private long recordStartedMs;
    private long recordLastByteMs;
    private bool hasPartialRecord;
    private bool discardingRecord;
    private bool discardingIngressOverflow;
    private bool isProcessing;
    private bool isFaulted;

    public KenwoodAsciiFrameParser(IMonotonicClock clock, KenwoodAsciiParserSink eventSink)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    public bool HasPartialRecord => hasPartialRecord;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        EnterProcessing();
        try
        {
            var now = clock.NowMs;
            var events = new List<KenwoodAsciiParserEvent>();
            if (bytes.IsEmpty)
            {
                return;
            }

            events.Add(new KenwoodAsciiReceiveActivity(now, bytes.Length));
            if (bytes.Length > MaximumIngressBytes)
            {
                var boundedSample = CopySample(bytes);
                AddAnomaly(
                    now,
                    KenwoodAsciiParserAnomalyKind.IngressOverflow,
                    bytes.Length,
                    boundedSample,
                    events,
                    boundedSample);
                EnterQuarantine(now);
                Dispatch(events);
                return;
            }

            ExpirePartialRecord(now, events);
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
            var events = new List<KenwoodAsciiParserEvent>();
            ExpirePartialRecord(clock.NowMs, events);
            Dispatch(events);
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void ProcessByte(byte value, long now, List<KenwoodAsciiParserEvent> events)
    {
        if (!hasPartialRecord)
        {
            StartRecord(now);
        }

        recordLastByteMs = now;
        AddDiagnosticByte(value);
        recordByteCount = SaturatingIncrement(recordByteCount);

        if (discardingRecord)
        {
            events.Add(new KenwoodRejectedByte(now, value));
            if (value == (byte)';')
            {
                ClearPartialRecord();
            }
            else if (!IsPrintableAscii(value))
            {
                AddInvalidByte(now, value, events, []);
            }

            return;
        }

        if (!IsPrintableAscii(value))
        {
            discardingRecord = true;
            var raw = new byte[recordLength + 1];
            record.AsSpan(0, recordLength).CopyTo(raw);
            raw[^1] = value;
            recordLength = 0;
            AddInvalidByte(now, value, events, raw);
            return;
        }

        if (value == (byte)';')
        {
            if (recordByteCount > MaximumRecordLength)
            {
                discardingRecord = true;
                recordLength = 0;
                AddAnomaly(
                    now,
                    KenwoodAsciiParserAnomalyKind.RecordTooLong,
                    recordByteCount,
                    GetDiagnosticSample(),
                    events,
                    GetPartialRaw(value));
                return;
            }

            var completeRecord = new byte[recordLength + 1];
            record.AsSpan(0, recordLength).CopyTo(completeRecord);
            completeRecord[^1] = value;
            ClearPartialRecord();
            events.Add(new KenwoodAsciiRecordReceived(
                now,
                ImmutableArray.CreateRange(completeRecord)));
            return;
        }

        if (recordLength == record.Length)
        {
            discardingRecord = true;
            var raw = new byte[recordLength + 1];
            record.AsSpan(0, recordLength).CopyTo(raw);
            raw[^1] = value;
            recordLength = 0;
            AddAnomaly(
                now,
                KenwoodAsciiParserAnomalyKind.RecordTooLong,
                recordByteCount,
                GetDiagnosticSample(),
                events,
                ImmutableArray.CreateRange(raw));
            return;
        }

        record[recordLength++] = value;
    }

    private void AddInvalidByte(long now, byte value, List<KenwoodAsciiParserEvent> events,
        byte[]? rawBytes = null) =>
        AddAnomaly(
            now,
            KenwoodAsciiParserAnomalyKind.InvalidByte,
            1,
            ImmutableArray.Create(value),
            events,
            rawBytes is null ? ImmutableArray.Create(value) : ImmutableArray.CreateRange(rawBytes));

    private void ExpirePartialRecord(long now, List<KenwoodAsciiParserEvent> events)
    {
        if (!hasPartialRecord || discardingIngressOverflow)
        {
            return;
        }

        var idleExpired = HasElapsed(now, recordLastByteMs, PartialRecordIdleTimeoutMs);
        var totalExpired = HasElapsed(now, recordStartedMs, PartialRecordTotalTimeoutMs);
        if (!idleExpired && !totalExpired)
        {
            return;
        }

        var kind = totalExpired &&
            (!idleExpired ||
                PartialRecordTotalTimeoutMs - (now - recordStartedMs) <=
                PartialRecordIdleTimeoutMs - (now - recordLastByteMs))
            ? KenwoodAsciiParserAnomalyKind.PartialRecordExpiredTotal
            : KenwoodAsciiParserAnomalyKind.PartialRecordExpiredIdle;
        var count = recordByteCount;
        var sample = GetDiagnosticSample();
        var rawBytes = discardingRecord
            ? ImmutableArray<byte>.Empty
            : GetPartialRaw();
        ClearPartialRecord();
        AddAnomaly(now, kind, count, sample, events, rawBytes);
    }

    private void StartRecord(long now)
    {
        hasPartialRecord = true;
        recordStartedMs = now;
        recordLastByteMs = now;
        recordLength = 0;
        recordByteCount = 0;
        diagnosticSampleLength = 0;
        discardingRecord = false;
        discardingIngressOverflow = false;
    }

    private void ClearPartialRecord()
    {
        hasPartialRecord = false;
        recordLength = 0;
        diagnosticSampleLength = 0;
        recordByteCount = 0;
        discardingRecord = false;
        discardingIngressOverflow = false;
    }

    private void EnterQuarantine(long now)
    {
        hasPartialRecord = true;
        recordStartedMs = now;
        recordLastByteMs = now;
        recordLength = 0;
        diagnosticSampleLength = 0;
        recordByteCount = 0;
        discardingRecord = true;
        discardingIngressOverflow = true;
    }

    private void AddDiagnosticByte(byte value)
    {
        if (diagnosticSampleLength < diagnosticSample.Length)
        {
            diagnosticSample[diagnosticSampleLength++] = value;
        }
    }

    private ImmutableArray<byte> GetDiagnosticSample() =>
        ImmutableArray.CreateRange(diagnosticSample.AsSpan(0, diagnosticSampleLength).ToArray());

    private ImmutableArray<byte> GetPartialRaw() =>
        ImmutableArray.CreateRange(record.AsSpan(0, recordLength).ToArray());

    private ImmutableArray<byte> GetPartialRaw(byte suffix)
    {
        var bytes = new byte[recordLength + 1];
        record.AsSpan(0, recordLength).CopyTo(bytes);
        bytes[^1] = suffix;
        return ImmutableArray.CreateRange(bytes);
    }

    private static ImmutableArray<byte> CopySample(ReadOnlySpan<byte> bytes) =>
        ImmutableArray.CreateRange(
            bytes[..Math.Min(bytes.Length, MaximumDiagnosticSampleLength)].ToArray());

    private static bool IsPrintableAscii(byte value) => value is >= 0x20 and <= 0x7E;

    private static bool HasElapsed(long now, long since, long duration) =>
        now >= since && now - since >= duration;

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private static void AddAnomaly(
        long now,
        KenwoodAsciiParserAnomalyKind kind,
        long observedCount,
        ImmutableArray<byte> sample,
        List<KenwoodAsciiParserEvent> events,
        ImmutableArray<byte> rawBytes = default) =>
        events.Add(new KenwoodAsciiParserAnomaly(now, kind, observedCount, sample)
        {
            RawBytes = rawBytes.IsDefault ? sample : rawBytes
        });

    private void Dispatch(List<KenwoodAsciiParserEvent> events)
    {
        foreach (var parserEvent in events)
        {
            try
            {
                if (!eventSink(parserEvent))
                {
                    isFaulted = true;
                    throw new InvalidOperationException("KenwoodAsciiFrameParser event delivery failed.");
                }
            }
            catch
            {
                isFaulted = true;
                throw;
            }
        }
    }

    private void EnterProcessing()
    {
        if (isFaulted)
        {
            throw new InvalidOperationException("KenwoodAsciiFrameParser is faulted.");
        }

        if (isProcessing)
        {
            throw new InvalidOperationException("KenwoodAsciiFrameParser does not allow reentrant calls.");
        }

        isProcessing = true;
    }
}
