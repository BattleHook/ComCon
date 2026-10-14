using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.Buffers.Binary;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;
using System.IO.Pipes;
using System.Runtime.Versioning;

namespace ComCon.Core.Tests;

[TestClass]
public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [DataRow("orderly", false)]
    [DataRow("invalid-ack", false)]
    [DataRow("control-eof", false)]
    [DataRow("listener-stop", false)]
    [DataRow("orderly", true)]
    [DataRow("invalid-ack", true)]
    [DataRow("control-eof", true)]
    [DataRow("listener-stop", true)]
    [SupportedOSPlatform("windows")]
    public async Task PersistentListenerUsesSessionLifecycleAcrossReconnectAndOwnerStop(string ending, bool continuingHost)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var clock = new FakeClock();
        var rig = Guid.NewGuid().ToString("D");
        var sessions = new ComConCommandSessions(clock, 5000, [new(rig, 1)]);
        var sharedFactory = new FakeFactory();
        await using var sharedHost = new SharedRadioHost(sharedFactory, clock, _ => { });
        if (continuingHost)
        {
            await sharedHost.StartAsync(Profile(), Options(), timeout.Token);
            await sharedFactory.Legacy.Send(Civ(0x94, 0x40));
            CollectionAssert.AreEqual(Civ(0x94, 0x40), await sharedFactory.Radio.Receive());
        }
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var listener = new ComConCommandTestPipeListener(
            "ComConTest_FreshHost_" + Guid.NewGuid().ToString("N"), sessions,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), errors.Enqueue);
        var started = Channel.CreateBounded<PipeLifecycleTestSession>(2);
        var run = listener.RunAsync(async (pipes, token) =>
        {
            var factory = continuingHost ? sharedFactory : new FakeFactory();
            var tracker = new ComConCommandNotifications(sessions, pipes.Session, clock, 32, 32768, 2000, 500);
            var endpoint = new ComConCommandEndpoint(pipes.CreateCatEndpoint(), sessions, pipes.Session, tracker);
            var host = continuingHost ? sharedHost : new SharedRadioHost(factory, clock, _ => { });
            var loop = new ComConCommandControlLoop(pipes.Control, tracker, 8, TimeSpan.FromMilliseconds(2));
            await using var lifecycle = continuingHost
                ? ComConCommandLifecycle.ForAttachedSession(host, endpoint, tracker, loop, TimeSpan.FromSeconds(3))
                : new ComConCommandLifecycle(host, endpoint, tracker, loop, TimeSpan.FromSeconds(3));
            try
            {
                if (continuingHost) await lifecycle.StartAttachedAsync(token);
                else await lifecycle.StartAsync(Profile(), Options(), token);
                var active = new PipeLifecycleTestSession(pipes, pipes.Session, factory, host, lifecycle, tracker);
                await started.Writer.WriteAsync(active, token);
                await active.Finish.Task.WaitAsync(token);
                if (lifecycle.ControlCompletion.IsCompleted)
                    await lifecycle.ControlCompletion;
                else
                    await lifecycle.StopOrderlyAsync("Reconnect lifecycle finished.", token);
            }
            finally
            {
                await lifecycle.DisposeAsync();
                if (!continuingHost) await host.DisposeAsync();
            }
        }, stop.Token);
        PipeLifecycleTestSession? previous = null;
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var cat = new NamedPipeClientStream(
                    ".", listener.CatPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await cat.ConnectAsync(timeout.Token);
                using var writer = new SemaphoreSlim(1, 1);
                await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion,
                    rigProfileId = rig
                }), writer, timeout.Token);
                var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
                using var control = new NamedPipeClientStream(
                    ".", listener.ControlPipeName(1), PipeDirection.InOut, PipeOptions.Asynchronous);
                await control.ConnectAsync(timeout.Token);
                var sessionId = accepted.GetProperty("sessionId").GetString();
                await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                    profileId = 1, sessionId
                }), writer, timeout.Token);
                await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token);
                await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
                    profileId = 1, sessionId
                }), writer, timeout.Token);
                var active = await started.Reader.ReadAsync(timeout.Token);
                if (previous is not null)
                {
                    Assert.AreNotEqual(previous.Session.SessionId, active.Session.SessionId);
                    if (continuingHost)
                    {
                        Assert.AreSame(previous.Host, active.Host);
                        Assert.AreEqual(SharedRadioHostState.Running, previous.Host.State);
                        Assert.IsFalse(previous.Factory.Radio.Disposed);
                        Assert.IsFalse(previous.Factory.Legacy.Disposed);
                        Assert.AreEqual(2, previous.Factory.OpenCount);
                    }
                    else
                    {
                        Assert.AreNotSame(previous.Host, active.Host);
                        Assert.AreEqual(SharedRadioHostState.Stopped, previous.Host.State);
                        Assert.IsTrue(previous.Factory.Radio.Disposed);
                        Assert.IsTrue(previous.Factory.Legacy.Disposed);
                    }
                    Assert.IsTrue(previous.Lifecycle.ControlCompletion.IsCompleted);
                    Assert.AreEqual((ending == "orderly" ? 1 : 2) + (continuingHost ? 1 : 0), previous.Factory.Radio.WriteCount);
                    await previous.Pipes.DisposeAsync();
                    Assert.IsTrue(sessions.CanRelayCat(active.Session));
                    await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
                        previous.Pipes.Cat.WriteAsync(Civ(0x94, 0x78), timeout.Token).AsTask());
                }
                var command = Civ(0x94, (byte)(attempt + 1));
                await cat.WriteAsync(command, timeout.Token);
                CollectionAssert.AreEqual(command, await active.Factory.Radio.Receive());
                Assert.AreEqual(continuingHost
                    ? 2 + (attempt == 0 ? 0 : ending == "orderly" ? 1 : 2)
                    : 1, active.Factory.Radio.WriteCount);
                var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token);
                AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
                Assert.AreEqual("1", outcome.GetProperty("notificationSequence").GetString());
                Assert.AreEqual(sessionId, outcome.GetProperty("sessionId").GetString());
                var reply = Civ(0xE1, (byte)(0x10 + attempt));
                await active.Factory.Radio.Send(reply);
                var bytes = new byte[reply.Length];
                await cat.ReadExactlyAsync(bytes, timeout.Token);
                CollectionAssert.AreEqual(reply, bytes);

                if (ending == "listener-stop")
                {
                    previous = active;
                    stop.Cancel();
                    await run.WaitAsync(TimeSpan.FromSeconds(4));
                    await Assert.ThrowsAsync<OperationCanceledException>(() => active.Lifecycle.ControlCompletion);
                    Assert.AreEqual(ComConCommandSessionState.Ended, active.Session.State);
                    break;
                }
                if (attempt == 0 && ending != "orderly")
                {
                    var disconnectStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (ending == "control-eof") control.Dispose();
                    else await Ack("999");
                    var failure = await Assert.ThrowsAsync<IOException>(() =>
                        active.Lifecycle.ControlCompletion.WaitAsync(timeout.Token));
                    Assert.AreEqual(ComConCommandSessionState.Ended, active.Session.State);
                    Assert.IsFalse(sessions.CanRelayCat(active.Session));
                    var legacyCommand = Civ(0x94, 0x44);
                    await active.Factory.Legacy.Send(legacyCommand);
                    CollectionAssert.AreEqual(legacyCommand, await active.Factory.Radio.Receive());
                    var legacyReply = Civ(0xE0, 0x45);
                    await active.Factory.Radio.Send(legacyReply);
                    CollectionAssert.AreEqual(legacyReply, await active.Factory.Legacy.Receive());
                    Assert.AreEqual(SharedRadioHostState.Running, active.Host.State);
                    Assert.IsFalse(active.Factory.Radio.Disposed);
                    Assert.IsFalse(active.Factory.Legacy.Disposed);
                    // Old-session bytes must be discarded when its pipe instance disconnects.
                    await cat.WriteAsync(Civ(0x94, 0x77), timeout.Token);
                    active.Finish.TrySetResult();
                    await WaitUntil(() => errors.Count == 1);
                    Assert.AreSame(failure, errors.Single());
                    Assert.IsLessThan(3000d,
                        System.Diagnostics.Stopwatch.GetElapsedTime(disconnectStarted).TotalMilliseconds,
                        "Unexpected disconnect must release its session and complete cleanup within three seconds.");
                    if (ending == "control-eof")
                        Assert.IsInstanceOfType<EndOfStreamException>(failure);
                }
                else
                {
                    await Ack(outcome.GetProperty("notificationSequence").GetString()!);
                    active.Finish.TrySetResult();
                    var final = await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token);
                    Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
                    await Ack(final.GetProperty("notificationSequence").GetString()!);
                    await active.Lifecycle.ControlCompletion.WaitAsync(timeout.Token);
                    Assert.AreEqual(0, active.Tracker.PendingCount);
                }
                previous = active;

                ValueTask Ack(string sequence) => ComConCommandProtocol.WriteEnvelopeAsync(
                    control, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
                        profileId = 1, sessionId, notificationSequence = sequence
                    }), writer, timeout.Token);
            }
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(4));
        }
        Assert.IsNotNull(previous);
        if (continuingHost)
        {
            Assert.AreEqual(SharedRadioHostState.Running, sharedHost.State);
            Assert.IsFalse(sharedFactory.Radio.Disposed);
            Assert.IsFalse(sharedFactory.Legacy.Disposed);
            await sharedFactory.Legacy.Send(Civ(0x94, 0x46));
            CollectionAssert.AreEqual(Civ(0x94, 0x46), await sharedFactory.Radio.Receive());
            Assert.AreEqual(2, sharedFactory.OpenCount);
            await sharedHost.StopAsync();
        }
        Assert.AreEqual(SharedRadioHostState.Stopped, previous.Host.State);
        Assert.IsTrue(previous.Factory.Radio.Disposed);
        Assert.IsTrue(previous.Factory.Legacy.Disposed);
        Assert.HasCount(ending is "orderly" or "listener-stop" ? 0 : 1, errors);
    }

    private sealed record PipeLifecycleTestSession(
        ComConAcceptedTestPipes Pipes, ComConCommandSession Session, FakeFactory Factory, SharedRadioHost Host,
        ComConCommandLifecycle Lifecycle, ComConCommandNotifications Tracker)
    {
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    [TestMethod]
    public async Task AttachedOrderlyStopSettlesWriteRejectsQueueAndKeepsLegacyPortsOpen()
    {
        var context = new Context();
        var factory = new FakeFactory();
        factory.Radio.HoldWrites = true;
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options());
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var loop = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = ComConCommandLifecycle.ForAttachedSession(
            host, context.Endpoint, context.Tracker, loop, TimeSpan.FromSeconds(3));
        await lifecycle.StartAttachedAsync();
        await context.Cat.Send([.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        await WaitUntil(() => factory.Radio.WriteCount == 1);
        var stop = lifecycle.StopOrderlyAsync("IPC only detached.");
        Assert.IsFalse(stop.IsCompleted);
        factory.Radio.ReleaseWrites();
        var outcome = await stream.Receive();
        var rejected = await stream.Receive();
        var final = await stream.Receive();
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        AssertOutcome(rejected, "record.rejected", "2", "NOT_SENT");
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        await SendControlAck(stream, context, final);
        await SendControlAck(stream, context, outcome);
        await SendControlAck(stream, context, rejected);
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
        Assert.IsTrue(context.Cat.Disposed);
        Assert.IsFalse(factory.Legacy.Disposed);
        Assert.IsFalse(factory.Radio.Disposed);
        Assert.AreEqual(1, factory.Radio.WriteCount);
        await factory.Legacy.Send(Civ(0x94, 3));
        CollectionAssert.AreEqual(Civ(0x94, 1), await factory.Radio.Receive());
        CollectionAssert.AreEqual(Civ(0x94, 3), await factory.Radio.Receive());
        Assert.AreEqual(2, factory.OpenCount);
    }

    [TestMethod]
    public async Task CompetingAttachedLifecycleFailureDoesNotStopHostOrOriginalOwner()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options(), context.Endpoint);
        var competitor = new Context();
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var loop = new ComConCommandControlLoop(stream, competitor.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = ComConCommandLifecycle.ForAttachedSession(
            host, competitor.Endpoint, competitor.Tracker, loop, TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => lifecycle.StartAttachedAsync());
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
        Assert.IsTrue(context.Sessions.CanRelayCat(context.Session));
        Assert.AreEqual(ComConCommandSessionState.Ended, competitor.Session.State);
        Assert.IsTrue(competitor.Cat.Disposed);
        Assert.IsTrue(stream.Disposed);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.DetachIpcAsync(competitor.Endpoint));
        await context.Cat.Send(Civ(0x94, 4));
        CollectionAssert.AreEqual(Civ(0x94, 4), await factory.Radio.Receive());
        Assert.AreEqual(2, factory.OpenCount);
    }

    [TestMethod]
    public async Task WrongProfileAttachAndCancelledAttachNeverAcquireHostOwnership()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile() with { Id = 2 }, Options());
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => host.AttachIpcAsync(context.Endpoint));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => host.AttachIpcAsync(context.Endpoint, cancelled.Token));
        Assert.IsTrue(context.Sessions.CanRelayCat(context.Session));
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
        await factory.Legacy.Send(Civ(0x94, 5));
        CollectionAssert.AreEqual(Civ(0x94, 5), await factory.Radio.Receive());
        Assert.AreEqual(2, factory.OpenCount);
        await context.Endpoint.DisposeAsync();
    }

    [TestMethod]
    public async Task AttachedLifecycleCleanupAfterProfileStopDoesNotRestartPorts()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options());
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var loop = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        var lifecycle = ComConCommandLifecycle.ForAttachedSession(
            host, context.Endpoint, context.Tracker, loop, TimeSpan.FromSeconds(3));
        await lifecycle.StartAttachedAsync();
        await host.StopAsync();
        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.AreEqual(2, factory.OpenCount);
        Assert.IsTrue(stream.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LifecycleCleanupPreservesProtocolFailureAlongsideEndpointCloseFailure(bool attached)
    {
        var context = new Context();
        var factory = new FakeFactory();
        var host = new SharedRadioHost(factory, context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var loop = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        var lifecycle = attached
            ? ComConCommandLifecycle.ForAttachedSession(
                host, context.Endpoint, context.Tracker, loop, TimeSpan.FromSeconds(3))
            : new ComConCommandLifecycle(
                host, context.Endpoint, context.Tracker, loop, TimeSpan.FromSeconds(3));
        if (attached)
        {
            await host.StartAsync(Profile(), Options());
            await lifecycle.StartAttachedAsync();
        }
        else
            await lifecycle.StartAsync(Profile(), Options());
        context.Cat.DisposeFailure = new IOException("Injected CAT close failure.");
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = Guid.NewGuid().ToString("D"), notificationSequence = "1"
        });
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        await stream.Send(frame);
        var primary = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            lifecycle.ControlCompletion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("PROFILE_SESSION_MISMATCH", primary.ErrorCode);
        await WaitUntil(() => context.Cat.Disposed);
        var cleanup = await Assert.ThrowsExactlyAsync<AggregateException>(() => lifecycle.DisposeAsync().AsTask());
        var failures = cleanup.Flatten().InnerExceptions;
        try
        {
            Assert.Contains(primary, failures, cleanup.ToString());
            Assert.IsTrue(failures.Any(failure => failure.GetBaseException().Message == "Injected CAT close failure."),
                cleanup.ToString());
            Assert.IsTrue(stream.Disposed);
            Assert.IsTrue(context.Cat.Disposed);
        }
        finally
        {
            try { await lifecycle.DisposeAsync(); }
            finally { await Assert.ThrowsAsync<AggregateException>(() => host.DisposeAsync().AsTask()); }
        }
    }

    [TestMethod]
    public async Task CancelledPendingAttachCannotAcquireOwnershipAfterRadioWriteSettles()
    {
        var context = new Context();
        var factory = new FakeFactory();
        factory.Radio.HoldWrites = true;
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options());
        try
        {
            await factory.Legacy.Send(Civ(0x94, 6));
            await WaitUntil(() => factory.Radio.WriteCount == 1);
            using var cancellation = new CancellationTokenSource();
            var attach = host.AttachIpcAsync(context.Endpoint, cancellation.Token);
            Assert.IsFalse(attach.IsCompleted);
            cancellation.Cancel();
            factory.Radio.ReleaseWrites();
            await Assert.ThrowsAsync<OperationCanceledException>(() => attach.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.IsTrue(context.Sessions.CanRelayCat(context.Session));
            Assert.IsFalse(context.Cat.Disposed);
            await host.AttachIpcAsync(context.Endpoint);
            await context.Cat.Send(Civ(0x94, 7));
            CollectionAssert.AreEqual(Civ(0x94, 6), await factory.Radio.Receive());
            CollectionAssert.AreEqual(Civ(0x94, 7), await factory.Radio.Receive());
            Assert.AreEqual(SharedRadioHostState.Running, host.State);
            Assert.AreEqual(2, factory.OpenCount);
        }
        finally { factory.Radio.ReleaseWrites(); }
    }

    [TestMethod]
    public async Task RadioFaultAllowsOldIpcCleanupButNeverNewAttachment()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options());
        await host.AttachIpcAsync(context.Endpoint);
        factory.Radio.WriteFailure = new IOException("Injected uncertain physical write.");
        await context.Cat.Send(Civ(0x94, 8));
        await WaitUntil(() => host.State == SharedRadioHostState.Faulted);
        await host.DetachIpcAsync(context.Endpoint);
        Assert.IsTrue(context.Cat.Disposed);
        var replacement = new Context();
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => host.AttachIpcAsync(replacement.Endpoint));
            Assert.IsTrue(replacement.Sessions.CanRelayCat(replacement.Session));
            Assert.AreEqual(SharedRadioHostState.Faulted, host.State);
            Assert.AreEqual(2, factory.OpenCount);
        }
        finally { await replacement.Endpoint.DisposeAsync(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AttachedPrestartCleanupAndCancelledStartupNeverStopProfileHost(bool cancelStartup)
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options());
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var loop = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        var lifecycle = ComConCommandLifecycle.ForAttachedSession(
            host, context.Endpoint, context.Tracker, loop, TimeSpan.FromSeconds(3));
        if (cancelStartup)
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => lifecycle.StartAttachedAsync(cancelled.Token));
        }
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                lifecycle.StartAsync(Profile(), Options()));
        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();
        Assert.IsTrue(context.Cat.Disposed);
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
        await factory.Legacy.Send(Civ(0x94, 9));
        CollectionAssert.AreEqual(Civ(0x94, 9), await factory.Radio.Receive());
        Assert.AreEqual(2, factory.OpenCount);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ProtectedPipeSetupComposesWithFakeHostAndOrderlyControlLifecycle()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new FakeClock();
        var rig = Guid.NewGuid().ToString("D");
        var sessions = new ComConCommandSessions(clock, 5000, [new(rig, 1)]);
        var acceptor = new ComConCommandTestPipeAcceptor(
            "ComConTest_Lifecycle_" + Guid.NewGuid().ToString("N"), sessions, TimeSpan.FromSeconds(5));
        var accept = acceptor.AcceptAsync(timeout.Token);
        using var cat = new NamedPipeClientStream(
            ".", acceptor.CatPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await cat.ConnectAsync(timeout.Token);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion,
            rigProfileId = rig
        }), writer, timeout.Token);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
        using var control = new NamedPipeClientStream(
            ".", acceptor.ControlPipeName(1), PipeDirection.InOut, PipeOptions.Asynchronous);
        await control.ConnectAsync(timeout.Token);
        await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = accepted.GetProperty("sessionId").GetString()
        }), writer, timeout.Token);
        Assert.AreEqual("control.bound",
            (await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token)).GetProperty("messageType").GetString());
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = accepted.GetProperty("sessionId").GetString()
        }), writer, timeout.Token);
        await using var pipes = await accept;
        var tracker = new ComConCommandNotifications(sessions, pipes.Session, clock, 32, 32768, 2000, 500);
        var endpoint = new ComConCommandEndpoint(pipes.CreateCatEndpoint(), sessions, pipes.Session, tracker);
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, clock, _ => { });
        var loop = new ComConCommandControlLoop(pipes.Control, tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = new ComConCommandLifecycle(
            host, endpoint, tracker, loop, TimeSpan.FromSeconds(3));
        await lifecycle.StartAsync(Profile(), Options(), timeout.Token);
        var command = Civ(0x94, 1);
        await cat.WriteAsync(command, timeout.Token);
        CollectionAssert.AreEqual(command, await factory.Radio.Receive());
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        var reply = Civ(0xE1, 0x10);
        await factory.Radio.Send(reply);
        var received = new byte[reply.Length];
        await cat.ReadExactlyAsync(received, timeout.Token);
        CollectionAssert.AreEqual(reply, received);
        await Ack(outcome);
        var stop = lifecycle.StopOrderlyAsync("Isolated pipe lifecycle finished.", timeout.Token);
        var final = await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token);
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        await Ack(final);
        await stop;
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.AreEqual(ComConCommandSessionState.Ended, pipes.Session.State);
        Assert.AreEqual(0, tracker.PendingCount);

        ValueTask Ack(JsonElement notification) =>
            ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = pipes.Session.SessionId,
                notificationSequence = notification.GetProperty("notificationSequence").GetString()
            }), writer, timeout.Token);
    }

    [TestMethod]
    public async Task LifecycleQuiescesHostSettlesWriteRejectsQueueThenSendsFinalNotification()
    {
        var context = new Context();
        var factory = new FakeFactory();
        factory.Radio.HoldWrites = true;
        var host = new SharedRadioHost(factory, context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromSeconds(3));
        await lifecycle.StartAsync(Profile(), Options());
        await context.Cat.Send([.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        await WaitUntil(() => factory.Radio.WriteCount == 1);
        var stop = lifecycle.StopOrderlyAsync("Profile stopped.");
        await WaitUntil(() => host.State == SharedRadioHostState.Stopping);
        factory.Radio.ReleaseWrites();
        var success = await stream.Receive();
        var rejected = await stream.Receive();
        var final = await stream.Receive();
        AssertOutcome(success, "record.outcome", "1", "WRITE_SUCCEEDED");
        AssertOutcome(rejected, "record.rejected", "2", "NOT_SENT");
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        Assert.AreEqual(1, factory.Radio.WriteCount);
        Assert.IsTrue(context.Cat.Disposed);
        Assert.AreEqual(ComConCommandSessionState.Active, context.Session.State);
        await SendControlAck(stream, context, final);
        await SendControlAck(stream, context, success);
        await SendControlAck(stream, context, rejected);
        await stop.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual("Profile stopped.", context.Session.EndReason);
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.IsTrue(stream.Disposed);
        Assert.AreEqual(0, context.Tracker.PendingCount);
    }

    [TestMethod]
    public async Task LifecycleControlFailureKeepsLegacyRunningAndIsObservable()
    {
        var context = new Context();
        var factory = new FakeFactory();
        var host = new SharedRadioHost(factory, context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromSeconds(3));
        await lifecycle.StartAsync(Profile(), Options());
        await stream.Send([0, 0, 0, 1, 0xFF]);
        var failure = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            lifecycle.ControlCompletion.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("INVALID_UTF8", failure.ErrorCode);
        await WaitUntil(() => context.Cat.Disposed);
        await factory.Legacy.Send(Civ(0x94, 4));
        CollectionAssert.AreEqual(Civ(0x94, 4), await factory.Radio.Receive());
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
    }

    [TestMethod]
    public async Task LifecycleShutdownDeadlineAbortsBlockedRadioWriteWithoutFinalNotification()
    {
        var context = new Context();
        var factory = new FakeFactory();
        factory.Radio.HoldWrites = true;
        var host = new SharedRadioHost(factory, context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromMilliseconds(50));
        await lifecycle.StartAsync(Profile(), Options());
        await context.Cat.Send(Civ(0x94, 1));
        await WaitUntil(() => factory.Radio.WriteCount == 1);
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            lifecycle.StopOrderlyAsync("Profile stopped.").WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.AreEqual(1, factory.Radio.WriteCount);
        Assert.IsTrue(stream.Disposed);
        Assert.IsTrue(context.Cat.Disposed);
    }

    [TestMethod]
    public async Task LifecycleStartupFailureClosesBothInjectedChannels()
    {
        var context = new Context();
        var factory = new FakeFactory { OpenFailure = true };
        var host = new SharedRadioHost(factory, context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<IOException>(() => lifecycle.StartAsync(Profile(), Options()));
        Assert.IsTrue(stream.Disposed);
        Assert.IsTrue(context.Cat.Disposed);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
    }

    [TestMethod]
    public async Task LifecycleFinalAckTimeoutPropagatesAndCleansUp()
    {
        var context = new Context();
        var factory = new FakeFactory();
        var host = new SharedRadioHost(factory, context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        await using var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromSeconds(3));
        await lifecycle.StartAsync(Profile(), Options());
        var stop = lifecycle.StopOrderlyAsync("Profile stopped.");
        Assert.AreEqual("session.ended", (await stream.Receive()).GetProperty("event").GetString());
        context.Clock.NowMs = 100;
        var failure = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() =>
            stop.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("NOTIFICATION_ACK_TIMEOUT", failure.ErrorCode);
        Assert.IsTrue(stream.Disposed);
        Assert.IsTrue(context.Cat.Disposed);
    }

    [TestMethod]
    public async Task LifecycleDisposeBeforeStartupClosesResourcesAndPreventsRestart()
    {
        var context = new Context();
        var host = new SharedRadioHost(new FakeFactory(), context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromSeconds(3));
        await lifecycle.DisposeAsync();
        await lifecycle.DisposeAsync();
        Assert.IsTrue(stream.Disposed);
        Assert.IsTrue(context.Cat.Disposed);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => lifecycle.StartAsync(Profile(), Options()));
    }

    [TestMethod]
    public async Task LifecycleCannotRestartAndAbortDisposalStopsHostAndControl()
    {
        var context = new Context();
        var host = new SharedRadioHost(new FakeFactory(), context.Clock, _ => { });
        var stream = new ComConCommandControlLoopTests.DuplexStream();
        var control = new ComConCommandControlLoop(stream, context.Tracker, 8, TimeSpan.FromMilliseconds(2));
        var lifecycle = new ComConCommandLifecycle(
            host, context.Endpoint, context.Tracker, control, TimeSpan.FromSeconds(3));
        await lifecycle.StartAsync(Profile(), Options());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => lifecycle.StartAsync(Profile(), Options()));
        await lifecycle.DisposeAsync();
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
        Assert.IsTrue(stream.Disposed);
        Assert.IsTrue(context.Cat.Disposed);
        await Assert.ThrowsAsync<OperationCanceledException>(() => lifecycle.ControlCompletion);
    }

    private static ValueTask SendControlAck(
        ComConCommandControlLoopTests.DuplexStream stream, Context context, JsonElement notification)
    {
        var payload = context.AckBytes(long.Parse(notification.GetProperty("notificationSequence").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame, 4);
        return stream.Send(frame);
    }

    [TestMethod]
    public async Task FakeHostRelaysOpaqueCatAndCorrelatesSuccessfulWrites()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options(), context.Endpoint);
        var first = Civ(0x94, 0x01);
        var second = Civ(0x94, 0x02);
        await context.Cat.Send([.. first, .. second]);
        CollectionAssert.AreEqual(first, await factory.Radio.Receive());
        CollectionAssert.AreEqual(second, await factory.Radio.Receive());
        var one = await context.NextNotification();
        var two = await context.NextNotification();
        AssertOutcome(one.Envelope, "record.outcome", "1", "WRITE_SUCCEEDED");
        AssertOutcome(two.Envelope, "record.outcome", "2", "WRITE_SUCCEEDED");
        context.Ack(one);
        context.Ack(two);
        var reply = Civ(0xE1, 0x10);
        await factory.Radio.Send(reply);
        CollectionAssert.AreEqual(reply, await context.Cat.Receive());
        await host.StopAsync();
        Assert.AreEqual(SharedRadioHostState.Stopped, host.State);
    }

    [TestMethod]
    public async Task InvalidAckDisconnectsOnlyIpcAndLegacyRelayContinues()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options(), context.Endpoint);
        Assert.ThrowsExactly<ComConCommandProtocolException>(() => context.Tracker.Acknowledge(context.AckBytes(1)));
        await WaitUntil(() => context.Cat.Disposed);
        var legacy = Civ(0x94, 0x33);
        await factory.Legacy.Send(legacy);
        CollectionAssert.AreEqual(legacy, await factory.Radio.Receive());
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
    }

    [TestMethod]
    public async Task NotificationDeadlineDisconnectsIpcWithoutAnyNewInput()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options(), context.Endpoint);
        await context.Cat.Send(Civ(0x94, 0x01));
        await factory.Radio.Receive();
        await WaitUntil(() => context.Tracker.PendingCount == 1);
        context.Clock.NowMs = 100;
        await WaitUntil(() => context.Cat.Disposed);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.AreEqual(0, context.Tracker.PendingCount);
        await factory.Legacy.Send(Civ(0x94, 0x04));
        await factory.Radio.Receive();
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
    }

    [TestMethod]
    public async Task HostStopsFurtherIpcWritesAfterNotificationOverflowButKeepsLegacyRunning()
    {
        var context = new Context(maximumCount: 1);
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options(), context.Endpoint);
        await context.Cat.Send([.. Civ(0x94, 1), .. Civ(0x94, 2), .. Civ(0x94, 3)]);
        await factory.Radio.Receive();
        await factory.Radio.Receive();
        await WaitUntil(() => context.Cat.Disposed);
        Assert.AreEqual(2, factory.Radio.WriteCount);
        await factory.Legacy.Send(Civ(0x94, 4));
        CollectionAssert.AreEqual(Civ(0x94, 4), await factory.Radio.Receive());
        Assert.AreEqual(SharedRadioHostState.Running, host.State);
    }

    [TestMethod]
    public async Task HostUnknownPhysicalWriteReportsOneUnknownAndDiscardsQueuedRecordsWithoutReplay()
    {
        var context = new Context();
        var factory = new FakeFactory();
        factory.Radio.WriteFailure = new IOException("Synthetic physical write failure.");
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await host.StartAsync(Profile(), Options(), context.Endpoint);
        await context.Cat.Send([.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        var outcome = await context.NextNotification();
        var discarded = await context.NextNotification();
        AssertOutcome(outcome.Envelope, "record.outcome", "1", "UNKNOWN_DELIVERY");
        AssertOutcome(discarded.Envelope, "record.rejected", "2", "NOT_SENT");
        context.Ack(outcome);
        context.Ack(discarded);
        await WaitUntil(() => host.State == SharedRadioHostState.Faulted);
        Assert.AreEqual(1, factory.Radio.WriteCount);
        var rx = Civ(0xE1, 0x10);
        await factory.Radio.Send(rx);
        CollectionAssert.AreEqual(rx, await context.Cat.Receive());
    }

    [TestMethod]
    public async Task WrongProfileEndpointFailsBeforeOpeningAnyFakePorts()
    {
        var context = new Context();
        var factory = new FakeFactory();
        await using var host = new SharedRadioHost(factory, context.Clock, _ => { });
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            host.StartAsync(Profile() with { Id = 2 }, Options(), context.Endpoint));
        Assert.AreEqual(0, factory.OpenCount);
        await context.Endpoint.DisposeAsync();
    }

    [TestMethod]
    public void CoordinatorQueueRejectionAndExpiryRetainCompleteRecordIdentity()
    {
        var context = new Context();
        var coordinator = context.Coordinator(Options() with { TxQueueRecordsPerClient = 1 });
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, [.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        coordinator.ProcessAvailable();
        var rejected = context.SendNotification();
        AssertOutcome(rejected.Envelope, "record.rejected", "2", "NOT_SENT");
        context.Ack(rejected);
        context.Clock.NowMs = 50;
        Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
        var expired = context.SendNotification();
        AssertOutcome(expired.Envelope, "record.rejected", "1", "NOT_SENT");
        context.Ack(expired);
        Assert.AreEqual(2L, coordinator.Metrics.NotSentTxRecords);
    }

    [TestMethod]
    [DataRow(AdapterWriteCompletion.Unknown, "UNKNOWN_DELIVERY")]
    [DataRow(AdapterWriteCompletion.ProvenZeroBytes, "NOT_SENT")]
    public void DispatchedWriteOutcomesNeverBecomePredispatchRejections(
        AdapterWriteCompletion completion, string status)
    {
        var context = new Context();
        var coordinator = context.Coordinator(Options());
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, [.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        Assert.AreEqual(1L, write!.RecordSequence);
        coordinator.CompleteRadioWrite(write.Id, completion);
        var outcome = context.SendNotification();
        AssertOutcome(outcome.Envelope, "record.outcome", "1", status);
        context.Ack(outcome);
        if (completion == AdapterWriteCompletion.Unknown)
        {
            var discarded = context.SendNotification();
            AssertOutcome(discarded.Envelope, "record.rejected", "2", "NOT_SENT");
            context.Ack(discarded);
            Assert.IsFalse(coordinator.TryTakeRadioWrite(out _));
            var rx = Civ(0xE1, 0x21);
            coordinator.TryPostRadioBytes(rx);
            coordinator.ProcessAvailable();
            Assert.IsTrue(coordinator.TryTakeClientDelivery(SharedRadioClient.IpcClient, out var delivery));
            CollectionAssert.AreEqual(rx, delivery!.Bytes);
        }
        else
        {
            Assert.IsTrue(coordinator.TryTakeRadioWrite(out var second));
            Assert.AreEqual(2L, second!.RecordSequence);
        }
    }

    [TestMethod]
    public void FramingNotificationDoesNotConsumeRecordSequenceAndRecoveryPreservesBytes()
    {
        var context = new Context();
        var coordinator = context.Coordinator(Options() with { Framing = FramingMode.KenwoodAscii });
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, [0x01, (byte)';']);
        coordinator.ProcessAvailable();
        var anomaly = context.SendNotification();
        Assert.AreEqual("framing.notification", anomaly.Envelope.GetProperty("messageType").GetString());
        Assert.IsFalse(anomaly.Envelope.TryGetProperty("recordSequence", out _));
        context.Ack(anomaly);
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, "FA123;"u8);
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        Assert.AreEqual(1L, write!.RecordSequence);
        CollectionAssert.AreEqual("FA123;"u8.ToArray(), write.Bytes);
        coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
        AssertOutcome(context.SendNotification().Envelope, "record.outcome", "1", "WRITE_SUCCEEDED");
    }

    [TestMethod]
    public void FramingOverflowAndLegacyRecordsAreNotMisclassifiedAsIpcOutcomes()
    {
        var context = new Context();
        context.Endpoint.OnDiagnostic(new(SharedRadioDiagnosticKind.FramingOverflow,
            "Synthetic framing limit violation.", SharedRadioClient.IpcClient));
        var overflow = context.SendNotification();
        Assert.AreEqual("FRAMING_OVERFLOW", overflow.Envelope.GetProperty("errorCode").GetString());
        Assert.IsFalse(overflow.Envelope.TryGetProperty("recordSequence", out _));
        context.Ack(overflow);
        var coordinator = context.Coordinator(Options());
        coordinator.TryPostClientBytes(SharedRadioClient.LegacyClient, Civ(0x94, 1));
        coordinator.ProcessAvailable();
        Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
        Assert.IsNull(write!.RecordSequence);
        coordinator.CompleteRadioWrite(write.Id, AdapterWriteCompletion.Succeeded);
        Assert.AreEqual(0, context.Tracker.PendingCount);
    }

    [TestMethod]
    public void NotificationOverflowInDiagnosticCallbackDoesNotThrowIntoCoordinator()
    {
        var context = new Context(maximumCount: 1);
        var coordinator = context.Coordinator(Options());
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, [.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        coordinator.ProcessAvailable();
        for (var index = 0; index < 2; index++)
        {
            Assert.IsTrue(coordinator.TryTakeRadioWrite(out var write));
            coordinator.CompleteRadioWrite(write!.Id, AdapterWriteCompletion.Succeeded);
        }
        Assert.IsFalse(context.Endpoint.Poll());
        Assert.AreEqual(SharedRadioCoordinatorState.Running, coordinator.State);
        Assert.AreEqual(ComConCommandSessionState.Ended, context.Session.State);
        Assert.IsTrue(context.Session.EndReason!.Contains("capacity", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DisconnectedClientQueuedRecordsAreIndividuallyClassified()
    {
        var context = new Context();
        var diagnostics = new List<SharedRadioDiagnostic>();
        var coordinator = new SharedRadioCoordinator(Options(), context.Clock, diagnostics.Add);
        coordinator.TryPostClientBytes(SharedRadioClient.IpcClient, [.. Civ(0x94, 1), .. Civ(0x94, 2)]);
        coordinator.ProcessAvailable();
        coordinator.NotifyClientDisconnected(SharedRadioClient.IpcClient, "Synthetic disconnect.");
        var rejected = diagnostics.Where(item => item.RecordSequence is not null).ToArray();
        Assert.HasCount(2, rejected);
        Assert.AreEqual(1L, rejected[0].RecordSequence);
        Assert.AreEqual(2L, rejected[1].RecordSequence);
        Assert.AreEqual(2L, coordinator.Metrics.NotSentTxRecords);
    }

    [TestMethod]
    public void AdapterRejectsForeignTrackerAndInactiveSession()
    {
        var first = new Context();
        var second = new Context();
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConCommandEndpoint(first.Cat, first.Sessions, first.Session, second.Tracker));
        first.Sessions.EndSession(first.Session, "Closed.");
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            new ComConCommandEndpoint(first.Cat, first.Sessions, first.Session, first.Tracker));
    }

    private static void AssertOutcome(JsonElement envelope, string type, string record, string status)
    {
        Assert.AreEqual(type, envelope.GetProperty("messageType").GetString());
        Assert.AreEqual(record, envelope.GetProperty("recordSequence").GetString());
        Assert.AreEqual(status, envelope.GetProperty("deliveryStatus").GetString());
        Assert.AreEqual(status != "WRITE_SUCCEEDED", envelope.TryGetProperty("reason", out _));
    }

    private static byte[] Civ(byte destination, byte command) => [0xFE, 0xFE, destination, 0xE1, command, 0xFD];

    private static ProfileConfig Profile() => new()
    {
        Id = 1, Label = "Fake IPC integration", Enabled = true, PhysicalPort = "COM3",
        VirtualPort = "COM31", CommanderPort = "COM21", Adapter = AdapterKind.Passthrough, Framing = FramingMode.Civ
    };

    private static SharedRadioCoordinatorOptions Options() => new(
        FramingMode.Civ, 0xE0, 0xE1, 32, 8192, 8, 4096, 16, 8, 4096, 50);

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class FakeClock : IMonotonicClock
    {
        private long now;
        public long NowMs { get => Interlocked.Read(ref now); set => Interlocked.Exchange(ref now, value); }
    }

    private sealed class Context
    {
        public FakeClock Clock { get; } = new();
        public FakeEndpoint Cat { get; } = new();
        public ComConCommandSessions Sessions { get; }
        public ComConCommandSession Session { get; }
        public ComConCommandNotifications Tracker { get; }
        public ComConCommandEndpoint Endpoint { get; }

        public Context(int maximumCount = 32)
        {
            var rig = Guid.NewGuid().ToString("D");
            Sessions = new(Clock, 100, [new(rig, 1)]);
            Session = Sessions.AcceptCatHandshake(JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion, rigProfileId = rig
            })).Session!;
            Sessions.ConfirmCatAcceptanceSent(Session);
            Sessions.BindControl(JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = Session.SessionId
            }), 1);
            Sessions.ConfirmControlAcceptanceSent(Session);
            Sessions.AcceptCatReady(Session, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = Session.SessionId
            }));
            Tracker = new(Sessions, Session, Clock, maximumCount, 16384, 100, 20);
            Endpoint = new(Cat, Sessions, Session, Tracker);
        }

        public SharedRadioCoordinator Coordinator(SharedRadioCoordinatorOptions options) =>
            new(options, Clock, Endpoint.OnDiagnostic);

        public ComConNotificationTransmission SendNotification()
        {
            var token = Tracker.BeginTransmission();
            Assert.IsNotNull(token);
            Tracker.ConfirmTransmission(token);
            return token;
        }

        public async Task<ComConNotificationTransmission> NextNotification()
        {
            ComConNotificationTransmission? token = null;
            await WaitUntil(() => (token = Tracker.BeginTransmission()) is not null);
            Tracker.ConfirmTransmission(token!);
            return token!;
        }

        public byte[] AckBytes(long sequence) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = Session.SessionId,
            notificationSequence = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });

        public void Ack(ComConNotificationTransmission token) => Tracker.Acknowledge(AckBytes(token.Sequence));
    }

    private sealed class FakeFactory : IByteBridgeEndpointFactory
    {
        public FakeEndpoint Legacy { get; } = new();
        public FakeEndpoint Radio { get; } = new();
        public int OpenCount { get; private set; }
        public bool OpenFailure { get; init; }
        public string RadioPort { get; init; } = "COM3";
        public ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName, SerialSettings settings, CancellationToken cancellationToken)
        {
            if (OpenFailure) throw new IOException("Injected host startup failure.");
            OpenCount++;
            return ValueTask.FromResult<IByteBridgeEndpoint>(portName == RadioPort ? Radio : Legacy);
        }
    }

    private sealed class FakeEndpoint : IBufferedByteBridgeEndpoint
    {
        private readonly Channel<byte[]> incoming = Channel.CreateBounded<byte[]>(32);
        public int BufferedInputBytes => incoming.Reader.TryPeek(out var bytes) ? bytes.Length : 0;
        private readonly Channel<byte[]> written = Channel.CreateBounded<byte[]>(32);
        private int disposed;
        private int writeCount;
        private readonly TaskCompletionSource writeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public int WriteCount => Volatile.Read(ref writeCount);
        public Exception? WriteFailure { get; set; }
        public Exception? DisposeFailure { get; set; }
        public bool HoldWrites { get; set; }
        public void ReleaseWrites() => writeRelease.TrySetResult();
        public ValueTask Send(byte[] bytes) => incoming.Writer.WriteAsync(bytes);
        public async Task<byte[]> Receive() =>
            await written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var bytes = await incoming.Reader.ReadAsync(cancellationToken);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref writeCount);
            if (WriteFailure is { } failure) throw failure;
            if (HoldWrites) await writeRelease.Task.WaitAsync(cancellationToken);
            await written.Writer.WriteAsync(buffer.ToArray(), cancellationToken);
        }
        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref disposed, 1);
            incoming.Writer.TryComplete();
            written.Writer.TryComplete();
            if (DisposeFailure is { } failure)
                return ValueTask.FromException(failure);
            return ValueTask.CompletedTask;
        }
    }
}
