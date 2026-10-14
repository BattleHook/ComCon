using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class LegacyInputDrainTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(FramingMode.Civ, 0xFD)]
    [DataRow(FramingMode.KenwoodAscii, 0x3B)]
    public async Task EmptyInputCompletesImmediatelyAndPartialInputWaitsForBoundary(FramingMode framing, int terminator)
    {
        var clock = new Clock();
        var empty = await LegacyInputDrain.RunAsync(new Endpoint(clock, []), framing, clock, CancellationToken.None);
        Assert.IsTrue(empty.Succeeded);
        Assert.AreEqual(0L, empty.ElapsedMs);
        var endpoint = new Endpoint(clock, [1, 2, (byte)terminator]);
        var result = await LegacyInputDrain.RunAsync(endpoint, framing, clock, CancellationToken.None);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.DiscardedBytes);
        TestContext.WriteLine($"PASS {framing}: no quiet delay; exact three-byte discard at protocol boundary.");
    }

    [TestMethod]
    [DataRow(65535, true)]
    [DataRow(65536, false)]
    [DataRow(65537, false)]
    public async Task ByteLimitIsMeasuredExactly(int offered, bool succeeds)
    {
        var clock = new Clock();
        var bytes = new byte[offered];
        bytes[^1] = 0xFD;
        var result = await LegacyInputDrain.RunAsync(new Endpoint(clock, bytes),
            FramingMode.Civ, clock, CancellationToken.None);
        Assert.AreEqual(succeeds, result.Succeeded);
        Assert.AreEqual(Math.Min(offered, 65536), result.DiscardedBytes);
        TestContext.WriteLine($"PASS: offered={offered}, discarded={result.DiscardedBytes}, succeeded={result.Succeeded}.");
    }

    [TestMethod]
    [DataRow(2999, true)]
    [DataRow(3000, false)]
    [DataRow(3001, false)]
    public async Task TimeLimitIsMeasuredExactlyEvenAtCompleteBoundary(int elapsed, bool succeeds)
    {
        var clock = new Clock();
        var endpoint = new Endpoint(clock, [0xFD]) { ElapsedOnRead = elapsed };
        var result = await LegacyInputDrain.RunAsync(endpoint, FramingMode.Civ, clock, CancellationToken.None);
        Assert.AreEqual(succeeds, result.Succeeded);
        Assert.AreEqual((long)elapsed, result.ElapsedMs);
        Assert.AreEqual(1, result.DiscardedBytes);
        TestContext.WriteLine($"PASS: observed {elapsed} ms, succeeded={result.Succeeded}; no timing-limit relaxation.");
    }

    [TestMethod]
    public async Task PartialFrameAndCancellationIgnoringReadCannotReportSuccessfulDrain()
    {
        var clock = new StopwatchClock();
        var endpoint = new Endpoint(new Clock(), [1]) { NeverCompleteRead = true };
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await LegacyInputDrain.RunAsync(endpoint, FramingMode.Civ, clock, CancellationToken.None);
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(0, result.DiscardedBytes);
            Assert.IsLessThan(4500L, started.ElapsedMilliseconds);
            StringAssert.Contains(result.Reason, "3000");
            TestContext.WriteLine($"PASS: cancellation-ignoring read rejected at {started.ElapsedMilliseconds} ms.");
        }
        finally { endpoint.CompletePendingRead(); }
    }

    [TestMethod]
    [DataRow(2999, true)]
    [DataRow(3000, false)]
    public async Task EmptyObservationMustStillBeInsideTimeBudget(int elapsed, bool succeeds)
    {
        var clock = new Clock();
        var endpoint = new Endpoint(clock, []) { ElapsedOnEmptyObservation = elapsed };
        var result = await LegacyInputDrain.RunAsync(endpoint, FramingMode.Civ, clock, CancellationToken.None);
        Assert.AreEqual(succeeds, result.Succeeded);
        Assert.AreEqual((long)elapsed, result.ElapsedMs);
    }

    [TestMethod]
    public async Task ReadFailureAndCallerCancellationAreExplicit()
    {
        var clock = new Clock();
        var endpoint = new Endpoint(clock, [0xFD]) { ReadFailure = new IOException("Injected drain failure.") };
        var failed = await LegacyInputDrain.RunAsync(endpoint, FramingMode.Civ, clock, CancellationToken.None);
        Assert.IsFalse(failed.Succeeded);
        StringAssert.Contains(failed.Reason, "Injected drain failure");
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            LegacyInputDrain.RunAsync(new Endpoint(clock, []), FramingMode.Civ, clock, stop.Token));
    }

    private sealed class Clock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }

    private sealed class Endpoint(Clock clock, byte[] input) : IBufferedByteBridgeEndpoint
    {
        private int offset;
        private readonly TaskCompletionSource<int> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BufferedInputBytes
        {
            get
            {
                if (input.Length == offset && ElapsedOnEmptyObservation is { } elapsed)
                    clock.NowMs = elapsed;
                return input.Length - offset;
            }
        }
        public int? ElapsedOnEmptyObservation { get; init; }
        public int ElapsedOnRead { get; init; }
        public bool NeverCompleteRead { get; init; }
        public Exception? ReadFailure { get; init; }
        public void CompletePendingRead() => pending.TrySetResult(0);
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token)
        {
            if (ReadFailure is { } failure) throw failure;
            if (NeverCompleteRead) return new(pending.Task);
            token.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, input.Length - offset);
            input.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            clock.NowMs = ElapsedOnRead;
            return ValueTask.FromResult(count);
        }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { CompletePendingRead(); return ValueTask.CompletedTask; }
    }
}
