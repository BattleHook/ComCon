using System.Text;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class SimulatedReceiveJournalTests
{
    [TestMethod]
    public void BytesRemainOpaqueCopiedAndAgeDoesNotRefreshOnRead()
    {
        var clock = new TestClock { NowMs = 100 };
        var journal = new SimulatedReceiveJournal(clock);
        byte[] bytes = [0xFE, 0xFE, 0xE0, 0x7A, 0x1C, 0, 0, 0xFD];
        journal.RecordFrame(bytes);
        bytes[0] = 0;
        clock.NowMs = 300;
        var first = journal.ReadAfter(0);
        Assert.AreEqual("FEFEE07A1C0000FD", first.Observations.Single().Hex);
        Assert.AreEqual(200L, first.Observations.Single().AgeMs);
        clock.NowMs = 500;
        Assert.AreEqual(400L, journal.ReadAfter(0).Observations.Single().AgeMs);
        Assert.AreEqual("simulatedPassiveRx", first.Observations.Single().Source);
        Assert.IsFalse(first.Gap);
    }

    [TestMethod]
    public void CapacityEvictionReportsGapAndEightItemPagination()
    {
        var journal = new SimulatedReceiveJournal(new TestClock());
        for (var i = 0; i < 129; i++) journal.RecordFrame([0x00]);
        var batch = journal.ReadAfter(0);
        Assert.HasCount(8, batch.Observations);
        Assert.AreEqual("2", batch.Observations[0].ObservationId);
        Assert.AreEqual("9", batch.NextObservationId);
        Assert.AreEqual("129", batch.LatestObservationId);
        Assert.IsTrue(batch.HasMore);
        Assert.IsTrue(batch.Gap);
        var next = journal.ReadAfter(9);
        Assert.AreEqual("10", next.Observations[0].ObservationId);
        Assert.IsFalse(next.Gap);
        var final = journal.ReadAfter(128);
        Assert.AreEqual("129", final.NextObservationId);
        Assert.IsFalse(final.HasMore);
    }

    [TestMethod]
    public void RetentionExpiresAtExactBoundaryAndCursorNeverRewinds()
    {
        var clock = new TestClock();
        var journal = new SimulatedReceiveJournal(clock);
        journal.RecordFrame([0xFD]);
        clock.NowMs = 59_999;
        Assert.HasCount(1, journal.ReadAfter(0).Observations);
        clock.NowMs = 60_000;
        var batch = journal.ReadAfter(0);
        Assert.IsEmpty(batch.Observations);
        Assert.IsTrue(batch.Gap);
        Assert.AreEqual("1", batch.NextObservationId);
        Assert.IsFalse(journal.ReadAfter(1).Gap);
        journal.RecordFrame([0xFE]);
        Assert.AreEqual("2", journal.ReadAfter(1).Observations.Single().ObservationId);
    }

    [TestMethod]
    public void EmptyJournalReturnsExplicitEmptyBatch()
    {
        var journal = new SimulatedReceiveJournal(new TestClock());
        var batch = journal.ReadAfter(0);
        Assert.IsEmpty(batch.Observations);
        Assert.AreEqual("0", batch.NextObservationId);
        Assert.IsFalse(batch.Gap);
        Assert.IsFalse(batch.HasMore);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.ReadAfter(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.ReadAfter(-1));
    }

    [TestMethod]
    public void PayloadBoundIsOneThroughTwoHundredFiftySixBytes()
    {
        var journal = new SimulatedReceiveJournal(new TestClock());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.RecordFrame([]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => journal.RecordFrame(new byte[257]));
        journal.RecordFrame(new byte[256]);
        Assert.AreEqual(512, journal.ReadAfter(0).Observations.Single().Hex.Length);
    }

    [TestMethod]
    public async Task MaximumBatchFitsWireLimit()
    {
        var clock = new TestClock();
        var journal = new SimulatedReceiveJournal(clock);
        for (var i = 0; i < 128; i++) journal.RecordFrame(new byte[256]);
        var session = new ComConReadOnlySession(1, "test-instance", clock, journal);
        var hello = session.Process(Parse(
            """{"v":1,"kind":"request","id":"1","op":"HELLO","args":{"clientName":"Test","supportedVersions":[1]}}"""));
        var sessionId = hello.Message.GetProperty("result").GetProperty("sessionId").GetString();
        var request = ObservationRequest(sessionId, """{"afterObservationId":"0"}""");
        var reply = session.Process(request);
        using var wire = new MemoryStream();
        using var writer = new SemaphoreSlim(1, 1);
        await ComConIpcProtocol.WriteJsonLineAsync(wire, reply.Message, writer);
        Assert.IsLessThan((long)ComConIpcProtocol.MaximumFrameBytes, wire.Length);
        Assert.AreEqual(8, reply.Message.GetProperty("result").GetProperty("observations").GetArrayLength());
    }

    [TestMethod]
    public void ReplayedObservationResponseKeepsDataButRecalculatesAge()
    {
        var clock = new TestClock();
        var journal = new SimulatedReceiveJournal(clock);
        journal.RecordFrame([0x00]);
        var session = new ComConReadOnlySession(1, "test-instance", clock, journal);
        var hello = session.Process(Parse(
            """{"v":1,"kind":"request","id":"1","op":"HELLO","args":{"clientName":"Test","supportedVersions":[1]}}"""));
        var sessionId = hello.Message.GetProperty("result").GetProperty("sessionId").GetString();
        var request = ObservationRequest(sessionId, """{"afterObservationId":"0"}""");
        var original = session.Process(request);
        clock.NowMs = 1000;
        var replay = session.Process(request);
        var observation = replay.Message.GetProperty("result").GetProperty("observations")[0];
        Assert.AreEqual(1000L, observation.GetProperty("ageMs").GetInt64());
        Assert.AreEqual("1", observation.GetProperty("observationId").GetString());
        Assert.AreEqual("00", observation.GetProperty("hex").GetString());
        Assert.AreEqual(0L, original.Message.GetProperty("result").GetProperty("observations")[0].GetProperty("ageMs").GetInt64());
        clock.NowMs = 2000;
        Assert.AreEqual(2000L, session.Process(request).Message.GetProperty("result").GetProperty("observations")[0].GetProperty("ageMs").GetInt64());
    }

    [TestMethod]
    [DataRow("""{"afterObservationId":"01"}""", "INVALID_ARGUMENTS")]
    [DataRow("""{"afterObservationId":0}""", "INVALID_ARGUMENTS")]
    [DataRow("""{"afterObservationId":"-1"}""", "INVALID_ARGUMENTS")]
    [DataRow("""{"afterObservationId":"9223372036854775808"}""", "INVALID_ARGUMENTS")]
    [DataRow("""{"afterObservationId":"0","extra":0}""", "INVALID_ARGUMENTS")]
    [DataRow("{}", "INVALID_ARGUMENTS")]
    [DataRow("""{"afterObservationId":"1"}""", "CURSOR_INVALID")]
    public void ObservationAdmissionValidatesCanonicalCursorAndUnknownFields(string args, string code)
    {
        var clock = new TestClock();
        var session = new ComConReadOnlySession(1, "test-instance", clock, new SimulatedReceiveJournal(clock));
        var hello = session.Process(Parse(
            """{"v":1,"kind":"request","id":"1","op":"HELLO","args":{"clientName":"Test","supportedVersions":[1]}}"""));
        var sessionId = hello.Message.GetProperty("result").GetProperty("sessionId").GetString();
        var request = ObservationRequest(sessionId, args);
        Assert.AreEqual(code, session.Process(request).Message.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public void SimulatedSessionStillRejectsLiveAndCacheCatRequests()
    {
        var clock = new TestClock();
        var session = new ComConReadOnlySession(1, "test-instance", clock, new SimulatedReceiveJournal(clock));
        var hello = session.Process(Parse(
            """{"v":1,"kind":"request","id":"1","op":"HELLO","args":{"clientName":"Test","supportedVersions":[1]}}"""));
        var sessionId = hello.Message.GetProperty("result").GetProperty("sessionId").GetString();
        foreach (var mode in new[] { "live", "cacheOnly" })
        {
            var request = ComConIpcProtocol.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = 1, kind = "request", id = mode == "live" ? "2" : "3",
                op = "CAT_REQUEST", sessionId, args = new { mode, hex = "FEFE7AE01C0001FD" }
            }));
            var error = session.Process(request).Message.GetProperty("error");
            Assert.AreEqual("OPERATION_DISABLED", error.GetProperty("code").GetString());
            Assert.AreEqual("notDispatched", error.GetProperty("dispatchState").GetString());
        }
    }

    private static ComConIpcRequest Parse(string json) => ComConIpcProtocol.ParseRequest(Encoding.UTF8.GetBytes(json));
    private static ComConIpcRequest ObservationRequest(string? sessionId, string args)
    {
        using var document = JsonDocument.Parse(args);
        return ComConIpcProtocol.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(new
        {
            v = 1, kind = "request", id = "2", op = "GET_OBSERVATIONS", sessionId, args = document.RootElement
        }));
    }
    internal sealed class TestClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
