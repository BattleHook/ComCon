using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Windows;

namespace ComCon.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ComConCommandTestPipeListenerTests
{
    [TestMethod]
    public async Task ReconnectCreatesFreshSessionAndKeepsEveryProtectedNameOwned()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var received = Channel.CreateBounded<ComConCommandSession>(4);
        var run = fixture.Listener.RunAsync(async (pipes, token) =>
        {
            await received.Writer.WriteAsync(pipes.Session, token);
            await using var endpoint = pipes.CreateCatEndpoint();
            var buffer = new byte[32];
            var count = await endpoint.ReadAsync(buffer, token);
            await endpoint.WriteAsync(buffer.AsMemory(0, count), token);
            var tracker = new ComConCommandNotifications(
                fixture.Sessions, pipes.Session, new StopwatchClock(), 8, 16384, 2000, 500);
            var control = new ComConCommandControlLoop(pipes.Control, tracker, 8, TimeSpan.FromMilliseconds(2));
            control.RequestOrderlyClose("Reconnect test finished.");
            await control.RunAsync(token);
        }, stop.Token);
        var firstId = "";
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var peer = await fixture.ConnectReady(attempt == 0 ? null : firstId);
            var session = await received.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            if (attempt == 0) firstId = session.SessionId;
            else Assert.AreNotEqual(firstId, session.SessionId);
            fixture.AssertNamesOwned();
            byte[] bytes = [0xFE, 0xFE, 0x94, 0xE1, 1, 0xFD];
            await peer.Cat.WriteAsync(bytes);
            var echo = new byte[bytes.Length];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await peer.Cat.ReadExactlyAsync(echo, timeout.Token);
            CollectionAssert.AreEqual(bytes, echo);
            var final = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
            using var writer = new SemaphoreSlim(1, 1);
            await ComConCommandProtocol.WriteEnvelopeAsync(peer.Control, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = session.SessionId,
                notificationSequence = final.GetProperty("notificationSequence").GetString()
            }), writer, timeout.Token);
            await WaitUntil(() => session.State == ComConCommandSessionState.Ended);
            fixture.AssertNamesOwned();
        }
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.HasCount(0, fixture.Errors);
        fixture.AssertNamesReleased();
    }

    [TestMethod]
    public async Task MalformedHandshakeIsReportedAndNextClientSucceeds()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fixture.Listener.RunAsync((_, _) =>
        {
            handled.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        using (var bad = await Connect(fixture.Listener.CatPipeName))
        {
            await bad.WriteAsync(new byte[] { 0, 0, 0, 1, (byte)'{' });
            var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(bad);
            Assert.AreEqual("INVALID_JSON", rejection.GetProperty("errorCode").GetString());
        }
        await WaitUntil(() => fixture.Errors.Count == 1);
        using var good = await fixture.ConnectReady();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsInstanceOfType<ComConCommandProtocolException>(fixture.Errors.Single());
    }

    [TestMethod]
    public async Task PartialSetupExpiresButIdleListenerDoesNotAndCanReconnect()
    {
        var fixture = new Fixture(setupTimeout: TimeSpan.FromMilliseconds(200));
        using var stop = new CancellationTokenSource();
        var handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fixture.Listener.RunAsync((_, _) =>
        {
            handled.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        await Task.Delay(350);
        Assert.IsFalse(run.IsCompleted);
        Assert.HasCount(0, fixture.Errors);
        fixture.AssertNamesOwned();
        using (var partial = await Connect(fixture.Listener.CatPipeName))
        {
            await partial.WriteAsync(new byte[] { 0, 0 });
            await WaitUntil(() => fixture.Errors.Count == 1);
        }
        using var good = await fixture.ConnectReady();
        await handled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsInstanceOfType<OperationCanceledException>(fixture.Errors.Single());
    }

    [TestMethod]
    public async Task SessionIoFailureEndsOwnerReportsFailureAndAllowsReconnect()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var sessions = Channel.CreateBounded<ComConCommandSession>(4);
        var calls = 0;
        var run = fixture.Listener.RunAsync(async (pipes, token) =>
        {
            await sessions.Writer.WriteAsync(pipes.Session, token);
            if (Interlocked.Increment(ref calls) == 1)
                throw new IOException("Injected session I/O failure.");
            await Task.Delay(Timeout.Infinite, token);
        }, stop.Token);
        using (var first = await fixture.ConnectReady())
        {
            var old = await sessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntil(() => fixture.Errors.Count == 1);
            Assert.AreEqual(ComConCommandSessionState.Ended, old.State);
        }
        using var next = await fixture.ConnectReady();
        var active = await sessions.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(fixture.Sessions.CanRelayCat(active));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(ComConCommandSessionState.Ended, active.State);
        Assert.AreEqual("Injected session I/O failure.", fixture.Errors.Single().Message);
    }

    [TestMethod]
    public async Task CooperativeStopCancelsBlockedCatReadAndReleasesAllNames()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var active = new TaskCompletionSource<ComConCommandSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fixture.Listener.RunAsync(async (pipes, token) =>
        {
            active.TrySetResult(pipes.Session);
            await using var endpoint = pipes.CreateCatEndpoint();
            await endpoint.ReadAsync(new byte[8], token);
        }, stop.Token);
        using var peer = await fixture.ConnectReady();
        var session = await active.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(ComConCommandSessionState.Ended, session.State);
        Assert.HasCount(0, fixture.Errors);
        fixture.AssertNamesReleased();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            fixture.Listener.RunAsync((_, _) => Task.CompletedTask, CancellationToken.None));
    }

    [TestMethod]
    public async Task BorrowedEndpointDisposalCancelsPendingReadWithoutClosingProtectedHandles()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fixture.Listener.RunAsync(async (pipes, token) =>
        {
            await using var endpoint = pipes.CreateCatEndpoint();
            var read = endpoint.ReadAsync(new byte[8], token).AsTask();
            Assert.IsFalse(read.IsCompleted);
            await endpoint.DisposeAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.IsFalse(pipes.Cat.CanRead);
            fixture.AssertNamesOwned();
            completed.TrySetResult();
        }, stop.Token);
        try
        {
            using var peer = await fixture.ConnectReady();
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.AssertNamesOwned();
        }
        finally
        {
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
        }
        Assert.HasCount(0, fixture.Errors);
        fixture.AssertNamesReleased();
    }

    [TestMethod]
    public async Task UncooperativeHandlerStopFailsExplicitlyWithoutReusingPipes()
    {
        var fixture = new Fixture(stopTimeout: TimeSpan.FromMilliseconds(100));
        using var stop = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = new TaskCompletionSource<ComConCommandSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = fixture.Listener.RunAsync((pipes, _) =>
        {
            active.TrySetResult(pipes.Session);
            return gate.Task;
        }, stop.Token);
        try
        {
            using var peer = await fixture.ConnectReady();
            var session = await active.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel();
            var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
            StringAssert.Contains(error.Message, "did not quiesce");
            Assert.AreEqual(ComConCommandSessionState.Ended, session.State);
            fixture.AssertNamesReleased();
            gate.TrySetException(new IOException("Late handler failure."));
            await WaitUntil(() => fixture.Errors.Count == 1);
            Assert.IsInstanceOfType<AggregateException>(fixture.Errors.Single());
        }
        finally { gate.TrySetResult(); }
    }

    [TestMethod]
    public async Task UnexpectedHandlerFailureFaultsListenerInsteadOfPretendingReconnectSucceeded()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var run = fixture.Listener.RunAsync((_, _) =>
            Task.FromException(new InvalidOperationException("Injected composition failure.")), stop.Token);
        using var peer = await fixture.ConnectReady();
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => run.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.AreEqual("Injected composition failure.", exception.Message);
        fixture.AssertNamesReleased();
    }

    [TestMethod]
    public async Task IdleStopAndStartupCollisionDoNotLeakPipeNames()
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var run = fixture.Listener.RunAsync((_, _) => Task.CompletedTask, stop.Token);
        var competitor = new ComConCommandTestPipeListener(fixture.Listener.CatPipeName,
            fixture.Sessions, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), _ => { });
        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() =>
            competitor.RunAsync((_, _) => Task.CompletedTask, CancellationToken.None));
        fixture.AssertNamesOwned();
        stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.AssertNamesReleased();
    }

    [TestMethod]
    [DataRow("handshake")]
    [DataRow("bind")]
    [DataRow("ready")]
    public async Task StopDuringEachSetupPhaseReleasesHandlesAndReservations(string phase)
    {
        var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var run = fixture.Listener.RunAsync((_, _) => throw new AssertFailedException("Setup unexpectedly activated."), stop.Token);
        using var cat = await Connect(fixture.Listener.CatPipeName);
        NamedPipeClientStream? control = null;
        try
        {
            if (phase != "handshake")
            {
                using var writer = new SemaphoreSlim(1, 1);
                await ComConCommandProtocol.WriteEnvelopeAsync(cat, fixture.Handshake(), writer);
                var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
                if (phase == "ready")
                {
                    control = await Connect(fixture.Listener.ControlPipeName(1));
                    await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                        profileId = 1, sessionId = accepted.GetProperty("sessionId").GetString()
                    }), writer);
                    await ComConCommandProtocol.ReadEnvelopeAsync(control);
                }
            }
            stop.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.HasCount(0, fixture.Errors);
            Assert.IsTrue(fixture.Sessions.AcceptCatHandshake(fixture.Handshake()).Accepted);
            fixture.AssertNamesReleased();
        }
        finally { control?.Dispose(); }
    }

    [TestMethod]
    public async Task PartialStartupCollisionClosesOnlyListenerCreatedHandles()
    {
        var fixture = new Fixture();
        using var owned = ComConReadOnlyPipeHost.CreateProtectedPipe(fixture.Listener.ControlPipeName(2));
        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() =>
            fixture.Listener.RunAsync((_, _) => Task.CompletedTask, CancellationToken.None));
        using var cat = ComConReadOnlyPipeHost.CreateProtectedPipe(fixture.Listener.CatPipeName);
        using var first = ComConReadOnlyPipeHost.CreateProtectedPipe(fixture.Listener.ControlPipeName(1));
        Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() =>
        {
            using var competitor = ComConReadOnlyPipeHost.CreateProtectedPipe(fixture.Listener.ControlPipeName(2));
        });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private static async Task<NamedPipeClientStream> Connect(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(2000); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private sealed class Peer(NamedPipeClientStream cat, NamedPipeClientStream control) : IDisposable
    {
        public NamedPipeClientStream Cat { get; } = cat;
        public NamedPipeClientStream Control { get; } = control;
        public void Dispose() { Cat.Dispose(); Control.Dispose(); }
    }

    private sealed class Fixture
    {
        private readonly string rig = Guid.NewGuid().ToString("D");
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public ComConCommandSessions Sessions { get; }
        public ComConCommandTestPipeListener Listener { get; }

        public Fixture(TimeSpan? setupTimeout = null, TimeSpan? stopTimeout = null)
        {
            Sessions = new(new StopwatchClock(), 5000, [new(rig, 1)]);
            Listener = new("ComConTest_Listener_" + Guid.NewGuid().ToString("N"), Sessions,
                setupTimeout ?? TimeSpan.FromSeconds(3), stopTimeout ?? TimeSpan.FromSeconds(1), Errors.Enqueue);
        }

        public byte[] Handshake() => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion,
            rigProfileId = rig
        });

        public async Task<Peer> ConnectReady(string? staleSessionId = null)
        {
            var cat = await Connect(Listener.CatPipeName);
            NamedPipeClientStream? control = null;
            try
            {
                using var writer = new SemaphoreSlim(1, 1);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ComConCommandProtocol.WriteEnvelopeAsync(cat, Handshake(), writer, timeout.Token);
                var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
                if (staleSessionId is not null)
                {
                    using var stale = await Connect(Listener.ControlPipeName(1));
                    await ComConCommandProtocol.WriteEnvelopeAsync(stale, JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                        profileId = 1, sessionId = staleSessionId
                    }), writer, timeout.Token);
                    var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(stale, timeout.Token);
                    Assert.AreEqual("UNKNOWN_SESSION", rejection.GetProperty("errorCode").GetString());
                    Assert.IsFalse(rejection.TryGetProperty("sessionId", out _));
                }
                control = await Connect(Listener.ControlPipeName(1));
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
                return new(cat, control);
            }
            catch { cat.Dispose(); control?.Dispose(); throw; }
        }

        public void AssertNamesOwned()
        {
            foreach (var name in Names())
                Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() =>
                {
                    using var pipe = ComConReadOnlyPipeHost.CreateProtectedPipe(name);
                });
        }

        public void AssertNamesReleased()
        {
            foreach (var name in Names())
            {
                using var pipe = ComConReadOnlyPipeHost.CreateProtectedPipe(name);
            }
        }

        private IEnumerable<string> Names() =>
            new[] { Listener.CatPipeName }.Concat(Enumerable.Range(1, 4).Select(Listener.ControlPipeName));
    }
}
