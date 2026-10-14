using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class SharedRadioCoordinatorTests
{
    [TestMethod]
    [DataRow("FEFEE07A0300400707A0FD", 0)]
    [DataRow("FEFEE07A040040070700FD", 0)]
    [DataRow("FEFEE07AFBFD", 0)]
    [DataRow("FEFE007A030040070700FD", 0)]
    [DataRow("FEFEE07A030040070700FD", 750)]
    public void InvalidUnrelatedAndExpiredRepliesNeverConfirmLegacyPolling(string replyHex, int elapsed)
    {
        var clock = new FakeClock();
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = NewCoordinator(Options() with
        { CivExchangeGateEnabled = true, InformationalRadioAddress = 0x7A }, clock, diagnostics);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Convert.FromHexString("FEFE7AE003FD"));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Succeeded);
        clock.NowMs = elapsed;
        coordinator.TryPostRadioBytes(Convert.FromHexString(replyHex));
        coordinator.ProcessAvailable();
        while (coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var delivery))
            coordinator.CompleteClientDelivery(delivery!.Id, delivered: true);
        Assert.IsFalse(diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.LegacyPollingConfirmed));
    }

    [TestMethod]
    [DataRow(FramingMode.Civ)]
    [DataRow(FramingMode.KenwoodAscii)]
    public void LegacyReconnectResetsOnlyLegacyParserAndQueues(FramingMode framing)
    {
        var coordinator = NewCoordinator(Options() with { Framing = framing });
        var old = framing == FramingMode.Civ ? Civ(0xE0, 1) : "FA00000000001;"u8.ToArray();
        var fresh = framing == FramingMode.Civ ? Civ(0xE0, 2) : "FA00000000002;"u8.ToArray();
        var ipc = framing == FramingMode.Civ ? Civ(0xE1, 3) : "FB00000000003;"u8.ToArray();
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [.. old, .. old[..3]]);
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, ipc);
        coordinator.ProcessAvailable();
        coordinator.NotifyClientDisconnected(SharedRadioClient.LegacyClient, "Explicit recovery.");
        coordinator.ReconnectLegacyClient();
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, fresh);
        coordinator.ProcessAvailable();
        var writes = new List<byte[]>();
        while (coordinator.TryTakeRadioWrite(out var write))
        {
            writes.Add(write!.Bytes);
            coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
        }
        Assert.HasCount(2, writes);
        Assert.IsTrue(writes.Any(bytes => bytes.SequenceEqual(fresh)));
        Assert.IsTrue(writes.Any(bytes => bytes.SequenceEqual(ipc)));
        Assert.IsFalse(writes.Any(bytes => bytes.SequenceEqual(old)));
    }

    [TestMethod]
    public void LegacyReconnectCannotBypassOutstandingCivReservationOrRadioFault()
    {
        var clock = new FakeClock();
        var coordinator = NewCoordinator(Options() with
        { CivExchangeGateEnabled = true, InformationalRadioAddress = 0x7A }, clock);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Convert.FromHexString("FEFE7AE003FD"));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        coordinator.NotifyClientDisconnected(SharedRadioClient.LegacyClient, "Detached.");
        Assert.ThrowsExactly<InvalidOperationException>(coordinator.ReconnectLegacyClient);
        coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Succeeded);
        Assert.ThrowsExactly<InvalidOperationException>(coordinator.ReconnectLegacyClient);
        clock.NowMs = 1000;
        coordinator.ProcessAvailable();
        coordinator.ReconnectLegacyClient();
        coordinator.NotifyClientDisconnected(SharedRadioClient.LegacyClient, "Detached again.");
        coordinator.NotifyRadioFailure("Injected radio fault.");
        Assert.ThrowsExactly<InvalidOperationException>(coordinator.ReconnectLegacyClient);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LegacyPollingConfirmationRequiresSuccessfulDelivery(bool delivered)
    {
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = new SharedRadioCoordinator(Options() with
        { CivExchangeGateEnabled = true, InformationalRadioAddress = 0x7A }, new FakeClock(), diagnostics.Add);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Convert.FromHexString("FEFE7AE003FD"));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Succeeded);
        coordinator.TryPostRadioBytes(Convert.FromHexString("FEFEE07A030040070700FD"));
        coordinator.ProcessAvailable();
        Assert.IsFalse(diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.LegacyPollingConfirmed));
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var delivery));
        coordinator.CompleteClientDelivery(delivery!.Id, delivered, "Injected delivery failure.");
        Assert.AreEqual(delivered, diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.LegacyPollingConfirmed));
    }

    [TestMethod]
    [DataRow(FramingMode.Civ)]
    [DataRow(FramingMode.KenwoodAscii)]
    public void ReconnectClearsOldIpcFramingQueuesAndSequenceWithoutResettingLegacy(FramingMode framing)
    {
        var coordinator = NewCoordinator(Options() with { Framing = framing });
        var first = framing == FramingMode.Civ ? Civ(0xE1, 1) : "FA00000000001;"u8.ToArray();
        var next = framing == FramingMode.Civ ? Civ(0xE1, 2) : "FA00000000002;"u8.ToArray();
        var legacy = framing == FramingMode.Civ ? Civ(0xE0, 3) : "FB00000000003;"u8.ToArray();
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, [.. first, .. first[..3]]);
        coordinator.ProcessAvailable();
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, legacy);
        coordinator.ProcessAvailable();
        coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient, "Old session ended.");
        coordinator.ReconnectIpcClient();
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, next);
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var legacyWrite));
        CollectionAssert.AreEqual(legacy, legacyWrite!.Bytes);
        coordinator.CompleteRadioWrite(legacyWrite.Id, AdapterWriteCompletion.Succeeded);
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var ipcWrite));
        CollectionAssert.AreEqual(next, ipcWrite!.Bytes);
        Assert.AreEqual(1L, ipcWrite.RecordSequence);
        coordinator.CompleteRadioWrite(ipcWrite.Id, AdapterWriteCompletion.Succeeded);
        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
        Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.LegacyClient));
    }

    [TestMethod]
    public void ReconnectResetsOldIngressOverflowButNeverClearsRadioFault()
    {
        var coordinator = NewCoordinator(Options() with { IngressChunksPerSource = 1 });
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, Civ(0xE1, 1)));
        Assert.IsFalse(coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, Civ(0xE1, 2)));
        coordinator.ProcessAvailable();
        Assert.IsFalse(coordinator.IsConnected(SharedRadioClient.IpcClient));
        coordinator.ReconnectIpcClient();
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, Civ(0xE1, 3)));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        Assert.AreEqual(1L, write!.RecordSequence);
        coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Unknown);
        coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient, "Detached after uncertain write.");
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.ReconnectIpcClient());
        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, coordinator.State);
    }

    [TestMethod]
    public void ReconnectRejectsConnectedClientAndUnsettledOldWrite()
    {
        var coordinator = NewCoordinator();
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.ReconnectIpcClient());
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, Civ(0xE1, 1));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient, "Detached with an active physical write.");
        Assert.ThrowsExactly<InvalidOperationException>(() => coordinator.ReconnectIpcClient());
        coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Succeeded);
        coordinator.ReconnectIpcClient();
        Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.IpcClient));
    }

    [TestMethod]
    public void FramedRecordsAreSerializedAndServedRoundRobin()
    {
        var coordinator = NewCoordinator();
        var legacy = Civ(0xE0, 0x98);
        var ipc = Civ(0xE1, 0x99);

        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, legacy[..3]));
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, ipc));
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, legacy[3..]));
        coordinator.ProcessAvailable();

        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var first));
        Assert.AreEqual(SharedRadioClient.LegacyClient, first!.Client);
        CollectionAssert.AreEqual(legacy, first.Bytes);
        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
        coordinator.CompleteRadioWrite(first.Id, AdapterWriteCompletion.Succeeded);

        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var second));
        Assert.AreEqual(SharedRadioClient.IpcClient, second!.Client);
        CollectionAssert.AreEqual(ipc, second.Bytes);
        coordinator.CompleteRadioWrite(second.Id, AdapterWriteCompletion.Succeeded);
    }

    [TestMethod]
    public void UnknownDeliveryStopsTxButContinuesRadioRxAndFanout()
    {
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = NewCoordinator(diagnostics: diagnostics);
        var legacyTx = Civ(0xE0, 0x98);
        var ipcTx = Civ(0xE1, 0x99);
        var radioRx = Civ(0xE1, 0x00);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, legacyTx);
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, ipcTx);
        coordinator.ProcessAvailable();

        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Unknown);
        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, coordinator.State);
        Assert.AreEqual(0, coordinator.QueuedTxRecords);

        Assert.IsTrue(coordinator.TryPostRadioBytes(radioRx));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out var delivery));
        CollectionAssert.AreEqual(radioRx, delivery!.Bytes);
        coordinator.CompleteClientDelivery(delivery.Id, delivered: true);

        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, legacyTx);
        coordinator.ProcessAvailable();
        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
        Assert.IsTrue(diagnostics.Any(item => item.Outcome == SharedRadioOutcome.UnknownDelivery));
        Assert.IsTrue(diagnostics.Any(item => item.Outcome == SharedRadioOutcome.NotSent));
    }

    [TestMethod]
    public void InFlightWriteCountsAgainstClientQueueCapacity()
    {
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = NewCoordinator(
            Options() with { TxQueueRecordsPerClient = 1, TxQueueRecordsPerProfile = 2 },
            diagnostics: diagnostics);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x98));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var inFlight));

        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x99));
        coordinator.ProcessAvailable();

        Assert.AreEqual(0, coordinator.QueuedTxRecords);
        Assert.IsTrue(diagnostics.Any(item => item.Outcome == SharedRadioOutcome.NotSent));
        coordinator.CompleteRadioWrite(inFlight!.Id, AdapterWriteCompletion.Succeeded);
    }

    [TestMethod]
    public void ProvenZeroByteWriteIsNotRetriedAndDoesNotLatchProfileFault()
    {
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = NewCoordinator(diagnostics: diagnostics);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x98));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.ProvenZeroBytes);

        Assert.AreEqual(SharedRadioCoordinatorState.Running, coordinator.State);
        Assert.IsTrue(diagnostics.Any(item => item.Outcome == SharedRadioOutcome.NotSent));
        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
    }

    [TestMethod]
    public void CivRoutingHonorsDirectedBroadcastAllowAndDenyPrecedence()
    {
        var options = Options() with
        {
            AllowUnmatchedEnabled = true,
            AllowedUnmatchedAddresses = new HashSet<byte> { 0x22, 0x23 },
            DenyUnmatchedEnabled = true,
            DeniedUnmatchedAddresses = new HashSet<byte> { 0x22 }
        };
        var coordinator = NewCoordinator(options);

        coordinator.TryPostRadioBytes(Civ(0xE0, 0x10));
        coordinator.TryPostRadioBytes(Civ(0x00, 0x10));
        coordinator.TryPostRadioBytes(Civ(0x22, 0x10));
        coordinator.TryPostRadioBytes(Civ(0x23, 0x10));
        coordinator.ProcessAvailable();

        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var legacy));
        CollectionAssert.AreEqual(Civ(0xE0, 0x10), legacy!.Bytes);
        coordinator.CompleteClientDelivery(legacy.Id, delivered: true);
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var broadcast));
        CollectionAssert.AreEqual(Civ(0x00, 0x10), broadcast!.Bytes);
        coordinator.CompleteClientDelivery(broadcast.Id, delivered: true);
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var allowed));
        CollectionAssert.AreEqual(Civ(0x23, 0x10), allowed!.Bytes);
        coordinator.CompleteClientDelivery(allowed.Id, delivered: true);
        Assert.IsFalse(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out _));

        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out var ipcBroadcast));
        CollectionAssert.AreEqual(Civ(0x00, 0x10), ipcBroadcast!.Bytes);
        coordinator.CompleteClientDelivery(ipcBroadcast.Id, delivered: true);
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out var ipcAllowed));
        CollectionAssert.AreEqual(Civ(0x23, 0x10), ipcAllowed!.Bytes);
        coordinator.CompleteClientDelivery(ipcAllowed.Id, delivered: true);
        Assert.IsFalse(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out _));
    }

    [TestMethod]
    public void ClientIngressOverflowDisconnectsOnlyThatClient()
    {
        var coordinator = NewCoordinator(Options() with { IngressChunksPerSource = 1 });
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [0xFE]));
        Assert.IsFalse(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [0xFE]));
        coordinator.ProcessAvailable();

        Assert.IsFalse(coordinator.IsConnected(SharedRadioClient.LegacyClient));
        Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.IpcClient));
        Assert.AreEqual(SharedRadioCoordinatorState.Running, coordinator.State);
        Assert.IsFalse(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, [0xFE]));
    }

    [TestMethod]
    public void RadioIngressOverflowFaultsProfileWithoutDisconnectingClients()
    {
        var coordinator = NewCoordinator(Options() with { IngressChunksPerSource = 1 });
        Assert.IsTrue(coordinator.TryPostRadioBytes([0xFE]));
        Assert.IsFalse(coordinator.TryPostRadioBytes([0xFE]));
        coordinator.ProcessAvailable();

        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, coordinator.State);
        Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.LegacyClient));
        Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.IpcClient));
    }

    [TestMethod]
    public void RxBackpressureDisconnectsOnlySlowClient()
    {
        var coordinator = NewCoordinator(Options() with { ClientRxQueueRecordsPerClient = 1 });
        var first = Civ(0x00, 0x10);
        var second = Civ(0x00, 0x11);

        coordinator.TryPostRadioBytes(first);
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out var delivered));
        coordinator.CompleteClientDelivery(delivered!.Id, delivered: true);

        coordinator.TryPostRadioBytes(second);
        coordinator.ProcessAvailable();
        Assert.IsFalse(coordinator.IsConnected(SharedRadioClient.LegacyClient));
        Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.IpcClient));
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out var next));
        CollectionAssert.AreEqual(second, next!.Bytes);
    }

    [TestMethod]
    public void LateClientDeliveryCompletionAfterDisconnectIsIgnored()
    {
        var coordinator = NewCoordinator(Options() with { ClientRxQueueRecordsPerClient = 1 });
        coordinator.TryPostRadioBytes(Civ(0xE0, 0x10));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var inFlight));

        coordinator.TryPostRadioBytes(Civ(0xE0, 0x11));
        coordinator.ProcessAvailable();
        Assert.IsFalse(coordinator.IsConnected(SharedRadioClient.LegacyClient));
        coordinator.CompleteClientDelivery(inFlight!.Id, delivered: true);

        Assert.AreEqual(0, coordinator.Metrics.ClientRxDeliveries);
    }

    [TestMethod]
    public void StopRejectsNewIngressAndDiscardsPendingChunks()
    {
        var coordinator = NewCoordinator();
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x98)));

        coordinator.Stop();

        Assert.IsFalse(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x99)));
        Assert.IsFalse(coordinator.TryPostRadioBytes(Civ(0xE0, 0x10)));
        coordinator.ProcessAvailable();
        Assert.AreEqual(SharedRadioCoordinatorState.Stopped, coordinator.State);
        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
    }

    [TestMethod]
    public void QueueRecordExpiresAtTheConfiguredBoundary()
    {
        var clock = new FakeClock();
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = NewCoordinator(clock: clock, diagnostics: diagnostics);
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x98));
        coordinator.ProcessAvailable();
        clock.NowMs = Options().QueueWaitLimitMs;

        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
        Assert.IsTrue(diagnostics.Any(item => item.Kind == SharedRadioDiagnosticKind.RecordExpired));
    }

    [TestMethod]
    public void TxQueueWaitMetricMeasuresAdmissionToDispatchDelay()
    {
        var clock = new FakeClock();
        var coordinator = NewCoordinator(clock: clock);
        Assert.IsTrue(coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0xE0, 0x98)));
        coordinator.ProcessAvailable();
        clock.NowMs = 37;

        Assert.IsTrue(coordinator.TryTakeRadioWrite(out _));
        Assert.AreEqual(37L, coordinator.Metrics.TxQueueWaitMsHighWater);
    }

    [TestMethod]
    public void KenwoodRecordsAreFramedAcrossChunksAndDeliveredToBothClients()
    {
        var coordinator = NewCoordinator(Options() with { Framing = FramingMode.KenwoodAscii });
        var record = "FA00014074000;"u8.ToArray();
        coordinator.TryPostRadioBytes(record[..5]);
        coordinator.TryPostRadioBytes(record[5..]);
        coordinator.ProcessAvailable();

        foreach (var client in Enum.GetValues<SharedRadioClient>())
        {
            Assert.IsTrue(coordinator.TryTakeClientDelivery(client, out var delivery));
            CollectionAssert.AreEqual(record, delivery!.Bytes);
            coordinator.CompleteClientDelivery(delivery.Id, delivered: true);
        }
    }

    [TestMethod]
    public void KenwoodTimeoutDiscardsThroughNextDefiniteBoundary()
    {
        var clock = new FakeClock();
        var coordinator = NewCoordinator(Options() with { Framing = FramingMode.KenwoodAscii }, clock);
        var prefix = "FA000"u8.ToArray();
        var tailAndNextRecord = "14074000;FA00014074000;"u8.ToArray();
        coordinator.TryPostRadioBytes(prefix);
        coordinator.ProcessAvailable();

        clock.NowMs = KenwoodAsciiTimeoutMs;
        coordinator.ProcessAvailable();
        coordinator.TryPostRadioBytes(tailAndNextRecord);
        coordinator.ProcessAvailable();

        Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out var delivery));
        CollectionAssert.AreEqual("FA00014074000;"u8.ToArray(), delivery!.Bytes);
        coordinator.CompleteClientDelivery(delivery.Id, delivered: true);
        Assert.IsFalse(coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out _));
    }

    [TestMethod]
    public void SharedModeRejectsUnframedOrAmbiguousClientAddressConfiguration()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            NewCoordinator(Options() with { Framing = FramingMode.None }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            NewCoordinator(Options() with { IpcClientAddress = 0xE0 }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            NewCoordinator(Options() with
            {
                AllowedUnmatchedAddresses = new HashSet<byte> { 0x00 }
            }));
        Assert.ThrowsExactly<ArgumentException>(() =>
            NewCoordinator(Options() with
            {
                InformationalRadioAddress = 0x22,
                AllowedUnmatchedAddresses = new HashSet<byte> { 0x22 }
            }));
    }

    [TestMethod]
    public void CandidateTxBoundsAcceptThirtyTwoMaximumRecordsThenRejectTheNextClientRecord()
    {
            var diagnostics = new List<SharedRadioDiagnostic>();
            var coordinator = NewCoordinator(CandidateQueueOptions(), diagnostics: diagnostics);
            foreach (var client in Enum.GetValues<SharedRadioClient>())
            {
                var address = client == SharedRadioClient.LegacyClient ? (byte)0xE0 : (byte)0xE1;
                for (var sequence = 1; sequence <= 16; sequence++)
                    Assert.IsTrue(coordinator.TryPostClientBytes(client, ClientRecord(address, (byte)sequence)));
            }

            coordinator.ProcessAvailable();
            Assert.AreEqual(32, coordinator.QueuedTxRecords);
            var metrics = coordinator.Metrics;
            Assert.AreEqual(32, metrics.TxQueueRecordsHighWater);
            Assert.AreEqual(2048, metrics.TxQueueBytesHighWater);
            Assert.AreEqual(16, metrics.LegacyTxQueueRecordsHighWater);
            Assert.AreEqual(1024, metrics.LegacyTxQueueBytesHighWater);
            Assert.AreEqual(16, metrics.IpcTxQueueRecordsHighWater);
            Assert.AreEqual(1024, metrics.IpcTxQueueBytesHighWater);
            Assert.AreEqual(16, metrics.LegacyIngressChunksHighWater);
            Assert.AreEqual(1024, metrics.LegacyIngressBytesHighWater);
            Assert.AreEqual(16, metrics.IpcIngressChunksHighWater);
            Assert.AreEqual(1024, metrics.IpcIngressBytesHighWater);

            Assert.IsTrue(coordinator.TryPostClientBytes(
                SharedRadioClient.IpcClient, ClientRecord(0xE1, 17)));
            coordinator.ProcessAvailable();
            Assert.AreEqual(32, coordinator.QueuedTxRecords);
            Assert.AreEqual(32, coordinator.Metrics.AcceptedTxRecords);
            Assert.AreEqual(1, coordinator.Metrics.NotSentTxRecords);
            Assert.IsTrue(diagnostics.Any(item =>
                item.Kind == SharedRadioDiagnosticKind.RecordRejected &&
                item.Outcome == SharedRadioOutcome.NotSent));
    }

    [TestMethod]
    public void CandidateRxBoundsAcceptSixteenMaximumRecordsThenDisconnectOnlyTheFullClient()
    {
            var coordinator = NewCoordinator(CandidateQueueOptions());
            for (var sequence = 1; sequence <= 16; sequence++)
                Assert.IsTrue(coordinator.TryPostRadioBytes(AddressedRadioRecord(0xE1, (byte)sequence)));

            coordinator.ProcessAvailable();
            var metrics = coordinator.Metrics;
            Assert.AreEqual(16, metrics.IpcRxQueueRecordsHighWater);
            Assert.AreEqual(1024, metrics.IpcRxQueueBytesHighWater);
            Assert.AreEqual(16, metrics.RadioIngressChunksHighWater);
            Assert.AreEqual(1024, metrics.RadioIngressBytesHighWater);

            Assert.IsTrue(coordinator.TryPostRadioBytes(AddressedRadioRecord(0xE1, 17)));
            coordinator.ProcessAvailable();
            Assert.IsFalse(coordinator.IsConnected(SharedRadioClient.IpcClient));
            Assert.IsTrue(coordinator.IsConnected(SharedRadioClient.LegacyClient));
            Assert.AreEqual(16, coordinator.Metrics.IpcRxQueueRecordsHighWater);
            Assert.AreEqual(1024, coordinator.Metrics.IpcRxQueueBytesHighWater);
    }

    [TestMethod]
    public void CandidateIngressBoundsAcceptSixtyFourChunksAndExactlyOneKilobyteBeforeOverflow()
    {
            var coordinator = NewCoordinator(CandidateQueueOptions());
            var noiseChunk = Enumerable.Repeat((byte)0x55, 16).ToArray();
            for (var chunk = 0; chunk < 64; chunk++)
                Assert.IsTrue(coordinator.TryPostRadioBytes(noiseChunk));

            Assert.IsFalse(coordinator.TryPostRadioBytes(noiseChunk));
            coordinator.ProcessAvailable();
            var metrics = coordinator.Metrics;
            Assert.AreEqual(64, metrics.RadioIngressChunksHighWater);
            Assert.AreEqual(1024, metrics.RadioIngressBytesHighWater);
            Assert.AreEqual(SharedRadioCoordinatorState.Faulted, coordinator.State);
    }

    private static SharedRadioCoordinator NewCoordinator(
            SharedRadioCoordinatorOptions? options = null,
            FakeClock? clock = null,
        List<SharedRadioDiagnostic>? diagnostics = null) =>
        new(options ?? Options(), clock ?? new FakeClock(),
            diagnostics is null ? _ => { } : diagnostics.Add);

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

    private static SharedRadioCoordinatorOptions CandidateQueueOptions() =>
        Options() with
        {
            IngressChunksPerSource = 64,
            IngressBytesPerSource = 1024,
            TxQueueRecordsPerClient = 16,
            TxQueueBytesPerClient = 1024,
            TxQueueRecordsPerProfile = 32,
            ClientRxQueueRecordsPerClient = 16,
            ClientRxQueueBytesPerClient = 1024
        };

    private static byte[] ClientRecord(byte sourceAddress, byte sequence) =>
        MaximumCivRecord(0x94, sourceAddress, sequence);

    private static byte[] AddressedRadioRecord(byte destination, byte sequence) =>
        MaximumCivRecord(destination, 0x94, sequence);

    private static byte[] MaximumCivRecord(byte destination, byte sourceAddress, byte sequence)
    {
        var record = Enumerable.Repeat((byte)0x55, 64).ToArray();
        record[0] = 0xFE;
        record[1] = 0xFE;
        record[2] = destination;
        record[3] = sourceAddress;
        record[4] = sequence;
        record[^1] = 0xFD;
        return record;
    }

    private static byte[] Civ(byte destination, byte command) =>
        [0xFE, 0xFE, destination, 0x00, command, 0xFD];

    private const long KenwoodAsciiTimeoutMs = 500;

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
