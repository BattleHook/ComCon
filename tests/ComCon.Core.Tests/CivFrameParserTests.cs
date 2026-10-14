using ComCon.Core.Protocol;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class CivFrameParserTests
{
    private static readonly byte[] Frame = [0xFE, 0xFE, 0xE0, 0x7A, 0x03, 0xFD];

    [TestMethod]
    public void FeedEmitsOneFrameAtEveryPossibleSplit()
    {
        for (var split = 0; split <= Frame.Length; split++)
        {
            var events = new List<CivParserEvent>();
            var parser = CreateParser(events, new TestClock());
            parser.Feed(Frame.AsSpan(0, split));
            parser.Feed(Frame.AsSpan(split));

            var received = GetFrames(events);
            Assert.HasCount(1, received, $"Split position {split}.");
            CollectionAssert.AreEqual(Frame, received[0].Frame.ToArray(), $"Split position {split}.");
            Assert.IsFalse(parser.HasPartialFrame);
        }
    }

    [TestMethod]
    public void FeedEmitsConcatenatedFramesSeparatelyAndAcceptsMaximumLength()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        var concatenated = Frame.Concat(Frame).ToArray();
        parser.Feed(concatenated);

        Assert.HasCount(2, GetFrames(events));
        var maximumFrame = new byte[CivFrameParser.MaximumFrameLength];
        Frame.AsSpan(0, 5).CopyTo(maximumFrame);
        Array.Fill(maximumFrame, (byte)0x01, 5, maximumFrame.Length - 6);
        maximumFrame[^1] = 0xFD;
        events.Clear();
        parser.Feed(maximumFrame);

        var received = GetFrames(events);
        Assert.HasCount(1, received);
        Assert.HasCount(CivFrameParser.MaximumFrameLength, received[0].Frame);
        CollectionAssert.AreEqual(maximumFrame, received[0].Frame.ToArray());
    }

    [TestMethod]
    public void FeedNormalizesOnlyExtraLeadingPreambleBytesAndReportsThem()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        byte[] input = [0xFE, 0xFE, 0xFE, 0xFE, 0xE0, 0x7A, 0x03, 0xFD];
        parser.Feed(input);

        var received = GetFrames(events);
        Assert.HasCount(1, received);
        CollectionAssert.AreEqual(Frame, received[0].Frame.ToArray());
        var normalized = GetAnomalies(events, CivParserAnomalyKind.PreambleNormalized);
        Assert.HasCount(1, normalized);
        Assert.AreEqual(2, normalized[0].ObservedCount);
    }

    [TestMethod]
    public void InteriorPreambleRejectsDamagedCandidateAndResynchronizes()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        byte[] input = [0xFE, 0xFE, 0xE0, 0x7A, 0x11, 0xFE, 0xFE, 0xE0, 0x7A, 0x03, 0xFD];
        parser.Feed(input);

        Assert.HasCount(1, GetFrames(events));
        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.DamagedCandidateResynchronized));
    }

    [TestMethod]
    public void SingleInteriorFeRejectsCandidateWithoutLosingFollowingPreamble()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        byte[] input = [0xFE, 0xFE, 0xE0, 0x7A, 0x11, 0xFE, 0x03, .. Frame];
        parser.Feed(input);

        Assert.HasCount(1, GetFrames(events));
        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.InteriorFrameDelimiter));
    }

    [TestMethod]
    public void InteriorFcRejectsCandidateAndOutsideFcIsCollision()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        byte[] input = [0xFC, 0xFE, 0xFE, 0xE0, 0x7A, 0xFC, .. Frame];
        parser.Feed(input);

        Assert.HasCount(1, GetFrames(events));
        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.Collision));
        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.InteriorCollision));
    }

    [TestMethod]
    public void MalformedShortAndOverlongCandidatesAreReportedAndParserRecovers()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        byte[] shortThenValid = [0xFE, 0xFE, 0xE0, 0xFD, .. Frame];
        parser.Feed(shortThenValid);
        var shortCandidate = GetAnomalies(events, CivParserAnomalyKind.CandidateTooShort).Single();
        Assert.AreEqual("FEFEE0FD", Convert.ToHexString(shortCandidate.RawBytes.AsSpan()));

        events.Clear();
        var overlongThenValid = new byte[CivFrameParser.MaximumFrameLength + 1 + Frame.Length];
        overlongThenValid[0] = 0xFE;
        overlongThenValid[1] = 0xFE;
        overlongThenValid.AsSpan(2, CivFrameParser.MaximumFrameLength - 2).Fill(0x01);
        overlongThenValid[CivFrameParser.MaximumFrameLength] = 0x02;
        Frame.CopyTo(overlongThenValid, CivFrameParser.MaximumFrameLength + 1);
        parser.Feed(overlongThenValid);

        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.CandidateTooLong));
        Assert.HasCount(1, GetFrames(events));
    }

    [TestMethod]
    public void ExcessiveLeadingPreambleIsRejectedWithBoundedDiagnostics()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        var excessivePreambleThenFrame = Enumerable.Repeat((byte)0xFE, CivFrameParser.MaximumFrameLength + 1)
            .Concat(Frame)
            .ToArray();
        parser.Feed(excessivePreambleThenFrame);

        var preambleError = GetAnomalies(events, CivParserAnomalyKind.PreambleTooLong).Single();
        Assert.AreEqual(CivFrameParser.MaximumFrameLength + 1, preambleError.ObservedCount);
        Assert.HasCount(CivFrameParser.MaximumDiagnosticSampleLength, preambleError.Sample);
        Assert.HasCount(1, GetFrames(events));
    }

    [TestMethod]
    public void NoiseDiagnosticsAggregateCountWithBoundedSampleAndEveryFeedReportsActivity()
    {
        var events = new List<CivParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        var noise = Enumerable.Repeat((byte)0x01, 80).ToArray();
        parser.Feed(noise);
        clock.NowMs = 1;
        parser.Tick();

        var activity = events.OfType<CivReceiveActivity>().Single();
        Assert.AreEqual(noise.Length, activity.ByteCount);
        var anomaly = GetAnomalies(events, CivParserAnomalyKind.Noise).Single();
        Assert.AreEqual(noise.Length, anomaly.ObservedCount);
        Assert.HasCount(CivFrameParser.MaximumDiagnosticSampleLength, anomaly.Sample);
    }

    [TestMethod]
    public void OversizedIngressIsRejectedAtomicallyAndReported()
    {
        var events = new List<CivParserEvent>();
        var parser = CreateParser(events, new TestClock());
        var oversized = new byte[CivFrameParser.MaximumIngressBytes + 1];
        Frame.CopyTo(oversized, 0);
        parser.Feed(oversized);

        Assert.IsEmpty(GetFrames(events));
        Assert.AreEqual(oversized.Length, events.OfType<CivReceiveActivity>().Single().ByteCount);
        var overflow = GetAnomalies(events, CivParserAnomalyKind.IngressOverflow).Single();
        Assert.AreEqual(oversized.Length, overflow.ObservedCount);
        Assert.HasCount(CivFrameParser.MaximumDiagnosticSampleLength, overflow.Sample);

        events.Clear();
        parser.Feed(Frame);
        Assert.HasCount(1, GetFrames(events));
    }

    [TestMethod]
    public void IdleExpiryOccursAtExactDeadlineAndNotBefore()
    {
        var events = new List<CivParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed([0xFE, 0xFE, 0xE0]);

        clock.NowMs = CivFrameParser.CandidateIdleTimeoutMs - 1;
        parser.Tick();
        Assert.IsEmpty(GetAnomalies(events, CivParserAnomalyKind.CandidateExpiredIdle));
        Assert.IsTrue(parser.HasPartialFrame);

        clock.NowMs = CivFrameParser.CandidateIdleTimeoutMs;
        parser.Tick();
        var expiredCandidate = GetAnomalies(events, CivParserAnomalyKind.CandidateExpiredIdle).Single();
        Assert.AreEqual("FEFEE0", Convert.ToHexString(expiredCandidate.RawBytes.AsSpan()));
        Assert.AreEqual(CivFrameParser.CandidateIdleTimeoutMs, expiredCandidate.CandidateIdleElapsedMs);
        Assert.AreEqual(CivFrameParser.CandidateIdleTimeoutMs, expiredCandidate.CandidateTotalElapsedMs);
        Assert.IsFalse(parser.HasPartialFrame);
    }

    [TestMethod]
    public void TotalExpiryOccursAtExactDeadlineEvenWhenIdleDeadlineWasRefreshed()
    {
        var events = new List<CivParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed([0xFE, 0xFE, 0xE0]);

        clock.NowMs = 200;
        parser.Feed([0x7A]);
        clock.NowMs = 400;
        parser.Feed([0x03]);
        clock.NowMs = 600;
        parser.Feed([0x04]);
        clock.NowMs = 800;
        parser.Feed([0x05]);
        clock.NowMs = 900;
        parser.Feed([0x06]);
        clock.NowMs = CivFrameParser.CandidateTotalTimeoutMs - 1;
        parser.Tick();
        Assert.IsEmpty(GetAnomalies(events, CivParserAnomalyKind.CandidateExpiredTotal));
        Assert.IsTrue(parser.HasPartialFrame);

        clock.NowMs = CivFrameParser.CandidateTotalTimeoutMs;
        parser.Tick();
        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.CandidateExpiredTotal));
        Assert.IsFalse(parser.HasPartialFrame);
    }

    [TestMethod]
    public void FeedExpiresStaleCandidateBeforeParsingNewBytes()
    {
        var events = new List<CivParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed([0xFE, 0xFE, 0xE0]);
        clock.NowMs = CivFrameParser.CandidateIdleTimeoutMs;
        parser.Feed(Frame);

        Assert.HasCount(1, GetAnomalies(events, CivParserAnomalyKind.CandidateExpiredIdle));
        Assert.HasCount(1, GetFrames(events));
    }

    [TestMethod]
    public void SplitPreambleUsesItsOwnIdleTimestampAfterACompletedFrame()
    {
        var events = new List<CivParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Frame);

        clock.NowMs = CivFrameParser.CandidateIdleTimeoutMs + 50;
        parser.Feed([0xFE]);
        clock.NowMs += 16;
        parser.Feed(Frame.AsSpan(1));

        var received = GetFrames(events);
        Assert.HasCount(2, received);
        CollectionAssert.AreEqual(Frame, received[1].Frame.ToArray());
        Assert.IsEmpty(GetAnomalies(events, CivParserAnomalyKind.CandidateExpiredIdle));
        Assert.IsEmpty(GetAnomalies(events, CivParserAnomalyKind.LonePreamble));
        Assert.IsFalse(parser.HasPartialFrame);
    }

    [TestMethod]
    public void EventSinkCannotReenterParser()
    {
        var clock = new TestClock();
        CivFrameParser? parser = null;
        parser = new CivFrameParser(clock, _ => parser!.Tick());

        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Feed(Frame));
    }

    private static CivFrameParser CreateParser(List<CivParserEvent> events, TestClock clock) =>
        new(clock, events.Add);

    private static List<CivFrameReceived> GetFrames(IEnumerable<CivParserEvent> events) =>
        events.OfType<CivFrameReceived>().ToList();

    private static List<CivParserAnomaly> GetAnomalies(
        IEnumerable<CivParserEvent> events,
        CivParserAnomalyKind kind) =>
        events.OfType<CivParserAnomaly>().Where(anomaly => anomaly.Kind == kind).ToList();

    private sealed class TestClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
