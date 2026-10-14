using System.Collections.Immutable;
using ComCon.Core.Models;
using ComCon.Core.Protocol;
using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

public sealed record ReceiveFramingFault(
    FramingMode Framing, long TimestampMs, string Reason, long ObservedCount,
    ImmutableArray<byte> Sample);

/// <summary>Single-producer simulation adapter; framing does not establish reply attribution.</summary>
public sealed class SimulatedReceiveFeed
{
    private readonly CivFrameParser? civ;
    private readonly KenwoodAsciiFrameParser? kenwood;
    private readonly Action<ReceiveFramingFault> reportFault;
    private bool processing;
    private readonly ReceiveClock parserClock;

    public SimulatedReceiveFeed(FramingMode framing, IMonotonicClock clock,
        Action<ReceiveFramingFault> reportFault)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(reportFault);
        this.reportFault = reportFault;
        parserClock = new ReceiveClock(clock);
        Journal = new SimulatedReceiveJournal(clock);
        switch (framing)
        {
            case FramingMode.Civ:
                civ = new CivFrameParser(parserClock, OnCivEvent);
                break;
            case FramingMode.KenwoodAscii:
                kenwood = new KenwoodAsciiFrameParser(parserClock, OnKenwoodEvent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(framing), "A supported structural framing mode is required.");
        }
    }

    public SimulatedReceiveJournal Journal { get; }
    public ReceiveFramingFault? Fault { get; private set; }
    public bool HasPartialRecord => civ?.HasPartialFrame ?? kenwood!.HasPartialRecord;

    public void Feed(ReadOnlySpan<byte> chunk)
        => FeedAt(chunk, null);

    internal void FeedAt(ReadOnlySpan<byte> chunk, long? receivedMs)
    {
        Enter();
        try
        {
            parserClock.ReceivedMs = receivedMs;
            if (civ is not null) civ.Feed(chunk);
            else kenwood!.Feed(chunk);
            ThrowIfFaulted();
        }
        finally
        {
            parserClock.ReceivedMs = null;
            processing = false;
        }
    }

    public void Tick()
    {
        Enter();
        try
        {
            if (civ is not null) civ.Tick();
            else kenwood!.Tick();
            ThrowIfFaulted();
        }
        finally
        {
            processing = false;
        }
    }

    private void Enter()
    {
        if (processing) throw new InvalidOperationException("Receive-feed calls must not overlap or reenter.");
        ThrowIfFaulted();
        processing = true;
    }

    private void ThrowIfFaulted()
    {
        if (Fault is { } fault)
            throw new InvalidDataException($"Simulated {fault.Framing} receive feed faulted: {fault.Reason}. Create a new isolated feed; do not reuse partial history.");
    }

    private void OnCivEvent(CivParserEvent parserEvent)
    {
        if (Fault is not null) return;
        switch (parserEvent)
        {
            case CivParserAnomaly anomaly:
                Latch(new(FramingMode.Civ, anomaly.TimestampMs, anomaly.Kind.ToString(), anomaly.ObservedCount, anomaly.Sample));
                break;
            case CivFrameReceived received:
                Journal.RecordFrame(received.Frame.AsSpan(), received.TimestampMs);
                break;
        }
    }

    private bool OnKenwoodEvent(KenwoodAsciiParserEvent parserEvent)
    {
        if (Fault is not null) return true;
        switch (parserEvent)
        {
            case KenwoodAsciiParserAnomaly anomaly:
                Latch(new(FramingMode.KenwoodAscii, anomaly.TimestampMs, anomaly.Kind.ToString(), anomaly.ObservedCount, anomaly.Sample));
                break;
            case KenwoodAsciiRecordReceived received:
                Journal.RecordFrame(received.Record.AsSpan(), received.TimestampMs);
                break;
        }
        return true;
    }

    private void Latch(ReceiveFramingFault fault)
    {
        Fault = fault;
        Journal.Invalidate($"{fault.Framing}: {fault.Reason}");
        reportFault(fault);
    }

    private sealed class ReceiveClock(IMonotonicClock clock) : IMonotonicClock
    {
        public long? ReceivedMs { get; set; }
        public long NowMs => ReceivedMs ?? clock.NowMs;
    }
}
