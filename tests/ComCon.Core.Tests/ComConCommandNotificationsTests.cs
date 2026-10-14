using System.Text;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConCommandNotificationsTests
{
    [TestMethod]
    public void NotificationsSendInOrderWithoutWaitingForEarlierAcks()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Tracker.Enqueue(Framing());
        var first = context.Send();
        var second = context.Send();
        Assert.AreEqual(1L, first.Sequence);
        Assert.AreEqual(2L, second.Sequence);
        Assert.IsFalse(first.IsRetry);
        Assert.AreEqual("1", first.Envelope.GetProperty("notificationSequence").GetString());
        Assert.IsNull(context.Tracker.BeginTransmission());
        context.Tracker.Acknowledge(context.Ack(2));
        Assert.AreEqual(1, context.Tracker.PendingCount);
        context.Tracker.Acknowledge(context.Ack(1));
        Assert.AreEqual(0, context.Tracker.PendingCount);
        Assert.AreEqual(0, context.Tracker.RetainedPayloadBytes);
        context.Tracker.Acknowledge(context.Ack(2));
        context.Tracker.Acknowledge(context.Ack(1));
        Assert.IsTrue(context.Sessions.CanRelayCat(context.Session));
    }

    [TestMethod]
    public void RetriesPreserveExactEnvelopeAndNeverResetDeadline()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        var original = context.Send();
        context.Clock.NowMs = 19;
        Assert.IsNull(context.Tracker.BeginTransmission());
        context.Clock.NowMs = 20;
        var retry = context.Send();
        Assert.IsTrue(retry.IsRetry);
        Assert.AreEqual(original.Sequence, retry.Sequence);
        Assert.AreEqual(original.Envelope.GetRawText(), retry.Envelope.GetRawText());
        context.Clock.NowMs = 99;
        context.Send();
        context.Clock.NowMs = 100;
        AssertTerminates(context, "NOTIFICATION_ACK_TIMEOUT", () => context.Tracker.CheckTimeouts());
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public void AckForUnassignedOrUnsentSequenceTerminatesOnlyItsSession(int scenario)
    {
        var context = new Context();
        if (scenario != 0) context.Tracker.Enqueue(Framing());
        if (scenario == 2) context.Send();
        AssertTerminates(context, "INVALID_NOTIFICATION_ACK",
            () => context.Tracker.Acknowledge(context.Ack(scenario == 2 ? 2 : 1)));
    }

    [TestMethod]
    [DataRow("99999999999999999999999999999")]
    [DataRow("01")]
    [DataRow("0")]
    public void InvalidAckSequenceIsNotCoerced(string sequence)
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Send();
        var code = sequence.Length > 20 ? "INVALID_NOTIFICATION_ACK" : "INVALID_FIELD";
        AssertTerminates(context, code, () => context.Tracker.Acknowledge(context.AckText(sequence)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WrongAckIdentityTerminatesSession(bool wrongProfile)
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Send();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = wrongProfile ? 2 : 1,
            sessionId = wrongProfile ? context.Session.SessionId : Guid.NewGuid().ToString("D"),
            notificationSequence = "1"
        });
        AssertTerminates(context, "PROFILE_SESSION_MISMATCH", () => context.Tracker.Acknowledge(payload));
    }

    [TestMethod]
    public void SchemaInvalidAckTerminatesWithExplicitCode()
    {
        var context = new Context();
        AssertTerminates(context, "INVALID_JSON", () => context.Tracker.Acknowledge("{"u8));
    }

    [TestMethod]
    public void NonAckControlMessageTerminates()
    {
        var context = new Context();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = context.Session.SessionId
        });
        AssertTerminates(context, "INVALID_MESSAGE_TYPE", () => context.Tracker.Acknowledge(payload));
    }

    [TestMethod]
    public void CountBoundIsExactAndCapacityIsReclaimedAfterAck()
    {
        var context = new Context(maximumCount: 2);
        context.Tracker.Enqueue(Framing());
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Tracker.Acknowledge(context.Ack(1));
        Assert.AreEqual(3L, context.Tracker.Enqueue(Framing()));
        AssertTerminates(context, "NOTIFICATION_OVERFLOW", () => context.Tracker.Enqueue(Framing()));
    }

    [TestMethod]
    public void SelectedCountBoundAcceptsNMinusOneAndNButRejectsNPlusOne()
    {
        var limit = ComConCommandBounds.MaximumPendingNotifications;
        var context = new Context(maximumCount: limit);
        for (var i = 0; i < limit - 1; i++)
            context.Tracker.Enqueue(Framing());
        Assert.AreEqual(limit - 1, context.Tracker.PendingCount);
        context.Tracker.Enqueue(Framing());
        Assert.AreEqual(limit, context.Tracker.PendingCount);
        AssertTerminates(context, "NOTIFICATION_OVERFLOW", () => context.Tracker.Enqueue(Framing()));
    }

    [TestMethod]
    public void RetainedByteBoundAcceptsExactCapacityAndRejectsOneByteLess()
    {
        var reference = new Context();
        reference.Tracker.Enqueue(Framing());
        var bytes = reference.Tracker.RetainedPayloadBytes;
        var exact = new Context(maximumBytes: bytes);
        exact.Tracker.Enqueue(Framing());
        Assert.AreEqual(bytes, exact.Tracker.RetainedPayloadBytes);
        var shortLimit = new Context(maximumBytes: bytes - 1);
        AssertTerminates(shortLimit, "NOTIFICATION_OVERFLOW", () => shortLimit.Tracker.Enqueue(Framing()));
    }

    [TestMethod]
    public void SelectedRetainedByteBoundAcceptsNMinusOneAndNButRejectsNPlusOne()
    {
        var limit = ComConCommandBounds.MaximumPendingNotificationBytes;
        var exact = new Context(maximumBytes: limit);
        foreach (var payloadBytes in SplitPayloadSize(limit))
            exact.Tracker.Enqueue(FramingWithReasonLength(ReasonLengthForPayloadBytes(payloadBytes)));
        Assert.AreEqual(limit, exact.Tracker.RetainedPayloadBytes);

        var over = new Context(maximumBytes: limit);
        var overSizes = SplitPayloadSize(limit);
        for (var i = 0; i < overSizes.Length - 1; i++)
            over.Tracker.Enqueue(FramingWithReasonLength(ReasonLengthForPayloadBytes(overSizes[i])));
        AssertTerminates(over, "NOTIFICATION_OVERFLOW", () => over.Tracker.Enqueue(
            FramingWithReasonLength(ReasonLengthForPayloadBytes(overSizes[^1] + 1))));

        var under = new Context(maximumBytes: limit);
        var underSizes = SplitPayloadSize(limit - 1);
        foreach (var payloadBytes in underSizes)
            under.Tracker.Enqueue(FramingWithReasonLength(ReasonLengthForPayloadBytes(payloadBytes)));
        Assert.AreEqual(limit - 1, under.Tracker.RetainedPayloadBytes);
    }

    [TestMethod]
    [DataRow(499, false)]
    [DataRow(500, true)]
    [DataRow(501, true)]
    public void SelectedRetryIntervalUsesExactNMinusOneNAndNPlusOneBoundary(int elapsedMs, bool retryExpected)
    {
        var context = new Context(
            acknowledgmentTimeoutMs: ComConCommandBounds.AcknowledgmentTimeoutMs,
            retryIntervalMs: ComConCommandBounds.AcknowledgmentRetryIntervalMs);
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Clock.NowMs = elapsedMs;
        var transmission = context.Tracker.BeginTransmission();
        Assert.AreEqual(retryExpected, transmission is not null);
        if (transmission is not null) Assert.IsTrue(transmission.IsRetry);
    }

    [TestMethod]
    [DataRow(1_999, false)]
    [DataRow(2_000, true)]
    [DataRow(2_001, true)]
    public void SelectedAcknowledgmentTimeoutUsesExactNMinusOneNAndNPlusOneBoundary(
        int elapsedMs, bool expired)
    {
        var context = new Context(
            acknowledgmentTimeoutMs: ComConCommandBounds.AcknowledgmentTimeoutMs,
            retryIntervalMs: ComConCommandBounds.AcknowledgmentRetryIntervalMs);
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Clock.NowMs = elapsedMs;
        if (expired)
        {
            AssertTerminates(context, "NOTIFICATION_ACK_TIMEOUT",
                () => context.Tracker.Acknowledge(context.Ack(1)));
            return;
        }

        context.Tracker.Acknowledge(context.Ack(1));
        Assert.AreEqual(0, context.Tracker.PendingCount);
        Assert.IsTrue(context.Sessions.CanRelayCat(context.Session));
    }

    [TestMethod]
    public void SequenceExhaustionNeverWrapsOrReusesAcknowledgedIds()
    {
        var context = new Context(maximumSequence: 2);
        for (var sequence = 1; sequence <= 2; sequence++)
        {
            Assert.AreEqual((long)sequence, context.Tracker.Enqueue(Framing()));
            context.Send();
            context.Tracker.Acknowledge(context.Ack(sequence));
        }
        AssertTerminates(context, "SEQUENCE_EXHAUSTED", () => context.Tracker.Enqueue(Framing()));
    }

    [TestMethod]
    public void UnsentNotificationsAlsoHaveBoundedDeadline()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Clock.NowMs = 100;
        AssertTerminates(context, "NOTIFICATION_ACK_TIMEOUT", () => context.Tracker.BeginTransmission());
    }

    [TestMethod]
    public void AckAtDeadlineCannotReviveSession()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Clock.NowMs = 100;
        AssertTerminates(context, "NOTIFICATION_ACK_TIMEOUT", () => context.Tracker.Acknowledge(context.Ack(1)));
    }

    [TestMethod]
    public void AckedNotificationsDoNotExpireAndNoHistoryIsRetained()
    {
        var context = new Context(maximumCount: 1);
        for (var sequence = 1; sequence <= 100; sequence++)
        {
            context.Tracker.Enqueue(Framing());
            context.Send();
            context.Tracker.Acknowledge(context.Ack(sequence));
        }
        context.Clock.NowMs = 1000;
        context.Tracker.CheckTimeouts();
        context.Tracker.Acknowledge(context.Ack(1));
        Assert.AreEqual(0, context.Tracker.PendingCount);
        Assert.AreEqual(0, context.Tracker.RetainedPayloadBytes);
    }

    [TestMethod]
    public void NewNotificationsAreNotBlockedByRetryEligibleOlderNotification()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Clock.NowMs = 20;
        context.Tracker.Enqueue(Framing());
        var next = context.Send();
        Assert.AreEqual(2L, next.Sequence);
        Assert.IsFalse(next.IsRetry);
        Assert.AreEqual(1L, context.Send().Sequence);
    }

    [TestMethod]
    public void TransmissionTokensAndAckProcessingAreSerialized()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        var token = context.Tracker.BeginTransmission()!;
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Tracker.BeginTransmission());
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Tracker.Acknowledge(context.Ack(1)));
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            context.Tracker.ConfirmTransmission(token with { }));
        context.Tracker.ConfirmTransmission(token);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Tracker.ConfirmTransmission(token));
        context.Tracker.Acknowledge(context.Ack(1));
    }

    [TestMethod]
    public void FailedNotificationWriteTerminatesAndDoesNotRetryCatOrNotification()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        var token = context.Tracker.BeginTransmission()!;
        AssertTerminates(context, "NOTIFICATION_WRITE_FAILED",
            () => context.Tracker.FailTransmission(token, "Partial control write failed."));
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Tracker.BeginTransmission());
    }

    [TestMethod]
    public void CallerMutationCannotChangeRetainedEnvelope()
    {
        var context = new Context();
        var fields = Framing();
        context.Tracker.Enqueue(fields);
        fields["reason"] = "Changed.";
        Assert.AreEqual("Malformed input.", context.Send().Envelope.GetProperty("reason").GetString());
    }

    [TestMethod]
    public void InvalidLocalNotificationDoesNotConsumeSequenceOrEndSession()
    {
        var context = new Context();
        var invalid = Framing();
        invalid["sessionId"] = Guid.NewGuid().ToString("D");
        Assert.ThrowsExactly<ArgumentException>(() => context.Tracker.Enqueue(invalid));
        invalid = Framing();
        invalid["recordSequence"] = "1";
        Assert.ThrowsExactly<ComConCommandProtocolException>(() => context.Tracker.Enqueue(invalid));
        Assert.AreEqual(1L, context.Tracker.Enqueue(Framing()));
        Assert.IsTrue(context.Sessions.CanRelayCat(context.Session));
    }

    [TestMethod]
    public void RecordOutcomeAndOrderlyEndUseSharedAckMechanism()
    {
        var context = new Context();
        context.Tracker.Enqueue(new Dictionary<string, object>
        {
            ["messageType"] = "record.outcome", ["recordSequence"] = "7",
            ["deliveryStatus"] = "WRITE_SUCCEEDED"
        });
        context.Tracker.Enqueue(new Dictionary<string, object>
        {
            ["messageType"] = "session.notification", ["event"] = "session.ended", ["reason"] = "Orderly closure."
        });
        Assert.AreEqual("7", context.Send().Envelope.GetProperty("recordSequence").GetString());
        Assert.AreEqual("session.ended", context.Send().Envelope.GetProperty("event").GetString());
        context.Tracker.Acknowledge(context.Ack(2));
        context.Tracker.Acknowledge(context.Ack(1));
        context.Tracker.Close("Orderly notifications acknowledged.");
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
    }

    [TestMethod]
    public void ExternalSessionClosureClearsRetainedDataOnNextOperation()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Sessions.EndSession(context.Session, "Control disconnected.");
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Tracker.CheckTimeouts());
        Assert.AreEqual(0, context.Tracker.PendingCount);
        context.Tracker.Close("Duplicate close callback.");
    }

    [TestMethod]
    public void DeadlineOverflowTerminatesRatherThanWrapping()
    {
        var context = new Context();
        context.Clock.NowMs = long.MaxValue;
        AssertTerminates(context, "NOTIFICATION_CLOCK_ERROR", () => context.Tracker.Enqueue(Framing()));
    }

    [TestMethod]
    public void SlowNotificationWriteCannotConfirmPastDeadline()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        var token = context.Tracker.BeginTransmission()!;
        context.Clock.NowMs = 100;
        AssertTerminates(context, "NOTIFICATION_ACK_TIMEOUT", () => context.Tracker.ConfirmTransmission(token));
    }

    [TestMethod]
    public void LaterAckDoesNotHideExpiredEarlierNotification()
    {
        var context = new Context();
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Clock.NowMs = 10;
        context.Tracker.Enqueue(Framing());
        context.Send();
        context.Tracker.Acknowledge(context.Ack(2));
        context.Clock.NowMs = 100;
        AssertTerminates(context, "NOTIFICATION_ACK_TIMEOUT", () => context.Tracker.CheckTimeouts());
    }

    [TestMethod]
    public void RecordRejectedUsesRecordSequenceButFramingNotificationDoesNot()
    {
        var context = new Context();
        context.Tracker.Enqueue(new Dictionary<string, object>
        {
            ["messageType"] = "record.rejected", ["recordSequence"] = "3",
            ["deliveryStatus"] = "NOT_SENT", ["reason"] = "Queue expired."
        });
        context.Tracker.Enqueue(Framing());
        Assert.AreEqual("3", context.Send().Envelope.GetProperty("recordSequence").GetString());
        Assert.IsFalse(context.Send().Envelope.TryGetProperty("recordSequence", out _));
    }

    [TestMethod]
    public void OversizeAndNonNotificationLocalInputDoesNotAllocateSequence()
    {
        var context = new Context();
        var fields = Framing();
        fields["reason"] = new string('x', ComConCommandProtocol.MaximumPayloadBytes);
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(() => context.Tracker.Enqueue(fields));
        Assert.AreEqual("FRAME_TOO_LARGE", exception.ErrorCode);
        Assert.ThrowsExactly<ArgumentException>(() => context.Tracker.Enqueue(
            new Dictionary<string, object> { ["messageType"] = "notification.ack" }));
        Assert.AreEqual(1L, context.Tracker.Enqueue(Framing()));
    }

    [TestMethod]
    public void ConstructorRequiresPositiveBoundsAndAnActiveOwnedSession()
    {
        var context = new Context();
        foreach (var (count, bytes, timeout, retry, sequence) in new (int, int, long, long, long)[]
        {
            (0, 1000, 100, 20, 10), (1, 0, 100, 20, 10), (1, 1000, 0, 20, 10),
            (1, 1000, 100, 0, 10), (1, 1000, 100, 100, 10), (1, 1000, 100, 20, 0)
        })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ComConCommandNotifications(
                context.Sessions, context.Session, context.Clock, count, bytes, timeout, retry, sequence));
        context.Sessions.EndSession(context.Session, "Closed.");
        Assert.ThrowsExactly<InvalidOperationException>(() => new ComConCommandNotifications(
            context.Sessions, context.Session, context.Clock, 1, 1000, 100, 20));
    }

    private static Dictionary<string, object> Framing() => new()
    {
        ["messageType"] = "framing.notification", ["errorCode"] = "FRAMING_ANOMALY", ["reason"] = "Malformed input."
    };

    private static Dictionary<string, object> FramingWithReasonLength(int length)
    {
        var fields = Framing();
        fields["reason"] = new string('x', length);
        return fields;
    }

    private static int ReasonLengthForPayloadBytes(int targetBytes)
    {
        var probe = new Context(maximumBytes: int.MaxValue);
        probe.Tracker.Enqueue(Framing());
        return "Malformed input.".Length + targetBytes - probe.Tracker.RetainedPayloadBytes;
    }

    private static int[] SplitPayloadSize(int totalBytes)
    {
        var first = totalBytes / 3;
        var second = totalBytes / 3;
        return [first, second, totalBytes - first - second];
    }

    private static void AssertTerminates(Context context, string code, Action action)
    {
        var exception = Assert.ThrowsExactly<ComConCommandProtocolException>(action);
        Assert.AreEqual(code, exception.ErrorCode);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.IsFalse(context.Sessions.CanRelayCat(context.Session));
        Assert.AreEqual(0, context.Tracker.PendingCount);
        Assert.AreEqual(0, context.Tracker.RetainedPayloadBytes);
        Assert.IsFalse(string.IsNullOrWhiteSpace(context.Session.EndReason));
        Assert.IsTrue(context.Sessions.CanRelayCat(context.OtherSession));
    }

    private sealed class FakeClock : IMonotonicClock
    {
        public long NowMs { get; set; }
    }

    private sealed class Context
    {
        public FakeClock Clock { get; } = new();
        public ComConCommandSessions Sessions { get; }
        public ComConCommandSession Session { get; }
        public ComConCommandSession OtherSession { get; }
        public ComConCommandNotifications Tracker { get; }

        public Context(
            int maximumCount = 8, int maximumBytes = 16_384, long maximumSequence = long.MaxValue,
            long acknowledgmentTimeoutMs = 100, long retryIntervalMs = 20)
        {
            var rig = Guid.NewGuid().ToString("D");
            var otherRig = Guid.NewGuid().ToString("D");
            Sessions = new(Clock, 100, [new(rig, 1), new(otherRig, 2)]);
            Session = Activate(rig);
            OtherSession = Activate(otherRig);
            Tracker = new(Sessions, Session, Clock, maximumCount, maximumBytes,
                acknowledgmentTimeoutMs, retryIntervalMs, maximumSequence);
        }

        private ComConCommandSession Activate(string rig)
        {
            var accepted = Sessions.AcceptCatHandshake(JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion,
                rigProfileId = rig
            }));
            var session = accepted.Session!;
            Sessions.ConfirmCatAcceptanceSent(session);
            Sessions.BindControl(JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = session.ProfileId, sessionId = session.SessionId
            }), session.ProfileId);
            Sessions.ConfirmControlAcceptanceSent(session);
            Sessions.AcceptCatReady(session, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = session.ProfileId, sessionId = session.SessionId
            }));
            return session;
        }

        public byte[] Ack(long sequence) => AckText(sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));

        public byte[] AckText(string sequence) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = Session.ProfileId, sessionId = Session.SessionId, notificationSequence = sequence
        });

        public ComConNotificationTransmission Send()
        {
            var token = Tracker.BeginTransmission()!;
            Assert.IsNotNull(token);
            ComConCommandProtocol.ParseEnvelope(Encoding.UTF8.GetBytes(token.Envelope.GetRawText()));
            Tracker.ConfirmTransmission(token);
            return token;
        }
    }
}
