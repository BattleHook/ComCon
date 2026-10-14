using System.Globalization;
using System.Text.Json.Serialization;
using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

public sealed record ReceiveObservation(
    [property: JsonPropertyName("observationId")] string ObservationId,
    [property: JsonPropertyName("hex")] string Hex,
    [property: JsonPropertyName("ageMs")] long AgeMs,
    [property: JsonPropertyName("source")] string Source = "simulatedPassiveRx");

public sealed record ReceiveObservationBatch(
    [property: JsonPropertyName("observations")] IReadOnlyList<ReceiveObservation> Observations,
    [property: JsonPropertyName("nextObservationId")] string NextObservationId,
    [property: JsonPropertyName("latestObservationId")] string LatestObservationId,
    [property: JsonPropertyName("gap")] bool Gap,
    [property: JsonPropertyName("hasMore")] bool HasMore);

/// <summary>Bounded opaque simulated RX frames only; neither parses CAT nor dispatches bytes.</summary>
public sealed class SimulatedReceiveJournal(IMonotonicClock clock)
{
    public const int Capacity = 128;
    public const int BatchSize = 8;
    public const int MaximumPayloadBytes = 256;
    public const long RetentionMs = 60_000;
    private readonly IMonotonicClock clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly object gate = new();
    private readonly Queue<Entry> entries = [];
    private long latestId;
    private string? fault;

    public string? Fault
    {
        get { lock (gate) return fault; }
    }

    internal void Invalidate(string reason)
    {
        lock (gate)
        {
            fault ??= reason;
            entries.Clear();
        }
    }

    public void RecordFrame(ReadOnlySpan<byte> payload)
        => RecordFrame(payload, clock.NowMs);

    internal void RecordFrame(ReadOnlySpan<byte> payload, long receivedMs)
    {
        if (payload.Length is < 1 or > MaximumPayloadBytes)
            throw new ArgumentOutOfRangeException(nameof(payload), "An observation must contain 1-256 bytes.");
        var hex = Convert.ToHexString(payload);
        var now = clock.NowMs;
        lock (gate)
        {
            ThrowIfFaulted();
            Prune(now);
            var id = checked(latestId + 1);
            entries.Enqueue(new(id, hex, receivedMs));
            latestId = id;
            if (entries.Count > Capacity) entries.Dequeue();
        }
    }

    public ReceiveObservationBatch ReadAfter(long afterId)
    {
        lock (gate)
        {
            ThrowIfFaulted();
            if (afterId < 0 || afterId > latestId)
                throw new ArgumentOutOfRangeException(nameof(afterId), "Cursor is outside this journal's history.");
            var now = clock.NowMs;
            Prune(now);
            var gap = entries.TryPeek(out var first) ? afterId < first.Id - 1 : afterId < latestId;
            var selected = entries.Where(entry => entry.Id > afterId).Take(BatchSize).ToArray();
            var next = selected.Length == 0 ? latestId : selected[^1].Id;
            return new(
                selected.Select(entry => new ReceiveObservation(
                    Decimal(entry.Id), entry.Hex, now - entry.ObservedMs)).ToArray(),
                Decimal(next), Decimal(latestId), gap, next < latestId);
        }
    }

    private void Prune(long now)
    {
        while (entries.TryPeek(out var first) && now - first.ObservedMs >= RetentionMs)
            entries.Dequeue();
    }

    private void ThrowIfFaulted()
    {
        if (fault is not null) throw new InvalidDataException($"Receive journal is invalid: {fault}");
    }

    private static string Decimal(long value) => value.ToString(CultureInfo.InvariantCulture);
    private sealed record Entry(long Id, string Hex, long ObservedMs);
}
