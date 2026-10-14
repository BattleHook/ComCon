using System.Text;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class SimulatedReceiveFeedTests
{
    [TestMethod]
    public void CivPublishesExactFramesAtEveryPossibleSplit()
    {
        var bytes = Convert.FromHexString("FEFEE07A1C0000FDFEFEE07A030000100700FD");
        for (var split = 1; split < bytes.Length; split++)
        {
            var feed = NewFeed(FramingMode.Civ);
            feed.Feed(bytes.AsSpan(0, split));
            feed.Feed(bytes.AsSpan(split));
            var batch = feed.Journal.ReadAfter(0);
            Assert.HasCount(2, batch.Observations);
            Assert.AreEqual("FEFEE07A1C0000FD", batch.Observations[0].Hex);
            Assert.AreEqual("FEFEE07A030000100700FD", batch.Observations[1].Hex);
            Assert.IsFalse(feed.HasPartialRecord);
        }
    }

    [TestMethod]
    public void KenwoodPublishesExactRecordsAtEveryPossibleSplit()
    {
        var bytes = Encoding.ASCII.GetBytes("FA00007100000;MD2;");
        for (var split = 1; split < bytes.Length; split++)
        {
            var feed = NewFeed(FramingMode.KenwoodAscii);
            feed.Feed(bytes.AsSpan(0, split));
            feed.Feed(bytes.AsSpan(split));
            var batch = feed.Journal.ReadAfter(0);
            Assert.HasCount(2, batch.Observations);
            Assert.AreEqual(Convert.ToHexString(Encoding.ASCII.GetBytes("FA00007100000;")), batch.Observations[0].Hex);
            Assert.AreEqual(Convert.ToHexString("MD2;"u8), batch.Observations[1].Hex);
            Assert.IsFalse(feed.HasPartialRecord);
        }
    }

    [TestMethod]
    [DataRow(FramingMode.Civ, "FEFEE07A1C0000FD", 250L)]
    [DataRow(FramingMode.KenwoodAscii, "464130303030373130303030303B", 500L)]
    public void PartialRecordIsNotPublishedAndExpiresAtExactIdleDeadline(
        FramingMode framing, string hex, long timeout)
    {
        var clock = new TestClock();
        var faults = new List<ReceiveFramingFault>();
        var feed = new SimulatedReceiveFeed(framing, clock, faults.Add);
        var bytes = Convert.FromHexString(hex);
        feed.Feed(bytes.AsSpan(0, bytes.Length - 1));
        Assert.IsTrue(feed.HasPartialRecord);
        Assert.IsEmpty(feed.Journal.ReadAfter(0).Observations);
        clock.NowMs = timeout - 1;
        feed.Tick();
        Assert.IsNull(feed.Fault);
        clock.NowMs = timeout;
        Assert.ThrowsExactly<InvalidDataException>(feed.Tick);
        Assert.HasCount(1, faults);
        Assert.AreEqual(timeout, faults[0].TimestampMs);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Journal.ReadAfter(0));
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Feed(bytes));
        Assert.HasCount(1, faults);
    }

    [TestMethod]
    [DataRow("FEFEFEE07A1C0000FD", "PreambleNormalized")]
    [DataRow("00FEFEE07A1C0000FD", "Noise")]
    [DataRow("FCFEFEE07A1C0000FD", "Collision")]
    [DataRow("FEFEFD", "CandidateTooShort")]
    [DataRow("FEFEE07A1CFEFEE07A1C0000FD", "DamagedCandidateResynchronized")]
    public void CivAnomalyLatchesBeforeRecoveredOrNormalizedFrameCanPublish(string hex, string reason)
    {
        var faults = new List<ReceiveFramingFault>();
        var feed = new SimulatedReceiveFeed(FramingMode.Civ, new TestClock(), faults.Add);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Feed(Convert.FromHexString(hex)));
        Assert.AreEqual(reason, feed.Fault!.Reason);
        Assert.HasCount(1, faults);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Journal.ReadAfter(0));
    }

    [TestMethod]
    public void KenwoodInvalidByteBlocksFollowingGoodRecordInSameChunk()
    {
        var feed = NewFeed(FramingMode.KenwoodAscii);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Feed("FA\0;MD2;"u8));
        Assert.AreEqual("InvalidByte", feed.Fault!.Reason);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Journal.ReadAfter(0));
    }

    [TestMethod]
    [DataRow(FramingMode.Civ)]
    [DataRow(FramingMode.KenwoodAscii)]
    public void OversizedIngressFaultsAndCannotCompleteExistingPartial(FramingMode framing)
    {
        var feed = NewFeed(framing);
        feed.Feed(framing == FramingMode.Civ ? [0xFE, 0xFE] : "FA"u8);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Feed(new byte[4097]));
        Assert.AreEqual("IngressOverflow", feed.Fault!.Reason);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Journal.ReadAfter(0));
        Assert.ThrowsExactly<InvalidDataException>(feed.Tick);
    }

    [TestMethod]
    [DataRow(FramingMode.Civ, 64)]
    [DataRow(FramingMode.KenwoodAscii, 256)]
    public void MaximumRecordSizeIsAcceptedButOneExtraByteFaults(FramingMode framing, int size)
    {
        var payload = framing == FramingMode.Civ
            ? new byte[] { 0xFE, 0xFE }.Concat(Enumerable.Repeat((byte)0x01, size - 3)).Append((byte)0xFD).ToArray()
            : Enumerable.Repeat((byte)'A', size - 1).Append((byte)';').ToArray();
        var feed = NewFeed(framing);
        feed.Feed(payload);
        Assert.AreEqual(Convert.ToHexString(payload), feed.Journal.ReadAfter(0).Observations.Single().Hex);
        var invalid = payload[..^1].Append((byte)0x01).Append(payload[^1]).ToArray();
        if (framing == FramingMode.KenwoodAscii) invalid[^2] = (byte)'A';
        var badFeed = NewFeed(framing);
        Assert.ThrowsExactly<InvalidDataException>(() => badFeed.Feed(invalid));
        Assert.ThrowsExactly<InvalidDataException>(() => badFeed.Journal.ReadAfter(0));
    }

    [TestMethod]
    public void AgeStartsAtFrameCompletionAndNeverRefreshesOnTick()
    {
        var clock = new TestClock();
        var feed = new SimulatedReceiveFeed(FramingMode.Civ, clock, _ => Assert.Fail("Unexpected fault."));
        feed.Feed(Convert.FromHexString("FEFEE07A1C0000"));
        clock.NowMs = 100;
        feed.Feed([0xFD]);
        clock.NowMs = 200;
        feed.Tick();
        Assert.AreEqual(100L, feed.Journal.ReadAfter(0).Observations.Single().AgeMs);
    }

    [TestMethod]
    [DataRow(FramingMode.Civ, 1000L, 200L)]
    [DataRow(FramingMode.KenwoodAscii, 2000L, 400L)]
    public void TotalDeadlineExpiresDespiteContinuingPartialActivity(
        FramingMode framing, long total, long interval)
    {
        var clock = new TestClock();
        var feed = new SimulatedReceiveFeed(framing, clock, _ => { });
        feed.Feed(framing == FramingMode.Civ ? [0xFE, 0xFE, 0xE0] : "FA"u8);
        for (var time = interval; time < total; time += interval)
        {
            clock.NowMs = time;
            feed.Feed([0x30]);
        }
        clock.NowMs = total - 1;
        feed.Tick();
        Assert.IsNull(feed.Fault);
        clock.NowMs = total;
        Assert.ThrowsExactly<InvalidDataException>(feed.Tick);
        Assert.AreEqual(
            framing == FramingMode.Civ ? "CandidateExpiredTotal" : "PartialRecordExpiredTotal",
            feed.Fault!.Reason);
    }

    [TestMethod]
    public void MaximumIngressChunkPublishesOnlyCompleteBoundedFrames()
    {
        var feed = NewFeed(FramingMode.Civ);
        var frame = Convert.FromHexString("FEFEE07A1C0000FD");
        feed.Feed(Enumerable.Range(0, 512).SelectMany(_ => frame).ToArray());
        Assert.IsNull(feed.Fault);
        var batch = feed.Journal.ReadAfter(0);
        Assert.IsTrue(batch.Gap);
        Assert.AreEqual("512", batch.LatestObservationId);
        Assert.AreEqual("385", batch.Observations[0].ObservationId);
        Assert.IsFalse(feed.HasPartialRecord);
    }

    [TestMethod]
    public void NoFramingOrUnknownFramingFailsExplicitly()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewFeed(FramingMode.None));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => NewFeed((FramingMode)99));
    }

    [TestMethod]
    public void FaultReportingFailurePropagatesAndFeedRemainsLatched()
    {
        var feed = new SimulatedReceiveFeed(FramingMode.Civ, new TestClock(),
            _ => throw new InvalidOperationException("Diagnostic sink failed."));
        Assert.ThrowsExactly<InvalidOperationException>(() => feed.Feed([0xFC]));
        Assert.IsNotNull(feed.Fault);
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Feed(Convert.FromHexString("FEFEE07A1C0000FD")));
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Journal.ReadAfter(0));
    }

    private static SimulatedReceiveFeed NewFeed(FramingMode framing) =>
        new(framing, new TestClock(), _ => { });

    private sealed class TestClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
