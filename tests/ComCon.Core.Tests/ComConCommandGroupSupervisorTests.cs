using System.Runtime.Versioning;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    public void ProductionPipeNamesAreFixedAndTestNameGuardRemainsIsolated()
    {
        var production = ComConCommandPipeNames.Production();
        Assert.IsTrue(production.IsProduction);
        Assert.AreEqual("ComConCmd_Cat", production.CatPipeName);
        Assert.AreEqual("ComConCmd_Cat_Control_Profile1", production.ControlPipeName(1));
        Assert.AreEqual("ComConCmd_Cat_Control_Profile4", production.ControlPipeName(4));
        Assert.ThrowsExactly<ArgumentException>(() =>
            ComConCommandPipeNames.IsolatedTest(production.CatPipeName));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => production.ControlPipeName(5));
    }

    [TestMethod]
    public void ProductionCoordinatorOptionsUseApprovedBoundsAndActivationIsEnabled()
    {
        var profile = Profile() with
        {
            Enabled = true,
            CommandIpcEnabled = true,
            Framing = FramingMode.Civ,
            RadioAddress = 0x7A,
            PhysicalPort = "COM101",
            VirtualPort = "COM102"
        };

        Assert.IsTrue(ComConProductionCoordinatorOptions.TryCreate(profile, out var options));
        Assert.IsNotNull(options);
        Assert.AreEqual(64, options.IngressChunksPerSource);
        Assert.AreEqual(1024, options.IngressBytesPerSource);
        Assert.AreEqual(16, options.TxQueueRecordsPerClient);
        Assert.AreEqual(1024, options.TxQueueBytesPerClient);
        Assert.AreEqual(32, options.TxQueueRecordsPerProfile);
        Assert.AreEqual(16, options.ClientRxQueueRecordsPerClient);
        Assert.AreEqual(1024, options.ClientRxQueueBytesPerClient);
        Assert.AreEqual(2500L, options.QueueWaitLimitMs);
        Assert.IsTrue(options.CivExchangeGateEnabled);
        Assert.IsTrue(ComConProductionCoordinatorOptions.ProductionActivationEnabled);
    }

    [TestMethod]
    public void ProductionGateRequiresRadioAddressAndDoesNotChangeAsciiScheduling()
    {
        var profile = Profile() with { Enabled = true, CommandIpcEnabled = true, Framing = FramingMode.Civ };
        Assert.IsFalse(ComConProductionCoordinatorOptions.TryCreate(profile with { RadioAddress = null }, out _));
        Assert.IsFalse(ComConProductionCoordinatorOptions.TryCreate(profile with { RadioAddress = 0 }, out _));
        Assert.IsFalse(ComConProductionCoordinatorOptions.TryCreate(
            profile with { RadioAddress = profile.LegacyClientAddress }, out _));
        Assert.IsTrue(ComConProductionCoordinatorOptions.TryCreate(
            profile with { Framing = FramingMode.KenwoodAscii, RadioAddress = null }, out var ascii));
        Assert.IsNotNull(ascii);
        Assert.IsFalse(ascii.CivExchangeGateEnabled);
        Assert.AreEqual(500L, ascii.QueueWaitLimitMs);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task CommandGroupStartsAllOptedInProfilesBehindOneCatPipeAndStopsAllHosts()
    {
        var settings = CommandSettings(2);
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var names = ComConCommandPipeNames.IsolatedTest(
            "ComConTest_Group_" + Guid.NewGuid().ToString("N"));
        await using var supervisor = NewGroupSupervisor(settings, factories, names);

        await supervisor.StartAsync();

        Assert.AreEqual(names.CatPipeName, supervisor.CatPipeName);
        Assert.HasCount(2, supervisor.Profiles);
        Assert.IsTrue(supervisor.Profiles.All(profile => profile.State == SharedRadioHostState.Running));
        Assert.IsTrue(supervisor.Profiles.All(profile =>
            profile.LegacyStatus.State == LegacyConnectionState.Ready && !profile.CanRecoverLegacy));
        Assert.HasCount(2, supervisor.Mappings);
        Assert.IsEmpty(supervisor.ActiveSessions);
        Assert.AreEqual(4, factories.Sum(factory => factory.OpenCount));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => supervisor.RecoverLegacyAsync(1));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => supervisor.RecoverLegacyAsync(5));
        Assert.AreEqual(4, factories.Sum(factory => factory.OpenCount),
            "Rejected recovery requests must not reopen endpoints.");
        Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() =>
            ComConReadOnlyPipeHost.CreateProtectedPipe(supervisor.CatPipeName));
        for (var id = 1; id <= 4; id++)
            Assert.ThrowsExactly<System.ComponentModel.Win32Exception>(() =>
                ComConReadOnlyPipeHost.CreateProtectedPipe(supervisor.ControlPipeName(id)));
        var mapping = settings.RigProfileMappings[1];
        var accepted = supervisor.Sessions.AcceptCatHandshake(MultiProfilePeer.Handshake(mapping.RigProfileId));
        Assert.IsTrue(accepted.Accepted);
        Assert.AreEqual(mapping.ProfileId, accepted.Session!.ProfileId);
        Assert.HasCount(1, supervisor.ActiveSessions);
        supervisor.Sessions.EndSession(accepted.Session, "Offline mapping snapshot assertion.");
        Assert.IsEmpty(supervisor.ActiveSessions);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var peer = await MultiProfilePeer.Open(supervisor.CatPipeName, supervisor.ControlPipeName,
            mapping.RigProfileId, mapping.ProfileId, timeout.Token, null);
        await peer.Cat.WriteAsync(Civ(0x94, 1), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 1), await factories[1].Radio.Receive());
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        await peer.Ack("1", timeout.Token);
        var stopping = supervisor.StopOrderlyAsync("Group integration test stop.", timeout.Token);
        var final = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
        Assert.AreEqual("session.ended", final.GetProperty("event").GetString());
        await peer.Ack(final.GetProperty("notificationSequence").GetString()!, timeout.Token);
        await stopping;
        Assert.IsTrue(factories.All(factory => factory.Legacy.Disposed && factory.Radio.Disposed));
        Assert.IsTrue(supervisor.Profiles.All(profile => profile.State == SharedRadioHostState.Stopped));
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task CommandGroupRollsBackEarlierHostsWhenLaterProfileFailsToOpen()
    {
        var settings = CommandSettings(2);
        var first = new FakeFactory { RadioPort = "COM101" };
        var second = new FakeFactory { RadioPort = "COM102", OpenFailure = true };
        await using var supervisor = NewGroupSupervisor(settings, [first, second],
            ComConCommandPipeNames.IsolatedTest(
                "ComConTest_Rollback_" + Guid.NewGuid().ToString("N")));

        await Assert.ThrowsExactlyAsync<IOException>(() => supervisor.StartAsync());

        Assert.IsTrue(first.Legacy.Disposed && first.Radio.Disposed);
        Assert.AreEqual(0, second.OpenCount);
        Assert.IsEmpty(supervisor.Profiles);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task CommandGroupPipeCollisionRollsBackEveryOpenedProfile()
    {
        var settings = CommandSettings(2);
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var names = ComConCommandPipeNames.IsolatedTest(
            "ComConTest_GroupCollision_" + Guid.NewGuid().ToString("N"));
        using var owner = ComConReadOnlyPipeHost.CreateProtectedPipe(names.ControlPipeName(3));
        await using var supervisor = NewGroupSupervisor(settings, factories, names);

        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => supervisor.StartAsync());

        Assert.AreEqual(0, factories.Sum(factory => factory.OpenCount),
            "The shared service must own all its names before any port is opened.");
        Assert.IsEmpty(supervisor.Profiles);
        using var releasedCat = ComConReadOnlyPipeHost.CreateProtectedPipe(names.CatPipeName);
    }

    private static AppSettings CommandSettings(int count)
    {
        var settings = SupervisorSettings(count);
        return settings with
        {
            Profiles = settings.Profiles.Select(profile => profile with
            {
                CommandIpcEnabled = true,
                LegacyClientAddress = 0xE0,
                IpcClientAddress = 0xE1
            }).ToArray()
        };
    }

    [SupportedOSPlatform("windows")]
    private static ComConCommandGroupSupervisor NewGroupSupervisor(
        AppSettings settings,
        FakeFactory[] factories,
        ComConCommandPipeNames names) =>
        new(settings,
            factories.Select((factory, index) => new ComConCommandProfilePlan(
                index + 1, factory, Options())),
            names,
            new FakeClock(),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3),
            (_, _) => { },
            (_, _) => { });
}
