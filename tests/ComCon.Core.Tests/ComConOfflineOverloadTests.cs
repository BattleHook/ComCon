using System.Collections.Concurrent;
using System.Runtime.Versioning;
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
    public async Task SupervisorIpcQueueOverloadRejectsExactRecordsWithoutReplayAndIsolatesOtherProfile(bool expireQueued)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        var plans = SupervisorPlans(factories);
        plans[0] = plans[0] with
        {
            CoordinatorOptions = Options() with { TxQueueRecordsPerClient = 2, TxQueueRecordsPerProfile = 4 }
        };
        var failures = new ConcurrentQueue<(int Id, Exception Error)>();
        var clock = new FakeClock();
        await using var supervisor = new ComConOfflineSupervisor(settings, plans, clock,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (id, error) => failures.Enqueue((id, error)), (_, _) => { });
        await supervisor.StartAsync(timeout.Token);
        using var first = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var second = await MultiProfilePeer.Open(supervisor.Profiles[1].Listener,
            settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);
        factories[0].Radio.HoldWrites = true;
        try
        {
            var records = Enumerable.Range(1, 5).Select(id => Civ(0x94, (byte)id)).ToArray();
            await first.Cat.WriteAsync(records.SelectMany(record => record).ToArray(), timeout.Token);
            await WaitUntil(() => factories[0].Radio.WriteCount == 1);
            for (var record = 3; record <= 5; record++)
            {
                var rejected = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
                AssertOutcome(rejected, "record.rejected", record.ToString(), "NOT_SENT");
                await first.Ack(rejected.GetProperty("notificationSequence").GetString()!, timeout.Token);
            }
            await second.Cat.WriteAsync(Civ(0x94, 0x21), timeout.Token);
            CollectionAssert.AreEqual(Civ(0x94, 0x21), await factories[1].Radio.Receive());
            var independent = await ComConCommandProtocol.ReadEnvelopeAsync(second.Control, timeout.Token);
            AssertOutcome(independent, "record.outcome", "1", "WRITE_SUCCEEDED");
            await second.Ack("1", timeout.Token);
            Assert.AreEqual(1, factories[0].Radio.WriteCount);
            if (expireQueued) clock.NowMs = 50;
            factories[0].Radio.ReleaseWrites();
            for (var record = 1; record <= 2; record++)
            {
                var expired = expireQueued && record == 2;
                if (!expired)
                    CollectionAssert.AreEqual(records[record - 1], await factories[0].Radio.Receive());
                var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
                AssertOutcome(outcome, expired ? "record.rejected" : "record.outcome",
                    record.ToString(), expired ? "NOT_SENT" : "WRITE_SUCCEEDED");
                await first.Ack(outcome.GetProperty("notificationSequence").GetString()!, timeout.Token);
            }
            await first.Cat.WriteAsync(Civ(0x94, 6), timeout.Token);
            CollectionAssert.AreEqual(Civ(0x94, 6), await factories[0].Radio.Receive());
            var recovered = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
            AssertOutcome(recovered, "record.outcome", "6", "WRITE_SUCCEEDED");
            await first.Ack(recovered.GetProperty("notificationSequence").GetString()!, timeout.Token);
            Assert.AreEqual(expireQueued ? 2 : 3, factories[0].Radio.WriteCount);
            Assert.IsEmpty(failures);
            Assert.IsTrue(supervisor.Profiles.All(runtime => runtime.Host.State == SharedRadioHostState.Running));
        }
        finally { factories[0].Radio.ReleaseWrites(); }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SupervisorAckDeadlineExpiresAtExactBoundaryAndReconnectDoesNotReplayOldSession()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        var clock = new FakeClock();
        var failures = new ConcurrentQueue<(int Id, Exception Error)>();
        var diagnostics = new ConcurrentQueue<(int Id, SharedRadioDiagnostic Diagnostic)>();
        await using var supervisor = new ComConOfflineSupervisor(settings, SupervisorPlans(factories), clock,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (id, error) => failures.Enqueue((id, error)), (id, diagnostic) => diagnostics.Enqueue((id, diagnostic)));
        await supervisor.StartAsync(timeout.Token);
        using var first = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var second = await MultiProfilePeer.Open(supervisor.Profiles[1].Listener,
            settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);
        await first.Cat.WriteAsync(Civ(0x94, 0x11), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 0x11), await factories[0].Radio.Receive());
        var original = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
        AssertOutcome(original, "record.outcome", "1", "WRITE_SUCCEEDED");
        clock.NowMs = 1999;
        await factories[0].Legacy.Send(Civ(0x94, 0x12));
        CollectionAssert.AreEqual(Civ(0x94, 0x12), await factories[0].Radio.Receive());
        Assert.IsFalse(failures.Any(entry => entry.Id == 1));
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles[0].Host.State);
        clock.NowMs = 2000;
        await WaitUntil(() => failures.Any(entry => entry.Id == 1));
        await factories[0].Legacy.Send(Civ(0x94, 0x14));
        CollectionAssert.AreEqual(Civ(0x94, 0x14), await factories[0].Radio.Receive());
        Assert.IsTrue(failures.Any(entry => entry.Id == 1 &&
            entry.Error is ComConCommandProtocolException { ErrorCode: "NOTIFICATION_ACK_TIMEOUT" }) ||
            diagnostics.Any(entry => entry.Id == 1 &&
                entry.Diagnostic.Kind == SharedRadioDiagnosticKind.ClientDisconnected &&
                entry.Diagnostic.Message.Contains(
                    "Notification receipt was not acknowledged before its deadline.", StringComparison.Ordinal)));
        await second.Cat.WriteAsync(Civ(0x94, 0x22), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 0x22), await factories[1].Radio.Receive());
        var other = await ComConCommandProtocol.ReadEnvelopeAsync(second.Control, timeout.Token);
        await second.Ack(other.GetProperty("notificationSequence").GetString()!, timeout.Token);
        using var fresh = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        await fresh.Cat.WriteAsync(Civ(0x94, 0x13), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 0x13), await factories[0].Radio.Receive());
        var newOutcome = await ComConCommandProtocol.ReadEnvelopeAsync(fresh.Control, timeout.Token);
        AssertOutcome(newOutcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        Assert.AreEqual("1", newOutcome.GetProperty("notificationSequence").GetString());
        Assert.AreNotEqual(original.GetProperty("sessionId").GetString(), newOutcome.GetProperty("sessionId").GetString());
        await fresh.Ack("1", timeout.Token);
        Assert.AreEqual(4, factories[0].Radio.WriteCount);
        Assert.IsFalse(failures.Any(entry => entry.Id == 2));
        Assert.IsTrue(factories.All(factory => factory.OpenCount == 2));
        Assert.IsTrue(supervisor.Profiles.All(runtime => runtime.Host.State == SharedRadioHostState.Running));
    }
}
