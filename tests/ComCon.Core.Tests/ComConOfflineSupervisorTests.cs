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
    public async Task OfflineSupervisorUsesPersistedMappingsAndIsolatesSessionFaultAndReconnect()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var factories = Enumerable.Range(1, 4).Select(id => new FakeFactory { RadioPort = $"COM{100 + id}" }).ToArray();
        var settings = SupervisorSettings(4);
        var plans = SupervisorPlans(factories);
        var failures = new ConcurrentQueue<(int Profile, Exception Failure)>();
        var diagnostics = new ConcurrentQueue<(int Profile, SharedRadioDiagnostic Diagnostic)>();
        await using var supervisor = new ComConOfflineSupervisor(
            ConfigurationJson.Deserialize(ConfigurationJson.Serialize(settings)), plans, new FakeClock(),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (id, error) => failures.Enqueue((id, error)), (id, diagnostic) => diagnostics.Enqueue((id, diagnostic)));
        Assert.IsEmpty(supervisor.Profiles);
        Assert.IsTrue(factories.All(factory => factory.OpenCount == 0));
        await supervisor.StartAsync(timeout.Token);
        Assert.HasCount(4, supervisor.Profiles);
        Assert.IsNull(supervisor.Profiles[0].Host.CoordinatorMetrics,
            "Offline supervisors should not capture coordinator metrics unless explicitly opted in.");
        Assert.IsTrue(supervisor.Profiles.All(profile => profile.Listener.Ready.IsCompletedSuccessfully));
        using var first = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var second = await MultiProfilePeer.Open(supervisor.Profiles[1].Listener,
            settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);
        await first.Cat.WriteAsync(Civ(0x94, 1), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 1), await factories[0].Radio.Receive());
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(first.Control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        await first.Ack("99", timeout.Token);
        await WaitUntil(() => failures.Any(failure => failure.Profile == 1));
        using var reconnect = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        await reconnect.Cat.WriteAsync(Civ(0x94, 2), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 2), await factories[0].Radio.Receive());
        var fresh = await ComConCommandProtocol.ReadEnvelopeAsync(reconnect.Control, timeout.Token);
        AssertOutcome(fresh, "record.outcome", "1", "WRITE_SUCCEEDED");
        await reconnect.Ack("1", timeout.Token);
        await second.Cat.WriteAsync(Civ(0x94, 3), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 3), await factories[1].Radio.Receive());
        var other = await ComConCommandProtocol.ReadEnvelopeAsync(second.Control, timeout.Token);
        Assert.AreEqual(2, other.GetProperty("profileId").GetInt32());
        await second.Ack("1", timeout.Token);
        Assert.IsFalse(failures.Any(failure => failure.Profile == 2));
        Assert.IsTrue(factories.All(factory => factory.OpenCount == 2));
        factories[2].Radio.WriteFailure = new IOException("Offline profile three uncertain write.");
        await factories[2].Legacy.Send(Civ(0x94, 4));
        await WaitUntil(() => supervisor.Profiles[2].Host.State == SharedRadioHostState.Faulted);
        Assert.IsTrue(diagnostics.Any(entry => entry.Profile == 3 &&
            entry.Diagnostic.Kind == SharedRadioDiagnosticKind.ProfileFaulted));
        Assert.IsFalse(diagnostics.Any(entry => entry.Profile != 3 &&
            entry.Diagnostic.Kind == SharedRadioDiagnosticKind.ProfileFaulted));
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles[3].Host.State);
        await factories[3].Legacy.Send(Civ(0x94, 5));
        CollectionAssert.AreEqual(Civ(0x94, 5), await factories[3].Radio.Receive());
        supervisor.Profiles[1].Stop.Cancel();
        await supervisor.Profiles[1].Completion.WaitAsync(timeout.Token);
        await factories[1].Legacy.Send(Civ(0x94, 6));
        CollectionAssert.AreEqual(Civ(0x94, 6), await factories[1].Radio.Receive());
        await reconnect.Cat.WriteAsync(Civ(0x94, 7), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 7), await factories[0].Radio.Receive());
        await ComConCommandProtocol.ReadEnvelopeAsync(reconnect.Control, timeout.Token);
        await reconnect.Ack("2", timeout.Token);
        await supervisor.StopAsync();
        Assert.IsTrue(factories.All(factory => factory.Legacy.Disposed && factory.Radio.Disposed));
        Assert.IsTrue(supervisor.Profiles.All(profile => profile.Completion.IsCompletedSuccessfully));
        foreach (var runtime in supervisor.Profiles)
        {
            using var catName = ComConReadOnlyPipeHost.CreateProtectedPipe(runtime.Listener.CatPipeName);
            for (var id = 1; id <= 4; id++)
            {
                using var controlName = ComConReadOnlyPipeHost.CreateProtectedPipe(runtime.Listener.ControlPipeName(id));
            }
            var reservation = supervisor.Sessions.AcceptCatHandshake(
                MultiProfilePeer.Handshake(settings.RigProfileMappings[runtime.Profile.Id - 1].RigProfileId));
            Assert.IsTrue(reservation.Accepted);
            supervisor.Sessions.EndSession(reservation.Session!, "Offline cleanup assertion.");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorRollsBackEarlierProfilesOnEndpointStartupFailure()
    {
        var first = new FakeFactory { RadioPort = "COM101" };
        var second = new FakeFactory { RadioPort = "COM102", OpenFailure = true };
        await using var supervisor = NewSupervisor(SupervisorSettings(2), SupervisorPlans([first, second]));
        await Assert.ThrowsExactlyAsync<IOException>(() => supervisor.StartAsync());
        Assert.IsTrue(first.Legacy.Disposed && first.Radio.Disposed);
        Assert.AreEqual(0, second.OpenCount);
        Assert.IsEmpty(supervisor.Profiles);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => supervisor.StartAsync());
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorRollsBackAllProfilesOnProtectedPipeCollision()
    {
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var plans = SupervisorPlans(factories);
        using var owner = ComConReadOnlyPipeHost.CreateProtectedPipe(plans[1].TestPipeName);
        await using var supervisor = NewSupervisor(SupervisorSettings(2), plans);
        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => supervisor.StartAsync());
        Assert.IsTrue(factories.All(factory => factory.Legacy.Disposed && factory.Radio.Disposed));
        using var released = ComConReadOnlyPipeHost.CreateProtectedPipe(plans[0].TestPipeName);
        Assert.IsFalse(owner.IsConnected);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorPrevalidatesCompleteSettingsAndExplicitSelection()
    {
        var factory = new FakeFactory { RadioPort = "COM101" };
        var plans = SupervisorPlans([factory]);
        var settings = SupervisorSettings(2);
        var overlapping = settings with
        {
            Profiles = [settings.Profiles[0], settings.Profiles[1] with { CommanderPort = "COM101" }]
        };
        Assert.ThrowsExactly<ConfigurationValidationException>(() => NewSupervisor(overlapping, plans));
        Assert.ThrowsExactly<ArgumentException>(() => NewSupervisor(settings, [plans[0], plans[0]]));
        Assert.ThrowsExactly<ArgumentException>(() => NewSupervisor(settings, []));
        Assert.ThrowsExactly<ArgumentException>(() => NewSupervisor(settings,
            [plans[0] with { TestPipeName = "Production" }]));
        var disabled = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile with { Enabled = false, AutoConnect = false }).ToArray()
        };
        Assert.ThrowsExactly<ArgumentException>(() => NewSupervisor(disabled, plans));
        var alias = plans[0].TestPipeName + "_Control_Profile1";
        Assert.ThrowsExactly<ArgumentException>(() => NewSupervisor(settings,
            [plans[0], plans[0] with { ProfileId = 2, TestPipeName = alias }]));
        var mismatched = settings with
        {
            Profiles = [settings.Profiles[0], settings.Profiles[1] with { Framing = FramingMode.None }]
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => NewSupervisor(mismatched,
            SupervisorPlans([factory, new FakeFactory { RadioPort = "COM102" }])));
        Assert.AreEqual(0, factory.OpenCount);
        await using var supervisor = NewSupervisor(settings, plans);
        await supervisor.StartAsync();
        Assert.HasCount(1, supervisor.Profiles);
        Assert.AreEqual(1, supervisor.Profiles[0].Profile.Id);
        await supervisor.StopAsync();
        await supervisor.StopAsync();
        Assert.IsTrue(factory.Radio.Disposed);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorCancelledStartAndDisposeBeforeStartOpenNothing()
    {
        var factory = new FakeFactory { RadioPort = "COM101" };
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await using var supervisor = NewSupervisor(SupervisorSettings(1), SupervisorPlans([factory]));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => supervisor.StartAsync(cancelled.Token));
        await supervisor.StopAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => supervisor.StartAsync());
        Assert.AreEqual(0, factory.OpenCount);
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorCancellationDuringSecondOpenRollsBackFirstProfile()
    {
        var first = new FakeFactory { RadioPort = "COM101" };
        var blocked = new CancelledSupervisorFactory();
        var plans = SupervisorPlans([first, new FakeFactory { RadioPort = "COM102" }]);
        plans[1] = plans[1] with { EndpointFactory = blocked };
        using var cancellation = new CancellationTokenSource();
        await using var supervisor = NewSupervisor(SupervisorSettings(2), plans);
        var start = supervisor.StartAsync(cancellation.Token);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => start);
        Assert.IsTrue(first.Legacy.Disposed && first.Radio.Disposed);
        using var released = ComConReadOnlyPipeHost.CreateProtectedPipe(plans[0].TestPipeName);
    }

    private sealed class CancelledSupervisorFactory : IByteBridgeEndpointFactory
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName, SerialSettings settings, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancelled offline factory must never open an endpoint.");
        }
    }

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task OfflineSupervisorReportsCleanupFailureButStillClosesOtherProfiles()
    {
        var first = new FakeFactory { RadioPort = "COM101" };
        var second = new FakeFactory { RadioPort = "COM102" };
        var plans = SupervisorPlans([first, second]);
        plans[0] = plans[0] with { EndpointFactory = new FailingSupervisorCleanupFactory(first) };
        await using var supervisor = NewSupervisor(SupervisorSettings(2), plans);
        await supervisor.StartAsync();
        var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => supervisor.StopAsync());
        StringAssert.Contains(failure.Flatten().Message, "Injected offline cleanup failure");
        Assert.IsTrue(first.Radio.Disposed && first.Legacy.Disposed);
        Assert.IsTrue(second.Radio.Disposed && second.Legacy.Disposed);
        Assert.IsTrue(supervisor.Profiles.All(profile => profile.Completion.IsCompletedSuccessfully));
        using var released = ComConReadOnlyPipeHost.CreateProtectedPipe(plans[1].TestPipeName);
    }

    private sealed class FailingSupervisorCleanupFactory(FakeFactory factory) : IByteBridgeEndpointFactory
    {
        public async ValueTask<IByteBridgeEndpoint> OpenAsync(
            string portName, SerialSettings settings, CancellationToken cancellationToken)
        {
            var endpoint = await factory.OpenAsync(portName, settings, cancellationToken);
            return portName == factory.RadioPort ? new FailingSupervisorCleanupEndpoint(endpoint) : endpoint;
        }
    }

    private sealed class FailingSupervisorCleanupEndpoint(IByteBridgeEndpoint endpoint) : IByteBridgeEndpoint
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            endpoint.ReadAsync(buffer, cancellationToken);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            endpoint.WriteAsync(buffer, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            await endpoint.DisposeAsync();
            throw new IOException("Injected offline cleanup failure.");
        }
    }

    [SupportedOSPlatform("windows")]
    [TestMethod]
    public async Task TestListenerReadinessFailsOnControlHandleCollisionAndCancelsBeforeStartup()
    {
        var sessions = new ComConCommandSessions(new FakeClock(), 3000, []);
        var name = "ComConTest_Readiness_" + Guid.NewGuid().ToString("N");
        var listener = new ComConCommandTestPipeListener(name, sessions, TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3), _ => { });
        using var owner = ComConReadOnlyPipeHost.CreateProtectedPipe(listener.ControlPipeName(3));
        var run = listener.RunAsync((_, _) => Task.CompletedTask, CancellationToken.None);
        var readinessFailure = await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => listener.Ready);
        var runFailure = await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => run);
        Assert.AreSame(readinessFailure, runFailure);
        using var cat = ComConReadOnlyPipeHost.CreateProtectedPipe(listener.CatPipeName);
        using var control = ComConReadOnlyPipeHost.CreateProtectedPipe(listener.ControlPipeName(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = new ComConCommandTestPipeListener(
            "ComConTest_Cancelled_" + Guid.NewGuid().ToString("N"), sessions, TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(3), _ => { });
        await cancelled.RunAsync((_, _) => Task.CompletedTask, cancellation.Token);
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => cancelled.Ready);
    }

    [SupportedOSPlatform("windows")]
    private static ComConOfflineSupervisor NewSupervisor(AppSettings settings, ComConOfflineProfilePlan[] plans) =>
        new(settings, plans, new FakeClock(), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), (_, _) => { }, (_, _) => { });

    private static AppSettings SupervisorSettings(int count) => AppSettings.CreateDefault() with
    {
        Profiles = Enumerable.Range(1, count).Select(id => Profile() with
        {
            Id = id, AutoConnect = true, PhysicalPort = $"COM{100 + id}",
            VirtualPort = $"COM{200 + id}", CommanderPort = $"COM{300 + id}"
        }).ToArray(),
        RigProfileMappings = Enumerable.Range(1, count).Select(id =>
            new ComConRigProfileMapping(Guid.NewGuid().ToString("D"), id)).ToArray()
    };

    private static ComConOfflineProfilePlan[] SupervisorPlans(FakeFactory[] factories) =>
        factories.Select((factory, index) => new ComConOfflineProfilePlan(index + 1,
            "ComConTest_Supervisor_" + Guid.NewGuid().ToString("N"), factory, Options())).ToArray();
}
