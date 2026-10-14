using System;
using System.Collections.Immutable;

namespace ComCon.Core.Protocol;

/// <summary>
/// Defines the specific classification of structural and temporal failures within any framing pipeline.
/// </summary>
public enum ParserAnomalyKind
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
    InvalidStructure,
    RecordTooLong,
    PartialRecordExpiredIdle,
    PartialRecordExpiredTotal,
    IngressOverflow
}

/// <summary>
/// Base record for all unified parser events dispatched by an IFrameParser.
/// </summary>
public abstract record ParserEvent(long TimestampMs);

public sealed record ReceiveActivity(long TimestampMs, int ByteCount) : ParserEvent(TimestampMs);

public sealed record FrameReceived(long TimestampMs, ImmutableArray<byte> Record) : ParserEvent(TimestampMs);

public sealed record RejectedByte(long TimestampMs, byte Value) : ParserEvent(TimestampMs);

public sealed record UnframedByte(long TimestampMs, byte Value) : ParserEvent(TimestampMs);

public sealed record ParserAnomaly(
    long TimestampMs,
    ParserAnomalyKind Kind,
    long ObservedCount,
    ImmutableArray<byte> Sample) : ParserEvent(TimestampMs)
{
    public ImmutableArray<byte> RawBytes { get; init; } = Sample;
}

public delegate bool ParserSink(ParserEvent parserEvent);

/// <summary>
/// A protocol-neutral base contract for all runtime frame-slicing engines.
/// </summary>
public interface IFrameParser
{
    bool HasPartialFrame { get; }
    void Feed(ReadOnlySpan<byte> bytes);
    void Tick();
}
