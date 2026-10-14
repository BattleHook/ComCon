using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ComCon.Core.Ipc;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SupervisorOrderlyStopWaitsForEveryProfilesFinalAckBeforeClosingHosts()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var factories = Enumerable.Range(1, 4).Select(id => new FakeFactory { RadioPort = $"COM{100 + id}" }).ToArray();
        var settings = SupervisorSettings(4);
        await using var supervisor = NewSupervisor(settings, SupervisorPlans(factories));
        await supervisor.StartAsync(timeout.Token);
        var peers = new List<MultiProfilePeer>();
        try
        {
            foreach (var runtime in supervisor.Profiles)
                peers.Add(await MultiProfilePeer.Open(runtime.Listener,
                    settings.RigProfileMappings[runtime.Profile.Id - 1].RigProfileId,
                    runtime.Profile.Id, timeout.Token, null));
            factories[0].Radio.HoldWrites = true;
            await peers[0].Cat.WriteAsync(Civ(0x94, 1), timeout.Token);
            await WaitUntil(() => factories[0].Radio.WriteCount == 1);
            var stopping = supervisor.StopOrderlyAsync("Offline application stopped.", timeout.Token);
            var otherFinals = await Task.WhenAll(peers.Skip(1).Select(peer =>
                ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token).AsTask()));
            Assert.IsFalse(stopping.IsCompleted);
            Assert.IsTrue(factories.All(factory => !factory.Radio.Disposed));
            factories[0].Radio.ReleaseWrites();
            CollectionAssert.AreEqual(Civ(0x94, 1), await factories[0].Radio.Receive());
            var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peers[0].Control, timeout.Token);
            AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
            await peers[0].Ack("1", timeout.Token);
            var firstFinal = await ComConCommandProtocol.ReadEnvelopeAsync(peers[0].Control, timeout.Token);
            Assert.AreEqual("session.ended", firstFinal.GetProperty("event").GetString());
            Assert.AreEqual("2", firstFinal.GetProperty("notificationSequence").GetString());
            await peers[0].Ack("2", timeout.Token);
            for (var index = 1; index < 3; index++)
            {
                Assert.AreEqual("session.ended", otherFinals[index - 1].GetProperty("event").GetString());
                Assert.AreEqual(index + 1, otherFinals[index - 1].GetProperty("profileId").GetInt32());
                await peers[index].Ack(otherFinals[index - 1].GetProperty("notificationSequence").GetString()!, timeout.Token);
            }
            Assert.IsFalse(stopping.IsCompleted);
            await peers[3].Ack(otherFinals[2].GetProperty("notificationSequence").GetString()!, timeout.Token);
            await stopping;
            Assert.IsTrue(factories.All(factory => factory.Radio.Disposed && factory.Legacy.Disposed));
            Assert.IsTrue(supervisor.Profiles.All(runtime => runtime.Completion.IsCompletedSuccessfully));
            foreach (var runtime in supervisor.Profiles)
            {
                using var released = ComConReadOnlyPipeHost.CreateProtectedPipe(runtime.Listener.CatPipeName);
                var reservation = supervisor.Sessions.AcceptCatHandshake(
                    MultiProfilePeer.Handshake(settings.RigProfileMappings[runtime.Profile.Id - 1].RigProfileId));
                Assert.IsTrue(reservation.Accepted);
                supervisor.Sessions.EndSession(reservation.Session!, "Offline assertion completed.");
            }
        }
        finally
        {
            factories[0].Radio.ReleaseWrites();
            foreach (var peer in peers) peer.Dispose();
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SupervisorMissingFinalAckFailsExplicitlyAndCleansUpAllProfiles()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        var failures = new ConcurrentQueue<(int Id, Exception Error)>();
        await using var supervisor = new ComConOfflineSupervisor(settings, SupervisorPlans(factories), new FakeClock(),
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250),
            (id, error) => failures.Enqueue((id, error)), (_, _) => { });
        await supervisor.StartAsync(timeout.Token);
        using var first = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var second = await MultiProfilePeer.Open(supervisor.Profiles[1].Listener,
            settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);
        await WaitUntil(() => supervisor.Sessions.ActiveSessions.Count == 2 &&
            supervisor.Sessions.ActiveSessions.All(session => session.State == ComConCommandSessionState.Active));
        var stopping = supervisor.StopOrderlyAsync("Missing final ACK test.", timeout.Token);
        var firstFinal = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
        var secondFinal = await ComConCommandProtocol.ReadEnvelopeAsync(second.Control, timeout.Token);
        Assert.AreEqual("session.ended", firstFinal.GetProperty("event").GetString());
        await second.Ack(secondFinal.GetProperty("notificationSequence").GetString()!, timeout.Token);
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => stopping);
        Assert.IsTrue(failure.Flatten().InnerExceptions.Any(error => error is OperationCanceledException));
        Assert.IsTrue(failures.Any(entry => entry.Id == 1));
        Assert.IsFalse(failures.Any(entry => entry.Id == 2));
        Assert.IsTrue(factories.All(factory => factory.Radio.Disposed && factory.Legacy.Disposed));
        await supervisor.StopAsync();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SupervisorOrderlyStopCancelsPartialSetupAndIdleAdmissionWithoutFinalAck()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        await using var supervisor = NewSupervisor(settings, SupervisorPlans(factories));
        await supervisor.StartAsync(timeout.Token);
        using var pending = await MultiProfilePeer.Connect(supervisor.Profiles[0].Listener.CatPipeName, timeout.Token);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(pending,
            MultiProfilePeer.Handshake(settings.RigProfileMappings[0].RigProfileId), writer, timeout.Token);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(pending, timeout.Token);
        Assert.AreEqual("cat.accepted", accepted.GetProperty("messageType").GetString());
        await supervisor.StopOrderlyAsync("Stop during bind.", timeout.Token);
        Assert.IsTrue(factories.All(factory => factory.Radio.Disposed && factory.Legacy.Disposed));
        var reservation = supervisor.Sessions.AcceptCatHandshake(
            MultiProfilePeer.Handshake(settings.RigProfileMappings[0].RigProfileId));
        Assert.IsTrue(reservation.Accepted);
        supervisor.Sessions.EndSession(reservation.Session!, "Offline assertion completed.");
        supervisor.Profiles[0].Listener.StopAccepting();
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SupervisorOrderlyStopCallerCancellationReportsFailureAndAbortsSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factory = new FakeFactory { RadioPort = "COM101" };
        var settings = SupervisorSettings(1);
        await using var supervisor = NewSupervisor(settings, SupervisorPlans([factory]));
        await supervisor.StartAsync(timeout.Token);
        using var peer = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var cancellation = new CancellationTokenSource();
        var stopping = supervisor.StopOrderlyAsync("Caller cancellation.", cancellation.Token);
        var final = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        cancellation.Cancel();
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => stopping);
        Assert.IsTrue(failure.Flatten().InnerExceptions.Any(error => error is OperationCanceledException));
        Assert.IsTrue(factory.Radio.Disposed && factory.Legacy.Disposed);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => supervisor.StopOrderlyAsync("Already stopped."));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SupervisorFaultedProfileCannotReportOrderlySuccessWhileOtherProfileGetsFinalAck()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        await using var supervisor = NewSupervisor(settings, SupervisorPlans(factories));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            supervisor.StopOrderlyAsync("Not started."));
        await supervisor.StartAsync(timeout.Token);
        using var first = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var second = await MultiProfilePeer.Open(supervisor.Profiles[1].Listener,
            settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);
        factories[0].Radio.WriteFailure = new IOException("Uncertain offline delivery.");
        await first.Cat.WriteAsync(Civ(0x94, 1), timeout.Token);
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "UNKNOWN_DELIVERY");
        await first.Ack("1", timeout.Token);
        await WaitUntil(() => supervisor.Profiles[0].Host.State == SharedRadioHostState.Faulted);
        var stopping = supervisor.StopOrderlyAsync("Stop after profile fault.", timeout.Token);
        var final = await ComConCommandProtocol.ReadEnvelopeAsync(second.Control, timeout.Token);
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        await second.Ack(final.GetProperty("notificationSequence").GetString()!, timeout.Token);
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => stopping);
        Assert.IsTrue(failure.Flatten().InnerExceptions.Any(error =>
            error is IOException && error.Message.Contains("faulted during shutdown", StringComparison.Ordinal)));
        Assert.AreEqual(1, factories[0].Radio.WriteCount);
        Assert.IsTrue(factories.All(factory => factory.Radio.Disposed && factory.Legacy.Disposed));
    }
}
