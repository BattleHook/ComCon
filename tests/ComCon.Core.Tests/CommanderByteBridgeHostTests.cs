using System.Threading.Channels;
using ComCon.Core.Models;
using ComCon.Core.Transport;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class CommanderByteBridgeHostTests
{
    [TestMethod]
    public async Task SimulatedObserverAdmitsOnlyRadioReceiveAndStopsWithBridge()
    {
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, new StopwatchClock());
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        var radio = factory.Get("COM3");
        var commander = factory.Get("COM31");
        var tx = Convert.FromHexString("FEFE7AE11C00FD");
        var rx = Convert.FromHexString("FEFEE17A1C0000FD");
        await commander.SendAsync(tx);
        CollectionAssert.AreEqual(tx, await radio.ReadWrittenAsync());
        await radio.SendAsync(rx[..3]);
        CollectionAssert.AreEqual(rx[..3], await commander.ReadWrittenAsync());
        await radio.SendAsync(rx[3..]);
        CollectionAssert.AreEqual(rx[3..], await commander.ReadWrittenAsync());
        await WaitForObservationAsync(observer);
        var batch = observer.Journal.ReadAfter(0);
        Assert.HasCount(1, batch.Observations);
        Assert.AreEqual(Convert.ToHexString(rx), batch.Observations[0].Hex);
        await host.StopAsync();
        Assert.ThrowsExactly<InvalidDataException>(() => observer.Journal.ReadAfter(0));
        Assert.AreEqual(CommanderBridgeState.Stopped, host.State);
    }

    [TestMethod]
    public async Task ObserverFramingFaultDoesNotInterruptEitherRelayDirection()
    {
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, new StopwatchClock());
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        var radio = factory.Get("COM3");
        var commander = factory.Get("COM31");
        await radio.SendAsync([0xFC]);
        CollectionAssert.AreEqual(new byte[] { 0xFC }, await commander.ReadWrittenAsync());
        await WaitForObserverFaultAsync(observer);
        var valid = Convert.FromHexString("FEFEE17A1C0000FD");
        await radio.SendAsync(valid);
        CollectionAssert.AreEqual(valid, await commander.ReadWrittenAsync());
        await commander.SendAsync([0x01, 0x02]);
        CollectionAssert.AreEqual(new byte[] { 0x01, 0x02 }, await radio.ReadWrittenAsync());
        Assert.AreEqual(CommanderBridgeState.Running, host.State);
        Assert.IsNull(host.Fault);
        Assert.ThrowsExactly<InvalidDataException>(() => observer.Journal.ReadAfter(0));
    }

    [TestMethod]
    public async Task ObserverExpiresIdlePartialRecordWithoutNewReceive()
    {
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, new StopwatchClock());
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        await factory.Get("COM3").SendAsync([0xFE, 0xFE, 0xE1]);
        await factory.Get("COM31").ReadWrittenAsync();
        await WaitForObserverFaultAsync(observer);
        StringAssert.Contains(observer.Journal.Fault, "CandidateExpiredIdle");
        Assert.AreEqual(CommanderBridgeState.Running, host.State);
    }

    [TestMethod]
    public async Task RelayFaultInvalidatesPreviouslyReceivedTelemetry()
    {
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, new StopwatchClock());
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        var radio = factory.Get("COM3");
        var commander = factory.Get("COM31");
        await radio.SendAsync(Convert.FromHexString("FEFEE17A1C0000FD"));
        await commander.ReadWrittenAsync();
        await WaitForObservationAsync(observer);
        await radio.FailReadAsync(new IOException("offline disconnect"));
        await WaitForFaultCleanupAsync(host, commander, radio);
        Assert.ThrowsExactly<InvalidDataException>(() => observer.Journal.ReadAfter(0));
        StringAssert.Contains(observer.Journal.Fault, "offline disconnect");
    }

    private static async Task WaitForObservationAsync(SimulatedBridgeReceiveObserver observer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (observer.Journal.ReadAfter(0).Observations.Count == 0)
            await Task.Delay(5, timeout.Token);
    }

    [TestMethod]
    public async Task ObserverBacklogOverflowInvalidatesTelemetryWithoutBlockingRelay()
    {
        using var clock = new BlockProcessingClock();
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, clock);
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        var frame = Convert.FromHexString("FEFEE17A1C0000FD");
        var radio = factory.Get("COM3");
        var commander = factory.Get("COM31");
        try
        {
            await radio.SendAsync(frame);
            await commander.ReadWrittenAsync();
            Assert.IsTrue(clock.ProcessingEntered.Wait(TimeSpan.FromSeconds(2)));
            for (var i = 0; i <= SimulatedBridgeReceiveObserver.QueueCapacity; i++)
            {
                await radio.SendAsync(frame);
                CollectionAssert.AreEqual(frame, await commander.ReadWrittenAsync());
                if (i < SimulatedBridgeReceiveObserver.QueueCapacity)
                    Assert.IsNull(observer.Journal.Fault);
            }
            StringAssert.Contains(observer.Journal.Fault, "queue overflow");
            Assert.AreEqual(CommanderBridgeState.Running, host.State);
            Assert.IsNull(host.Fault);
        }
        finally { clock.Release.Set(); }
    }

    [TestMethod]
    public async Task DisposedObserverLeavesRelayRunningAndRejectsOldHistory()
    {
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.KenwoodAscii, new StopwatchClock());
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        await factory.Get("COM3").SendAsync("FA00014074000;"u8.ToArray());
        await factory.Get("COM31").ReadWrittenAsync();
        await WaitForObservationAsync(observer);
        await observer.DisposeAsync();
        await observer.DisposeAsync();
        await factory.Get("COM3").SendAsync("TX0;"u8.ToArray());
        CollectionAssert.AreEqual("TX0;"u8.ToArray(), await factory.Get("COM31").ReadWrittenAsync());
        Assert.AreEqual(CommanderBridgeState.Running, host.State);
        Assert.ThrowsExactly<InvalidDataException>(() => observer.Journal.ReadAfter(0));
        await host.StopAsync();
        Assert.AreEqual(CommanderBridgeState.Stopped, host.State);
    }

    [TestMethod]
    public async Task ObserverQueueDelayDoesNotRejuvenateReceiptAge()
    {
        using var clock = new BlockProcessingClock();
        var factory = new FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, clock);
        await using var host = CommanderByteBridgeHost.CreateSimulation(factory, observer);
        await host.StartAsync(CreateProfile());
        try
        {
            await factory.Get("COM3").SendAsync(Convert.FromHexString("FEFEE17A1C0000FD"));
            await factory.Get("COM31").ReadWrittenAsync();
            Assert.IsTrue(clock.ProcessingEntered.Wait(TimeSpan.FromSeconds(2)));
            clock.Now = 100;
        }
        finally { clock.Release.Set(); }
        await WaitForObservationAsync(observer);
        Assert.AreEqual(100L, observer.Journal.ReadAfter(0).Observations.Single().AgeMs);
    }

    private sealed class BlockProcessingClock : IMonotonicClock, IDisposable
    {
        private int reads;
        public long Now;
        public ManualResetEventSlim ProcessingEntered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public long NowMs
        {
            get
            {
                if (Interlocked.Increment(ref reads) == 2)
                {
                    ProcessingEntered.Set();
                    if (!Release.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Offline worker barrier was not released.");
                }
                return Volatile.Read(ref Now);
            }
        }
        public void Dispose()
        {
            ProcessingEntered.Dispose();
            Release.Dispose();
        }
    }

    private static async Task WaitForObserverFaultAsync(SimulatedBridgeReceiveObserver observer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (observer.Journal.Fault is null) await Task.Delay(5, timeout.Token);
    }

    [TestMethod]
    public async Task StartForwardsOpaqueBytesBothDirectionsAndStops()
    {
        var factory = new FakeEndpointFactory();
        await using var host = new CommanderByteBridgeHost(factory);
        await host.StartAsync(CreateProfile());

        Assert.AreEqual(CommanderBridgeState.Running, host.State);
        CollectionAssert.AreEqual(new[] { "COM31", "COM3" }, factory.OpenedPorts);
        var commanderEndpoint = factory.Get("COM31");
        var radioEndpoint = factory.Get("COM3");
        var commanderBytes = new byte[] { 0xFE, 0xFE, 0x7A, 0xE1, 0x1C, 0x00, 0xFD };
        var radioBytes = new byte[] { 0xFE, 0xFE, 0xE1, 0x7A, 0x1C, 0x00, 0x00, 0xFD };

        await commanderEndpoint.SendAsync(commanderBytes);
        CollectionAssert.AreEqual(commanderBytes, await radioEndpoint.ReadWrittenAsync());
        await radioEndpoint.SendAsync(radioBytes);
        CollectionAssert.AreEqual(radioBytes, await commanderEndpoint.ReadWrittenAsync());

        await host.StopAsync();

        Assert.AreEqual(CommanderBridgeState.Stopped, host.State);
        Assert.IsTrue(commanderEndpoint.IsDisposed);
        Assert.IsTrue(radioEndpoint.IsDisposed);
    }

    [TestMethod]
    public async Task FailedPhysicalOpenClosesAlreadyOpenedVirtualEndpoint()
    {
        var factory = new FakeEndpointFactory { FailedPortName = "COM3" };
        await using var host = new CommanderByteBridgeHost(factory);

        await AssertThrowsAsync<IOException>(() => host.StartAsync(CreateProfile()));

        Assert.AreEqual(CommanderBridgeState.Stopped, host.State);
        Assert.IsTrue(factory.Get("COM31").IsDisposed);
    }

    [TestMethod]
    public async Task DisabledUnconfiguredProfileDoesNotOpenAnyPort()
    {
        var factory = new FakeEndpointFactory();
        await using var host = new CommanderByteBridgeHost(factory);

        await AssertThrowsAsync<InvalidOperationException>(() => host.StartAsync(new ProfileConfig
        {
            Id = 2,
            Label = "Unconfigured profile",
            Enabled = false
        }));

        Assert.IsEmpty(factory.OpenedPorts);
        Assert.AreEqual(CommanderBridgeState.Stopped, host.State);
    }

    [TestMethod]
    public async Task EndpointFaultClosesBothPortsAndBlocksRestart()
    {
        var factory = new FakeEndpointFactory();
        await using var host = new CommanderByteBridgeHost(factory);
        await host.StartAsync(CreateProfile());
        var commanderEndpoint = factory.Get("COM31");
        var radioEndpoint = factory.Get("COM3");

        await commanderEndpoint.FailReadAsync(new IOException("test disconnect"));
        await WaitForFaultCleanupAsync(host, commanderEndpoint, radioEndpoint);

        Assert.IsNotNull(host.Fault);
        Assert.IsTrue(commanderEndpoint.IsDisposed);
        Assert.IsTrue(radioEndpoint.IsDisposed);
        await AssertThrowsAsync<InvalidOperationException>(() => host.StartAsync(CreateProfile()));
    }

    internal static ProfileConfig CreateProfile() => new()
    {
        Id = 1,
        Label = "Commander bridge test",
        Enabled = true,
        PhysicalPort = "COM3",
        VirtualPort = "COM31",
        CommanderPort = "COM21",
        Adapter = AdapterKind.Passthrough
    };

    private static async Task WaitForFaultCleanupAsync(
        CommanderByteBridgeHost host, FakeEndpoint commanderEndpoint, FakeEndpoint radioEndpoint)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (host.State != CommanderBridgeState.Faulted ||
            !commanderEndpoint.IsDisposed || !radioEndpoint.IsDisposed)
            await Task.Delay(10, timeout.Token);
    }

    private static async Task AssertThrowsAsync<TException>(Func<Task> action)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        Assert.Fail($"Expected {typeof(TException).Name}.");
    }

    internal sealed class FakeEndpointFactory : IByteBridgeEndpointFactory
    {
        private readonly Dictionary<string, FakeEndpoint> endpoints = new(StringComparer.OrdinalIgnoreCase);

        public string? FailedPortName { get; init; }
        public List<string> OpenedPorts { get; } = [];

        public ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName, SerialSettings settings, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(portName, FailedPortName, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Could not open {portName}.");
            if (endpoints.TryGetValue(portName, out var existing) && !existing.IsDisposed)
                throw new IOException($"Fake endpoint {portName} is already open.");
            var endpoint = new FakeEndpoint();
            endpoints[portName] = endpoint;
            OpenedPorts.Add(portName);
            return ValueTask.FromResult<IByteBridgeEndpoint>(endpoint);
        }

        public FakeEndpoint Get(string portName) => endpoints[portName];
    }

    internal sealed class FakeEndpoint : IBufferedByteBridgeEndpoint
    {
        private readonly Channel<ReadItem> incoming = Channel.CreateUnbounded<ReadItem>();
        public int BufferedInputBytes => incoming.Reader.TryPeek(out var item) ? item.Bytes?.Length ?? 1 : 0;
        private readonly Channel<byte[]> written = Channel.CreateUnbounded<byte[]>();

        public bool IsDisposed { get; private set; }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            try
            {
                var item = await incoming.Reader.ReadAsync(cancellationToken);
                if (item.Error is not null) throw item.Error;
                var bytes = item.Bytes!;
                bytes.AsSpan().CopyTo(buffer.Span);
                return bytes.Length;
            }
            catch (ChannelClosedException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsDisposed) throw new IOException("Endpoint was closed.");
            if (!written.Writer.TryWrite(buffer.ToArray()))
                throw new IOException("Could not record fake endpoint output.");
            return ValueTask.CompletedTask;
        }

        public ValueTask SendAsync(byte[] bytes) =>
            incoming.Writer.WriteAsync(new(bytes, null));

        public ValueTask FailReadAsync(Exception exception) =>
            incoming.Writer.WriteAsync(new(null, exception));

        public async Task<byte[]> ReadWrittenAsync() =>
            await written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            incoming.Writer.TryComplete();
            written.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private sealed record ReadItem(byte[]? Bytes, Exception? Error);
    }
}
