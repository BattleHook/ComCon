using System.Threading.Channels;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

/// <summary>Offline bridge observer; never attached by the station UI.</summary>
public sealed class SimulatedBridgeReceiveObserver : IAsyncDisposable
{
    public const int QueueCapacity = 16;
    private readonly Channel<Chunk> queue = Channel.CreateBounded<Chunk>(
        new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly IMonotonicClock clock;
    private readonly SimulatedReceiveFeed feed;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private int stopped;
    private readonly object lifetime = new();
    private bool disposed;

    public SimulatedBridgeReceiveObserver(FramingMode framing, IMonotonicClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        feed = new(framing, clock, fault => Invalidate($"{fault.Framing}: {fault.Reason}"));
        worker = Task.Run(RunAsync);
    }

    public SimulatedReceiveJournal Journal => feed.Journal;

    internal void Observe(ReadOnlySpan<byte> bytes)
    {
        if (Volatile.Read(ref stopped) != 0) return;
        if (bytes.Length is < 1 or > 4096)
        {
            Invalidate("Receive observer ingress is outside 1-4096 bytes.");
            return;
        }
        var receivedMs = clock.NowMs;
        if (!queue.Writer.TryWrite(new(bytes.ToArray(), receivedMs)))
            Invalidate("Receive observer queue overflow.");
    }

    internal void Invalidate(string reason)
    {
        Journal.Invalidate(reason);
        Interlocked.Exchange(ref stopped, 1);
        queue.Writer.TryComplete();
        lock (lifetime)
        {
            if (!disposed) shutdown.Cancel();
        }
    }

    private async Task RunAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            while (await timer.WaitForNextTickAsync(shutdown.Token).ConfigureAwait(false))
            {
                while (queue.Reader.TryRead(out var chunk))
                {
                    shutdown.Token.ThrowIfCancellationRequested();
                    feed.FeedAt(chunk.Bytes, chunk.ReceivedMs);
                }
                if (feed.HasPartialRecord) feed.Tick();
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
        catch (InvalidDataException exception)
        {
            Invalidate(exception.Message);
        }
        catch (Exception exception)
        {
            Invalidate($"Receive observer failed: {exception.GetType().Name}: {exception.Message}");
            throw;
        }
        finally
        {
            while (queue.Reader.TryRead(out _)) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Invalidate("Receive observer stopped.");
        try { await worker.ConfigureAwait(false); }
        finally
        {
            lock (lifetime)
            {
                if (!disposed)
                {
                    disposed = true;
                    shutdown.Dispose();
                }
            }
        }
    }

    private sealed record Chunk(byte[] Bytes, long ReceivedMs);
}
