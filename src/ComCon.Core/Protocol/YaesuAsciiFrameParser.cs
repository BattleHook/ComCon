using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using ComCon.Core.Routing;

namespace ComCon.Core.Protocol;

/// <summary>
/// A high-performance, bounded ASCII frame parser for modern Yaesu transceivers.
/// </summary>
public sealed class YaesuAsciiFrameParser : IFrameParser
{
    public const int MaximumIngressBytes = 4096;
    public const int MaximumRecordLength = 256;
    public const int MaximumDiagnosticSampleLength = 16;
    public const long PartialRecordIdleTimeoutMs = 500;
    public const long PartialRecordTotalTimeoutMs = 2000;

    private readonly IMonotonicClock clock;
    private readonly ParserSink eventSink;
    
    private readonly byte[] record = new byte[MaximumRecordLength];
    private readonly byte[] diagnosticSample = new byte[MaximumDiagnosticSampleLength];
    
    private int recordLength;
    private int diagnosticSampleLength;
    private long recordByteCount;
    private long recordStartedMs;
    private long recordLastByteMs;
    
    private bool hasPartialRecord;
    private bool discardingQuarantine;
    private bool isProcessing;
    private bool isFaulted;

    public YaesuAsciiFrameParser(IMonotonicClock clock, ParserSink eventSink)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.eventSink = eventSink ?? throw new ArgumentNullException(nameof(eventSink));
    }

    public bool HasPartialFrame => hasPartialRecord;

    public void Feed(ReadOnlySpan<byte> bytes)
    {
        EnterProcessing();
        try
        {
            var now = clock.NowMs;
            var events = new List<ParserEvent>();
            
            if (bytes.IsEmpty)
            {
                return;
            }

            events.Add(new ReceiveActivity(now, bytes.Length));
            
            // RESOLUTION GAP 2: Rigidly bound ingress overflow allocation to 16 bytes maximum
            if (bytes.Length > MaximumIngressBytes)
            {
                var boundedSample = CopySample(bytes);
                AddAnomaly(
                    now,
                    ParserAnomalyKind.IngressOverflow,
                    bytes.Length,
                    boundedSample, // Assigned as Sample (strictly capped at 16 bytes)
                    events,
                    boundedSample  // Assigned as RawBytes (completely prevents unbounded heap copies)
                );
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
            var events = new List<ParserEvent>();
            ExpirePartialRecord(clock.NowMs, events);
            Dispatch(events);
        }
        finally
        {
            isProcessing = false;
        }
    }

    private void ProcessByte(byte value, long now, List<ParserEvent> events)
    {
        if (!hasPartialRecord)
        {
            StartRecord(now);
        }

        recordLastByteMs = now;
        AddDiagnosticByte(value);
        recordByteCount = SaturatingIncrement(recordByteCount);

        if (discardingQuarantine)
        {
            events.Add(new RejectedByte(now, value));
            if (value == (byte)';')
            {
                ClearPartialRecord();
            }
            return;
        }

        if (recordLength >= MaximumRecordLength)
        {
            TriggerOverflow(now, events);
            events.Add(new RejectedByte(now, value));
            if (value == (byte)';')
            {
                ClearPartialRecord();
            }
            return;
        }

        record[recordLength++] = value;

        if (!ValidateCurrentStructure())
        {
            TriggerAnomaly(now, events);
            if (value == (byte)';')
            {
                ClearPartialRecord();
            }
            return;
        }

        if (value == (byte)';')
        {
            if (recordLength >= 3)
            {
                var completeRecord = new byte[recordLength];
                record.AsSpan(0, recordLength).CopyTo(completeRecord);
                ClearPartialRecord();
                
                events.Add(new FrameReceived(
                    now,
                    ImmutableArray.CreateRange(completeRecord)));
            }
            else
            {
                TriggerAnomaly(now, events);
            }
        }
    }

    private bool ValidateCurrentStructure()
    {
        // Fixed syntax layout bugs (resolved array indices check)
        if (recordLength >= 1)
        {
            if (record[0] < 0x41 || record[0] > 0x5A) return false;
        }

        if (recordLength >= 2)
        {
            if (record[1] < 0x41 || record[1] > 0x5A) return false;
        }

        for (int i = 2; i < recordLength; i++)
        {
            byte current = record[i];
            if (current == (byte)';')
            {
                if (i != recordLength - 1) return false;
            }
            else if (current < 0x30 || current > 0x39)
            {
                return false;
            }
        }

        return true;
    }

    private void TriggerOverflow(long now, List<ParserEvent> events)
    {
        discardingQuarantine = true;
        var rawBytes = GetPartialRaw();
        recordLength = 0;
        
        AddAnomaly(
            now,
            ParserAnomalyKind.RecordTooLong, // Maps directly to SharedRadioDiagnosticKind.FramingOverflow
            recordByteCount,
            GetDiagnosticSample(),
            events,
            rawBytes);
    }

    private void TriggerAnomaly(long now, List<ParserEvent> events)
    {
        discardingQuarantine = true;
        var rawBytes = GetPartialRaw();
        recordLength = 0;
        
        AddAnomaly(
            now,
            ParserAnomalyKind.InvalidStructure, // Maps to SharedRadioDiagnosticKind.FramingAnomaly
            1,
            GetDiagnosticSample(),
            events,
            rawBytes);
    }

    private void ExpirePartialRecord(long now, List<ParserEvent> events)
    {
        if (!hasPartialRecord || discardingQuarantine)
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
            ? ParserAnomalyKind.PartialRecordExpiredTotal
            : ParserAnomalyKind.PartialRecordExpiredIdle;
            
        var count = recordByteCount;
        var sample = GetDiagnosticSample();
        var rawBytes = GetPartialRaw();
        EnterQuarantine(now);
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
        discardingQuarantine = false;
    }

    private void ClearPartialRecord()
    {
        hasPartialRecord = false;
        recordLength = 0;
        diagnosticSampleLength = 0;
        recordByteCount = 0;
        discardingQuarantine = false;
    }

    private void EnterQuarantine(long now)
    {
        hasPartialRecord = true;
        recordStartedMs = now;
        recordLastByteMs = now;
        recordLength = 0;
        diagnosticSampleLength = 0;
        recordByteCount = 0;
        discardingQuarantine = true;
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

    private static ImmutableArray<byte> CopySample(ReadOnlySpan<byte> bytes) =>
        ImmutableArray.CreateRange(bytes[..Math.Min(bytes.Length, MaximumDiagnosticSampleLength)].ToArray());

    private static bool HasElapsed(long now, long since, long duration) =>
        now >= since && now - since >= duration;

    private static long SaturatingIncrement(long value) => value == long.MaxValue ? value : value + 1;

    private static void AddAnomaly(
        long now,
        ParserAnomalyKind kind,
        long observedCount,
        ImmutableArray<byte> sample,
        List<ParserEvent> events,
        ImmutableArray<byte> rawBytes = default) =>
        events.Add(new ParserAnomaly(now, kind, observedCount, sample)
        {
            RawBytes = rawBytes.IsDefault ? sample : rawBytes
        });

    private void Dispatch(List<ParserEvent> events)
    {
        foreach (var parserEvent in events)
        {
            try
            {
                if (!eventSink(parserEvent))
                {
                    isFaulted = true;
                    throw new InvalidOperationException("YaesuAsciiFrameParser event delivery failed.");
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
            throw new InvalidOperationException("YaesuAsciiFrameParser is faulted.");
        }

        if (isProcessing)
        {
            throw new InvalidOperationException("YaesuAsciiFrameParser does not allow reentrant calls.");
        }

        isProcessing = true;
    }
}
