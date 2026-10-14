using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class CivSharedExchangeTests
{
    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }

    private sealed class Fixture
    {
        public FakeClock Clock { get; } = new();
        public List<SharedRadioDiagnostic> Diagnostics { get; } = [];
        public SharedRadioCoordinator Coordinator { get; }

        public Fixture()
        {
            Coordinator = new(new(FramingMode.Civ, 0xE0, 0xE1,
                64, 1024, 16, 1024, 32, 16, 1024, 2500,
                InformationalRadioAddress: 0x7A, CivExchangeGateEnabled: true),
                Clock, Diagnostics.Add);
        }

        public SharedRadioWrite Start(bool complete = true)
        {
            Coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Request(0xE0));
            Coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, Request(0xE1));
            Coordinator.ProcessAvailable();
            Assert.IsTrue(Coordinator.TryTakeRadioWrite(out var write));
            if (complete)
                Coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Succeeded);
            return write!;
        }

        public void Receive(byte[] bytes)
        {
            Assert.IsTrue(Coordinator.TryPostRadioBytes(bytes));
            Coordinator.ProcessAvailable();
        }

        public void Tick(long now)
        {
            Clock.NowMs = now;
            Coordinator.ProcessAvailable();
        }
    }

    private static byte[] Request(byte controller) => [0xFE, 0xFE, 0x7A, controller, 0x03, 0xFD];
    private static byte[] Reply(byte controller = 0xE0, byte radio = 0x7A, byte command = 0x03) =>
        [0xFE, 0xFE, controller, radio, command, 0xFD];

    [TestMethod]
    [DataRow((byte)0x03)]
    [DataRow((byte)0xFB)]
    [DataRow((byte)0xFA)]
    public void DirectedFrameClosesExchangeWithoutCommandInterpretation(byte command)
    {
        var f = new Fixture();
        f.Start();
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        f.Receive(Reply(command: command));
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out var next));
        Assert.AreEqual(SharedRadioClient.IpcClient, next!.Client);
    }

    [TestMethod]
    public void EchoBroadcastWrongControllerAndWrongRadioNeverReleaseReservation()
    {
        var f = new Fixture();
        f.Start();
        foreach (var frame in new[] { Request(0xE0), Reply(0), Reply(0xE1), Reply(radio: 0x66) })
        {
            f.Receive(frame);
            Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        }
        Assert.IsTrue(f.Coordinator.TryTakeClientDelivery(SharedRadioClient.LegacyClient, out _));
        f.Receive(Reply());
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out _));
    }

    [TestMethod]
    public void SplitReplyDoesNotReleaseUntilCompleteAndEarlyReplyWaitsForWriteCompletion()
    {
        var f = new Fixture();
        var write = f.Start(complete: false);
        var reply = Reply();
        f.Receive(reply[..3]);
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        f.Receive(reply[3..]);
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        f.Coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out _));
    }

    [TestMethod]
    public void AnswerClockStartsAtWriteCompletionAndSilenceStartsAtTimeout()
    {
        var f = new Fixture();
        var write = f.Start(complete: false);
        f.Tick(500);
        f.Coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
        f.Tick(1249);
        Assert.IsFalse(f.Diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.RadioReplyTimeout));
        f.Tick(1250);
        Assert.AreEqual(1, f.Diagnostics.Count(d => d.Kind == SharedRadioDiagnosticKind.RadioReplyTimeout));
        f.Tick(1499);
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        f.Tick(1500);
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out _));
        Assert.AreEqual(1, f.Diagnostics.Count(d => d.Kind == SharedRadioDiagnosticKind.RadioExchangeRecovered));
    }

    [TestMethod]
    public void EveryReceivedByteRestartsSilenceAndPartialFrameMustExpire()
    {
        var f = new Fixture();
        f.Start();
        f.Tick(750);
        f.Clock.NowMs = 900;
        f.Receive([0xFE, 0xFE]);
        f.Tick(1149);
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        f.Tick(1150);
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out _));
        Assert.IsTrue(f.Diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.FramingAnomaly));
    }

    [TestMethod]
    public void LateDirectedReplyResolvesRecoveryWithoutReplay()
    {
        var f = new Fixture();
        f.Start();
        f.Tick(750);
        f.Clock.NowMs = 800;
        f.Receive(Reply());
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out var next));
        Assert.AreEqual(SharedRadioClient.IpcClient, next!.Client);
        Assert.AreEqual(2L, f.Coordinator.Metrics.DispatchedTxRecords);
    }

    [TestMethod]
    public void ContinuousNoiseExhaustsRecoveryButKeepsEndpointsAndRxOpen()
    {
        var f = new Fixture();
        f.Start();
        f.Tick(750);
        foreach (var time in new[] { 900, 1100, 1300, 1499 })
        {
            f.Clock.NowMs = time;
            f.Receive([0x55]);
        }
        f.Tick(1500);
        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, f.Coordinator.State);
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        Assert.IsTrue(f.Coordinator.IsConnected(SharedRadioClient.LegacyClient));
        Assert.IsTrue(f.Coordinator.IsConnected(SharedRadioClient.IpcClient));
        Assert.IsTrue(f.Diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.RecordRejected &&
            d.Outcome == SharedRadioOutcome.NotSent));
        f.Receive(Reply(0xE1));
        Assert.IsTrue(f.Coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out _));
        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, f.Coordinator.State);
    }

    [TestMethod]
    public void DisconnectAndReattachDoNotReleaseOldReservation()
    {
        var f = new Fixture();
        f.Start();
        f.Coordinator.NotifyClientDisconnected(SharedRadioClient.LegacyClient, "Old owner disconnected.");
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        f.Receive(Reply());
        Assert.IsTrue(f.Coordinator.TryTakeRadioWrite(out _));
    }

    [TestMethod]
    public void ProvenZeroWriteReleasesButUnknownWriteFaults()
    {
        foreach (var completion in new[] { AdapterWriteCompletion.ProvenZeroBytes, AdapterWriteCompletion.Unknown })
        {
            var f = new Fixture();
            var write = f.Start(complete: false);
            f.Coordinator.CompleteRadioWrite(write.Id, completion);
            Assert.AreEqual(completion == AdapterWriteCompletion.ProvenZeroBytes,
                f.Coordinator.TryTakeRadioWrite(out _));
        }
    }

    [TestMethod]
    public void QueueExpiresAt2500EvenWhileReservationIsHeld()
    {
        var f = new Fixture();
        f.Start(complete: false);
        f.Tick(2499);
        Assert.AreEqual(1, f.Coordinator.QueuedTxRecords);
        f.Tick(2500);
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        Assert.AreEqual(0, f.Coordinator.QueuedTxRecords);
        Assert.IsTrue(f.Diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.RecordExpired &&
            d.Outcome == SharedRadioOutcome.NotSent));
    }

    [TestMethod]
    public void UnsupportedAddressShapeIsExplicitlyNotSent()
    {
        var f = new Fixture();
        f.Coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Request(0xE1));
        f.Coordinator.ProcessAvailable();
        Assert.IsFalse(f.Coordinator.TryTakeRadioWrite(out _));
        Assert.IsTrue(f.Diagnostics.Any(d => d.Kind == SharedRadioDiagnosticKind.RecordRejected &&
            d.Outcome == SharedRadioOutcome.NotSent));
    }

    [TestMethod]
    public void ReservationStillCountsAgainstBoundedAdmission()
    {
        var f = new Fixture();
        f.Start();
        for (var i = 0; i < 16; i++)
            f.Coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Request(0xE0));
        f.Coordinator.ProcessAvailable();
        Assert.AreEqual(16, f.Coordinator.QueuedTxRecords);
        Assert.AreEqual(1, f.Diagnostics.Count(d => d.Kind == SharedRadioDiagnosticKind.RecordRejected));
    }

    [TestMethod]
    [DataRow(1500, SharedRadioCoordinatorState.Running)]
    [DataRow(1501, SharedRadioCoordinatorState.Faulted)]
    public void MatchingReplyAtRecoveryBoundaryWinsButBeyondBoundaryDoesNot(
        int time, SharedRadioCoordinatorState expected)
    {
        var f = new Fixture();
        f.Start();
        f.Tick(750);
        f.Clock.NowMs = 1400;
        f.Receive([0x55]);
        f.Clock.NowMs = time;
        f.Receive(Reply());
        Assert.AreEqual(expected, f.Coordinator.State);
    }

    [TestMethod]
    public void SilenceThatOnlyQualifiesAfterRecoveryDeadlineCannotResume()
    {
        var f = new Fixture();
        f.Start();
        f.Tick(750);
        f.Clock.NowMs = 1400;
        f.Receive([0x55]);
        f.Tick(1700);
        Assert.AreEqual(SharedRadioCoordinatorState.Faulted, f.Coordinator.State);
    }
}
