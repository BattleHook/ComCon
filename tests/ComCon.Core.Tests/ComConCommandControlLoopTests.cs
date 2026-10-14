using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConCommandControlLoopTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FinalAckFollowedByEofClosesOrderlyEvenBeforeFlushSettles(bool holdFlush)
    {
        var context = new Context();
        context.Stream.HoldFlush = holdFlush;
        context.Loop.RequestOrderlyClose("Finished.");
        var run = context.Loop.RunAsync();
        await context.Stream.Receive();
        await context.SendAck("1");
        context.Stream.EndInput();
        await WaitUntil(() => context.Stream.ReadByteCount == context.AckFrameLength("1"));
        context.Stream.ReleaseFlush();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual("Finished.", context.Session.EndReason);
        Assert.AreEqual(0, context.Tracker.PendingCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EofWithoutFinalAckOrAfterTruncatedEnvelopeStillFails(bool truncated)
    {
        var context = new Context();
        context.Loop.RequestOrderlyClose("Finished.");
        var run = context.Loop.RunAsync();
        await context.Stream.Receive();
        if (truncated)
        {
            await context.SendAck("1");
            await context.Stream.Send([0x00, 0x00]);
        }
        context.Stream.EndInput();
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreNotEqual("Finished.", context.Session.EndReason);
    }

    [TestMethod]
    public async Task AutomaticallySendsFramesProcessesOutOfOrderAcksAndClosesOrderly()
    {
        var context = new Context();
        context.Enqueue();
        context.Enqueue();
        var run = context.Loop.RunAsync();
        var first = await context.Stream.Receive();
        var second = await context.Stream.Receive();
        Assert.AreEqual("1", first.GetProperty("notificationSequence").GetString());
        Assert.AreEqual("2", second.GetProperty("notificationSequence").GetString());
        await context.SendAck("2");
        await context.SendAck("1");
        await WaitUntil(() => context.Tracker.PendingCount == 0);
        await context.SendAck("1");
        context.Loop.RequestOrderlyClose("Finished.");
        var final = await context.Stream.Receive();
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        await context.SendAck("3");
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual("Finished.", context.Session.EndReason);
        Assert.IsTrue(context.Stream.Disposed);
        Assert.AreEqual(0, context.Tracker.RetainedPayloadBytes);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.Loop.RunAsync());
    }

    [TestMethod]
    public async Task AckArrivingBeforeFlushCompletionIsDeferredUntilConfirmation()
    {
        var context = new Context();
        context.Stream.HoldFlush = true;
        context.Enqueue();
        using var stop = new CancellationTokenSource();
        var run = context.Loop.RunAsync(stop.Token);
        await context.Stream.Receive();
        await context.SendAck("1");
        await WaitUntil(() => context.Stream.ReadByteCount > 0);
        Assert.AreEqual(1, context.Tracker.PendingCount);
        context.Stream.ReleaseFlush();
        await WaitUntil(() => context.Tracker.PendingCount == 0);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
    }

    [TestMethod]
    public async Task SelectedDeferredAckBoundAcceptsNMinusOneAndNButRejectsNPlusOne()
    {
        var limit = ComConCommandBounds.MaximumDeferredAcknowledgments;
        var context = new Context(maximumAcks: limit);
        context.Stream.HoldFlush = true;
        context.Enqueue();
        var run = context.Loop.RunAsync();
        await context.Stream.Receive();
        var bytesRead = 0;
        for (var i = 0; i < limit - 1; i++)
        {
            bytesRead += context.AckFrameLength("1");
            await context.SendAck("1");
        }
        await WaitUntil(() => context.Stream.ReadByteCount >= bytesRead);
        Assert.AreEqual(ComConCommandSessionState.Active, context.Session.State);

        bytesRead += context.AckFrameLength("1");
        await context.SendAck("1");
        await WaitUntil(() => context.Stream.ReadByteCount >= bytesRead);
        Assert.AreEqual(ComConCommandSessionState.Active, context.Session.State);

        await context.SendAck("1");
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("CONTROL_INGRESS_OVERFLOW", exception.ErrorCode);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.IsTrue(context.Stream.Disposed);
    }

    [TestMethod]
    public async Task RetriesAreIdenticalAndIdleTimeoutDoesNotExtendDeadline()
    {
        var context = new Context();
        context.Enqueue();
        var run = context.Loop.RunAsync();
        var first = await context.Stream.Receive();
        await WaitUntil(() => context.Tracker.TransmittedSequence == 1);
        context.Clock.NowMs = 20;
        var retry = await context.Stream.Receive();
        Assert.AreEqual(first.GetRawText(), retry.GetRawText());
        context.Clock.NowMs = 100;
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("NOTIFICATION_ACK_TIMEOUT", exception.ErrorCode);
        Assert.AreEqual(0, context.Tracker.PendingCount);
    }

    [TestMethod]
    public async Task UnsentAckTerminatesWithoutFinalLifecycleNotification()
    {
        var context = new Context();
        var run = context.Loop.RunAsync();
        await context.SendAck("1");
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("INVALID_NOTIFICATION_ACK", exception.ErrorCode);
        Assert.AreEqual(0, context.Stream.FlushCount);
    }

    [TestMethod]
    public async Task TruncatedControlInputPropagatesEofAndClearsSession()
    {
        var context = new Context();
        var run = context.Loop.RunAsync();
        await context.Stream.Send([0, 0]);
        context.Stream.EndInput();
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.IsTrue(context.Stream.Disposed);
    }

    [TestMethod]
    public async Task WriteFailureEndsSessionWithoutRetry()
    {
        var context = new Context();
        context.Stream.FailWrite = true;
        context.Enqueue();
        var run = context.Loop.RunAsync();
        await Assert.ThrowsExactlyAsync<IOException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(1, context.Stream.WriteCount);
        Assert.AreEqual(0, context.Tracker.PendingCount);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
    }

    [TestMethod]
    public async Task BlockedWriteIsCancelledAtNotificationDeadline()
    {
        var context = new Context();
        context.Stream.HoldFlush = true;
        context.Enqueue();
        var run = context.Loop.RunAsync();
        await context.Stream.Receive();
        context.Clock.NowMs = 100;
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("NOTIFICATION_ACK_TIMEOUT", exception.ErrorCode);
        Assert.IsTrue(context.Stream.Disposed);
    }

    [TestMethod]
    public async Task OrderlyClosureStillExpiresWhenFinalAckIsMissing()
    {
        var context = new Context();
        context.Loop.RequestOrderlyClose("Finished.");
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Loop.RequestOrderlyClose("Again."));
        var run = context.Loop.RunAsync();
        await context.Stream.Receive();
        context.Clock.NowMs = 100;
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("NOTIFICATION_ACK_TIMEOUT", exception.ErrorCode);
        Assert.IsTrue(context.Session.EndReason!.Contains("deadline", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task IdleCancellationStopsReaderClosesStreamAndEndsSession()
    {
        var context = new Context();
        using var stop = new CancellationTokenSource();
        var run = context.Loop.RunAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(context.Stream.Disposed);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Loop.RequestOrderlyClose("Too late."));
    }

    [TestMethod]
    public async Task InvalidUtf8ControlInputPropagatesExplicitFailureWithoutResponse()
    {
        var context = new Context();
        var run = context.Loop.RunAsync();
        await context.Stream.Send([0, 0, 0, 1, 0xFF]);
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("INVALID_UTF8", exception.ErrorCode);
        Assert.AreEqual(0, context.Stream.FlushCount);
        Assert.IsTrue(context.Stream.Disposed);
    }

    [TestMethod]
    [DataRow("control.bind")]
    [DataRow("cat.ready")]
    public async Task ValidNonAckEnvelopeOnControlChannelTerminates(string messageType)
    {
        var context = new Context();
        var run = context.Loop.RunAsync();
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType, contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = context.Session.SessionId
        });
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await context.Stream.Send(frame);
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("INVALID_MESSAGE_TYPE", exception.ErrorCode);
        Assert.IsTrue(context.Stream.Disposed);
    }

    [TestMethod]
    public void ConstructorRejectsInvalidLimitsOrNonDuplexStream()
    {
        var context = new Context();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ComConCommandControlLoop(context.Stream, context.Tracker, 0, TimeSpan.FromMilliseconds(1)));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            new ComConCommandControlLoop(context.Stream, context.Tracker, 1, TimeSpan.Zero));
        using var readOnly = new MemoryStream([], writable: false);
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConCommandControlLoop(readOnly, context.Tracker, 1, TimeSpan.FromMilliseconds(1)));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Clock : IMonotonicClock
    {
        private long now;
        public long NowMs { get => Interlocked.Read(ref now); set => Interlocked.Exchange(ref now, value); }
    }

    private sealed class Context
    {
        public Clock Clock { get; } = new();
        public DuplexStream Stream { get; } = new();
        public ComConCommandSession Session { get; }
        public ComConCommandNotifications Tracker { get; }
        public ComConCommandControlLoop Loop { get; }

        public Context(int maximumAcks = ComConCommandBounds.MaximumDeferredAcknowledgments)
        {
            var rig = Guid.NewGuid().ToString("D");
            var sessions = new ComConCommandSessions(Clock, 100, [new(rig, 1)]);
            Session = sessions.AcceptCatHandshake(JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion, rigProfileId = rig
            })).Session!;
            sessions.ConfirmCatAcceptanceSent(Session);
            sessions.BindControl(JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = Session.SessionId
            }), 1);
            sessions.ConfirmControlAcceptanceSent(Session);
            sessions.AcceptCatReady(Session, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = Session.SessionId
            }));
            Tracker = new(sessions, Session, Clock, 8, 16384, 100, 20);
            Loop = new(Stream, Tracker, maximumAcks,
                TimeSpan.FromMilliseconds(ComConCommandBounds.ControlPollIntervalMs));
        }

        public void Enqueue() => Tracker.Enqueue(new Dictionary<string, object>
        {
            ["messageType"] = "framing.notification", ["errorCode"] = "FRAMING_ANOMALY", ["reason"] = "Malformed input."
        });

        public ValueTask SendAck(string sequence)
        {
            var payload = AckPayload(sequence);
            var bytes = new byte[payload.Length + 4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)payload.Length);
            payload.CopyTo(bytes, 4);
            return Stream.Send(bytes);
        }

        public int AckFrameLength(string sequence) => AckPayload(sequence).Length + 4;

        private byte[] AckPayload(string sequence) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = Session.SessionId, notificationSequence = sequence
        });
    }

    internal sealed class DuplexStream : Stream
    {
        private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(16);
        private readonly Channel<byte[]> outgoing = Channel.CreateBounded<byte[]>(16);
        private readonly MemoryStream output = new();
        private readonly TaskCompletionSource flushRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? current;
        private int offset;
        private int flushCount;
        private int readByteCount;
        private int writeCount;
        public bool HoldFlush { get; set; }
        public bool FailWrite { get; set; }
        public bool Disposed { get; private set; }
        public int FlushCount => Volatile.Read(ref flushCount);
        public int ReadByteCount => Volatile.Read(ref readByteCount);
        public int WriteCount => Volatile.Read(ref writeCount);
        public void ReleaseFlush() => flushRelease.TrySetResult();
        public void EndInput() => incoming.Writer.TryComplete();
        public ValueTask Send(byte[] bytes) => incoming.Writer.WriteAsync(bytes);

        public async Task<JsonElement> Receive()
        {
            var bytes = await outgoing.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual((uint)(bytes.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(bytes));
            return ComConCommandProtocol.ParseEnvelope(bytes.AsSpan(4));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (current is null || offset == current.Length)
            {
                if (!await incoming.Reader.WaitToReadAsync(cancellationToken)) return 0;
                current = await incoming.Reader.ReadAsync(cancellationToken);
                offset = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            Interlocked.Add(ref readByteCount, count);
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref writeCount);
            if (FailWrite) throw new IOException("Injected control write failure.");
            return output.WriteAsync(buffer, cancellationToken);
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            await outgoing.Writer.WriteAsync(output.ToArray(), cancellationToken);
            output.SetLength(0);
            if (HoldFlush) await flushRelease.Task.WaitAsync(cancellationToken);
            Interlocked.Increment(ref flushCount);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            incoming.Writer.TryComplete();
            outgoing.Writer.TryComplete();
            flushRelease.TrySetCanceled();
            output.Dispose();
            base.Dispose(disposing);
        }

        public override bool CanRead => !Disposed;
        public override bool CanWrite => !Disposed;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
