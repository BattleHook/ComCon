using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorRetains32NotificationsThenIsolatesOverflowAndReconnectsFresh()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var clock = new FakeClock { NowMs = 0 };
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        var plans = SupervisorPlans(factories);
        var failures = new ConcurrentQueue<(int Profile, Exception Failure)>();
        await using var supervisor = new ComConOfflineSupervisor(
            ConfigurationJson.Deserialize(ConfigurationJson.Serialize(settings)), plans, clock,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (profile, failure) => failures.Enqueue((profile, failure)), (_, _) => { });
        await supervisor.StartAsync(timeout.Token);

        using var saturated = await MultiProfilePeer.Open(
            supervisor.Profiles[0].Listener, settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var other = await MultiProfilePeer.Open(
            supervisor.Profiles[1].Listener, settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);

        var firstCommand = Civ(0x94, 1);
        await saturated.Cat.WriteAsync(firstCommand, timeout.Token);
        CollectionAssert.AreEqual(firstCommand, await factories[0].Radio.Receive());
        var firstNotification = await ComConCommandProtocol.ReadEnvelopeAsync(saturated.Control, timeout.Token);
        AssertOutcome(firstNotification, "record.outcome", "1", "WRITE_SUCCEEDED");
        Assert.AreEqual("1", firstNotification.GetProperty("notificationSequence").GetString());
        var oldSessionId = firstNotification.GetProperty("sessionId").GetString();

        for (byte record = 2; record <= 32; record++)
        {
            var command = Civ(0x94, record);
            await saturated.Cat.WriteAsync(command, timeout.Token);
            CollectionAssert.AreEqual(command, await factories[0].Radio.Receive());
        }

        Assert.AreEqual(32, factories[0].Radio.WriteCount);
        Assert.AreEqual(0, clock.NowMs, "The injected clock must remain frozen during saturation.");
        Assert.IsFalse(failures.Any(failure => failure.Profile == 1),
            "The session must remain active after exactly 32 retained notifications.");

        var otherCommand = Civ(0x94, 40);
        await other.Cat.WriteAsync(otherCommand, timeout.Token);
        CollectionAssert.AreEqual(otherCommand, await factories[1].Radio.Receive());
        var otherNotification = await ComConCommandProtocol.ReadEnvelopeAsync(other.Control, timeout.Token);
        AssertOutcome(otherNotification, "record.outcome", "1", "WRITE_SUCCEEDED");
        await other.Ack("1", timeout.Token);

        var overflowCommand = Civ(0x94, 33);
        await saturated.Cat.WriteAsync(overflowCommand, timeout.Token);
        CollectionAssert.AreEqual(overflowCommand, await factories[0].Radio.Receive());
        Assert.AreEqual(33, factories[0].Radio.WriteCount);
        await WaitUntil(() => failures.Any(failure => failure.Profile == 1));
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles[0].Host.State);
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles[1].Host.State);

        var legacyCommand = Civ(0x94, 41);
        await factories[0].Legacy.Send(legacyCommand);
        CollectionAssert.AreEqual(legacyCommand, await factories[0].Radio.Receive());

        saturated.Dispose();
        using var reconnect = await MultiProfilePeer.Open(
            supervisor.Profiles[0].Listener, settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        var freshCommand = Civ(0x94, 42);
        await reconnect.Cat.WriteAsync(freshCommand, timeout.Token);
        CollectionAssert.AreEqual(freshCommand, await factories[0].Radio.Receive());
        var freshNotification = await ComConCommandProtocol.ReadEnvelopeAsync(reconnect.Control, timeout.Token);
        AssertOutcome(freshNotification, "record.outcome", "1", "WRITE_SUCCEEDED");
        Assert.AreEqual("1", freshNotification.GetProperty("notificationSequence").GetString());
        Assert.AreNotEqual(oldSessionId, freshNotification.GetProperty("sessionId").GetString());
        await reconnect.Ack("1", timeout.Token);

        var otherCommandAfterOverflow = Civ(0x94, 43);
        await other.Cat.WriteAsync(otherCommandAfterOverflow, timeout.Token);
        CollectionAssert.AreEqual(otherCommandAfterOverflow, await factories[1].Radio.Receive());
        var otherAfterOverflow = await ComConCommandProtocol.ReadEnvelopeAsync(other.Control, timeout.Token);
        AssertOutcome(otherAfterOverflow, "record.outcome", "2", "WRITE_SUCCEEDED");
        await other.Ack("2", timeout.Token);
        Assert.AreEqual(2, factories[0].OpenCount);
        Assert.AreEqual(2, factories[1].OpenCount);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorReportsNotificationExpiryAndKeepsLegacyAndOtherProfileRunning()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var clock = new FakeClock { NowMs = 0 };
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        var diagnostics = new ConcurrentQueue<(int Profile, SharedRadioDiagnostic Diagnostic)>();
        await using var supervisor = new ComConOfflineSupervisor(
            ConfigurationJson.Deserialize(ConfigurationJson.Serialize(settings)), SupervisorPlans(factories), clock,
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (_, _) => { },
            (profile, diagnostic) => diagnostics.Enqueue((profile, diagnostic)));
        await supervisor.StartAsync(timeout.Token);

        using var expiring = await MultiProfilePeer.Open(
            supervisor.Profiles[0].Listener, settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var other = await MultiProfilePeer.Open(
            supervisor.Profiles[1].Listener, settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);

        var command = Civ(0x94, 50);
        await expiring.Cat.WriteAsync(command, timeout.Token);
        CollectionAssert.AreEqual(command, await factories[0].Radio.Receive());
        var notification = await ComConCommandProtocol.ReadEnvelopeAsync(expiring.Control, timeout.Token);
        AssertOutcome(notification, "record.outcome", "1", "WRITE_SUCCEEDED");
        Assert.AreEqual(0, clock.NowMs);

        clock.NowMs = 2000;
        await WaitUntil(() => diagnostics.Any(entry =>
            entry.Profile == 1 &&
            entry.Diagnostic.Kind == SharedRadioDiagnosticKind.ClientDisconnected &&
            entry.Diagnostic.Message.Contains(
                "receipt was not acknowledged before its deadline", StringComparison.OrdinalIgnoreCase)));

        var legacyCommand = Civ(0x94, 51);
        await factories[0].Legacy.Send(legacyCommand);
        CollectionAssert.AreEqual(legacyCommand, await factories[0].Radio.Receive());
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles[0].Host.State);

        var otherCommand = Civ(0x94, 52);
        await other.Cat.WriteAsync(otherCommand, timeout.Token);
        CollectionAssert.AreEqual(otherCommand, await factories[1].Radio.Receive());
        var otherNotification = await ComConCommandProtocol.ReadEnvelopeAsync(other.Control, timeout.Token);
        AssertOutcome(otherNotification, "record.outcome", "1", "WRITE_SUCCEEDED");
        await other.Ack("1", timeout.Token);
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles[1].Host.State);
        Assert.AreEqual(2000, clock.NowMs, "No wall-clock progress is required to trigger expiry.");
    }
}
