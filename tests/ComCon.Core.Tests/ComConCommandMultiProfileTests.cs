using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using ComCon.Core.Ipc;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task FourProfileListenersRouteSimultaneouslyAndIsolateReconnectCancellationAndRadioFault()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = new FakeClock();
        var rigs = Enumerable.Range(1, 4).Select(_ => Guid.NewGuid().ToString("D")).ToArray();
        var sessions = new ComConCommandSessions(clock, 5000,
            rigs.Select((rig, index) => new ComConRigProfileMapping(rig, index + 1)));
        var profiles = new List<MultiProfileFixture>();
        var peers = new List<MultiProfilePeer>();
        try
        {
            for (var id = 1; id <= 4; id++)
            {
                var fixture = new MultiProfileFixture(id, rigs[id - 1], sessions, clock, timeout.Token);
                profiles.Add(fixture);
                await fixture.Start();
            }
            foreach (var fixture in profiles)
                peers.Add(await fixture.ConnectReady(timeout.Token));
            var active = new List<PipeLifecycleTestSession>();
            foreach (var fixture in profiles)
                active.Add(await fixture.Started.Reader.ReadAsync(timeout.Token));
            await Task.WhenAll(profiles.Select((fixture, index) =>
                Exchange(fixture, peers[index], active[index], (byte)(index + 1), "1")));

            // First-profile protocol failure must not interrupt the other three actors.
            await peers[0].Ack("999", timeout.Token);
            await Assert.ThrowsAsync<IOException>(() => active[0].Lifecycle.ControlCompletion.WaitAsync(timeout.Token));
            active[0].Finish.TrySetResult();
            await WaitUntil(() => profiles[0].Errors.Count == 1);
            await Task.WhenAll(profiles.Skip(1).Select((fixture, offset) =>
                Exchange(fixture, peers[offset + 1], active[offset + 1], (byte)(offset + 10), "2")));
            peers[0].Dispose();
            peers[0] = await profiles[0].ConnectReady(timeout.Token, active[0].Session.SessionId);
            var old = active[0];
            active[0] = await profiles[0].Started.Reader.ReadAsync(timeout.Token);
            Assert.AreNotEqual(old.Session.SessionId, active[0].Session.SessionId);
            Assert.AreSame(old.Host, active[0].Host);
            await old.Pipes.DisposeAsync();
            Assert.IsTrue(sessions.CanRelayCat(active[0].Session));
            await Exchange(profiles[0], peers[0], active[0], 20, "1");

            // Stopping profile two's listener detaches IPC, not its continuing host.
            profiles[1].Stop.Cancel();
            await profiles[1].Completion.WaitAsync(timeout.Token);
            Assert.AreEqual(SharedRadioHostState.Running, profiles[1].Host.State);
            Assert.AreEqual(ComConCommandSessionState.Ended, active[1].Session.State);
            await profiles[1].Factory.Legacy.Send(Civ(0x94, 30));
            CollectionAssert.AreEqual(Civ(0x94, 30), await profiles[1].Factory.Radio.Receive());
            await Exchange(profiles[0], peers[0], active[0], 21, "2");
            await Task.WhenAll(new[] { 2, 3 }.Select(index =>
                Exchange(profiles[index], peers[index], active[index], (byte)(index + 30), "3")));

            // An uncertain write faults only profile three, with no implicit recovery.
            profiles[2].Factory.Radio.WriteFailure = new IOException("Injected profile-three uncertain write.");
            await peers[2].Cat.WriteAsync(Civ(0x94, 40), timeout.Token);
            var uncertain = await ComConCommandProtocol.ReadEnvelopeAsync(peers[2].Control, timeout.Token);
            AssertOutcome(uncertain, "record.outcome", "4", "UNKNOWN_DELIVERY");
            await peers[2].Ack(uncertain.GetProperty("notificationSequence").GetString()!, timeout.Token);
            await WaitUntil(() => profiles[2].Host.State == SharedRadioHostState.Faulted);
            await Task.WhenAll(new[] { 0, 3 }.Select(index =>
                Exchange(profiles[index], peers[index], active[index], (byte)(index + 50), index == 0 ? "3" : "4")));

            profiles[3].Factory.Radio.HoldWrites = true;
            try
            {
                await peers[3].Cat.WriteAsync(Civ(0x94, 60), timeout.Token);
                await WaitUntil(() => profiles[3].Factory.Radio.WriteCount == 5);
                await Exchange(profiles[0], peers[0], active[0], 61, "4");
                await profiles[1].Factory.Legacy.Send(Civ(0x94, 62));
                CollectionAssert.AreEqual(Civ(0x94, 62), await profiles[1].Factory.Radio.Receive());
            }
            finally { profiles[3].Factory.Radio.ReleaseWrites(); }
            CollectionAssert.AreEqual(Civ(0x94, 60), await profiles[3].Factory.Radio.Receive());
            var settled = await ComConCommandProtocol.ReadEnvelopeAsync(peers[3].Control, timeout.Token);
            AssertOutcome(settled, "record.outcome", "5", "WRITE_SUCCEEDED");
            await peers[3].Ack(settled.GetProperty("notificationSequence").GetString()!, timeout.Token);
            foreach (var fixture in profiles)
            {
                Assert.AreEqual(2, fixture.Factory.OpenCount);
                Assert.IsFalse(fixture.Factory.Legacy.Disposed);
                Assert.IsFalse(fixture.Factory.Radio.Disposed);
            }
            Assert.HasCount(1, profiles[0].Errors);
            Assert.HasCount(0, profiles[1].Errors);
            Assert.HasCount(0, profiles[3].Errors);
        }
        finally
        {
            foreach (var fixture in profiles) fixture.Stop.Cancel();
            foreach (var peer in peers) peer.Dispose();
            try { await Task.WhenAll(profiles.Select(fixture => fixture.Completion)); }
            finally
            {
                foreach (var fixture in profiles) await fixture.DisposeAsync();
            }
        }

        async Task Exchange(
            MultiProfileFixture fixture, MultiProfilePeer peer, PipeLifecycleTestSession active,
            byte commandByte, string sequence)
        {
            var command = Civ(0x94, commandByte);
            await peer.Cat.WriteAsync(command, timeout.Token);
            CollectionAssert.AreEqual(command, await fixture.Factory.Radio.Receive());
            var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            AssertOutcome(outcome, "record.outcome", sequence, "WRITE_SUCCEEDED");
            Assert.AreEqual(fixture.ProfileId, outcome.GetProperty("profileId").GetInt32());
            Assert.AreEqual(active.Session.SessionId, outcome.GetProperty("sessionId").GetString());
            await peer.Ack(outcome.GetProperty("notificationSequence").GetString()!, timeout.Token);
            var reply = Civ(0xE1, commandByte);
            await fixture.Factory.Radio.Send(reply);
            var buffer = new byte[reply.Length];
            await peer.Cat.ReadExactlyAsync(buffer, timeout.Token);
            CollectionAssert.AreEqual(reply, buffer);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ProfileListenerStartupCollisionDoesNotStopOtherProfileOrContinuingHosts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new FakeClock();
        var rigs = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
        var sessions = new ComConCommandSessions(clock, 5000, [new(rigs[0], 1), new(rigs[1], 4)]);
        await using var first = new MultiProfileFixture(1, rigs[0], sessions, clock, timeout.Token);
        await using var fourth = new MultiProfileFixture(4, rigs[1], sessions, clock, timeout.Token);
        await first.Start();
        await fourth.Start();
        try
        {
            var collision = new ComConCommandTestPipeListener(first.Listener.CatPipeName, sessions,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), _ => { }, 1);
            await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() =>
                collision.RunAsync((_, _) => Task.CompletedTask, timeout.Token));
            using var peer = await fourth.ConnectReady(timeout.Token);
            var active = await fourth.Started.Reader.ReadAsync(timeout.Token);
            await peer.Cat.WriteAsync(Civ(0x94, 70), timeout.Token);
            CollectionAssert.AreEqual(Civ(0x94, 70), await fourth.Factory.Radio.Receive());
            var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
            Assert.AreEqual(4, outcome.GetProperty("profileId").GetInt32());
            Assert.AreEqual(active.Session.SessionId, outcome.GetProperty("sessionId").GetString());
            await peer.Ack(outcome.GetProperty("notificationSequence").GetString()!, timeout.Token);
            await first.Factory.Legacy.Send(Civ(0x94, 71));
            CollectionAssert.AreEqual(Civ(0x94, 71), await first.Factory.Radio.Receive());
            Assert.AreEqual(SharedRadioHostState.Running, first.Host.State);
            Assert.AreEqual(SharedRadioHostState.Running, fourth.Host.State);
            Assert.HasCount(0, first.Errors);
            Assert.HasCount(0, fourth.Errors);
        }
        finally
        {
            first.Stop.Cancel();
            fourth.Stop.Cancel();
            await Task.WhenAll(first.Completion, fourth.Completion);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ProfilePinnedHandshakeAndCrossProfileBindCannotReserveOrStealOtherProfile()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var clock = new FakeClock();
        var rigs = new[] { Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D") };
        var sessions = new ComConCommandSessions(clock, 5000, [new(rigs[0], 1), new(rigs[1], 2)]);
        await using var first = new MultiProfileFixture(1, rigs[0], sessions, clock, timeout.Token);
        await using var second = new MultiProfileFixture(2, rigs[1], sessions, clock, timeout.Token);
        await first.Start();
        await second.Start();
        try
        {
            using (var wrong = await MultiProfilePeer.Connect(first.Listener.CatPipeName, timeout.Token))
            {
                using var writer = new SemaphoreSlim(1, 1);
                await ComConCommandProtocol.WriteEnvelopeAsync(wrong, MultiProfilePeer.Handshake(rigs[1]), writer, timeout.Token);
                var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(wrong, timeout.Token);
                Assert.AreEqual("PROFILE_SESSION_MISMATCH", rejection.GetProperty("errorCode").GetString());
                Assert.IsFalse(rejection.TryGetProperty("sessionId", out _));
            }
            await WaitUntil(() => first.Errors.Count == 1);
            using var secondPeer = await second.ConnectReady(timeout.Token);
            var secondActive = await second.Started.Reader.ReadAsync(timeout.Token);
            using var firstPeer = await first.ConnectReady(timeout.Token, secondActive.Session.SessionId);
            var firstActive = await first.Started.Reader.ReadAsync(timeout.Token);
            Assert.IsTrue(sessions.CanRelayCat(firstActive.Session));
            Assert.IsTrue(sessions.CanRelayCat(secondActive.Session));
            Assert.AreNotEqual(firstActive.Session.SessionId, secondActive.Session.SessionId);
            Assert.AreEqual(2, first.Factory.OpenCount);
            Assert.AreEqual(2, second.Factory.OpenCount);
        }
        finally
        {
            first.Stop.Cancel();
            second.Stop.Cancel();
            await Task.WhenAll(first.Completion, second.Completion);
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class MultiProfileFixture : IAsyncDisposable
    {
        private readonly string rig;
        private readonly ComConCommandSessions sessions;
        private readonly FakeClock clock;
        public int ProfileId { get; }
        public FakeFactory Factory { get; }
        public SharedRadioHost Host { get; }
        public ComConCommandTestPipeListener Listener { get; }
        public CancellationTokenSource Stop { get; }
        public ConcurrentQueue<Exception> Errors { get; } = new();
        public Channel<PipeLifecycleTestSession> Started { get; } = Channel.CreateBounded<PipeLifecycleTestSession>(4);
        public Task Completion { get; private set; } = Task.CompletedTask;

        public MultiProfileFixture(
            int profileId, string rig, ComConCommandSessions sessions, FakeClock clock, CancellationToken stop)
        {
            ProfileId = profileId;
            this.rig = rig;
            this.sessions = sessions;
            this.clock = clock;
            Factory = new() { RadioPort = $"COM{100 + profileId}" };
            Stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            Host = new(Factory, clock, _ => { });
            Listener = new("ComConTest_Multi_" + Guid.NewGuid().ToString("N"), sessions,
                TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), Errors.Enqueue, profileId);
        }

        public async Task Start()
        {
            await Host.StartAsync(Profile() with
            {
                Id = ProfileId,
                PhysicalPort = Factory.RadioPort,
                VirtualPort = $"COM{200 + ProfileId}",
                CommanderPort = $"COM{300 + ProfileId}"
            }, Options(), Stop.Token);
            Completion = Listener.RunAsync(async (pipes, token) =>
            {
                var tracker = new ComConCommandNotifications(sessions, pipes.Session, clock, 32, 32768, 2000, 500);
                var endpoint = new ComConCommandEndpoint(pipes.CreateCatEndpoint(), sessions, pipes.Session, tracker);
                var control = new ComConCommandControlLoop(pipes.Control, tracker, 8, TimeSpan.FromMilliseconds(2));
                await using var lifecycle = ComConCommandLifecycle.ForAttachedSession(
                    Host, endpoint, tracker, control, TimeSpan.FromSeconds(3));
                await lifecycle.StartAttachedAsync(token);
                var active = new PipeLifecycleTestSession(pipes, pipes.Session, Factory, Host, lifecycle, tracker);
                await Started.Writer.WriteAsync(active, token);
                var finished = active.Finish.Task.WaitAsync(token);
                if (await Task.WhenAny(finished, lifecycle.ControlCompletion) == lifecycle.ControlCompletion)
                    await lifecycle.ControlCompletion;
                await finished;
                await lifecycle.StopOrderlyAsync("Multi-profile IPC stopped.", token);
            }, Stop.Token);
        }

        public Task<MultiProfilePeer> ConnectReady(CancellationToken token, string? foreignSessionId = null) =>
            MultiProfilePeer.Open(Listener, rig, ProfileId, token, foreignSessionId);

        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            try { await Completion; }
            finally
            {
                try { await Host.DisposeAsync(); }
                finally { Stop.Dispose(); }
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class MultiProfilePeer(
        NamedPipeClientStream cat, NamedPipeClientStream control, int profileId, string sessionId) : IDisposable
    {
        public NamedPipeClientStream Cat { get; } = cat;
        public NamedPipeClientStream Control { get; } = control;
        public string SessionId { get; } = sessionId;
        private readonly SemaphoreSlim writer = new(1, 1);
        public ValueTask Ack(string sequence, CancellationToken token) =>
            ComConCommandProtocol.WriteEnvelopeAsync(Control, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "notification.ack",
                contractVersion = ComConCommandProtocol.ContractVersion,
                profileId,
                sessionId = SessionId,
                notificationSequence = sequence
            }), writer, token);

        public void Dispose() { Cat.Dispose(); Control.Dispose(); writer.Dispose(); }

        public static byte[] Handshake(string rig) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.handshake",
            contractVersion = ComConCommandProtocol.ContractVersion,
            rigProfileId = rig
        });

        public static async Task<NamedPipeClientStream> Connect(string name, CancellationToken token)
        {
            var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await pipe.ConnectAsync(token); return pipe; }
            catch { pipe.Dispose(); throw; }
        }

        public static async Task<MultiProfilePeer> Open(
            ComConCommandTestPipeListener listener, string rig, int profileId,
            CancellationToken token, string? foreignSessionId)
        {
            return await Open(listener.CatPipeName, listener.ControlPipeName, rig, profileId,
                token, foreignSessionId);
        }

        public static async Task<MultiProfilePeer> Open(
            string catPipeName, Func<int, string> controlPipeName, string rig, int profileId,
            CancellationToken token, string? foreignSessionId)
        {
            var cat = await Connect(catPipeName, token);
            NamedPipeClientStream? control = null;
            try
            {
                using var writer = new SemaphoreSlim(1, 1);
                await ComConCommandProtocol.WriteEnvelopeAsync(cat, Handshake(rig), writer, token);
                var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat, token);
                Assert.AreEqual(profileId, accepted.GetProperty("profileId").GetInt32());
                var sessionId = accepted.GetProperty("sessionId").GetString()!;
                if (foreignSessionId is not null)
                {
                    using var bad = await Connect(controlPipeName(profileId), token);
                    await ComConCommandProtocol.WriteEnvelopeAsync(bad, Bind(foreignSessionId), writer, token);
                    var rejected = await ComConCommandProtocol.ReadEnvelopeAsync(bad, token);
                    Assert.AreEqual("UNKNOWN_SESSION", rejected.GetProperty("errorCode").GetString());
                    Assert.IsFalse(rejected.TryGetProperty("sessionId", out _));
                }
                control = await Connect(controlPipeName(profileId), token);
                await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(sessionId), writer, token);
                Assert.AreEqual("control.bound",
                    (await ComConCommandProtocol.ReadEnvelopeAsync(control, token)).GetProperty("messageType").GetString());
                await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
                {
                    messageType = "cat.ready",
                    contractVersion = ComConCommandProtocol.ContractVersion,
                    profileId,
                    sessionId
                }), writer, token);
                return new(cat, control, profileId, sessionId);

                byte[] Bind(string id) => JsonSerializer.SerializeToUtf8Bytes(new
                {
                    messageType = "control.bind",
                    contractVersion = ComConCommandProtocol.ContractVersion,
                    profileId,
                    sessionId = id
                });
            }
            catch { cat.Dispose(); control?.Dispose(); throw; }
        }
    }
}
