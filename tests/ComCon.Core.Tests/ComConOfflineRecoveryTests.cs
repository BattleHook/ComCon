using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Text.Json;
using ComCon.Core.Models;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("windows")]
    public async Task ComConOfflineRecoveryNewSupervisorReusesNamesAndRejectsOldState(bool runtimeFault)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var settings = SupervisorSettings(1);
        var oldFactory = new FakeFactory { RadioPort = "COM101" };
        var factory = new RecoveryFactory(oldFactory, runtimeFault);
        var plans = SupervisorPlans([oldFactory]);
        plans[0] = plans[0] with
        {
            EndpointFactory = factory,
            CoordinatorOptions = Options() with { TxQueueRecordsPerClient = 2, TxQueueRecordsPerProfile = 4 }
        };
        var failures = new ConcurrentQueue<Exception>();
        ComConCommandNotifications? oldTracker = null;
        string oldSessionId;
        await using (var old = new ComConOfflineSupervisor(settings, plans, new FakeClock(),
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), (_, error) => failures.Enqueue(error), (_, _) => { })
        {
            WrapControlStream = (_, stream, tracker) => { oldTracker = tracker; return stream; }
        })
        {
            await old.StartAsync(timeout.Token);
            using var peer = await MultiProfilePeer.Open(old.Profiles[0].Listener,
                settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
            await peer.Cat.WriteAsync(Civ(0x94, 1), timeout.Token);
            CollectionAssert.AreEqual(Civ(0x94, 1), await oldFactory.Radio.Receive());
            var retained = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            oldSessionId = retained.GetProperty("sessionId").GetString()!;
            AssertOutcome(retained, "record.outcome", "1", "WRITE_SUCCEEDED");
            await peer.Cat.WriteAsync(new byte[] { 2, 3, 4 }.SelectMany(id => Civ(0x94, id)).ToArray(), timeout.Token);
            await factory.HeldWrite.Task.WaitAsync(timeout.Token);
            var rejected = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            AssertOutcome(rejected, "record.rejected", "4", "NOT_SENT");
            Assert.IsNotNull(oldTracker);
            Assert.AreEqual(2, oldTracker.PendingCount, "Both receipts are deliberately unacknowledged.");
            if (!runtimeFault)
            {
                await peer.Ack("99", timeout.Token);
                await WaitUntil(() => oldTracker.PendingCount == 0);
            }
            factory.Release.TrySetResult();
            if (runtimeFault)
                await WaitUntil(() => old.Profiles[0].Host.State == SharedRadioHostState.Faulted);
            else await WaitUntil(() => !failures.IsEmpty);
            await old.StopAsync();
            Assert.IsTrue(oldFactory.Radio.Disposed && oldFactory.Legacy.Disposed);
            Assert.IsTrue(old.Profiles[0].Completion.IsCompletedSuccessfully);
            Assert.AreEqual(0, oldTracker.PendingCount);
            Assert.AreEqual(0, oldTracker.RetainedPayloadBytes);
            using var catName = ComConReadOnlyPipeHost.CreateProtectedPipe(old.Profiles[0].Listener.CatPipeName);
            for (var id = 1; id <= 4; id++)
            {
                using var controlName = ComConReadOnlyPipeHost.CreateProtectedPipe(old.Profiles[0].Listener.ControlPipeName(id));
            }
        }
        var freshFactory = new FakeFactory { RadioPort = "COM101" };
        factory.Current = freshFactory;
        var clock = new FakeClock();
        var freshFailures = new ConcurrentQueue<Exception>();
        var freshTrackers = new ConcurrentQueue<ComConCommandNotifications>();
        await using var fresh = new ComConOfflineSupervisor(settings, plans, clock,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), (_, error) => freshFailures.Enqueue(error), (_, _) => { })
        {
            WrapControlStream = (_, stream, tracker) => { freshTrackers.Enqueue(tracker); return stream; }
        };
        await fresh.StartAsync(timeout.Token);
        Assert.AreEqual(2, freshFactory.OpenCount);
        Assert.AreEqual(0, freshFactory.Radio.WriteCount);
        Assert.AreEqual(0, freshFactory.Legacy.WriteCount);
        using (var cat = await MultiProfilePeer.Connect(fresh.Profiles[0].Listener.CatPipeName, timeout.Token))
        using (var writer = new SemaphoreSlim(1, 1))
        {
            await ComConCommandProtocol.WriteEnvelopeAsync(cat,
                MultiProfilePeer.Handshake(settings.RigProfileMappings[0].RigProfileId), writer, timeout.Token);
            var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
            var sessionId = accepted.GetProperty("sessionId").GetString()!;
            Assert.AreNotEqual(oldSessionId, sessionId);
            using var control = await MultiProfilePeer.Connect(fresh.Profiles[0].Listener.ControlPipeName(1), timeout.Token);
            await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId
            }), writer, timeout.Token);
            Assert.AreEqual("control.bound",
                (await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token)).GetProperty("messageType").GetString());
            Assert.IsEmpty(freshTrackers, "A bound control channel alone cannot create an active CAT runtime.");
            await freshFactory.Legacy.Send(Civ(0x94, 0x51));
            CollectionAssert.AreEqual(Civ(0x94, 0x51), await freshFactory.Radio.Receive());
            Assert.AreEqual(1, freshFactory.Radio.WriteCount, "Only explicit new input reached the new radio.");
            await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId
            }), writer, timeout.Token);
            await WaitUntil(() => freshTrackers.Count == 1);
            Assert.AreEqual(0, freshTrackers.Single().PendingCount);
            Assert.AreEqual(0L, freshTrackers.Single().TransmittedSequence);
            await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = 1, sessionId = oldSessionId, notificationSequence = "1"
            }), writer, timeout.Token);
            await WaitUntil(() => !freshFailures.IsEmpty);
            Assert.IsTrue(freshFailures.Any(error => error is ComConCommandProtocolException
                { ErrorCode: "PROFILE_SESSION_MISMATCH" }),
                string.Join(Environment.NewLine, freshFailures.Select(error => error.ToString())));
        }
        using var ready = await MultiProfilePeer.Open(fresh.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        await ready.Cat.WriteAsync(Civ(0x94, 0x52), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 0x52), await freshFactory.Radio.Receive());
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(ready.Control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        Assert.AreEqual("1", outcome.GetProperty("notificationSequence").GetString());
        Assert.AreNotEqual(oldSessionId, outcome.GetProperty("sessionId").GetString());
        await ready.Ack("1", timeout.Token);
        await WaitUntil(() => freshTrackers.Count == 2 && freshTrackers.Last().PendingCount == 0);
        clock.NowMs = 2000;
        await freshFactory.Legacy.Send(Civ(0x94, 0x53));
        CollectionAssert.AreEqual(Civ(0x94, 0x53), await freshFactory.Radio.Receive());
        Assert.AreEqual(3, freshFactory.Radio.WriteCount);
        Assert.AreEqual(0, freshFactory.Legacy.WriteCount);
        Assert.HasCount(1, freshFailures);
        Assert.AreEqual(SharedRadioHostState.Running, fresh.Profiles[0].Host.State);
        await fresh.StopAsync();
        Assert.IsTrue(freshFactory.Radio.Disposed && freshFactory.Legacy.Disposed);
    }

    private sealed class RecoveryFactory(FakeFactory initial, bool faultHeldWrite) : IByteBridgeEndpointFactory
    {
        private readonly FakeFactory original = initial;
        public FakeFactory Current { get; set; } = initial;
        public TaskCompletionSource HeldWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName, SerialSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await Current.OpenAsync(portName, settings, cancellationToken);
            return ReferenceEquals(Current, original) && portName == Current.RadioPort
                ? new RecoveryRadioEndpoint(endpoint, this, faultHeldWrite) : endpoint;
        }
    }

    private sealed class RecoveryRadioEndpoint(
        IByteBridgeEndpoint inner, RecoveryFactory factory, bool faultHeldWrite) : IByteBridgeEndpoint
    {
        private int writes;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, cancellationToken);
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref writes) == 2)
            {
                factory.HeldWrite.TrySetResult();
                await factory.Release.Task.WaitAsync(cancellationToken);
                if (faultHeldWrite) throw new IOException("Injected composed runtime uncertain write.");
            }
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
