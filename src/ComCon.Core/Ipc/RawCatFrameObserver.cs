using ComCon.Core.Models;
using ComCon.Core.Protocol;
using ComCon.Core.Routing;
using System.Diagnostics;

namespace ComCon.Core.Ipc;

internal sealed class RawCatFrameObserver
{
    private readonly RawCatCapture capture;
    private readonly RawCatCapture.CaptureSession session;
    private readonly string source;
    private readonly string direction;
    private readonly string completeOutcome;
    private readonly bool captureIngressChunks;
    private readonly IMonotonicClock clock;
    private readonly object gate = new();
    private readonly CivFrameParser? civ;
    private readonly KenwoodAsciiFrameParser? kenwood;
    private readonly YaesuAsciiFrameParser? yaesu;

    public RawCatFrameObserver(
        FramingMode framing, string source, string direction, RawCatCapture capture,
        RawCatCapture.CaptureSession session, string completeOutcome = "CompleteRecord",
        bool captureIngressChunks = false, IMonotonicClock? clock = null)
    {
        this.capture = capture ?? throw new ArgumentNullException(nameof(capture));
        this.session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(completeOutcome);
        this.source = source;
        this.direction = direction;
        this.completeOutcome = completeOutcome;
        this.captureIngressChunks = captureIngressChunks;
        this.clock = clock ?? new StopwatchClock();
        switch (framing)
        {
            case FramingMode.Civ:
                civ = new(this.clock, OnCivEvent);
                break;
            case FramingMode.KenwoodAscii:
                kenwood = new(this.clock, OnKenwoodEvent);
                break;
            case FramingMode.YaesuAscii:
                yaesu = new(this.clock, OnYaesuEvent);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(framing), "Raw record capture requires structural framing.");
        }
    }

    public void Observe(
        ReadOnlySpan<byte> bytes,
        long? monotonicTimestampMs = null,
        long? monotonicTimestampTicks = null)
    {
        var timestampMs = monotonicTimestampMs ?? clock.NowMs;
        var timestampTicks = monotonicTimestampTicks ?? Stopwatch.GetTimestamp();
        if (captureIngressChunks)
            capture.TryCapture(session, source, direction, "IngressChunk", bytes, timestampMs, timestampTicks);
        lock (gate)
        {
            if (civ is not null) civ.Feed(bytes);
            else if (kenwood is not null) kenwood.Feed(bytes);
            else yaesu?.Feed(bytes);
        }
    }

    public void Complete()
    {
        lock (gate)
        {
            if (civ is not null) civ.Tick();
            else if (kenwood is not null) kenwood.Tick();
            else yaesu?.Tick();
        }
    }

    private void OnCivEvent(CivParserEvent parserEvent)
    {
        switch (parserEvent)
        {
            case CivFrameReceived frame:
                capture.TryCapture(session, source, direction, completeOutcome, frame.Frame.AsSpan(),
                    frame.TimestampMs);
                break;
            case CivUnframedByte value:
                capture.TryCapture(session, source, direction, "RejectedUnframedByte", [value.Value],
                    value.TimestampMs);
                break;
            case CivParserAnomaly { Kind: not CivParserAnomalyKind.Noise } anomaly
                when !anomaly.RawBytes.IsEmpty:
                capture.TryCapture(session, source, direction, $"Rejected:{anomaly.Kind}", anomaly.RawBytes.AsSpan(),
                    anomaly.TimestampMs);
                break;
        }
    }

    private bool OnKenwoodEvent(KenwoodAsciiParserEvent parserEvent)
    {
        switch (parserEvent)
        {
            case KenwoodAsciiRecordReceived record:
                capture.TryCapture(session, source, direction, completeOutcome, record.Record.AsSpan(),
                    record.TimestampMs);
                break;
            case KenwoodRejectedByte value:
                capture.TryCapture(session, source, direction, "RejectedMalformedRecordByte", [value.Value],
                    value.TimestampMs);
                break;
            case KenwoodAsciiParserAnomaly anomaly when !anomaly.RawBytes.IsEmpty:
                capture.TryCapture(session, source, direction, $"Rejected:{anomaly.Kind}", anomaly.RawBytes.AsSpan(),
                    anomaly.TimestampMs);
                break;
        }
        return true;
    }

    private bool OnYaesuEvent(ParserEvent parserEvent)
    {
        switch (parserEvent)
        {
            case FrameReceived frame:
                capture.TryCapture(session, source, direction, completeOutcome, frame.Record.AsSpan(),
                    frame.TimestampMs);
                break;
            case RejectedByte value:
                capture.TryCapture(session, source, direction, "RejectedMalformedRecordByte", [value.Value],
                    value.TimestampMs);
                break;
            case ParserAnomaly anomaly when !anomaly.RawBytes.IsEmpty:
                capture.TryCapture(session, source, direction, $"Rejected:{anomaly.Kind}", anomaly.RawBytes.AsSpan(),
                    anomaly.TimestampMs);
                break;
        }
        return true;
    }
}
