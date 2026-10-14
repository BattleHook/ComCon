using System.Threading.Channels;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class SharedRadioEndpointSimulationTests
{
    [TestMethod]
    public void ThreeEndpointSimulationSerializesBothClientsAndRoutesRadioRx()
    {
        var simulation = new SharedRadioEndpointSimulation(Options());
        var legacyFirst = Civ(0xE0, 0x98);
        var legacySecond = Civ(0xE0, 0x99);
        var ipc = Civ(0xE1, 0x9A);
        simulation.LegacyClient.Send(legacyFirst.AsSpan(0, 3));
        simulation.LegacyClient.Send(legacyFirst.AsSpan(3));
        simulation.LegacyClient.Send(legacySecond);
        simulation.IpcClient.Send(ipc);

        simulation.PumpClient(SharedRadioClient.LegacyClient);
        simulation.PumpClient(SharedRadioClient.IpcClient);
        simulation.ProcessAvailable();
        simulation.CompleteRadioWrites(AdapterWriteCompletion.Succeeded);

        Assert.HasCount(3, simulation.Radio.Written);
        CollectionAssert.AreEqual(legacyFirst, simulation.Radio.Written[0]);
        CollectionAssert.AreEqual(ipc, simulation.Radio.Written[1]);
        CollectionAssert.AreEqual(legacySecond, simulation.Radio.Written[2]);

        var directed = Civ(0xE1, 0x10);
        var broadcast = Civ(0x00, 0x11);
        simulation.Radio.Send(directed);
        simulation.Radio.Send(broadcast);
        simulation.PumpRadio();
        simulation.ProcessAvailable();
        simulation.DeliverClient(SharedRadioClient.LegacyClient);
        simulation.DeliverClient(SharedRadioClient.IpcClient);

        Assert.HasCount(1, simulation.LegacyClient.Written);
        CollectionAssert.AreEqual(broadcast, simulation.LegacyClient.Written[0]);
        Assert.HasCount(2, simulation.IpcClient.Written);
        CollectionAssert.AreEqual(directed, simulation.IpcClient.Written[0]);
        CollectionAssert.AreEqual(broadcast, simulation.IpcClient.Written[1]);
    }

    [TestMethod]
    public void RadioIngressOverflowLatchesAndNeverAcceptsBytesAcrossTheGap()
    {
        foreach (var framing in new[] { FramingMode.Civ, FramingMode.KenwoodAscii })
        {
            var simulation = new SharedRadioEndpointSimulation(
                Options() with { Framing = framing, IngressChunksPerSource = 1 });
            simulation.Radio.Send(framing == FramingMode.Civ
                ? [0xFE, 0xFE, 0xE0, 0x00]
                : "FA000"u8.ToArray());
            Assert.IsTrue(simulation.PumpRadio());
            simulation.ProcessAvailable();

            simulation.Radio.Send([0x31]);
            simulation.Radio.Send([0x32]);
            Assert.IsFalse(simulation.PumpRadio());
            simulation.ProcessAvailable();

            Assert.AreEqual(SharedRadioCoordinatorState.Faulted, simulation.Coordinator.State);
            Assert.IsTrue(simulation.Coordinator.IsConnected(SharedRadioClient.LegacyClient));
            Assert.IsTrue(simulation.Coordinator.IsConnected(SharedRadioClient.IpcClient));
            simulation.Radio.Send(
                framing == FramingMode.Civ ? Civ(0xE0, 0x98) : "FA00014074000;"u8.ToArray());
            Assert.IsFalse(simulation.PumpRadio());
            simulation.ProcessAvailable();
            Assert.IsEmpty(simulation.LegacyClient.Written);
            Assert.IsEmpty(simulation.IpcClient.Written);
            Assert.IsFalse(simulation.Coordinator.TryTakeRadioWrite(out _));
        }
    }

    [TestMethod]
    public void UnknownRadioWriteStopsFurtherTxButKeepsClientRxDelivery()
    {
        var simulation = new SharedRadioEndpointSimulation(Options());
        simulation.LegacyClient.Send(Civ(0xE0, 0x98));
        simulation.IpcClient.Send(Civ(0xE1, 0x99));
        simulation.PumpClient(SharedRadioClient.LegacyClient);
        simulation.PumpClient(SharedRadioClient.IpcClient);
        simulation.ProcessAvailable();

        simulation.CompleteRadioWrites(AdapterWriteCompletion.Unknown);

        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, simulation.Coordinator.State);
        Assert.IsEmpty(simulation.Radio.Written);

        var rx = Civ(0xE1, 0x10);
        simulation.Radio.Send(rx);
        Assert.IsTrue(simulation.PumpRadio());
        simulation.ProcessAvailable();
        simulation.DeliverClient(SharedRadioClient.IpcClient);

        Assert.HasCount(1, simulation.IpcClient.Written);
        CollectionAssert.AreEqual(rx, simulation.IpcClient.Written[0]);
        Assert.IsEmpty(simulation.LegacyClient.Written);
        Assert.IsFalse(simulation.Coordinator.TryTakeRadioWrite(out _));
    }

    [TestMethod]
    public void ProfileChangeStopsOldCoordinatorAndUsesNewRoutingSnapshot()
    {
        var previous = new SharedRadioEndpointSimulation(Options());
        previous.LegacyClient.Send(Civ(0xE0, 0x98));
        Assert.IsTrue(previous.PumpClient(SharedRadioClient.LegacyClient));
        previous.ProcessAvailable();
        previous.Coordinator.Stop();

        Assert.IsFalse(previous.Coordinator.TryPostClientBytes(
            SharedRadioClient.LegacyClient, Civ(0xE0, 0x99)));
        Assert.IsFalse(previous.Coordinator.TryTakeRadioWrite(out _));

        var updated = new SharedRadioEndpointSimulation(
            Options() with { LegacyClientAddress = 0xE2 });
        var updatedRoute = Civ(0xE2, 0x10);
        updated.Radio.Send(updatedRoute);
        Assert.IsTrue(updated.PumpRadio());
        updated.ProcessAvailable();
        updated.DeliverClient(SharedRadioClient.LegacyClient);

        Assert.IsEmpty(previous.Radio.Written);
        Assert.HasCount(1, updated.LegacyClient.Written);
        CollectionAssert.AreEqual(updatedRoute, updated.LegacyClient.Written[0]);
        Assert.IsEmpty(updated.IpcClient.Written);
    }

    private static SharedRadioCoordinatorOptions Options() =>
        new(FramingMode.Civ, 0xE0, 0xE1,
            IngressChunksPerSource: 8,
            IngressBytesPerSource: 4096,
            TxQueueRecordsPerClient: 8,
            TxQueueBytesPerClient: 2048,
            TxQueueRecordsPerProfile: 16,
            ClientRxQueueRecordsPerClient: 8,
            ClientRxQueueBytesPerClient: 2048,
            QueueWaitLimitMs: 100);

    private static byte[] Civ(byte destination, byte command) =>
        [0xFE, 0xFE, destination, 0x00, command, 0xFD];

    private sealed class SharedRadioEndpointSimulation
    {
        public SharedRadioEndpointSimulation(SharedRadioCoordinatorOptions options)
        {
            Coordinator = new(options, new FakeClock(), _ => { });
        }

        public SharedRadioCoordinator Coordinator { get; }
        public FakeByteEndpoint LegacyClient { get; } = new();
        public FakeByteEndpoint IpcClient { get; } = new();
        public FakeByteEndpoint Radio { get; } = new();

        public bool PumpClient(SharedRadioClient client)
        {
            var endpoint = client == SharedRadioClient.LegacyClient ? LegacyClient : IpcClient;
            var accepted = true;
            while (endpoint.TryRead(out var bytes))
            {
                accepted &= Coordinator.TryPostClientBytes(client, bytes);
            }
            return accepted;
        }

        public bool PumpRadio()
        {
            var accepted = true;
            while (Radio.TryRead(out var bytes))
            {
                accepted &= Coordinator.TryPostRadioBytes(bytes);
            }
            return accepted;
        }

        public void ProcessAvailable() => Coordinator.ProcessAvailable();

        public void CompleteRadioWrites(AdapterWriteCompletion completion)
        {
            while (Coordinator.TryTakeRadioWrite(out var write))
            {
                if (completion == AdapterWriteCompletion.Succeeded)
                {
                    Radio.WriteAsync(write!.Bytes, CancellationToken.None).GetAwaiter().GetResult();
                }
                Coordinator.CompleteRadioWrite(write!.Id, completion);
            }
        }

        public void DeliverClient(SharedRadioClient client)
        {
            var endpoint = client == SharedRadioClient.LegacyClient ? LegacyClient : IpcClient;
            while (Coordinator.TryTakeClientDelivery(client, out var delivery))
            {
                endpoint.WriteAsync(delivery!.Bytes, CancellationToken.None).GetAwaiter().GetResult();
                Coordinator.CompleteClientDelivery(delivery.Id, delivered: true);
            }
        }
    }

    private sealed class FakeByteEndpoint : IByteBridgeEndpoint
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();

        public List<byte[]> Written { get; } = [];

        public void Send(ReadOnlySpan<byte> bytes)
        {
            if (!incoming.Writer.TryWrite(bytes.ToArray()))
            {
                throw new InvalidOperationException("Could not queue fake endpoint input.");
            }
        }

        public bool TryRead(out byte[] bytes) => incoming.Reader.TryRead(out bytes!);

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var bytes = await incoming.Reader.ReadAsync(cancellationToken);
            bytes.AsMemory().CopyTo(buffer);
            return bytes.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Written.Add(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs => 0;
    }
}
