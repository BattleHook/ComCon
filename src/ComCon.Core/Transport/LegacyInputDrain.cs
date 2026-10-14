using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Transport;

public sealed record LegacyDrainResult(bool Succeeded, int DiscardedBytes, long ElapsedMs, string Reason);

internal static class LegacyInputDrain
{
    internal const int MaximumBytes = 65536;
    internal const long MaximumMs = 3000;

    internal static async Task<LegacyDrainResult> RunAsync(
        IBufferedByteBridgeEndpoint endpoint, FramingMode framing,
        IMonotonicClock clock, CancellationToken cancellationToken, bool atBoundary = true,
        Action<bool>? boundaryObserver = null)
    {
        var started = clock.NowMs;
        var discarded = 0;
        var buffer = new byte[4096];
        if (framing is not (FramingMode.Civ or FramingMode.KenwoodAscii))
            throw new NotSupportedException("Legacy drain requires supported CAT framing.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(MaximumMs));
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var elapsed = clock.NowMs - started;
                if (discarded >= MaximumBytes)
                    return new(false, discarded, elapsed, "Input drain reached the 65536-byte limit.");
                if (elapsed >= MaximumMs || deadline.IsCancellationRequested)
                    return new(false, discarded, elapsed, "Input drain reached the 3000-ms limit.");

                var available = endpoint.BufferedInputBytes;
                if (available < 0) throw new IOException("Endpoint reported a negative buffered-input count.");
                if (available == 0 && atBoundary)
                {
                    elapsed = clock.NowMs - started;
                    if (elapsed >= MaximumMs || deadline.IsCancellationRequested)
                        return new(false, discarded, elapsed, "Input drain reached the 3000-ms limit.");
                    return new(true, discarded, elapsed,
                        "Observed input drained at a frame boundary; hidden peer queues are not asserted empty.");
                }
                if (available == 0)
                {
                    await Task.Delay(2, deadline.Token).ConfigureAwait(false);
                    continue;
                }
                var count = Math.Min(Math.Min(available, buffer.Length), MaximumBytes - discarded);
                var read = await endpoint.ReadAsync(buffer.AsMemory(0, count), deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
                if (read <= 0 || read > count) throw new IOException("Invalid buffered-input read count.");
                discarded += read;
                atBoundary = framing switch
                {
                    FramingMode.Civ => buffer[read - 1] == 0xFD,
                    FramingMode.KenwoodAscii => buffer[read - 1] == (byte)';',
                    _ => throw new NotSupportedException("Legacy drain requires supported CAT framing.")
                };
                boundaryObserver?.Invoke(atBoundary);
                if (clock.NowMs - started >= MaximumMs || deadline.IsCancellationRequested)
                    return new(false, discarded, clock.NowMs - started, "Input drain reached the 3000-ms limit.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, discarded, clock.NowMs - started, "Input drain reached the 3000-ms limit.");
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            NotSupportedException or TimeoutException)
        {
            return new(false, discarded, clock.NowMs - started, $"Input drain failed: {exception.Message}");
        }
    }
}
