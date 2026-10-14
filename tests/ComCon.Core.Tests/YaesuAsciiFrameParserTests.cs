using System.Text;
using ComCon.Core.Protocol;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class YaesuAsciiFrameParserTests
{
    [TestMethod]
    public void MaximumLengthRecordIsAcceptedAndOverLimitRecordRecoversAtItsDelimiter()
    {
        var events = new List<ParserEvent>();
        var parser = CreateParser(events, new TestClock());
        var maximum = Encoding.ASCII.GetBytes("AB" + new string('1', YaesuAsciiFrameParser.MaximumRecordLength - 3) + ";");
        parser.Feed(maximum);

        CollectionAssert.AreEqual(maximum, GetFrames(events).Single().Record.ToArray());

        events.Clear();
        var overLimit = Encoding.ASCII.GetBytes("AB" + new string('1', YaesuAsciiFrameParser.MaximumRecordLength - 2) + ";FA;");
        parser.Feed(overLimit);

        var anomaly = GetAnomalies(events, ParserAnomalyKind.RecordTooLong).Single();
        Assert.AreEqual(YaesuAsciiFrameParser.MaximumRecordLength + 1, anomaly.ObservedCount);
        Assert.HasCount(YaesuAsciiFrameParser.MaximumDiagnosticSampleLength, anomaly.Sample);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("FA;"), GetFrames(events).Single().Record.ToArray());
    }

    [TestMethod]
    public void MalformedRecordEndingAtDelimiterDoesNotQuarantineFollowingRecord()
    {
        var events = new List<ParserEvent>();
        var parser = CreateParser(events, new TestClock());

        parser.Feed(Encoding.ASCII.GetBytes("A;FA;"));

        Assert.HasCount(1, GetAnomalies(events, ParserAnomalyKind.InvalidStructure));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("FA;"), GetFrames(events).Single().Record.ToArray());
        Assert.IsFalse(parser.HasPartialFrame);
    }

    [TestMethod]
    public void TimeoutOnTickQuarantinesUntilTheNextDelimiter()
    {
        var events = new List<ParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Encoding.ASCII.GetBytes("FA123"));

        clock.NowMs = YaesuAsciiFrameParser.PartialRecordIdleTimeoutMs;
        parser.Tick();

        Assert.HasCount(1, GetAnomalies(events, ParserAnomalyKind.PartialRecordExpiredIdle));
        Assert.IsTrue(parser.HasPartialFrame);
        parser.Feed(Encoding.ASCII.GetBytes("FB;FA;"));

        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("FA;"), GetFrames(events).Single().Record.ToArray());
        Assert.IsFalse(parser.HasPartialFrame);
    }

    [TestMethod]
    public void IngressOverflowInvalidatesPartialRecordAndKeepsDiagnosticsBounded()
    {
        var events = new List<ParserEvent>();
        var parser = CreateParser(events, new TestClock());
        parser.Feed(Encoding.ASCII.GetBytes("FA123"));
        var oversized = Enumerable.Repeat((byte)'7', YaesuAsciiFrameParser.MaximumIngressBytes + 1).ToArray();

        parser.Feed(oversized);

        var anomaly = GetAnomalies(events, ParserAnomalyKind.IngressOverflow).Single();
        Assert.AreEqual(oversized.Length, anomaly.ObservedCount);
        Assert.HasCount(YaesuAsciiFrameParser.MaximumDiagnosticSampleLength, anomaly.Sample);
        Assert.HasCount(YaesuAsciiFrameParser.MaximumDiagnosticSampleLength, anomaly.RawBytes);
        Assert.IsTrue(parser.HasPartialFrame);

        parser.Feed(Encoding.ASCII.GetBytes(";FA;"));

        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("FA;"), GetFrames(events).Single().Record.ToArray());
        Assert.IsFalse(parser.HasPartialFrame);
    }

    private static YaesuAsciiFrameParser CreateParser(List<ParserEvent> events, TestClock clock) =>
        new(clock, parserEvent =>
        {
            events.Add(parserEvent);
            return true;
        });

    private static List<FrameReceived> GetFrames(IEnumerable<ParserEvent> events) =>
        events.OfType<FrameReceived>().ToList();

    private static List<ParserAnomaly> GetAnomalies(
        IEnumerable<ParserEvent> events,
        ParserAnomalyKind kind) =>
        events.OfType<ParserAnomaly>().Where(anomaly => anomaly.Kind == kind).ToList();

    private sealed class TestClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
