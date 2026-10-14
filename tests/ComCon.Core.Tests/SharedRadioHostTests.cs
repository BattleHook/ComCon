using System.Threading.Channels;
using System.Diagnostics;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class SharedRadioHostTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PendingLegacyCloseHasExplicitBoundedStopFailure()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeEndpointFactory
        {
            LegacyObservationFailure = new IOException("Injected drain failure."),
            LegacyCloseRelease = release
        };
        var host = NewHost(factory);
        try
        {
            await host.StartAsync(Profile(), Options());
            var started = Stopwatch.StartNew();
            await Assert.ThrowsExactlyAsync<AggregateException>(host.StopAsync);
            Assert.IsLessThan(4500L, started.ElapsedMilliseconds);
            Assert.AreEqual(SharedRadioHostState.Faulted, host.State);
            StringAssert.Contains(host.LegacyStatus.Reason!, "close outcome is unknown");
            TestContext.WriteLine($"PASS: blocked Legacy close failed stop explicitly in {started.ElapsedMilliseconds} ms; no second concurrent Dispose.");
        }
        finally
        {
            release.TrySetResult();
            await Assert.ThrowsExactlyAsync<AggregateException>(() => host.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    public async Task UnsupportedLegacyDrainIsExplicitAndKeepsIpcAvailable()
    {
        var factory = new FakeEndpointFactory { UnsupportedLegacyDrain = true };
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);
        await host.StartAsync(Profile(), Options(), ipc);
        Assert.AreEqual(LegacyConnectionState.Unavailable, host.LegacyStatus.State);
        StringAssert.Contains(host.LegacyStatus.Reason!, "does not support");
        var request = Civ(0xE1, 0x98);
        await ipc.SendAsync(request);
        CollectionAssert.AreEqual(request, await factory.Get("COM3").ReadWrittenAsync());
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
    }

    [TestMethod]
    public async Task LegacyCloseInProgressDoesNotStallHealthyIpc()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factory = new FakeEndpointFactory
        {
            LegacyObservationFailure = new IOException("Injected input observation failure."),
            LegacyCloseRelease = release
        };
        var ipc = new FakeEndpoint();
        var host = NewHost(factory);
        try
        {
            await host.StartAsync(Profile(), Options(), ipc);
            Assert.AreEqual(LegacyConnectionState.Unavailable, host.LegacyStatus.State);
            StringAssert.Contains(host.LegacyStatus.Reason!, "Injected input observation failure");
            var request = Civ(0xE1, 0x98);
            await ipc.SendAsync(request);
            CollectionAssert.AreEqual(request, await factory.Get("COM3").ReadWrittenAsync());
            Assert.IsFalse(release.Task.IsCompleted);
            TestContext.WriteLine("PASS: Legacy observation failure/close wait does not block HSC/IPC radio dispatch.");
        }
        finally
        {
            release.TrySetResult();
            await host.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task CallerCancellationDuringInitialDrainNeverAdmitsLegacy()
    {
        var factory = new FakeEndpointFactory { InitialLegacyInput = [0xFE] };
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);
        using var stop = new CancellationTokenSource();
        var start = host.StartAsync(Profile(), Options(), ipc, stop.Token);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (host.State != SharedRadioHostState.Running || factory.Get("COM31").BufferedInputBytes != 0)
            await Task.Delay(2, timeout.Token);
        stop.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => start);
        Assert.AreEqual(LegacyConnectionState.Unavailable, host.LegacyStatus.State);
        Assert.IsFalse(factory.Get("COM3").TryReadWritten(out _));
        await ipc.SendAsync(Civ(0xE1, 0x98));
        CollectionAssert.AreEqual(Civ(0xE1, 0x98), await factory.Get("COM3").ReadWrittenAsync());
    }

    [TestMethod]
    public async Task StartupDiscardsOversizedBacklogBeforeAdmission()
    {
        var old = Convert.FromHexString("FEFE7AE003FD");
        var factory = new FakeEndpointFactory
        {
            InitialLegacyInput = Enumerable.Range(0, 333).SelectMany(_ => old).ToArray()
        };
        await using var host = NewHost(factory);
        await host.StartAsync(Profile(), Options() with { IngressBytesPerSource = 1024 });
        Assert.AreEqual(LegacyConnectionState.Ready, host.LegacyStatus.State);
        Assert.AreEqual(1998, host.LegacyStatus.DiscardedBytes);
        var radio = factory.Get("COM3");
        Assert.IsFalse(radio.TryReadWritten(out _));
        await factory.Get("COM31").SendAsync(old);
        CollectionAssert.AreEqual(old, await radio.ReadWrittenAsync());
        TestContext.WriteLine("PASS: 1998 stale bytes discarded before admission; one new request forwarded, unchanged 1024-byte budget.");
    }

    [TestMethod]
    public async Task StaleStartupBurstProducesAggregateCaptureNotHundredsOfFrameRows()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_startup_drain_capture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var frame = Convert.FromHexString("FEFE7AE003FD");
            var factory = new FakeEndpointFactory
            { InitialLegacyInput = Enumerable.Range(0, 333).SelectMany(_ => frame).ToArray() };
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            await using var host = new SharedRadioHost(factory, new StopwatchClock(), _ => { }, capture);
            await host.StartAsync(Profile(), Options() with { IngressBytesPerSource = 1024 });
            Assert.AreEqual(1998, host.LegacyStatus.DiscardedBytes);
            await host.StopAsync();
            await capture.DisposeAsync();
            var entries = File.ReadLines(capture.Snapshot.LogPath).Select(ReadCaptureEntry).ToArray();
            Assert.IsFalse(entries.Any(entry => entry.GetProperty("source").GetString() == "Commander"));
            Assert.IsTrue(entries.Any(entry => IsCoordinatorDiagnostic(entry, "LegacyLifecycle", "LegacyClient", 1998)));
            Assert.AreEqual(RawCatCaptureStatus.Armed, capture.Snapshot.Status);
            TestContext.WriteLine("PASS: 333 stale frames generated no Commander chunk/frame rows; aggregate 1998-byte lifecycle capture retained.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PartialStartupDrainKeepsIpcRunningAndHandsOverAtTerminator()
    {
        var factory = new FakeEndpointFactory { InitialLegacyInput = [0xFE, 0xFE, 0x7A] };
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);
        var start = host.StartAsync(Profile(), Options(), ipc);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (host.State != SharedRadioHostState.Running || factory.Get("COM31").BufferedInputBytes != 0)
            await Task.Delay(2, timeout.Token);
        Assert.AreEqual(LegacyConnectionState.Initialising, host.LegacyStatus.State);
        var ipcRequest = Civ(0xE1, 0x98);
        await ipc.SendAsync(ipcRequest);
        CollectionAssert.AreEqual(ipcRequest, await factory.Get("COM3").ReadWrittenAsync());
        Assert.IsFalse(start.IsCompleted);
        await factory.Get("COM31").SendAsync(new byte[] { 0xE0, 0x03, 0xFD });
        await start.WaitAsync(timeout.Token);
        Assert.AreEqual(6, host.LegacyStatus.DiscardedBytes);
        var fresh = Convert.FromHexString("FEFE7AE003FD");
        await factory.Get("COM31").SendAsync(fresh);
        CollectionAssert.AreEqual(fresh, await factory.Get("COM3").ReadWrittenAsync());
        TestContext.WriteLine("PASS: HSC/IPC forwarded while Legacy awaited a partial-frame terminator; stale frame never replayed.");
    }

    [TestMethod]
    public async Task ExactStartupByteCapBlocksOnlyLegacyAndExplicitRecoveryReopensIt()
    {
        var backlog = new byte[65536];
        backlog[^1] = 0xFD;
        var factory = new FakeEndpointFactory { InitialLegacyInput = backlog };
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);
        await host.StartAsync(Profile(), Options(), ipc);
        Assert.AreEqual(LegacyConnectionState.Unavailable, host.LegacyStatus.State);
        Assert.AreEqual(65536, host.LegacyStatus.DiscardedBytes);
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
        var request = Civ(0xE1, 0x98);
        await ipc.SendAsync(request);
        CollectionAssert.AreEqual(request, await factory.Get("COM3").ReadWrittenAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!factory.Get("COM31").IsDisposed) await Task.Delay(2, timeout.Token);
        factory.InitialLegacyInput = null;
        await host.RecoverLegacyAsync(timeout.Token);
        Assert.AreEqual(LegacyConnectionState.Ready, host.LegacyStatus.State);
        Assert.HasCount(2, factory.OpenedPorts.Where(port => port == "COM31").ToArray());
        Assert.HasCount(1, factory.OpenedPorts.Where(port => port == "COM3").ToArray());
        await factory.Get("COM31").SendAsync(Civ(0xE0, 0x99));
        CollectionAssert.AreEqual(Civ(0xE0, 0x99), await factory.Get("COM3").ReadWrittenAsync());
        TestContext.WriteLine("PASS: exact 65536-byte exhaustion is failure; HSC stays running; recovery reopens only Legacy.");
    }

    [TestMethod]
    public async Task FinalisationDiscardsInputThenClosesEvenAtExactByteCap()
    {
        var factory = new FakeEndpointFactory();
        var backlog = new byte[65536];
        backlog[^1] = 0xFD;
        await using var host = new SharedRadioHost(factory, new StopwatchClock(), diagnostic =>
        {
            if (diagnostic.Kind == SharedRadioDiagnosticKind.LegacyLifecycle &&
                diagnostic.Message.StartsWith("Finalising:", StringComparison.Ordinal))
                factory.Get("COM31").SendAsync(backlog).GetAwaiter().GetResult();
        });
        await host.StartAsync(Profile(), Options());
        await host.StopAsync();
        Assert.IsTrue(factory.Get("COM31").IsDisposed);
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.AreEqual(65536, host.LegacyStatus.DiscardedBytes);
        StringAssert.Contains(host.LegacyStatus.Reason!, "incomplete");
        Assert.IsFalse(factory.Get("COM3").TryReadWritten(out _));
        TestContext.WriteLine("PASS: finalisation discarded exactly 65536 bytes, reported incomplete and closed without forwarding.");
    }

    [TestMethod]
    public async Task PollingConfirmationRequiresNewFrequencyReplyAndSuccessfulDelivery()
    {
        var factory = new FakeEndpointFactory();
        await using var host = NewHost(factory);
        await host.StartAsync(Profile(), Options() with
        { InformationalRadioAddress = 0x7A, CivExchangeGateEnabled = true });
        var radio = factory.Get("COM3");
        var legacy = factory.Get("COM31");
        var reply = Convert.FromHexString("FEFEE07A030040070700FD");
        await radio.SendAsync(reply);
        CollectionAssert.AreEqual(reply, await legacy.ReadWrittenAsync());
        Assert.AreEqual(LegacyConnectionState.Ready, host.LegacyStatus.State);
        var request = Convert.FromHexString("FEFE7AE003FD");
        await legacy.SendAsync(request);
        CollectionAssert.AreEqual(request, await radio.ReadWrittenAsync());
        await radio.SendAsync(request);
        CollectionAssert.AreEqual(request, await legacy.ReadWrittenAsync());
        Assert.AreEqual(LegacyConnectionState.Ready, host.LegacyStatus.State);
        await radio.SendAsync(reply);
        CollectionAssert.AreEqual(reply, await legacy.ReadWrittenAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (host.LegacyStatus.State != LegacyConnectionState.PollingConfirmed)
            await Task.Delay(2, timeout.Token);
        TestContext.WriteLine("PASS: unsolicited reply and echo do not confirm polling; delivered matching post-admission reply does.");
    }

    [TestMethod]
    public async Task InvestigateOversizedCommanderStartupChunkDisconnectsLegacyWhileHostRuns()
    {
        var factory = new FakeEndpointFactory();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SharedRadioDiagnostic>();
        await using var host = new SharedRadioHost(factory, new StopwatchClock(), diagnostics.Enqueue);
        await host.StartAsync(Profile(), Options() with { IngressBytesPerSource = 1024 });
        var legacy = factory.Get("COM31");
        var radio = factory.Get("COM3");
        var frame = Convert.FromHexString("FEFE7AE003FD");
        var burst = Enumerable.Range(0, 333).SelectMany(_ => frame).ToArray();
        Assert.HasCount(1998, burst);
        await legacy.SendAsync(burst);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!legacy.IsDisposed) await Task.Delay(5, timeout.Token);
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
        Assert.IsNull(host.Fault);
        Assert.IsFalse(radio.IsDisposed);
        Assert.IsFalse(radio.TryReadWritten(out _), "Oversized input must not be dispatched or replayed.");
        var disconnects = diagnostics.Where(item =>
            item.Kind == SharedRadioDiagnosticKind.ClientDisconnected &&
            item.Client == SharedRadioClient.LegacyClient &&
            !item.Message.StartsWith("Legacy input initialisation.", StringComparison.Ordinal)).ToArray();
        Assert.HasCount(1, disconnects);
        TestContext.WriteLine(
            $"STARTUP REPRODUCED: {burst.Length}-byte/333-frame chunk exceeds 1024-byte ingress; " +
            $"Legacy disposed, host Running/no fault, zero radio writes. Reason: {disconnects[0].Message}");
        await host.StopAsync();
    }

    [TestMethod]
    public async Task SharedHostHoldsIpcBehindLegacyReplyAndPreservesEchoDelivery()
    {
        var factory = new FakeEndpointFactory();
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);
        await host.StartAsync(Profile(), Options() with
        {
            QueueWaitLimitMs = 2500,
            InformationalRadioAddress = 0x7A,
            CivExchangeGateEnabled = true
        }, ipc);
        var legacy = factory.Get("COM31");
        var radio = factory.Get("COM3");
        var legacyRequest = Convert.FromHexString("FEFE7AE01502FD");
        var ipcRequest = Convert.FromHexString("FEFE7AE103FD");
        await legacy.SendAsync(legacyRequest);
        CollectionAssert.AreEqual(legacyRequest, await radio.ReadWrittenAsync());
        await ipc.SendAsync(ipcRequest);
        await radio.SendAsync(legacyRequest);
        CollectionAssert.AreEqual(legacyRequest, await legacy.ReadWrittenAsync());
        CollectionAssert.AreEqual(legacyRequest, await ipc.ReadWrittenAsync());
        Assert.IsFalse(radio.TryReadWritten(out _));
        var legacyReply = Convert.FromHexString("FEFEE07A15020120FD");
        await radio.SendAsync(legacyReply);
        CollectionAssert.AreEqual(ipcRequest, await radio.ReadWrittenAsync());
        CollectionAssert.AreEqual(legacyReply, await legacy.ReadWrittenAsync());
        var ipcReply = Convert.FromHexString("FEFEE17A030040070700FD");
        await radio.SendAsync(ipcReply);
        CollectionAssert.AreEqual(ipcReply, await ipc.ReadWrittenAsync());
        await host.StopAsync();
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
    }

    [TestMethod]
    public async Task SharedHostOpensThreeInjectedEndpointsAndRoutesTraffic()
    {
        var factory = new FakeEndpointFactory();
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);
        await host.StartAsync(Profile(), Options(), ipc);

        var legacy = factory.Get("COM31");
        var radio = factory.Get("COM3");
        var legacyTx = Civ(0xE0, 0x98);
        var ipcTx = Civ(0xE1, 0x99);
        await legacy.SendAsync(legacyTx);
        await ipc.SendAsync(ipcTx);

        var writes = new[]
        {
            await radio.ReadWrittenAsync(),
            await radio.ReadWrittenAsync()
        };
        Assert.IsTrue(writes.Any(bytes => bytes.SequenceEqual(legacyTx)));
        Assert.IsTrue(writes.Any(bytes => bytes.SequenceEqual(ipcTx)));

        var directed = Civ(0xE1, 0x10);
        await radio.SendAsync(directed);
        CollectionAssert.AreEqual(directed, await ipc.ReadWrittenAsync());

        var broadcast = Civ(0x00, 0x11);
        await radio.SendAsync(broadcast);
        CollectionAssert.AreEqual(broadcast, await legacy.ReadWrittenAsync());
        CollectionAssert.AreEqual(broadcast, await ipc.ReadWrittenAsync());

        await host.StopAsync();
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.IsTrue(legacy.IsDisposed);
        Assert.IsTrue(ipc.IsDisposed);
        Assert.IsTrue(radio.IsDisposed);
    }

    [TestMethod]
    public async Task SharedHostCaptureStorageFailureDoesNotFaultCoordinatorOrForwarding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_shared_capture_failure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new FakeEndpointFactory();
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            Directory.CreateDirectory(capture.Snapshot.LogPath);
            await using var host = new SharedRadioHost(factory, new StopwatchClock(), _ => { }, capture);
            await host.StartAsync(Profile(), Options());
            var legacy = factory.Get("COM31");
            var radio = factory.Get("COM3");
            var request = Convert.FromHexString("FEFE7AE003FD");
            var reply = Convert.FromHexString("FEFEE07A030040070700FD");
            for (var index = 0; index < 20; index++)
            {
                await legacy.SendAsync(request);
                CollectionAssert.AreEqual(request, await radio.ReadWrittenAsync());
                await radio.SendAsync(reply);
                CollectionAssert.AreEqual(reply, await legacy.ReadWrittenAsync());
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (capture.Snapshot.Status != RawCatCaptureStatus.Error)
                await Task.Delay(5, timeout.Token);
            Assert.IsFalse(capture.Snapshot.Enabled);
            StringAssert.Contains(capture.Snapshot.Error, "capture failed");
            Assert.AreEqual(SharedRadioHostState.Running, host.State);
            Assert.IsNull(host.Fault);
            await host.StopAsync();
            Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task SharedHostCaptureShowsCommanderAndRadioIngressAndForwarding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_shared_host_capture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new FakeEndpointFactory();
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            await using var host = new SharedRadioHost(factory, new StopwatchClock(), _ => { }, capture);
            await host.StartAsync(Profile(), Options());

            var legacy = factory.Get("COM31");
            var radio = factory.Get("COM3");
            var request = Convert.FromHexString("FEFE7AE003FD");
            await legacy.SendAsync(request);
            CollectionAssert.AreEqual(request, await radio.ReadWrittenAsync());

            var reply = Convert.FromHexString("FEFEE07A030050070700FD");
            await radio.SendAsync(reply[..1]);
            await Task.Delay(TimeSpan.FromMilliseconds(16));
            await radio.SendAsync(reply[1..]);
            CollectionAssert.AreEqual(reply, await legacy.ReadWrittenAsync());

            await host.StopAsync();
            await capture.DisposeAsync();
            var entries = File.ReadLines(capture.Snapshot.LogPath).Select(ReadCaptureEntry).ToArray();
            var commanderChunk = entries.Single(entry => IsCapture(entry, "Commander", "CommanderToComCon",
                "IngressChunk", request));
            Assert.IsGreaterThanOrEqualTo(
                0, commanderChunk.GetProperty("monotonicTimestampMs").GetInt64());
            Assert.IsTrue(entries.Any(entry => IsCapture(entry, "Commander", "CommanderToComCon",
                "CompleteRecord", request)));
            Assert.IsTrue(entries.Any(entry => IsCapture(entry, "ComCon", "ComConToRadio",
                "WriteCompleted", request)));
            var radioPreambleChunk = entries.Single(entry => IsCapture(entry, "Radio", "RadioToComCon",
                "IngressChunk", reply[..1]));
            Assert.IsGreaterThanOrEqualTo(
                0, radioPreambleChunk.GetProperty("monotonicTimestampMs").GetInt64());
            Assert.IsTrue(entries.Any(entry => IsCapture(entry, "Radio", "RadioToComCon",
                "IngressChunk", reply[1..])));
            Assert.IsTrue(entries.Any(entry => IsCapture(entry, "Radio", "RadioToComCon",
                "CompleteRecord", reply)));
            Assert.IsTrue(entries.Any(entry => IsCapture(entry, "ComCon", "ComConToCommander",
                "WriteCompleted", reply)));
            Assert.IsTrue(entries.Any(entry => IsCoordinatorDiagnostic(entry, "ReceiveDelivered",
                "LegacyClient", reply.Length)));
            var ingressProcessed = entries.Where(entry =>
                IsCoordinatorDiagnostic(entry, "RadioIngressProcessed", null, null)).ToArray();
            Assert.HasCount(2, ingressProcessed);
            Assert.IsTrue(ingressProcessed.Any(entry => GetDiagnosticByteCount(entry) == reply[..1].Length));
            Assert.IsTrue(ingressProcessed.Any(entry => GetDiagnosticByteCount(entry) == reply.Length - 1));
            Assert.IsTrue(radioPreambleChunk.TryGetProperty("monotonicTimestampTicks", out var readTicks));
            Assert.IsTrue(ingressProcessed.All(entry =>
                entry.GetProperty("monotonicTimestampTicks").GetInt64() > 0));
            Assert.IsTrue(ingressProcessed.All(entry =>
                entry.GetProperty("monotonicTimestampFrequency").GetInt64() == Stopwatch.Frequency));
            var processedTicks = ingressProcessed
                .Select(entry => entry.GetProperty("monotonicTimestampTicks").GetInt64()).Order().ToArray();
            Assert.IsGreaterThanOrEqualTo(readTicks.GetInt64(), processedTicks[0]);
            Assert.IsLessThan(Stopwatch.Frequency / 4, processedTicks[1] - readTicks.GetInt64());
            Assert.AreEqual(RawCatCaptureStatus.Armed, capture.Snapshot.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task UnknownRadioWriteFaultsTxButKeepsRadioRxForwarding()
    {
        var factory = new FakeEndpointFactory();
        var ipc = new FakeEndpoint();
        var diagnostics = new List<SharedRadioDiagnostic>();
        await using var host = NewHost(factory, diagnostics);
        await host.StartAsync(Profile(), Options(), ipc);

        var radio = factory.Get("COM3");
        radio.WriteFailure = new IOException("synthetic write failure");
        await factory.Get("COM31").SendAsync(Civ(0xE0, 0x98));

        await WaitForStateAsync(host, SharedRadioHostState.Faulted);
        Assert.IsTrue(diagnostics.Any(item => item.Outcome == SharedRadioOutcome.UnknownDelivery));
        Assert.IsTrue(host.Fault!.Contains("unknown", StringComparison.OrdinalIgnoreCase));

        var rx = Civ(0xE1, 0x10);
        await radio.SendAsync(rx);
        CollectionAssert.AreEqual(rx, await ipc.ReadWrittenAsync());
        Assert.IsEmpty(factory.Get("COM31").Written);

        await host.StopAsync();
        Assert.AreEqual(SharedRadioHostState.Faulted, host.State);
    }

    [TestMethod]
    public async Task StartupFailureClosesEveryEndpointAlreadyOwned()
    {
        var factory = new FakeEndpointFactory { FailedPortName = "COM3" };
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);

        await Assert.ThrowsExactlyAsync<IOException>(
            () => host.StartAsync(Profile(), Options(), ipc));

        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.IsTrue(factory.Get("COM31").IsDisposed);
        Assert.IsTrue(ipc.IsDisposed);
        CollectionAssert.AreEqual(new[] { "COM31" }, factory.OpenedPorts);
    }

    [TestMethod]
    public async Task SharedHostRejectsFramingMismatchBeforeOpeningPorts()
    {
        var factory = new FakeEndpointFactory();
        var ipc = new FakeEndpoint();
        await using var host = NewHost(factory);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StartAsync(Profile() with { Framing = FramingMode.KenwoodAscii },
                Options(), ipc));

        Assert.IsEmpty(factory.OpenedPorts);
        Assert.IsFalse(ipc.IsDisposed);
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
    }

    private static SharedRadioHost NewHost(
        FakeEndpointFactory factory,
        List<SharedRadioDiagnostic>? diagnostics = null) =>
        new(factory, new StopwatchClock(),
            diagnostics is null ? _ => { } : diagnostics.Add);

    private static ProfileConfig Profile() => new()
    {
        Id = 1,
        Label = "Shared host test",
        Enabled = true,
        PhysicalPort = "COM3",
        VirtualPort = "COM31",
        CommanderPort = "COM21",
        Adapter = AdapterKind.Passthrough,
        Framing = FramingMode.Civ
    };

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

    private static bool IsCapture(
        JsonElement entry, string source, string direction, string outcome, byte[] bytes) =>
        entry.GetProperty("source").GetString() == source &&
        entry.GetProperty("direction").GetString() == direction &&
        entry.GetProperty("outcome").GetString() == outcome &&
        entry.GetProperty("hex").GetString() == Convert.ToHexString(bytes);

    private static bool IsCoordinatorDiagnostic(
        JsonElement entry, string kind, string? client, int? byteCount)
    {
        if (entry.GetProperty("source").GetString() != "Coordinator" ||
            entry.GetProperty("direction").GetString() != "CoordinatorDiagnostic" ||
            entry.GetProperty("outcome").GetString() != kind)
            return false;
        using var details = JsonDocument.Parse(
            Convert.FromHexString(entry.GetProperty("hex").GetString()!));
        var hasClient = details.RootElement.TryGetProperty("client", out var clientValue);
        return details.RootElement.GetProperty("kind").GetString() == kind &&
            (client is null
                ? !hasClient || clientValue.ValueKind == JsonValueKind.Null
                : hasClient && clientValue.GetString() == client) &&
            (byteCount is null ||
                details.RootElement.GetProperty("byteCount").GetInt32() == byteCount.Value) &&
            details.RootElement.GetProperty("monotonicTimestampMs").GetInt64() >= 0;
    }

    private static int GetDiagnosticByteCount(JsonElement entry)
    {
        using var details = JsonDocument.Parse(
            Convert.FromHexString(entry.GetProperty("hex").GetString()!));
        return details.RootElement.GetProperty("byteCount").GetInt32();
    }

    private static JsonElement ReadCaptureEntry(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.Clone();
    }

    private static async Task WaitForStateAsync(
        SharedRadioHost host,
        SharedRadioHostState expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (host.State != expected)
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    private sealed class FakeEndpointFactory : IByteBridgeEndpointFactory
    {
        private readonly Dictionary<string, FakeEndpoint> endpoints =
            new(StringComparer.OrdinalIgnoreCase);

        public string? FailedPortName { get; init; }
        public byte[]? InitialLegacyInput { get; set; }
        public bool UnsupportedLegacyDrain { get; init; }
        public Exception? LegacyObservationFailure { get; init; }
        public TaskCompletionSource? LegacyCloseRelease { get; init; }
        public List<string> OpenedPorts { get; } = [];

        public ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName,
            SerialSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(portName, FailedPortName, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"Synthetic failure opening {portName}.");
            }
            if (endpoints.TryGetValue(portName, out var existing) && !existing.IsDisposed)
            {
                throw new IOException($"Fake endpoint {portName} is already open.");
            }

            var endpoint = new FakeEndpoint
            {
                ObservationFailure = portName == "COM31" ? LegacyObservationFailure : null,
                CloseRelease = portName == "COM31" ? LegacyCloseRelease : null
            };
            if (portName == "COM31" && InitialLegacyInput is { } input)
                endpoint.SendAsync(input).GetAwaiter().GetResult();
            endpoints[portName] = endpoint;
            OpenedPorts.Add(portName);
            return ValueTask.FromResult<IByteBridgeEndpoint>(
                portName == "COM31" && UnsupportedLegacyDrain ? new UnbufferedEndpoint(endpoint) : endpoint);
        }

        private sealed class UnbufferedEndpoint(IByteBridgeEndpoint endpoint) : IByteBridgeEndpoint
        {
            public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => endpoint.ReadAsync(buffer, token);
            public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => endpoint.WriteAsync(buffer, token);
            public ValueTask DisposeAsync() => endpoint.DisposeAsync();
        }

        public FakeEndpoint Get(string portName) => endpoints[portName];
    }

    private sealed class FakeEndpoint : IBufferedByteBridgeEndpoint
    {
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> written = Channel.CreateUnbounded<byte[]>();
        private byte[]? remainder;
        public Exception? ObservationFailure { get; init; }
        public TaskCompletionSource? CloseRelease { get; init; }
        public int BufferedInputBytes => ObservationFailure is { } failure ? throw failure : remainder?.Length ??
            (incoming.Reader.TryPeek(out var bytes) ? bytes.Length : 0);

        public bool IsDisposed { get; private set; }
        public Exception? WriteFailure { get; set; }
        public List<byte[]> Written { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<byte> bytes) =>
            incoming.Writer.WriteAsync(bytes.ToArray());

        public async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            var bytes = remainder ?? await incoming.Reader.ReadAsync(cancellationToken);
            var count = Math.Min(bytes.Length, buffer.Length);
            bytes.AsMemory(0, count).CopyTo(buffer);
            remainder = count == bytes.Length ? null : bytes[count..];
            return count;
        }

        public ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WriteFailure is { } failure)
            {
                throw failure;
            }
            var copy = buffer.ToArray();
            Written.Add(copy);
            return written.Writer.WriteAsync(copy, cancellationToken);
        }

        public async Task<byte[]> ReadWrittenAsync() =>
            await written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        public bool TryReadWritten(out byte[]? bytes) => written.Reader.TryRead(out bytes);

        public async ValueTask DisposeAsync()
        {
            IsDisposed = true;
            if (CloseRelease is { } release) await release.Task;
            incoming.Writer.TryComplete();
            written.Writer.TryComplete();
        }
    }
}
