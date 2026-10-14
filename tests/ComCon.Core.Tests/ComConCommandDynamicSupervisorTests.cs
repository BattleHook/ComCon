using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SharedDynamicServiceServesFourPeersAndStopOnePreservesTheOtherThree()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var settings = CommandSettings(4);
        var factories = Enumerable.Range(1, 4).Select(id => new FakeFactory { RadioPort = $"COM10{id}" }).ToArray();
        await using var supervisor = NewDynamicSupervisor(settings);
        var peers = new List<MultiProfilePeer>();
        try
        {
            Assert.IsFalse(supervisor.IpcAvailable);
            Assert.IsEmpty(supervisor.Profiles);
            foreach (var id in Enumerable.Range(1, 4))
                await supervisor.StartProfileAsync(settings, new(id, factories[id - 1], Options()), timeout.Token);
            peers.AddRange(await Task.WhenAll(settings.RigProfileMappings.Select(mapping =>
                MultiProfilePeer.Open(supervisor.CatPipeName, supervisor.ControlPipeName,
                    mapping.RigProfileId, mapping.ProfileId, timeout.Token, null))));
            await WaitUntil(() => supervisor.ActiveSessions.Count == 4 &&
                supervisor.ActiveSessions.All(session => session.State == ComConCommandSessionState.Active));
            Assert.IsTrue(supervisor.IpcAvailable);
            Assert.HasCount(4, supervisor.Profiles);
            Assert.IsTrue(supervisor.Profiles.All(status => status.ActiveConfiguration is not null));

            await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
                DynamicExchange(factories[index], peers[index], (byte)(index + 1), "1", timeout.Token)));

            await AssertHandshakeRejected(supervisor, settings.RigProfileMappings[0].RigProfileId,
                "PROFILE_BUSY", timeout.Token);
            var old = supervisor.ActiveSessions.Single(session => session.ProfileId == 1);
            var stopping = supervisor.StopProfileAsync(1, "Stop profile one only.", timeout.Token);
            var ended = await ComConCommandProtocol.ReadEnvelopeAsync(peers[0].Control, timeout.Token);
            Assert.AreEqual("session.ended", ended.GetProperty("event").GetString());
            await peers[0].Ack(ended.GetProperty("notificationSequence").GetString()!, timeout.Token);
            await stopping;
            peers[0].Dispose();
            Assert.AreEqual(ComConCommandSessionState.Ended, old.State);
            Assert.HasCount(3, supervisor.Profiles);
            Assert.IsTrue(supervisor.IpcAvailable);
            Assert.IsTrue(factories[0].Legacy.Disposed && factories[0].Radio.Disposed);
            Assert.IsTrue(factories.Skip(1).All(factory => !factory.Legacy.Disposed && !factory.Radio.Disposed));
            await Task.WhenAll(Enumerable.Range(1, 3).Select(index =>
                DynamicExchange(factories[index], peers[index], (byte)(index + 10), "2", timeout.Token)));
            await AssertHandshakeRejected(supervisor, settings.RigProfileMappings[0].RigProfileId,
                "RIG_PROFILE_NOT_MAPPED", timeout.Token);
        }
        finally
        {
            foreach (var peer in peers) peer.Dispose();
            await WaitUntil(() => supervisor.ActiveSessions.Count == 0);
        }
        await supervisor.StopOrderlyAsync("Finish four-peer test.", timeout.Token);
        Assert.IsFalse(supervisor.IpcAvailable);
        Assert.IsEmpty(supervisor.Profiles);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task FailedDynamicAddKeepsExistingSessionAndLastStopPermitsFreshServiceStart()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var settings = CommandSettings(2);
        var first = new FakeFactory { RadioPort = "COM101" };
        var failed = new FakeFactory { RadioPort = "COM102", OpenFailure = true };
        await using var supervisor = NewDynamicSupervisor(settings);
        await supervisor.StartProfileAsync(settings, new(1, first, Options()), timeout.Token);
        using var peer = await MultiProfilePeer.Open(supervisor.CatPipeName, supervisor.ControlPipeName,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        await DynamicExchange(first, peer, 1, "1", timeout.Token);
        var oldId = supervisor.ActiveSessions.Single().SessionId;
        await Assert.ThrowsExactlyAsync<IOException>(() =>
            supervisor.StartProfileAsync(settings, new(2, failed, Options()), timeout.Token));
        Assert.HasCount(1, supervisor.Profiles);
        Assert.AreEqual(1, supervisor.Profiles[0].ProfileId);
        Assert.AreEqual(oldId, supervisor.ActiveSessions.Single().SessionId);
        Assert.IsTrue(supervisor.IpcAvailable);
        Assert.IsNull(supervisor.IpcFault);
        await DynamicExchange(first, peer, 2, "2", timeout.Token);
        var stopping = supervisor.StopProfileAsync(1, "Last IPC profile.", timeout.Token);
        var ended = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
        await peer.Ack(ended.GetProperty("notificationSequence").GetString()!, timeout.Token);
        await stopping;
        Assert.IsEmpty(supervisor.Profiles);
        Assert.IsFalse(supervisor.IpcAvailable);
        using (var released = ComConReadOnlyPipeHost.CreateProtectedPipe(supervisor.CatPipeName)) { }
        var replacement = new FakeFactory { RadioPort = "COM102" };
        await supervisor.StartProfileAsync(settings, new(2, replacement, Options()), timeout.Token);
        Assert.IsTrue(supervisor.IpcAvailable);
        Assert.HasCount(1, supervisor.Profiles);
        Assert.AreEqual(2, supervisor.Profiles[0].ProfileId);
        await supervisor.StopProfileAsync(2, "Stop replacement profile.", timeout.Token);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ExplicitServiceRecoveryQuiescesOldSessionsAndDoesNotRestartLegacyOrReplayCat()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var settings = CommandSettings(2);
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        await using var supervisor = NewDynamicSupervisor(settings);
        var peers = new List<MultiProfilePeer>();
        try
        {
            for (var id = 1; id <= 2; id++)
                await supervisor.StartProfileAsync(settings, new(id, factories[id - 1], Options()), timeout.Token);
            foreach (var mapping in settings.RigProfileMappings)
                peers.Add(await MultiProfilePeer.Open(supervisor.CatPipeName, supervisor.ControlPipeName,
                    mapping.RigProfileId, mapping.ProfileId, timeout.Token, null));
            await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
                DynamicExchange(factories[index], peers[index], (byte)(index + 1), "1", timeout.Token)));
            var oldSessions = supervisor.ActiveSessions.ToArray();
            await supervisor.RecoverIpcServiceAsync(timeout.Token);
            Assert.IsTrue(supervisor.IpcAvailable);
            Assert.IsNull(supervisor.IpcFault);
            Assert.IsEmpty(supervisor.Faults);
            Assert.IsEmpty(supervisor.ActiveSessions);
            Assert.IsTrue(oldSessions.All(session => session.State == ComConCommandSessionState.Ended));
            Assert.IsTrue(factories.All(factory => factory.OpenCount == 2 &&
                !factory.Legacy.Disposed && !factory.Radio.Disposed && factory.Radio.WriteCount == 1));
            foreach (var peer in peers) peer.Dispose();
            peers.Clear();
            foreach (var mapping in settings.RigProfileMappings)
                peers.Add(await MultiProfilePeer.Open(supervisor.CatPipeName, supervisor.ControlPipeName,
                    mapping.RigProfileId, mapping.ProfileId, timeout.Token,
                    oldSessions.Single(session => session.ProfileId == mapping.ProfileId).SessionId));
            await Task.WhenAll(Enumerable.Range(0, 2).Select(index =>
                DynamicExchange(factories[index], peers[index], (byte)(index + 10), "1", timeout.Token)));
            Assert.IsTrue(supervisor.ActiveSessions.All(session =>
                oldSessions.All(old => old.SessionId != session.SessionId)));
            foreach (var factory in factories)
            {
                await factory.Legacy.Send(Civ(0x94, 30));
                CollectionAssert.AreEqual(Civ(0x94, 30), await factory.Radio.Receive());
                Assert.AreEqual(2, factory.OpenCount);
                Assert.AreEqual(3, factory.Radio.WriteCount);
            }
        }
        finally
        {
            foreach (var peer in peers) peer.Dispose();
            await WaitUntil(() => supervisor.ActiveSessions.Count == 0);
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task ListenerFaultDisablesIpcAdmissionButPreservesLegacyUntilExplicitRecovery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var settings = CommandSettings(2);
        var first = new FakeFactory { RadioPort = "COM101" };
        var second = new FakeFactory { RadioPort = "COM102" };
        await using var supervisor = NewDynamicSupervisor(settings);
        await supervisor.StartProfileAsync(settings, new(1, first, Options()), timeout.Token);
        var listener = (ComConCommandTestPipeListener)typeof(ComConCommandGroupSupervisor)
            .GetField("listener", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(supervisor)!;
        listener.StopAccepting();
        await WaitUntil(() => !supervisor.IpcAvailable && supervisor.IpcFault is not null);
        Assert.IsTrue(supervisor.Faults.Any(fault => fault.StartsWith("IPC service: ", StringComparison.Ordinal)));
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles.Single().State);
        Assert.IsFalse(first.Legacy.Disposed || first.Radio.Disposed);
        await first.Legacy.Send(Civ(0x94, 9));
        CollectionAssert.AreEqual(Civ(0x94, 9), await first.Radio.Receive());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            supervisor.StartProfileAsync(settings, new(2, second, Options()), timeout.Token));
        Assert.AreEqual(0, second.OpenCount);
        await supervisor.RecoverIpcServiceAsync(timeout.Token);
        Assert.IsTrue(supervisor.IpcAvailable);
        Assert.IsNull(supervisor.IpcFault);
        Assert.IsEmpty(supervisor.Faults);
        Assert.AreEqual(2, first.OpenCount);
        await supervisor.StartProfileAsync(settings, new(2, second, Options()), timeout.Token);
        Assert.HasCount(2, supervisor.Profiles);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task SharedServiceNamesAreEstablishedBeforeOpeningAnyDynamicProfilePort()
    {
        var settings = CommandSettings(1);
        var names = ComConCommandPipeNames.IsolatedTest("ComConTest_DynamicCollision_" + Guid.NewGuid().ToString("N"));
        using var owner = ComConReadOnlyPipeHost.CreateProtectedPipe(names.ControlPipeName(4));
        await using var supervisor = NewDynamicSupervisor(settings, names);
        var factory = new FakeFactory { RadioPort = "COM101" };
        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() =>
            supervisor.StartProfileAsync(settings, new(1, factory, Options())));
        Assert.AreEqual(0, factory.OpenCount);
        Assert.IsEmpty(supervisor.Profiles);
        Assert.IsFalse(supervisor.IpcAvailable);
        Assert.IsNotNull(supervisor.IpcFault);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            supervisor.StartProfileAsync(settings, new(1, factory, Options())));
        owner.Dispose();
        await supervisor.RecoverIpcServiceAsync();
        Assert.IsNull(supervisor.IpcFault);
        Assert.IsFalse(supervisor.IpcAvailable,
            "Recovery without profiles must not advertise a running service.");
        Assert.IsEmpty(supervisor.Profiles);
        Assert.AreEqual(0, factory.OpenCount,
            "Zero-profile recovery must never open radio or Legacy endpoints.");
        using (var released = ComConReadOnlyPipeHost.CreateProtectedPipe(names.CatPipeName)) { }
        await supervisor.StartProfileAsync(settings, new(1, factory, Options()));
        Assert.IsTrue(supervisor.IpcAvailable);
        Assert.HasCount(1, supervisor.Profiles);
        Assert.AreEqual(2, factory.OpenCount);
        await supervisor.StopProfileAsync(1, "Finish recovered startup collision test.");
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task UncertainStartupCloseRetainsTheFailedProfileAndDoesNotDisturbHealthyHost()
    {
        var settings = CommandSettings(2);
        var healthy = new FakeFactory { RadioPort = "COM101" };
        var supervisor = NewDynamicSupervisor(settings);
        try
        {
            await supervisor.StartProfileAsync(settings, new(1, healthy, Options()));
            await Assert.ThrowsExactlyAsync<AggregateException>(() =>
                supervisor.StartProfileAsync(settings, new(2, new UncertainStartupFactory(), Options())));
            Assert.HasCount(2, supervisor.Profiles);
            var retained = supervisor.Profiles.Single(status => status.ProfileId == 2);
            Assert.AreEqual(SharedRadioHostState.Faulted, retained.State);
            Assert.IsNotNull(retained.Fault);
            Assert.AreEqual(settings.Profiles[1], retained.ActiveConfiguration);
            Assert.AreEqual(SharedRadioHostState.Running,
                supervisor.Profiles.Single(status => status.ProfileId == 1).State);
            Assert.IsTrue(supervisor.IpcAvailable);
            Assert.IsNull(supervisor.IpcFault);
            await healthy.Legacy.Send(Civ(0x94, 5));
            CollectionAssert.AreEqual(Civ(0x94, 5), await healthy.Radio.Receive());
            await Assert.ThrowsExactlyAsync<IOException>(() => supervisor.StopProfileAsync(2, "Cannot release uncertain ports."));
            Assert.HasCount(2, supervisor.Profiles);
            await supervisor.StopProfileAsync(1, "Healthy host can still stop independently.");
            Assert.HasCount(1, supervisor.Profiles);
            Assert.AreEqual(2, supervisor.Profiles.Single().ProfileId);
        }
        finally
        {
            await Assert.ThrowsExactlyAsync<AggregateException>(() => supervisor.DisposeAsync().AsTask());
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task DynamicAddPinsExistingConfigurationMappingsAndRejectsRetainedPortConflicts()
    {
        var settings = CommandSettings(2);
        var first = new FakeFactory { RadioPort = "COM101" };
        var second = new FakeFactory { RadioPort = "COM102" };
        await using var supervisor = NewDynamicSupervisor(settings);
        await supervisor.StartProfileAsync(settings, new(1, first, Options()));
        var changed = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 1
                ? profile with { Label = "Changed saved label", PhysicalPort = "COM501", VirtualPort = "COM601" }
                : profile).ToArray(),
            RigProfileMappings = settings.RigProfileMappings.Select(mapping => mapping.ProfileId == 1
                ? mapping with { RigProfileId = Guid.NewGuid().ToString("D") } : mapping).ToArray()
        };
        var conflict = changed with
        {
            Profiles = changed.Profiles.Select(profile => profile.Id == 2
                ? profile with { PhysicalPort = "COM101" } : profile).ToArray()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            supervisor.StartProfileAsync(conflict, new(2, second, Options())));
        Assert.AreEqual(0, second.OpenCount);
        await supervisor.StartProfileAsync(changed, new(2, second, Options()));
        Assert.AreEqual(settings.Profiles[0], supervisor.Profiles.Single(status => status.ProfileId == 1).ActiveConfiguration);
        Assert.AreEqual(settings.RigProfileMappings[0], supervisor.Mappings.Single(mapping => mapping.ProfileId == 1));
        Assert.AreEqual(2, first.OpenCount);
    }

    private sealed class UncertainStartupFactory : IByteBridgeEndpointFactory
    {
        private int attempts;
        public ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName, SerialSettings settings, CancellationToken cancellationToken)
        {
            if (++attempts == 1) return ValueTask.FromResult<IByteBridgeEndpoint>(new UncertainCloseEndpoint());
            throw new IOException("Injected physical endpoint open failure.");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task PersistentSupervisorAfterLastStopUsesOnlyTheNewSettingsAndPlanSnapshot()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var former = CommandSettings(1);
        var first = new FakeFactory { RadioPort = "COM101" };
        await using var supervisor = NewDynamicSupervisor(former);
        await supervisor.StartProfileAsync(former, new(1, first, Options()), timeout.Token);
        await supervisor.StopProfileAsync(1, "Allow another settings file.", timeout.Token);
        Assert.IsEmpty(supervisor.Profiles);
        Assert.IsEmpty(supervisor.Mappings);
        Assert.IsFalse(supervisor.IpcAvailable);

        var replacement = CommandSettings(1) with
        {
            Profiles = [former.Profiles[0] with
            {
                Label = "New file radio",
                PhysicalPort = "COM501",
                VirtualPort = "COM601",
                CommanderPort = "COM701",
                LegacyClientAddress = 0xE2,
                IpcClientAddress = 0xE3
            }],
            RoutingLimits = former.RoutingLimits with { HelloDeadlineMs = 2500 }
        };
        var replacementFactory = new FakeFactory { RadioPort = "COM501" };
        var replacementOptions = Options() with
        {
            LegacyClientAddress = 0xE2,
            IpcClientAddress = 0xE3,
            TxQueueRecordsPerClient = 2,
            TxQueueRecordsPerProfile = 4
        };
        await supervisor.StartProfileAsync(replacement,
            new(1, replacementFactory, replacementOptions), timeout.Token);
        Assert.AreEqual(replacement.Profiles[0], supervisor.Profiles.Single().ActiveConfiguration);
        Assert.AreEqual(replacement.RigProfileMappings[0], supervisor.Mappings.Single());
        Assert.AreEqual(2, first.OpenCount);
        Assert.IsTrue(first.Legacy.Disposed && first.Radio.Disposed);
        await AssertHandshakeRejected(supervisor, former.RigProfileMappings[0].RigProfileId,
            "RIG_PROFILE_NOT_MAPPED", timeout.Token);
        using var peer = await MultiProfilePeer.Open(supervisor.CatPipeName, supervisor.ControlPipeName,
            replacement.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        byte[] cat = [0xFE, 0xFE, 0x94, 0xE3, 1, 0xFD];
        await peer.Cat.WriteAsync(cat, timeout.Token);
        CollectionAssert.AreEqual(cat, await replacementFactory.Radio.Receive());
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        await peer.Ack("1", timeout.Token);
        var stop = supervisor.StopProfileAsync(1, "New configuration also stops normally.", timeout.Token);
        var ended = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
        await peer.Ack(ended.GetProperty("notificationSequence").GetString()!, timeout.Token);
        await stop;
        Assert.IsEmpty(supervisor.Profiles);
    }

    private sealed class UncertainCloseEndpoint : IByteBridgeEndpoint
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.FromResult(0);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.FromException(new IOException("Injected endpoint close failure."));
    }

    [SupportedOSPlatform("windows")]
    private static ComConCommandGroupSupervisor NewDynamicSupervisor(
        AppSettings settings, ComConCommandPipeNames? names = null) =>
        new(settings, [], names ?? ComConCommandPipeNames.IsolatedTest(
            "ComConTest_Dynamic_" + Guid.NewGuid().ToString("N")),
            new FakeClock(), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), (_, _) => { }, (_, _) => { });

    [SupportedOSPlatform("windows")]
    private static async Task DynamicExchange(
        FakeFactory factory, MultiProfilePeer peer, byte command, string sequence, CancellationToken token)
    {
        await peer.Cat.WriteAsync(Civ(0x94, command), token);
        CollectionAssert.AreEqual(Civ(0x94, command), await factory.Radio.Receive());
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, token);
        AssertOutcome(outcome, "record.outcome", sequence, "WRITE_SUCCEEDED");
        await peer.Ack(outcome.GetProperty("notificationSequence").GetString()!, token);
    }

    [SupportedOSPlatform("windows")]
    private static async Task AssertHandshakeRejected(
        ComConCommandGroupSupervisor supervisor, string rig, string code, CancellationToken token)
    {
        using NamedPipeClientStream cat = await MultiProfilePeer.Connect(supervisor.CatPipeName, token);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, MultiProfilePeer.Handshake(rig), writer, token);
        var rejected = await ComConCommandProtocol.ReadEnvelopeAsync(cat, token);
        Assert.AreEqual("cat.rejected", rejected.GetProperty("messageType").GetString());
        Assert.AreEqual(code, rejected.GetProperty("errorCode").GetString());
    }
}
