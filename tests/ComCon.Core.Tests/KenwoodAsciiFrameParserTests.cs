using System.Text;
using ComCon.Core.Protocol;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class KenwoodAsciiFrameParserTests
{
    private static readonly byte[] ReadFrequency = Encoding.ASCII.GetBytes("FA;");
    private static readonly byte[] AnswerFrequency = Encoding.ASCII.GetBytes("FA00007000000;");

    [TestMethod]
    public void FeedEmitsRecordsAtEveryPossibleSplitAndPreservesTheirBytes()
    {
        byte[] input = [.. ReadFrequency, .. AnswerFrequency, .. Encoding.ASCII.GetBytes("?;E;O;")];
        for (var split = 0; split <= input.Length; split++)
        {
            var events = new List<KenwoodAsciiParserEvent>();
            var parser = CreateParser(events, new TestClock());
            parser.Feed(input.AsSpan(0, split));
            parser.Feed(input.AsSpan(split));

            var records = GetRecords(events);
            Assert.HasCount(5, records, $"Split position {split}.");
            CollectionAssert.AreEqual(ReadFrequency, records[0].Record.ToArray(), $"Split position {split}.");
            CollectionAssert.AreEqual(AnswerFrequency, records[1].Record.ToArray(), $"Split position {split}.");
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("?;"), records[2].Record.ToArray());
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("E;"), records[3].Record.ToArray());
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("O;"), records[4].Record.ToArray());
            Assert.IsFalse(parser.HasPartialRecord);
        }
    }

    [TestMethod]
    public void FeedEmitsConcatenatedRecordsAndRecordsOwnImmutableCopies()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var parser = CreateParser(events, new TestClock());
        var input = Encoding.ASCII.GetBytes("FA;FB;");
        parser.Feed(input);
        Array.Fill(input, (byte)'X');

        var records = GetRecords(events);
        Assert.HasCount(2, records);
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("FA;"), records[0].Record.ToArray());
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("FB;"), records[1].Record.ToArray());
    }

    [TestMethod]
    public void MaximumRecordLengthIsAcceptedAndLongerRecordIsRejectedUntilTerminator()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var parser = CreateParser(events, new TestClock());
        var maximum = Enumerable.Repeat((byte)'A', KenwoodAsciiFrameParser.MaximumRecordLength - 1)
            .Append((byte)';')
            .ToArray();
        parser.Feed(maximum);

        var received = GetRecords(events).Single();
        Assert.HasCount(KenwoodAsciiFrameParser.MaximumRecordLength, received.Record);
        CollectionAssert.AreEqual(maximum, received.Record.ToArray());

        events.Clear();
        var tooLong = Enumerable.Repeat((byte)'B', KenwoodAsciiFrameParser.MaximumRecordLength)
            .Concat(Encoding.ASCII.GetBytes(";IF;"))
            .ToArray();
        parser.Feed(tooLong);

        Assert.HasCount(1, GetRecords(events));
        CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("IF;"), GetRecords(events)[0].Record.ToArray());
        var anomaly = GetAnomalies(events, KenwoodAsciiParserAnomalyKind.RecordTooLong).Single();
        Assert.AreEqual(KenwoodAsciiFrameParser.MaximumRecordLength, anomaly.ObservedCount);
        Assert.HasCount(KenwoodAsciiFrameParser.MaximumDiagnosticSampleLength, anomaly.Sample);
    }

    [TestMethod]
    public void OversizedIngressInvalidatesPartialRecordAndQuarantinesUntilBoundary()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Encoding.ASCII.GetBytes("FA0000"));
        var oversized = new byte[KenwoodAsciiFrameParser.MaximumIngressBytes + 1];
        Array.Fill(oversized, (byte)'Z');
        parser.Feed(oversized);

        Assert.IsTrue(parser.HasPartialRecord);
        Assert.IsEmpty(GetRecords(events));
        var activity = events.OfType<KenwoodAsciiReceiveActivity>().Last();
        Assert.AreEqual(oversized.Length, activity.ByteCount);
        var anomaly = GetAnomalies(events, KenwoodAsciiParserAnomalyKind.IngressOverflow).Single();
        Assert.AreEqual(oversized.Length, anomaly.ObservedCount);
        Assert.HasCount(KenwoodAsciiFrameParser.MaximumDiagnosticSampleLength, anomaly.Sample);
        Assert.HasCount(KenwoodAsciiFrameParser.MaximumDiagnosticSampleLength, anomaly.RawBytes);

        clock.NowMs = KenwoodAsciiFrameParser.PartialRecordIdleTimeoutMs;
        parser.Tick();
        Assert.IsTrue(parser.HasPartialRecord);
        Assert.IsEmpty(GetAnomalies(events, KenwoodAsciiParserAnomalyKind.PartialRecordExpiredIdle));

        parser.Feed(Encoding.ASCII.GetBytes(";"));
        Assert.IsEmpty(GetRecords(events));
        parser.Feed(AnswerFrequency);
        CollectionAssert.AreEqual(AnswerFrequency, GetRecords(events).Single().Record.ToArray());
    }

    [TestMethod]
    public void ControlCrLfAndNonAsciiBytesAreReportedAndDoNotRepairTheirRecords()
    {
        byte[] invalidValues = [0x00, 0x1F, 0x7F, 0x80, (byte)'\r', (byte)'\n'];
        foreach (var invalid in invalidValues)
        {
            var events = new List<KenwoodAsciiParserEvent>();
            var parser = CreateParser(events, new TestClock());
            byte[] input = [(byte)'F', (byte)'A', invalid, (byte)'B', (byte)';', .. Encoding.ASCII.GetBytes("IF;")];
            parser.Feed(input);

            var anomaly = GetAnomalies(events, KenwoodAsciiParserAnomalyKind.InvalidByte).Single();
            Assert.AreEqual(1, anomaly.ObservedCount);
            CollectionAssert.AreEqual(new[] { invalid }, anomaly.Sample.ToArray());
            CollectionAssert.AreEqual(Encoding.ASCII.GetBytes("IF;"), GetRecords(events).Single().Record.ToArray());
        }
    }

    [TestMethod]
    public void EveryNonEmptyFeedReportsReceiveActivity()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Encoding.ASCII.GetBytes("FA"));
        clock.NowMs = 1;
        parser.Feed(Encoding.ASCII.GetBytes(";"));
        parser.Feed(ReadOnlySpan<byte>.Empty);

        var activities = events.OfType<KenwoodAsciiReceiveActivity>().ToArray();
        Assert.HasCount(2, activities);
        Assert.AreEqual(2, activities[0].ByteCount);
        Assert.AreEqual(1, activities[1].ByteCount);
        Assert.AreEqual(0, activities[0].TimestampMs);
        Assert.AreEqual(1, activities[1].TimestampMs);
    }

    [TestMethod]
    public void IdleExpiryOccursAtExactDeadlineAndNotBefore()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Encoding.ASCII.GetBytes("FA"));

        clock.NowMs = KenwoodAsciiFrameParser.PartialRecordIdleTimeoutMs - 1;
        parser.Tick();
        Assert.IsEmpty(GetAnomalies(events, KenwoodAsciiParserAnomalyKind.PartialRecordExpiredIdle));
        Assert.IsTrue(parser.HasPartialRecord);

        clock.NowMs = KenwoodAsciiFrameParser.PartialRecordIdleTimeoutMs;
        parser.Tick();
        var anomaly = GetAnomalies(events, KenwoodAsciiParserAnomalyKind.PartialRecordExpiredIdle).Single();
        Assert.AreEqual(2, anomaly.ObservedCount);
        Assert.IsFalse(parser.HasPartialRecord);
    }

    [TestMethod]
    public void TotalExpiryOccursAtExactDeadlineDespiteRecentInput()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Encoding.ASCII.GetBytes("F"));

        for (var now = 400; now <= 1600; now += 400)
        {
            clock.NowMs = now;
            parser.Feed(Encoding.ASCII.GetBytes("A"));
        }

        clock.NowMs = KenwoodAsciiFrameParser.PartialRecordTotalTimeoutMs - 1;
        parser.Tick();
        Assert.IsEmpty(GetAnomalies(events, KenwoodAsciiParserAnomalyKind.PartialRecordExpiredTotal));
        Assert.IsTrue(parser.HasPartialRecord);

        clock.NowMs = KenwoodAsciiFrameParser.PartialRecordTotalTimeoutMs;
        parser.Tick();
        Assert.HasCount(1, GetAnomalies(events, KenwoodAsciiParserAnomalyKind.PartialRecordExpiredTotal));
        Assert.IsFalse(parser.HasPartialRecord);
    }

    [TestMethod]
    public void FeedExpiresStaleRecordBeforeParsingNewInput()
    {
        var events = new List<KenwoodAsciiParserEvent>();
        var clock = new TestClock();
        var parser = CreateParser(events, clock);
        parser.Feed(Encoding.ASCII.GetBytes("FA"));
        clock.NowMs = KenwoodAsciiFrameParser.PartialRecordIdleTimeoutMs;
        parser.Feed(ReadFrequency);

        Assert.HasCount(1, GetAnomalies(events, KenwoodAsciiParserAnomalyKind.PartialRecordExpiredIdle));
        Assert.HasCount(1, GetRecords(events));
        CollectionAssert.AreEqual(ReadFrequency, GetRecords(events).Single().Record.ToArray());
    }

    [TestMethod]
    public void OverflowAnomalyDeliveryFailureFaultsParser()
    {
        var parser = new KenwoodAsciiFrameParser(new TestClock(), parserEvent =>
            parserEvent is not KenwoodAsciiParserAnomaly { Kind: KenwoodAsciiParserAnomalyKind.IngressOverflow });
        var oversized = new byte[KenwoodAsciiFrameParser.MaximumIngressBytes + 1];

        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Feed(oversized));
        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Feed(ReadFrequency));
    }

    [TestMethod]
    public void AnomalySinkExceptionFaultsParserAndPropagates()
    {
        var parser = new KenwoodAsciiFrameParser(new TestClock(), parserEvent =>
        {
            if (parserEvent is KenwoodAsciiParserAnomaly)
            {
                throw new InvalidOperationException("sink failed");
            }

            return true;
        });

        var invalidInput = new byte[] { 0x80 };
        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Feed(invalidInput));
        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Tick());
    }

    [TestMethod]
    public void EventSinkCannotReenterParser()
    {
        KenwoodAsciiFrameParser? parser = null;
        parser = new KenwoodAsciiFrameParser(new TestClock(), _ =>
        {
            parser!.Tick();
            return true;
        });

        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Feed(ReadFrequency));
        Assert.ThrowsExactly<InvalidOperationException>(() => parser.Tick());
    }

    private static KenwoodAsciiFrameParser CreateParser(
        List<KenwoodAsciiParserEvent> events,
        TestClock clock) =>
        new(clock, parserEvent =>
        {
            events.Add(parserEvent);
            return true;
        });

    private static List<KenwoodAsciiRecordReceived> GetRecords(IEnumerable<KenwoodAsciiParserEvent> events) =>
        events.OfType<KenwoodAsciiRecordReceived>().ToList();

    private static List<KenwoodAsciiParserAnomaly> GetAnomalies(
        IEnumerable<KenwoodAsciiParserEvent> events,
        KenwoodAsciiParserAnomalyKind kind) =>
        events.OfType<KenwoodAsciiParserAnomaly>().Where(anomaly => anomaly.Kind == kind).ToList();

    private sealed class TestClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
