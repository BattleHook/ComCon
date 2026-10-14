using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

[TestClass]
[TestCategory("Measurement")]
[DoNotParallelize]
[SupportedOSPlatform("windows")]
public sealed class ComConOfflineMeasurementTests
{
    private const int Seed = 10102026;
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task MeasurementReceiptReaderAcceptsOnlyIdenticalPreviouslyValidatedRetries()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var first = Receipt(1);
        var second = Receipt(2);
        using var stream = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        foreach (var payload in new[] { first, first, second, first })
            await ComConCommandProtocol.WriteEnvelopeAsync(stream, payload, writer, timeout.Token);
        stream.Position = 0;
        var reader = new ComConCommandIntegrationTests.MeasurementReceiptReader();
        var one = await reader.ReadNext(stream, timeout.Token);
        reader.Remember(one);
        var two = await reader.ReadNext(stream, timeout.Token);
        Assert.AreEqual("2", two.GetProperty("notificationSequence").GetString());
        reader.Remember(two);
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => reader.ReadNext(stream, timeout.Token));
    }

    [TestMethod]
    [DataRow(1, 2, "12345678-1234-4234-8234-123456789abc")]
    [DataRow(1, 1, "12345678-1234-4234-8234-123456789abd")]
    [DataRow(3, 3, "12345678-1234-4234-8234-123456789abc")]
    public async Task MeasurementReceiptReaderRejectsChangedOrOutOfOrderReceipts(
        int sequence, int recordSequence, string sessionId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var stream = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(stream, Receipt(sequence, recordSequence, sessionId),
            writer, timeout.Token);
        stream.Position = 0;
        var reader = new ComConCommandIntegrationTests.MeasurementReceiptReader();
        using var first = JsonDocument.Parse(Receipt(1));
        reader.Remember(first.RootElement);
        await Assert.ThrowsExactlyAsync<AssertFailedException>(() => reader.ReadNext(stream, timeout.Token));
    }

    private static byte[] Receipt(int sequence, int? recordSequence = null,
        string sessionId = "12345678-1234-4234-8234-123456789abc") =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "record.outcome", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId, notificationSequence = sequence.ToString(CultureInfo.InvariantCulture),
            recordSequence = (recordSequence ?? sequence).ToString(CultureInfo.InvariantCulture),
            deliveryStatus = "WRITE_SUCCEEDED"
        });

    [TestMethod]
    [TestCategory("Measurement")]
    public async Task ComConOfflineMeasurementCombinedRealClockScenarios()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var results = new List<ScenarioResult>();
        foreach (var scenario in new[]
        {
            new Scenario("idle", 1, 0),
            new Scenario("steady", 4, 200),
            new Scenario("notification-bursts", 4, 198, Burst: 6),
            new Scenario("capacity-edge-bursts", 4, 200, Burst: 8),
            new Scenario("sustained-bursts", 4, 800, Burst: 8),
            new Scenario("slow-radio-bursts", 4, 200, Burst: 8, RadioResponseDelayMs: 10),
            new Scenario("maximum-record-bursts", 4, 200, Burst: 8, RecordLength: 64),
            new Scenario("expanded-capacity-large-records", 4, 256, Burst: 16,
                RadioResponseDelayMs: 10, RecordLength: 64,
                QueueCapacityRecords: 16, QueueCapacityBytes: 1024),
            new Scenario("slow-recipient", 4, 200, Slow: true),
            new Scenario("stop-recovery", 4, 200, Recover: true)
        })
        {
            results.Add(await Measure(scenario, timeout.Token));
        }
        var report = new
        {
            utc = DateTimeOffset.UtcNow, machineName = Environment.MachineName,
            processorCount = Environment.ProcessorCount, runtimeVersion = Environment.Version.ToString(),
            osVersion = Environment.OSVersion.ToString(), stopwatchFrequency = Stopwatch.Frequency,
            buildConfiguration = BuildConfiguration, seed = Seed,
            simulatedRadioResponseDelayMs = "specified per scenario; 1 ms default",
            slowControlPayloadDelayMs = 8,
            retentionLimitCount = 32, retentionLimitBytes = 32768,
            acknowledgmentTimeoutMs = 2000, retryIntervalMs = 500,
            deferredAcknowledgments = 8, controlPollIntervalMs = 2,
            coordinatorMeasurementOptions = new
            {
                ingressChunksPerSource = 32, ingressBytesPerSource = 512,
                txQueueRecordsPerClient = 8, txQueueBytesPerClient = 512,
                txQueueRecordsPerProfile = 16,
                clientRxQueueRecordsPerClient = 8, clientRxQueueBytesPerClient = 512,
                queueWaitLimitMs = 1000,
                note = "Baseline offline measurement-only capacities; expanded scenario records any larger overrides; not production bounds"
            },
            commandRttDefinition = "CAT write start to complete correlated fake-radio CAT response read",
            ackLatencyDefinition = "Server notification write start (including injected delay) to client observation that ACK drained retention; includes polling and scheduling, not exact server ACK timestamp",
            notificationDepthDefinition = "Sampled existing notification PendingCount/RetainedPayloadBytes; lower bounds on maxima",
            coordinatorMetricsDefinition = "Per-profile coordinator high-water metrics captured on the serialized actor; TX/RX and ingress queue maxima plus maximum complete-record admission-to-dispatch queue wait; not production bounds",
            shutdownDefinition = "Orderly stop overlaps final unacknowledged receipts on all four active command sessions; no submissions after stop begins",
            recoveryDefinition = "New supervisor with same pipe names/settings and fresh fake endpoints: construction, startup, all session handshakes and one verified command/ACK per profile",
            scenarios = results
        };
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "ComConApp.sln")))
            root = root.Parent ?? throw new InvalidOperationException("Cannot locate measurement artifact root.");
        var directory = Path.Combine(root.FullName, "artifacts", "measurements-6c");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"6c-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            await JsonSerializer.SerializeAsync(output, report, new JsonSerializerOptions { WriteIndented = true }, timeout.Token);
        TestContext.WriteLine($"seed={Seed}; runtime={Environment.Version}; stopwatchFrequency={Stopwatch.Frequency}; build={BuildConfiguration}; JSON={path}");
        foreach (var result in results)
            TestContext.WriteLine(JsonSerializer.Serialize(result));
        TestContext.AddResultFile(path);
    }

    private static async Task<ScenarioResult> Measure(Scenario scenario, CancellationToken token)
    {
        var before = MemorySnapshot.Take();
        await using var fixture = new ComConCommandIntegrationTests.MeasurementFixture(
            scenario.Profiles, scenario.Slow, scenario.RadioResponseDelayMs,
            scenario.RecordLength, scenario.QueueCapacityRecords, scenario.QueueCapacityBytes);
        var startup = Stopwatch.StartNew();
        await fixture.Start(token);
        startup.Stop();
        await using var sampling = new Sampler(fixture, token);
        var rtt = new ConcurrentBag<double>();
        var ack = new ConcurrentBag<double>();
        var load = Stopwatch.StartNew();
        double? idleWorkingSet = null;
        if (scenario.Commands == 0)
        {
            await Task.Delay(250, token);
            idleWorkingSet = MemorySnapshot.Take().WorkingSetBytes;
        }
        var finalReceipts = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        var workers = Enumerable.Range(0, scenario.Profiles).Select(async profile =>
        {
            var random = new Random(Seed + profile);
            for (var offset = 0; offset < scenario.Commands; offset += scenario.Burst)
            {
                var batch = Math.Min(scenario.Burst, scenario.Commands - offset);
                if (batch > 1)
                {
                    foreach (var result in await fixture.CommandBurst(
                        profile, offset + 1,
                        Enumerable.Range(0, batch).Select(_ => (byte)random.Next(1, 121)).ToArray(),
                        token))
                    {
                        rtt.Add(result.CommandRttMs);
                        ack.Add(result.NotificationAckMs);
                    }
                    continue;
                }
                else
                    rtt.Add(await fixture.Command(profile, offset + 1, (byte)random.Next(1, 121), token));

                for (var item = 0; item < batch; item++)
                {
                    var sequence = offset + item + 1;
                    await fixture.ReadOutcome(profile, sequence, token);
                    if (scenario.Recover && sequence == scenario.Commands)
                    {
                        if (Interlocked.Increment(ref ready) == scenario.Profiles) finalReceipts.TrySetResult();
                        await release.Task.WaitAsync(token);
                    }
                    ack.Add(await fixture.Acknowledge(profile, sequence, token));
                }
            }
        }).ToArray();
        double stopMs;
        if (scenario.Recover)
        {
            await finalReceipts.Task.WaitAsync(token);
            var stop = Stopwatch.StartNew();
            var stopping = fixture.Stop(token);
            release.TrySetResult();
            await Task.WhenAll(workers);
            load.Stop();
            await stopping;
            stopMs = stop.Elapsed.TotalMilliseconds;
        }
        else
        {
            await Task.WhenAll(workers);
            load.Stop();
            var stop = Stopwatch.StartNew();
            await fixture.Stop(token);
            stopMs = stop.Elapsed.TotalMilliseconds;
        }
        sampling.Observe();
        fixture.AssertCorrect(scenario.Commands);
        double? recoveryMs = null;
        if (scenario.Recover)
        {
            var recovery = Stopwatch.StartNew();
            await using var fresh = fixture.Fresh();
            await fresh.Start(token);
            for (var profile = 0; profile < scenario.Profiles; profile++)
            {
                await fresh.Command(profile, 1, 42, token);
                await fresh.ReadOutcome(profile, 1, token);
                await fresh.Acknowledge(profile, 1, token);
            }
            recoveryMs = recovery.Elapsed.TotalMilliseconds;
            await fresh.Stop(token);
            fresh.AssertCorrect(1);
        }
        var after = MemorySnapshot.Take();
        var commandStats = Percentiles.From(rtt);
        if (commandStats is not null) Assert.IsLessThan(5000d, commandStats.Max, "Loose RTT sanity ceiling only.");
        var coordinatorMetrics = fixture.CoordinatorMetrics;
        Assert.HasCount(scenario.Profiles, coordinatorMetrics, "Each fake profile must expose its final coordinator metrics snapshot.");
        Assert.AreEqual(scenario.Profiles * scenario.Commands,
            coordinatorMetrics.Sum(metrics => metrics.DispatchedTxRecords),
            "Coordinator dispatch counts must account for every measured command.");
        return new ScenarioResult(scenario.Name, scenario.Profiles, scenario.Commands,
            scenario.Burst, scenario.RadioResponseDelayMs, scenario.Slow, scenario.RecordLength,
            scenario.QueueCapacityRecords, scenario.QueueCapacityBytes,
            rtt.Count, ack.Count, commandStats, Percentiles.From(ack),
            scenario.Commands == 0 ? null : rtt.Count / load.Elapsed.TotalSeconds,
            load.Elapsed.TotalMilliseconds, startup.Elapsed.TotalMilliseconds, stopMs, recoveryMs,
            sampling.MaximumPending, sampling.MaximumRetainedBytes, coordinatorMetrics,
            before.WorkingSetBytes, after.WorkingSetBytes, sampling.MaximumWorkingSetBytes, idleWorkingSet,
            after.AllocatedBytes - before.AllocatedBytes,
            after.Collections.Zip(before.Collections, (a, b) => a - b).ToArray(),
            "Exact correlated responses, ordered record/notification sequences and ACK drains verified per session; no faults");
    }

    private sealed class Sampler : IAsyncDisposable
    {
        private readonly ComConCommandIntegrationTests.MeasurementFixture fixture;
        private readonly CancellationTokenSource stop;
        private readonly Task sampling;
        public int MaximumPending { get; private set; }
        public int MaximumRetainedBytes { get; private set; }
        public long MaximumWorkingSetBytes { get; private set; }
        public Sampler(ComConCommandIntegrationTests.MeasurementFixture fixture, CancellationToken token)
        {
            this.fixture = fixture;
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            sampling = Run();
        }
        public void Observe()
        {
            lock (this)
            {
                MaximumPending = Math.Max(MaximumPending, fixture.MaximumPending);
                MaximumRetainedBytes = Math.Max(MaximumRetainedBytes, fixture.MaximumRetainedBytes);
                using var process = Process.GetCurrentProcess();
                MaximumWorkingSetBytes = Math.Max(MaximumWorkingSetBytes, process.WorkingSet64);
            }
        }
        private async Task Run()
        {
            try { while (true) { Observe(); await Task.Delay(2, stop.Token); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await sampling; stop.Dispose(); }
    }

    private sealed record Scenario(
        string Name, int Profiles, int Commands, int Burst = 1, bool Slow = false,
        bool Recover = false, int RadioResponseDelayMs = 1, int RecordLength = 7,
        int QueueCapacityRecords = 8, int QueueCapacityBytes = 512);
    private sealed record Percentiles(double P50, double P95, double P99, double Max)
    {
        public static Percentiles? From(IEnumerable<double> values)
        {
            var sorted = values.Order().ToArray();
            if (sorted.Length == 0) return null;
            double P(double p) => sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * p) - 1)];
            return new(P(.50), P(.95), P(.99), sorted[^1]);
        }
    }
    private sealed record MemorySnapshot(long WorkingSetBytes, long AllocatedBytes, int[] Collections)
    {
        public static MemorySnapshot Take()
        {
            using var process = Process.GetCurrentProcess();
            return new(process.WorkingSet64, GC.GetTotalAllocatedBytes(precise: true),
                Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray());
        }
    }
    private sealed record ScenarioResult(
        string Name, int Profiles, int CommandsPerProfile, int BurstSize,
        int FakeRadioResponseDelayMs, bool SlowNotificationRecipient, int CommandRecordBytes,
        int QueueCapacityRecordsPerClient, int QueueCapacityBytesPerClient,
        int Responses, int Acknowledgments, Percentiles? CommandRttMs, Percentiles? NotificationAckMs,
        double? CommandsPerSecond, double WorkloadDurationMs, double StartupMs, double OrderlyStopMs,
        double? RecoveryMs, int MaximumObservedPendingPerSession, int MaximumObservedRetainedBytesPerSession,
        SharedRadioCoordinatorMetrics[] CoordinatorMetrics, long WorkingSetStartBytes, long WorkingSetEndBytes,
        long MaximumObservedWorkingSetBytes, double? IdleWorkingSetBytes, long AllocatedBytesDelta,
        int[] GcCollectionsDelta, string Correctness);
#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif
}

public sealed partial class ComConCommandIntegrationTests
{
    internal sealed class MeasurementReceiptReader
    {
        private readonly Dictionary<long, string> receipts = [];
        private long latest;

        public void Remember(JsonElement envelope)
        {
            var sequence = long.Parse(envelope.GetProperty("notificationSequence").GetString()!,
                CultureInfo.InvariantCulture);
            Assert.AreEqual(latest + 1, sequence);
            receipts.Add(sequence, envelope.GetRawText());
            latest = sequence;
        }

        public async Task<JsonElement> ReadNext(Stream stream, CancellationToken token)
        {
            while (true)
            {
                var envelope = await ComConCommandProtocol.ReadEnvelopeAsync(stream, token);
                var sequence = long.Parse(envelope.GetProperty("notificationSequence").GetString()!,
                    CultureInfo.InvariantCulture);
                if (sequence > latest)
                {
                    Assert.AreEqual(latest + 1, sequence, "New receipts must remain strictly ordered.");
                    return envelope;
                }
                Assert.IsTrue(receipts.TryGetValue(sequence, out var prior), "Unknown prior notification.");
                Assert.AreEqual(prior, envelope.GetRawText(), "A retry must preserve the entire receipt.");
            }
        }
    }

    // Keeping this fixture in the existing partial class reuses its fake ports, options and handshake peer.
    [SupportedOSPlatform("windows")]
    internal sealed class MeasurementFixture : IAsyncDisposable
    {
        private readonly ComCon.Core.Models.AppSettings settings;
        private readonly ComConOfflineProfilePlan[] plans;
        private readonly FakeFactory[] factories;
        private readonly ConcurrentQueue<Exception> failures = new();
        private readonly ConcurrentQueue<SharedRadioDiagnostic> diagnostics = new();
        private readonly ConcurrentDictionary<int, MeasuredControlStream> streams = new();
        private readonly MultiProfilePeer[] peers;
        private readonly int[] commands;
        private readonly int[] outcomes;
        private readonly int[] acknowledgments;
        private readonly MeasurementReceiptReader[] receiptReaders;
        private readonly bool slow;
        private readonly int radioResponseDelayMs;
        private readonly int recordLength;
        private readonly int queueCapacityRecords;
        private readonly int queueCapacityBytes;
        private readonly ComConOfflineSupervisor supervisor;
        public int MaximumPending => streams.Values.Select(s => s.Tracker.PendingCount).DefaultIfEmpty().Max();
        public int MaximumRetainedBytes => streams.Values.Select(s => s.Tracker.RetainedPayloadBytes).DefaultIfEmpty().Max();
        public SharedRadioCoordinatorMetrics[] CoordinatorMetrics => supervisor.Profiles
            .Select(profile => profile.Host.CoordinatorMetrics
                ?? throw new InvalidOperationException($"Profile {profile.Profile.Id} has no coordinator metrics snapshot."))
            .ToArray();

        public MeasurementFixture(
            int profiles, bool slow, int radioResponseDelayMs, int recordLength,
            int queueCapacityRecords, int queueCapacityBytes) :
            this(SupervisorSettings(profiles), null, slow, radioResponseDelayMs, recordLength,
                queueCapacityRecords, queueCapacityBytes) { }
        private MeasurementFixture(
            ComCon.Core.Models.AppSettings settings, ComConOfflineProfilePlan[]? prior,
            bool slow, int radioResponseDelayMs, int recordLength,
            int queueCapacityRecords, int queueCapacityBytes)
        {
            this.settings = settings;
            this.slow = slow;
            this.radioResponseDelayMs = radioResponseDelayMs;
            this.recordLength = recordLength;
            this.queueCapacityRecords = queueCapacityRecords;
            this.queueCapacityBytes = queueCapacityBytes;
            factories = Enumerable.Range(1, settings.Profiles.Count).Select(id => new FakeFactory { RadioPort = $"COM{100 + id}" }).ToArray();
            plans = SupervisorPlans(factories);
            for (var i = 0; i < plans.Length; i++)
                plans[i] = plans[i] with
                {
                    TestPipeName = prior?[i].TestPipeName ?? plans[i].TestPipeName,
                    EndpointFactory = new RespondingFactory(factories[i], radioResponseDelayMs, recordLength),
                    CaptureCoordinatorMetrics = true,
                    CoordinatorOptions = plans[i].CoordinatorOptions with
                    {
                        IngressChunksPerSource = queueCapacityRecords * 4,
                        IngressBytesPerSource = queueCapacityBytes,
                        TxQueueRecordsPerClient = queueCapacityRecords,
                        TxQueueBytesPerClient = queueCapacityBytes,
                        TxQueueRecordsPerProfile = queueCapacityRecords * 2,
                        ClientRxQueueRecordsPerClient = queueCapacityRecords,
                        ClientRxQueueBytesPerClient = queueCapacityBytes,
                        QueueWaitLimitMs = 1000
                    }
                };
            peers = new MultiProfilePeer[plans.Length];
            commands = new int[plans.Length];
            outcomes = new int[plans.Length];
            acknowledgments = new int[plans.Length];
            receiptReaders = Enumerable.Range(0, plans.Length).Select(_ => new MeasurementReceiptReader()).ToArray();
            supervisor = new(settings, plans, new RealClock(), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
                (_, error) => failures.Enqueue(error), (_, diagnostic) => diagnostics.Enqueue(diagnostic))
            {
                WrapControlStream = (id, stream, tracker) =>
                {
                    var measured = new MeasuredControlStream(stream, tracker, slow && id == 1);
                    streams[id] = measured;
                    return measured;
                }
            };
        }
        public MeasurementFixture Fresh() => new(settings, plans, slow, radioResponseDelayMs, recordLength,
            queueCapacityRecords, queueCapacityBytes);
        public async Task Start(CancellationToken token)
        {
            await supervisor.StartAsync(token);
            for (var i = 0; i < peers.Length; i++)
                peers[i] = await MultiProfilePeer.Open(supervisor.Profiles[i].Listener, settings.RigProfileMappings[i].RigProfileId, i + 1, token, null);
            while (streams.Count != peers.Length) await Task.Delay(1, token);
        }
        public async Task<double> Command(int profile, int sequence, byte payload, CancellationToken token)
        {
            Assert.AreEqual(commands[profile] + 1, sequence);
            var timer = Stopwatch.StartNew();
            var wireSequence = WireSequence(sequence);
            await peers[profile].Cat.WriteAsync(BuildCommand(wireSequence, payload, recordLength), token);
            var response = new byte[recordLength];
            await peers[profile].Cat.ReadExactlyAsync(response, token);
            var elapsed = timer.Elapsed.TotalMilliseconds;
            CollectionAssert.AreEqual(BuildResponse(wireSequence, payload, recordLength), response,
                $"Profile {profile + 1}, response {sequence}");
            commands[profile]++;
            return elapsed;
        }
        public async Task<(double CommandRttMs, double NotificationAckMs)[]> CommandBurst(
            int profile, int firstSequence, IReadOnlyList<byte> payloads, CancellationToken token)
        {
            Assert.IsGreaterThan(1, payloads.Count);
            Assert.AreEqual(commands[profile] + 1, firstSequence);
            var starts = new long[payloads.Count];
            for (var index = 0; index < payloads.Count; index++)
            {
                starts[index] = Stopwatch.GetTimestamp();
                await peers[profile].Cat.WriteAsync(
                    BuildCommand(WireSequence(firstSequence + index), payloads[index], recordLength), token);
            }

            var results = new (double CommandRttMs, double NotificationAckMs)[payloads.Count];
            for (var index = 0; index < payloads.Count; index++)
            {
                var response = new byte[recordLength];
                var sequence = firstSequence + index;
                try
                {
                    await peers[profile].Cat.ReadExactlyAsync(response, token);
                }
                catch (EndOfStreamException exception)
                {
                    var failureDetails = string.Join(" | ", failures.Select(failure => failure.Message));
                    var diagnosticDetails = string.Join(" | ", diagnostics.Select(diagnostic =>
                        $"{diagnostic.Kind}: {diagnostic.Message}"));
                    throw new InvalidOperationException(
                        $"Profile {profile + 1} closed during burst response {sequence}. " +
                        $"Failures: {failureDetails}; diagnostics: {diagnosticDetails}", exception);
                }
                CollectionAssert.AreEqual(
                    BuildResponse(WireSequence(sequence), payloads[index], recordLength), response,
                    $"Profile {profile + 1}, burst response {sequence}");
                var commandRttMs = Stopwatch.GetElapsedTime(starts[index]).TotalMilliseconds;
                commands[profile]++;
                await ReadOutcome(profile, sequence, token);
                var notificationAckMs = await Acknowledge(profile, sequence, token);
                results[index] = (commandRttMs, notificationAckMs);
            }
            return results;
        }

        private static byte[] BuildCommand(byte sequence, byte payload, int recordLength)
        {
            var command = Enumerable.Repeat((byte)0x55, recordLength).ToArray();
            command[0] = 0xFE;
            command[1] = 0xFE;
            command[2] = 0x94;
            command[3] = 0xE1;
            command[4] = sequence;
            command[5] = payload;
            command[^1] = 0xFD;
            return command;
        }

        private static byte[] BuildResponse(byte sequence, byte payload, int recordLength)
        {
            var response = BuildCommand(sequence, payload, recordLength);
            response[2] = 0xE1;
            response[3] = 0x94;
            return response;
        }

        private static byte WireSequence(int sequence) => (byte)((sequence - 1) % 120 + 1);
        public async Task ReadOutcome(int profile, int sequence, CancellationToken token)
        {
            Assert.AreEqual(outcomes[profile] + 1, sequence);
            var envelope = await receiptReaders[profile].ReadNext(peers[profile].Control, token);
            AssertOutcome(envelope, "record.outcome", sequence.ToString(CultureInfo.InvariantCulture), "WRITE_SUCCEEDED");
            Assert.AreEqual(profile + 1, envelope.GetProperty("profileId").GetInt32());
            Assert.AreEqual(sequence.ToString(CultureInfo.InvariantCulture), envelope.GetProperty("notificationSequence").GetString());
            Assert.AreEqual(peers[profile].SessionId, envelope.GetProperty("sessionId").GetString());
            receiptReaders[profile].Remember(envelope);
            outcomes[profile]++;
        }
        public async Task<double> Acknowledge(int profile, int sequence, CancellationToken token)
        {
            var stream = streams[profile + 1];
            var start = stream.WriteStarts[sequence];
            // PendingCount is existing synchronized state: require each receipt to be actually drained, not just sent.
            var remaining = stream.Tracker.PendingCount - 1;
            await peers[profile].Ack(sequence.ToString(CultureInfo.InvariantCulture), token);
            Interlocked.Increment(ref acknowledgments[profile]);
            while (stream.Tracker.PendingCount > remaining) await Task.Delay(1, token);
            return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        public async Task Stop(CancellationToken token)
        {
            var stopping = supervisor.StopOrderlyAsync("Measurement orderly stop.", token);
            var drains = Enumerable.Range(0, peers.Length).Select(async profile =>
            {
                while (Volatile.Read(ref acknowledgments[profile]) != commands[profile]) await Task.Delay(1, token);
                var closing = await receiptReaders[profile].ReadNext(peers[profile].Control, token);
                Assert.AreEqual("session.notification", closing.GetProperty("messageType").GetString());
                Assert.AreEqual("session.ended", closing.GetProperty("event").GetString());
                Assert.AreEqual(profile + 1, closing.GetProperty("profileId").GetInt32());
                Assert.AreEqual(peers[profile].SessionId, closing.GetProperty("sessionId").GetString());
                Assert.AreEqual((commands[profile] + 1).ToString(CultureInfo.InvariantCulture), closing.GetProperty("notificationSequence").GetString());
                receiptReaders[profile].Remember(closing);
                await peers[profile].Ack(closing.GetProperty("notificationSequence").GetString()!, token);
            });
            await Task.WhenAll(drains.Append(stopping));
        }
        public void AssertCorrect(int count)
        {
            Assert.IsTrue(commands.All(value => value == count));
            Assert.IsTrue(outcomes.All(value => value == count));
            Assert.IsTrue(acknowledgments.All(value => value == count));
            Assert.IsTrue(factories.All(factory => factory.Radio.WriteCount == count));
            Assert.IsEmpty(failures);
            Assert.IsFalse(diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.ProfileFaulted));
            Assert.IsTrue(streams.Values.All(s => s.Tracker.PendingCount == 0));
            Assert.IsTrue(supervisor.Profiles.All(p => p.Host.State == SharedRadioHostState.Stopped && p.Completion.IsCompletedSuccessfully));
        }
        public async ValueTask DisposeAsync()
        {
            await supervisor.DisposeAsync();
            foreach (var peer in peers) peer?.Dispose();
        }
        private sealed class RealClock : IMonotonicClock
        {
            private readonly Stopwatch clock = Stopwatch.StartNew();
            public long NowMs => clock.ElapsedMilliseconds;
        }
        private sealed class RespondingFactory(
            FakeFactory factory, int radioResponseDelayMs, int recordLength) : IByteBridgeEndpointFactory
        {
            public async ValueTask<IByteBridgeEndpoint> OpenAsync(string name, ComCon.Core.Models.SerialSettings serial, CancellationToken token)
            {
                var endpoint = await factory.OpenAsync(name, serial, token);
                return name == factory.RadioPort
                    ? new RespondingEndpoint(factory.Radio, radioResponseDelayMs, recordLength)
                    : endpoint;
            }
        }
        private sealed class RespondingEndpoint(
            FakeEndpoint endpoint, int radioResponseDelayMs, int recordLength) : IByteBridgeEndpoint
        {
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => endpoint.ReadAsync(buffer, token);
            public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token)
            {
                await endpoint.WriteAsync(buffer, token);
                var command = await endpoint.Receive();
                Assert.HasCount(recordLength, command);
                await Task.Delay(radioResponseDelayMs, token);
                (command[2], command[3]) = (command[3], command[2]);
                await endpoint.Send(command);
            }
            public ValueTask DisposeAsync() => endpoint.DisposeAsync();
        }
        private sealed class MeasuredControlStream(Stream inner, ComConCommandNotifications tracker, bool slow) : Stream
        {
            private long prefixStarted;
            public ComConCommandNotifications Tracker { get; } = tracker;
            public ConcurrentDictionary<long, long> WriteStarts { get; } = new();
            public override bool CanRead => inner.CanRead;
            public override bool CanWrite => inner.CanWrite;
            public override bool CanSeek => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
            {
                if (buffer.Length == 4) prefixStarted = Stopwatch.GetTimestamp();
                else
                {
                    var envelope = ComConCommandProtocol.ParseEnvelope(buffer.Span);
                    WriteStarts.TryAdd(long.Parse(envelope.GetProperty("notificationSequence").GetString()!, CultureInfo.InvariantCulture), prefixStarted);
                    if (slow) await Task.Delay(8, token);
                }
                await inner.WriteAsync(buffer, token);
            }
            public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
            public override void Flush() => inner.Flush();
            public override ValueTask DisposeAsync() => inner.DisposeAsync();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
