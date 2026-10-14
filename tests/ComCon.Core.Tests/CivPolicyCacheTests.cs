using ComCon.Core.Protocol;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class CivPolicyCacheTests
{
    private static readonly CivObservationContext Context = new(1, 2, "IC-7600", null);

    [TestMethod]
    public void OnlyExactVerifiedReadsAreAdmitted()
    {
        var ptt = Contract();
        Assert.AreEqual(CivReadKind.Ptt, ptt.Kind);
        Assert.AreEqual(CivReadKind.RfSetting, Contract(true).Kind);
        Assert.ThrowsExactly<ArgumentException>(() => Ic7600ReadPolicy.ValidateRequest(
            [0xFE, 0xFE, 0x7A, 0xE1, 0x1C, 0, 0, 0xFD], 0x7A, 0xE1));
        Assert.ThrowsExactly<ArgumentException>(() => Ic7600ReadPolicy.ValidateRequest(
            [0xFE, 0xFE, 0x7A, 0xE1, 3, 0, 0xFD], 0x7A, 0xE1));
        Assert.ThrowsExactly<ArgumentException>(() => Ic7600ReadPolicy.ValidateRequest(
            ptt.CopyRequest(), 0x7A, 0x7A));
        Assert.ThrowsExactly<ArgumentException>(() => Ic7600ReadPolicy.ValidateRequest(
            ptt.CopyRequest(), 0, 0xE1));
        Assert.AreEqual(CivReplyKind.Echo, ptt.Classify(ptt.CopyRequest()).Kind);
        var copy = ptt.CopyRequest();
        copy[2] = 0;
        Assert.AreEqual((byte)0x7A, ptt.ToPayload().CopyBytes()[2]);
    }

    [TestMethod]
    [DataRow(0, CivReplyKind.Value)]
    [DataRow(1, CivReplyKind.Value)]
    [DataRow(2, CivReplyKind.Anomaly)]
    [DataRow(255, CivReplyKind.Anomaly)]
    public void PttRequiresExactBooleanValue(int value, CivReplyKind expected)
    {
        var reply = PttReply((byte)value);
        Assert.AreEqual(expected, Contract().Classify(reply).Kind);
        Assert.AreEqual(CivReplyKind.Anomaly, Contract().Classify(reply[..^1]).Kind);
    }

    [TestMethod]
    [DataRow(0x00, 0x00, 0)]
    [DataRow(0x00, 0x01, 1)]
    [DataRow(0x02, 0x55, 255)]
    public void RfSettingDecodesBigEndianBcd(int high, int low, int expected)
    {
        var result = Contract(true).Classify(
            [0xFE, 0xFE, 0xE1, 0x7A, 0x14, 0x0A, (byte)high, (byte)low, 0xFD]);
        Assert.AreEqual(CivReplyKind.Value, result.Kind);
        Assert.AreEqual(expected, result.Value);
    }

    [TestMethod]
    [DataRow(0x02, 0x56)]
    [DataRow(0x00, 0xFA)]
    [DataRow(0x0A, 0x01)]
    public void RfSettingRejectsOutOfRangeAndInvalidBcd(int high, int low)
    {
        Assert.AreEqual(CivReplyKind.Anomaly, Contract(true).Classify(
            [0xFE, 0xFE, 0xE1, 0x7A, 0x14, 0x0A, (byte)high, (byte)low, 0xFD]).Kind);
    }

    [TestMethod]
    public void EchoForeignSelectorsBroadcastAndAckNeverProduceValues()
    {
        var contract = Contract();
        Assert.AreEqual(CivReplyKind.Unrelated, contract.Classify(
            [0xFE, 0xFE, 0xE0, 0x7A, 0x1C, 0, 1, 0xFD]).Kind);
        Assert.AreEqual(CivReplyKind.Unrelated, contract.Classify(
            [0xFE, 0xFE, 0, 0x7A, 0x1C, 0, 1, 0xFD]).Kind);
        Assert.AreEqual(CivReplyKind.Unrelated, contract.Classify(
            [0xFE, 0xFE, 0xE1, 0x7A, 0x1C, 1, 1, 0xFD]).Kind);
        Assert.AreEqual(CivReplyKind.Anomaly, contract.Classify(
            [0xFE, 0xFE, 0xE1, 0x7A, 0xFB, 0xFD]).Kind);
        Assert.AreEqual(CivReplyKind.RadioRejected, contract.Classify(
            [0xFE, 0xFE, 0xE1, 0x7A, 0xFA, 0xFD]).Kind);
    }

    [TestMethod]
    public void EarlyReplyNeedsSuccessfulWriteAndObservationIsCopiedAndConsumedOnce()
    {
        var clock = new Clock();
        var exchange = Exchange(clock);
        var reply = PttReply(1);
        Assert.AreEqual(CivReplyKind.Value, exchange.Receive(reply).Kind);
        Assert.IsNull(exchange.TakeObservation());
        reply[6] = 0;
        exchange.CompleteWrite(AdapterWriteCompletion.Succeeded);
        var observation = exchange.TakeObservation();
        Assert.IsNotNull(observation);
        Assert.AreEqual(1, observation.Value);
        var copy = observation.CopyResponse();
        copy[6] = 0;
        Assert.AreEqual((byte)1, observation.CopyResponse()[6]);
        Assert.IsNull(exchange.TakeObservation());
    }

    [TestMethod]
    public void MalformedThenValidAndDuplicateRepliesRemainTainted()
    {
        var clock = new Clock();
        var exchange = Exchange(clock);
        exchange.Receive(PttReply(2));
        exchange.Receive(PttReply(1));
        exchange.CompleteWrite(AdapterWriteCompletion.Succeeded);
        Assert.IsTrue(exchange.IsTainted);
        Assert.IsNull(exchange.TakeObservation());
        var duplicate = Exchange(clock);
        duplicate.Receive(PttReply(1));
        duplicate.Receive(PttReply(1));
        duplicate.CompleteWrite(AdapterWriteCompletion.Succeeded);
        Assert.IsTrue(duplicate.IsTainted);
        Assert.IsNull(duplicate.TakeObservation());
    }

    [TestMethod]
    [DataRow(AdapterWriteCompletion.Unknown)]
    [DataRow(AdapterWriteCompletion.ProvenZeroBytes)]
    public void FailedWriteCannotPopulateCache(AdapterWriteCompletion completion)
    {
        var clock = new Clock();
        var exchange = Exchange(clock);
        exchange.Receive(PttReply(1));
        exchange.CompleteWrite(completion);
        Assert.IsNull(exchange.TakeObservation());
        Assert.IsTrue(exchange.IsTainted);
    }

    [TestMethod]
    public void DeadlinesExpireAtEqualityAndSilenceDoesNotRecover()
    {
        var clock = new Clock();
        var write = Exchange(clock);
        clock.NowMs = 249;
        write.Tick();
        Assert.IsFalse(write.IsTainted);
        clock.NowMs = 250;
        write.CompleteWrite(AdapterWriteCompletion.Succeeded);
        Assert.IsTrue(write.IsTainted);
        clock.NowMs = 5000;
        write.Receive(PttReply(1));
        Assert.IsNull(write.TakeObservation());

        clock.NowMs = 0;
        var reply = Exchange(clock);
        reply.CompleteWrite(AdapterWriteCompletion.Succeeded);
        clock.NowMs = 249;
        reply.Tick();
        Assert.IsFalse(reply.IsTainted);
        clock.NowMs = 250;
        Assert.AreEqual(CivReplyKind.Anomaly, reply.Receive(PttReply(1)).Kind);
        Assert.IsNull(reply.TakeObservation());
    }

    [TestMethod]
    public void CacheIsDisplayOnlyFreshnessNeverExtendedAndControllerRewriteIsExplicit()
    {
        var clock = new Clock();
        var cache = new PassiveReadCache(clock, Context);
        Assert.AreEqual(CacheReadStatus.Missing, cache.Read(Contract()).Status);
        Assert.AreEqual(CacheReadStatus.Hit, cache.Observe(Observe(clock)));
        clock.NowMs = 749;
        var request = Ic7600ReadPolicy.ValidateRequest(
            [0xFE, 0xFE, 0x7A, 0xE0, 0x1C, 0, 0xFD], 0x7A, 0xE0);
        var hit = cache.Read(request);
        Assert.AreEqual(CacheReadStatus.Hit, hit.Status);
        Assert.AreEqual(749L, hit.AgeMs);
        Assert.IsNotNull(hit.Response);
        Assert.AreEqual((byte)0xE0, hit.Response[2]);
        hit.Response[6] = 0;
        Assert.AreEqual((byte)1, cache.Read(request).Response![6]);
        clock.NowMs = 750;
        var stale = cache.Read(request);
        Assert.AreEqual(CacheReadStatus.Stale, stale.Status);
        Assert.IsNull(stale.Response);
        Assert.AreEqual(CacheReadStatus.Unsupported, cache.Read(Contract(true)).Status);
    }

    [TestMethod]
    public void GenerationContextFirmwareAndModelChangesInvalidateCache()
    {
        var clock = new Clock();
        var cache = new PassiveReadCache(clock, Context);
        var observation = Observe(clock);
        foreach (var changed in new[]
        {
            Context with { ProfileGeneration = 2 }, Context with { ContextEpoch = 3 },
            Context with { Firmware = "known" }, Context with { Model = "different" }
        })
        {
            cache.Invalidate(changed, "Profile/context changed.");
            Assert.AreEqual(CacheReadStatus.ContextMismatch, cache.Observe(observation));
            Assert.AreEqual(CacheReadStatus.Missing, cache.Read(Contract()).Status);
        }
    }

    [TestMethod]
    public void StaleObservationInvalidationAndClockRegressionCannotGiveSuccess()
    {
        var clock = new Clock();
        var cache = new PassiveReadCache(clock, Context);
        var observation = Observe(clock);
        clock.NowMs = 750;
        Assert.AreEqual(CacheReadStatus.Stale, cache.Observe(observation));
        cache.Invalidate(Context, "Before affecting write.");
        Assert.AreEqual(CacheReadStatus.Missing, cache.Read(Contract()).Status);
        clock.NowMs = 751;
        cache.Observe(Observe(clock));
        clock.NowMs = 750;
        Assert.ThrowsExactly<InvalidOperationException>(() => cache.Read(Contract()));
        Assert.AreEqual("Monotonic clock moved backwards.", cache.LastInvalidationReason);
    }

    [TestMethod]
    public void InvalidationCannotBeUndoneByReplayingAnOldObservation()
    {
        var clock = new Clock();
        var cache = new PassiveReadCache(clock, Context);
        var old = Observe(clock);
        cache.Observe(old);
        clock.NowMs = 1;
        cache.Invalidate(Context, "Before affecting write.");
        Assert.AreEqual(CacheReadStatus.ContextMismatch, cache.Observe(old));
        Assert.AreEqual(CacheReadStatus.ContextMismatch, cache.Observe(Observe(clock)));
        clock.NowMs = 2;
        Assert.AreEqual(CacheReadStatus.Hit, cache.Observe(Observe(clock)));
        Assert.AreEqual(CacheReadStatus.Hit, cache.Read(Contract()).Status);
    }

    [TestMethod]
    public void ProfilesAndRadioIdentitiesRemainIsolated()
    {
        var clock = new Clock();
        var first = new PassiveReadCache(clock, Context);
        var second = new PassiveReadCache(clock, Context);
        first.Observe(Observe(clock));
        Assert.AreEqual(CacheReadStatus.Missing, second.Read(Contract()).Status);
        var otherRadio = Ic7600ReadPolicy.ValidateRequest(
            [0xFE, 0xFE, 0x7B, 0xE1, 0x1C, 0, 0xFD], 0x7B, 0xE1);
        Assert.AreEqual(CacheReadStatus.ContextMismatch, first.Read(otherRadio).Status);
    }

    private static Ic7600ReadContract Contract(bool rf = false) =>
        Ic7600ReadPolicy.ValidateRequest(
            [0xFE, 0xFE, 0x7A, 0xE1, rf ? (byte)0x14 : (byte)0x1C,
                rf ? (byte)0x0A : (byte)0, 0xFD], 0x7A, 0xE1);

    private static byte[] PttReply(byte value) => [0xFE, 0xFE, 0xE1, 0x7A, 0x1C, 0, value, 0xFD];

    private static CivReadExchange Exchange(Clock clock, bool rf = false)
    {
        var contract = Contract(rf);
        var action = new DispatchAction(Guid.NewGuid(),
            new(new(RequestOrigin.DXLab, Guid.Empty, 1), contract.ToPayload(),
                ClientCompletionMode.ReplyExpected), 1,
            clock.NowMs, clock.NowMs + 500, clock.NowMs + 250);
        return new(action, contract, Context, clock);
    }

    private static HardwareReadObservation Observe(Clock clock)
    {
        var exchange = Exchange(clock);
        exchange.CompleteWrite(AdapterWriteCompletion.Succeeded);
        exchange.Receive(PttReply(1));
        return exchange.TakeObservation() ?? throw new AssertFailedException("Expected hardware observation.");
    }

    private sealed class Clock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }
}
