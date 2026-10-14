using System.Diagnostics;
using System.Globalization;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
[TestCategory("Performance")]
public sealed class SharedRadioPerformanceTests
{
    private const int Seed = 1082026;
    private const int WarmupRuns = 1;
    private const int TimedRuns = 3;

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void SeededOfflineWorkloadsReportThroughputLatencyQueuesAndMemory()
    {
        var scenarios = new[]
        {
            new Workload("scheduler-isolated-legacy", FramingMode.Civ, 200, 0, false),
            new Workload("scheduler-isolated-ipc", FramingMode.Civ, 0, 200, false),
            new Workload("scheduler-balanced-burst", FramingMode.Civ, 400, 400, false),
            new Workload("capacity-load-50", FramingMode.Civ, 25, 25, false, TxCapacityPerProfile: 256),
            new Workload("capacity-load-100", FramingMode.Civ, 50, 50, false, TxCapacityPerProfile: 256),
            new Workload("capacity-load-200", FramingMode.Civ, 100, 100, false, TxCapacityPerProfile: 256),
            new Workload("capacity-load-224", FramingMode.Civ, 112, 112, false, TxCapacityPerProfile: 256),
            new Workload("capacity-load-256", FramingMode.Civ, 128, 128, false, TxCapacityPerProfile: 256),
            new Workload("capacity-load-288", FramingMode.Civ, 144, 144, false, TxCapacityPerProfile: 256),
            new Workload("capacity-load-400", FramingMode.Civ, 200, 200, false, TxCapacityPerProfile: 256),
            new Workload("end-to-end-sustained", FramingMode.Civ, 300, 300, true, Sustained: true),
            new Workload("end-to-end-asymmetric", FramingMode.KenwoodAscii, 500, 100, true),
            new Workload("radio-rx-fanout", FramingMode.Civ, 100, 100, true, IncludeRadioRx: true),
            new Workload("slow-client", FramingMode.Civ, 0, 0, true, SlowLegacyClient: true),
            new Workload("ingress-saturation", FramingMode.Civ, 0, 0, true, SaturateIngress: true)
        };

        TestContext.WriteLine(
            $"seed={Seed}; warmup={WarmupRuns}; timedRuns={TimedRuns}; " +
            $"runtime={Environment.Version}; os={Environment.OSVersion}; " +
            $"stopwatchFrequency={Stopwatch.Frequency}; build={GetBuildConfiguration()}");

        foreach (var scenario in scenarios)
        {
            for (var i = 0; i < WarmupRuns; i++)
            {
                _ = Run(scenario, Seed);
            }

            for (var run = 1; run <= TimedRuns; run++)
            {
                var result = Run(scenario, Seed);
                TestContext.WriteLine(result.ToString());
            }
        }
    }

    private static RunResult Run(Workload workload, int seed)
    {
        var random = new Random(seed);
        var diagnostics = new List<SharedRadioDiagnostic>();
        var clock = new FakeClock();
        var options = Options(workload.Framing, workload.SlowLegacyClient ? 16 : 2048,
            workload.SaturateIngress ? 1 : 16_384, workload.TxCapacityPerProfile);
        var coordinator = new SharedRadioCoordinator(options, clock, diagnostics.Add);
        var txLatencies = new List<long>();
        var rxLatencies = new List<long>();
        var memoryStart = GC.GetTotalMemory(forceFullCollection: true);
        var memoryHighWater = memoryStart;
        var stopwatch = Stopwatch.StartNew();
        var offeredTx = 0;
        var offeredRx = 0;
        var offeredIngressChunks = 0;
        var accepted = 0;
        var dispatched = 0;
        var delivered = 0;
        var legacyDispatched = 0;
        var ipcDispatched = 0;
        long legacyMaxWaitMs = 0;
        long ipcMaxWaitMs = 0;
        var drainMilliseconds = 0d;
        var dispatchedRecords = new List<byte[]>();

        if (workload.SaturateIngress)
        {
            offeredIngressChunks = 3;
            coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [0xFE]);
            coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [0xFE]);
            coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [0xFE]);
            coordinator.ProcessAvailable();
        }
        else if (workload.SlowLegacyClient)
        {
            var receiveCount = options.ClientRxQueueRecordsPerClient + 2;
            offeredRx = receiveCount;
            for (var i = 0; i < receiveCount; i++)
            {
                clock.NowMs++;
                coordinator.TryPostRadioBytes(Civ(0x00, 0x94, (byte)i));
                coordinator.ProcessAvailable();
                DrainClient(coordinator, SharedRadioClient.IpcClient, clock, rxLatencies, ref delivered);
                memoryHighWater = Math.Max(memoryHighWater, GC.GetTotalMemory(forceFullCollection: false));
            }
        }
        else
        {
            var legacyRecords = Enumerable.Range(0, workload.LegacyTxRecords)
                .Select(_ => CreateRecord(workload.Framing, 0xE0, random)).ToArray();
            var ipcRecords = Enumerable.Range(0, workload.IpcTxRecords)
                .Select(_ => CreateRecord(workload.Framing, 0xE1, random)).ToArray();
            offeredTx = legacyRecords.Length + ipcRecords.Length;

            if (workload.EndToEnd)
            {
                if (workload.Sustained)
                {
                    for (var offset = 0; offset < Math.Max(legacyRecords.Length, ipcRecords.Length); offset += 16)
                    {
                        PostChunked(coordinator, SharedRadioClient.LegacyClient,
                            legacyRecords.Skip(offset).Take(16).ToArray(), random, clock);
                        PostChunked(coordinator, SharedRadioClient.IpcClient,
                            ipcRecords.Skip(offset).Take(16).ToArray(), random, clock);
                        coordinator.ProcessAvailable();
                        DrainRadio(coordinator, clock, txLatencies, dispatchedRecords, ref dispatched,
                            ref legacyDispatched, ref ipcDispatched, ref legacyMaxWaitMs,
                            ref ipcMaxWaitMs, ref drainMilliseconds);
                    }
                }
                else
                {
                    PostChunked(coordinator, SharedRadioClient.LegacyClient, legacyRecords, random, clock);
                    PostChunked(coordinator, SharedRadioClient.IpcClient, ipcRecords, random, clock);
                }
            }
            else
            {
                foreach (var record in legacyRecords)
                {
                    clock.NowMs++;
                    coordinator.AdmitCompleteTxRecordForTesting(SharedRadioClient.LegacyClient, record);
                }
                foreach (var record in ipcRecords)
                {
                    clock.NowMs++;
                    coordinator.AdmitCompleteTxRecordForTesting(SharedRadioClient.IpcClient, record);
                }
            }

            coordinator.ProcessAvailable();
            accepted = checked((int)coordinator.Metrics.AcceptedTxRecords);
            DrainRadio(coordinator, clock, txLatencies, dispatchedRecords, ref dispatched,
                ref legacyDispatched, ref ipcDispatched, ref legacyMaxWaitMs,
                ref ipcMaxWaitMs, ref drainMilliseconds);

            if (workload.IncludeRadioRx)
            {
                offeredRx = dispatchedRecords.Count;
                for (var i = 0; i < dispatchedRecords.Count; i++)
                {
                    clock.NowMs++;
                    var address = i % 4 == 0 ? (byte)0 : i % 2 == 0 ? (byte)0xE0 : (byte)0xE1;
                    var rx = workload.Framing == FramingMode.Civ
                        ? Civ(address, 0x94, (byte)i)
                        : Kenwood(i);
                    PostChunked(coordinator, rx, random, radio: true);
                }
                coordinator.ProcessAvailable();
                foreach (var client in Enum.GetValues<SharedRadioClient>())
                {
                    DrainClient(coordinator, client, clock, rxLatencies, ref delivered);
                }
            }
            accepted = checked((int)coordinator.Metrics.AcceptedTxRecords);
        }

        var metrics = coordinator.Metrics;
        stopwatch.Stop();
        var memoryEnd = GC.GetTotalMemory(forceFullCollection: true);
        memoryHighWater = Math.Max(memoryHighWater, memoryEnd);
        var elapsedSeconds = Math.Max(stopwatch.Elapsed.TotalSeconds, double.Epsilon);
        return new(
            workload.Name,
            offeredTx,
            offeredRx,
            offeredIngressChunks,
            accepted,
            dispatched,
            delivered,
            legacyDispatched,
            ipcDispatched,
            legacyMaxWaitMs,
            ipcMaxWaitMs,
            dispatched / elapsedSeconds,
            Percentile(txLatencies, 0.50),
            Percentile(txLatencies, 0.95),
            Percentile(txLatencies, 0.99),
            Percentile(rxLatencies, 0.50),
            Percentile(rxLatencies, 0.95),
            Percentile(rxLatencies, 0.99),
            metrics.TxQueueRecordsHighWater,
            metrics.TxQueueBytesHighWater,
            metrics.LegacyTxQueueRecordsHighWater,
            metrics.LegacyTxQueueBytesHighWater,
            metrics.IpcTxQueueRecordsHighWater,
            metrics.IpcTxQueueBytesHighWater,
            metrics.LegacyRxQueueRecordsHighWater,
            metrics.LegacyRxQueueBytesHighWater,
            metrics.IpcRxQueueRecordsHighWater,
            metrics.IpcRxQueueBytesHighWater,
            metrics.LegacyIngressChunksHighWater,
            metrics.IpcIngressChunksHighWater,
            metrics.RadioIngressChunksHighWater,
            metrics.LegacyIngressBytesHighWater,
            metrics.IpcIngressBytesHighWater,
            metrics.RadioIngressBytesHighWater,
            memoryHighWater - memoryStart,
            coordinator.QueuedTxRecords,
            drainMilliseconds,
            diagnostics.Count(item => item.Kind == SharedRadioDiagnosticKind.ClientDisconnected),
            coordinator.State);
    }

    private static void DrainRadio(
        SharedRadioCoordinator coordinator,
        FakeClock clock,
        List<long> txLatencies,
        List<byte[]> dispatchedRecords,
        ref int dispatched,
        ref int legacyDispatched,
        ref int ipcDispatched,
        ref long legacyMaxWaitMs,
        ref long ipcMaxWaitMs,
        ref double drainMilliseconds)
    {
        var drain = Stopwatch.StartNew();
        while (coordinator.TryTakeRadioWrite(out var write))
        {
            var wait = Math.Max(0, clock.NowMs - write!.EnqueuedMs);
            txLatencies.Add(wait);
            dispatchedRecords.Add(write.Bytes);
            dispatched++;
            if (write.Client == SharedRadioClient.LegacyClient)
            {
                legacyDispatched++;
                legacyMaxWaitMs = Math.Max(legacyMaxWaitMs, wait);
            }
            else
            {
                ipcDispatched++;
                ipcMaxWaitMs = Math.Max(ipcMaxWaitMs, wait);
            }
            coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
        }
        drain.Stop();
        drainMilliseconds += drain.Elapsed.TotalMilliseconds;
    }

    private static void PostChunked(
        SharedRadioCoordinator coordinator,
        SharedRadioClient client,
        IReadOnlyList<byte[]> records,
        Random random,
        FakeClock clock)
    {
        foreach (var record in records)
        {
            clock.NowMs++;
            var offset = 0;
            while (offset < record.Length)
            {
                var count = Math.Min(random.Next(1, 12), record.Length - offset);
                if (!coordinator.TryPostClientBytes(client, record.AsSpan(offset, count)))
                {
                    Assert.Fail("Synthetic load unexpectedly saturated client ingress.");
                }
                offset += count;
            }
        }
    }

    private static void PostChunked(
        SharedRadioCoordinator coordinator,
        byte[] record,
        Random random,
        bool radio)
    {
        var offset = 0;
        while (offset < record.Length)
        {
            var count = Math.Min(random.Next(1, 12), record.Length - offset);
            var accepted = radio
                ? coordinator.TryPostRadioBytes(record.AsSpan(offset, count))
                : coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, record.AsSpan(offset, count));
            if (!accepted)
            {
                Assert.Fail("Synthetic load unexpectedly saturated ingress.");
            }
            offset += count;
        }
    }

    private static void DrainClient(
        SharedRadioCoordinator coordinator,
        SharedRadioClient client,
        FakeClock clock,
        List<long> latencies,
        ref int delivered)
    {
        while (coordinator.TryTakeClientDelivery(client, out var delivery))
        {
            latencies.Add(Math.Max(0, clock.NowMs - delivery!.ReceivedMs));
            coordinator.CompleteClientDelivery(delivery.Id, delivered: true);
            delivered++;
        }
    }

    private static byte[] CreateRecord(FramingMode framing, byte destination, Random random)
    {
        if (framing == FramingMode.KenwoodAscii)
        {
            var length = random.Next(6, 40);
            var record = new byte[length];
            for (var i = 0; i < length - 1; i++)
            {
                record[i] = (byte)random.Next(0x20, 0x7F);
            }
            record[^1] = (byte)';';
            return record;
        }

        var civLength = random.Next(6, 32);
        var frame = new byte[civLength];
        frame[0] = 0xFE;
        frame[1] = 0xFE;
        frame[2] = destination;
        frame[3] = 0x94;
        for (var i = 4; i < frame.Length - 1; i++)
        {
            byte value;
            do
            {
                value = (byte)random.Next(0x20, 0xFD);
            } while (value is 0xFC or 0xFE);
            frame[i] = value;
        }
        frame[^1] = 0xFD;
        return frame;
    }

    private static byte[] Civ(byte destination, byte source, byte command) =>
        [0xFE, 0xFE, destination, source, command, 0xFD];

    private static byte[] Kenwood(int index) =>
        System.Text.Encoding.ASCII.GetBytes($"FA0001{index % 10:0}00;");

    private static SharedRadioCoordinatorOptions Options(
        FramingMode framing,
        int clientRxCapacity,
        int ingressChunks,
        int txCapacityPerProfile) =>
        new(framing, 0xE0, 0xE1,
            IngressChunksPerSource: ingressChunks,
            IngressBytesPerSource: 1_000_000,
            TxQueueRecordsPerClient: txCapacityPerProfile,
            TxQueueBytesPerClient: 1_000_000,
            TxQueueRecordsPerProfile: txCapacityPerProfile,
            ClientRxQueueRecordsPerClient: clientRxCapacity,
            ClientRxQueueBytesPerClient: 1_000_000,
            QueueWaitLimitMs: 1_000_000);

    private static long Percentile(IReadOnlyList<long> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static string GetBuildConfiguration() =>
        AppContext.BaseDirectory.Contains("Release", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : "Debug";

    private sealed record Workload(
        string Name,
        FramingMode Framing,
        int LegacyTxRecords,
        int IpcTxRecords,
        bool EndToEnd,
        int TxCapacityPerProfile = 4096,
        bool Sustained = false,
        bool IncludeRadioRx = false,
        bool SlowLegacyClient = false,
        bool SaturateIngress = false);

    private sealed record RunResult(
        string Scenario,
        int OfferedTx,
        int OfferedRx,
        int OfferedIngressChunks,
        int Accepted,
        int Dispatched,
        int Delivered,
        int LegacyDispatched,
        int IpcDispatched,
        long LegacyMaxWaitMs,
        long IpcMaxWaitMs,
        double ThroughputRecordsPerSecond,
        long TxP50Ms,
        long TxP95Ms,
        long TxP99Ms,
        long RxP50Ms,
        long RxP95Ms,
        long RxP99Ms,
        int TxQueueRecordsHighWater,
        int TxQueueBytesHighWater,
        int LegacyTxQueueRecordsHighWater,
        int LegacyTxQueueBytesHighWater,
        int IpcTxQueueRecordsHighWater,
        int IpcTxQueueBytesHighWater,
        int LegacyRxQueueRecordsHighWater,
        int LegacyRxQueueBytesHighWater,
        int IpcRxQueueRecordsHighWater,
        int IpcRxQueueBytesHighWater,
        int LegacyIngressChunksHighWater,
        int IpcIngressChunksHighWater,
        int RadioIngressChunksHighWater,
        int LegacyIngressBytesHighWater,
        int IpcIngressBytesHighWater,
        int RadioIngressBytesHighWater,
        long EstimatedManagedHeapGrowthBytes,
        int RemainingQueuedRecords,
        double DrainMs,
        int ClientDisconnects,
        SharedRadioCoordinatorState State)
    {
        public override string ToString() =>
            string.Join("; ",
                $"scenario={Scenario}",
                $"offered_tx/rx/ingress_chunks={OfferedTx}/{OfferedRx}/{OfferedIngressChunks}",
                $"accepted={Accepted}",
                $"dispatched={Dispatched}",
                $"service_share={LegacyDispatched}/{IpcDispatched}",
                $"max_wait={LegacyMaxWaitMs}/{IpcMaxWaitMs} ms",
                $"delivered={Delivered}",
                $"throughput={ThroughputRecordsPerSecond.ToString("F0", CultureInfo.InvariantCulture)} records/s",
                $"tx_p50/p95/p99={TxP50Ms}/{TxP95Ms}/{TxP99Ms} ms",
                $"rx_p50/p95/p99={RxP50Ms}/{RxP95Ms}/{RxP99Ms} ms",
                $"tx_queue_high_water={TxQueueRecordsHighWater} records/{TxQueueBytesHighWater} bytes",
                $"legacy_tx_queue_high_water={LegacyTxQueueRecordsHighWater} records/{LegacyTxQueueBytesHighWater} bytes",
                $"ipc_tx_queue_high_water={IpcTxQueueRecordsHighWater} records/{IpcTxQueueBytesHighWater} bytes",
                $"legacy_rx_queue_high_water={LegacyRxQueueRecordsHighWater} records/{LegacyRxQueueBytesHighWater} bytes",
                $"ipc_rx_queue_high_water={IpcRxQueueRecordsHighWater} records/{IpcRxQueueBytesHighWater} bytes",
                $"ingress_chunk_high_water={LegacyIngressChunksHighWater}/{IpcIngressChunksHighWater}/{RadioIngressChunksHighWater}",
                $"ingress_byte_high_water={LegacyIngressBytesHighWater}/{IpcIngressBytesHighWater}/{RadioIngressBytesHighWater}",
                $"estimated_heap_growth={EstimatedManagedHeapGrowthBytes} bytes",
                $"drain_ms={DrainMs.ToString("F2", CultureInfo.InvariantCulture)}",
                $"client_disconnects={ClientDisconnects}",
                $"state={State}");
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
