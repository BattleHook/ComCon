using System.Runtime.Versioning;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ComConBridgeRuntimeTests
{
    [TestMethod]
    public async Task IndependentRawBridgesStopSelectedWithoutInterruptingSibling()
    {
        var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail());
        var settings = BridgeStartSelectionTests.Settings();
        await runtime.StartProfileAsync(settings, 1);
        await runtime.StartProfileAsync(settings, 2);
        Assert.HasCount(2, runtime.Profiles);
        Assert.IsTrue(runtime.Profiles.All(profile => profile.ActualState == "Running RAW"));
        await factory.Get("COM12").SendAsync([0x11, 0x22]);
        CollectionAssert.AreEqual(new byte[] { 0x11, 0x22 }, await factory.Get("COM11").ReadWrittenAsync());
        await runtime.StopProfileAsync(1, "offline selected stop");
        Assert.HasCount(1, runtime.Profiles);
        Assert.AreEqual(2, runtime.Profiles[0].ProfileId);
        Assert.IsTrue(factory.Get("COM11").IsDisposed);
        Assert.IsFalse(factory.Get("COM21").IsDisposed);
        await factory.Get("COM21").SendAsync([0x33]);
        CollectionAssert.AreEqual(new byte[] { 0x33 }, await factory.Get("COM22").ReadWrittenAsync());
        await runtime.StartProfileAsync(settings, 1);
        Assert.HasCount(2, runtime.Profiles);
        Assert.HasCount(0, await runtime.StopAllAsync("offline all stop"));
        Assert.HasCount(0, runtime.Profiles);
    }

    [TestMethod]
    public async Task FailedAdditionalRawStartPreservesRunningProfileAndReleasesKnownClosedPorts()
    {
        var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory { FailedPortName = "COM21" };
        await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail());
        var settings = BridgeStartSelectionTests.Settings();
        await runtime.StartProfileAsync(settings, 1);
        await Assert.ThrowsExactlyAsync<IOException>(() => runtime.StartProfileAsync(settings, 2));
        Assert.HasCount(1, runtime.Profiles);
        Assert.AreEqual(1, runtime.Profiles[0].ProfileId);
        Assert.IsFalse(factory.Get("COM11").IsDisposed);
        Assert.IsTrue(factory.Get("COM22").IsDisposed);
    }

    [TestMethod]
    public async Task RuntimeRechecksActualPortOwnershipAgainstChangedSavedConfiguration()
    {
        var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail());
        var settings = BridgeStartSelectionTests.Settings();
        await runtime.StartProfileAsync(settings, 1);
        var changed = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id switch
            {
                1 => profile with { PhysicalPort = "COM51" },
                2 => profile with { PhysicalPort = "COM11" },
                _ => profile
            }).ToArray()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.StartProfileAsync(changed, 2));
        Assert.HasCount(1, runtime.Profiles);
        StringAssert.Contains(runtime.Conflict(changed.Profiles[1]), "COM11");
    }

    [TestMethod]
    public async Task FourIndependentRawProfilesCanRunAndStopTogether()
    {
        var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail());
        var settings = BridgeStartSelectionTests.Settings();
        foreach (var profile in settings.Profiles)
            await runtime.StartProfileAsync(settings, profile.Id);
        Assert.HasCount(4, runtime.Profiles);
        Assert.HasCount(8, factory.OpenedPorts);
        Assert.HasCount(0, await runtime.StopAllAsync("offline four-profile stop"));
        Assert.IsTrue(factory.OpenedPorts.All(port => factory.Get(port).IsDisposed));
    }

    [TestMethod]
    public async Task MixedRawAndIpcStopLastIpcServiceWithoutStoppingRawSibling()
    {
        var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail(),
            names: ComConCommandPipeNames.IsolatedTest($"ComConTest_mixed_{Guid.NewGuid():N}"));
        var settings = BridgeStartSelectionTests.Settings();
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 2
                ? profile with { CommandIpcEnabled = true } : profile).ToArray()
        };
        await runtime.StartProfileAsync(settings, 1);
        await runtime.StartProfileAsync(settings, 2);
        Assert.HasCount(2, runtime.Profiles);
        Assert.AreEqual("Running RAW", runtime.Profiles[0].ActualState);
        Assert.AreEqual("Running IPC", runtime.Profiles[1].ActualState);
        Assert.IsTrue(runtime.CommandSupervisor!.IpcAvailable);
        await runtime.StopProfileAsync(2, "offline last IPC stop");
        Assert.HasCount(1, runtime.Profiles);
        Assert.IsFalse(runtime.CommandSupervisor.IpcAvailable);
        Assert.IsFalse(factory.Get("COM11").IsDisposed);
        await factory.Get("COM11").SendAsync([0x44]);
        CollectionAssert.AreEqual(new byte[] { 0x44 }, await factory.Get("COM12").ReadWrittenAsync());
        await runtime.StartProfileAsync(settings, 2);
        Assert.IsTrue(runtime.CommandSupervisor.IpcAvailable);
    }

    [TestMethod]
    public async Task RawToIpcStopsOnlyChosenProfileBeforeReopeningItsPorts()
    {
        var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail(),
            names: ComConCommandPipeNames.IsolatedTest($"ComConTest_switch_{Guid.NewGuid():N}"));
        var settings = BridgeStartSelectionTests.Settings();
        await runtime.StartProfileAsync(settings, 1);
        await runtime.StartProfileAsync(settings, 2);
        var originalRadio = factory.Get("COM11");
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 1
                ? profile with { CommandIpcEnabled = true } : profile).ToArray()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.StartProfileAsync(settings, 1));
        Assert.IsFalse(originalRadio.IsDisposed);
        await runtime.StopProfileAsync(1, "offline confirmed replacement");
        await runtime.StartProfileAsync(settings, 1);
        Assert.IsTrue(originalRadio.IsDisposed);
        Assert.HasCount(2, runtime.Profiles);
        Assert.AreEqual("Running IPC", runtime.Profiles[0].ActualState);
        Assert.AreEqual("Running RAW", runtime.Profiles[1].ActualState);
        Assert.IsFalse(factory.Get("COM21").IsDisposed);
    }

    [TestMethod]
    public async Task FailedRawToIpcDoesNotRestartRawOrStopSuccessfulSibling()
    {
        var initial = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        var failing = new CommanderByteBridgeHostTests.FakeEndpointFactory { FailedPortName = "COM11" };
        var replacement = false;
        var reported = new List<string>();
        await using var runtime = new ComConBridgeRuntime(
            id => id == 1 && replacement ? failing : initial,
            (_, exception) => reported.Add(exception.Message),
            names: ComConCommandPipeNames.IsolatedTest($"ComConTest_switchfail_{Guid.NewGuid():N}"));
        var settings = BridgeStartSelectionTests.Settings();
        await runtime.StartProfileAsync(settings, 1);
        await runtime.StartProfileAsync(settings, 2);
        await runtime.StopProfileAsync(1, "offline confirmed replacement");
        replacement = true;
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 1
                ? profile with { CommandIpcEnabled = true } : profile).ToArray()
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => runtime.StartProfileAsync(settings, 1));
        Assert.IsTrue(settings.Profiles[0].CommandIpcEnabled);
        Assert.HasCount(1, runtime.Profiles);
        Assert.AreEqual(2, runtime.Profiles[0].ProfileId);
        Assert.IsFalse(initial.Get("COM21").IsDisposed);
        Assert.IsTrue(initial.Get("COM11").IsDisposed);
        Assert.IsTrue(failing.Get("COM12").IsDisposed);
    }

    [TestMethod]
    public async Task StopAllAttemptsEveryProfileAndRetainsFailedCloseReservation()
    {
        var factory = new CloseFailureFactory();
        var failures = new List<int>();
        var runtime = new ComConBridgeRuntime(_ => factory, (id, _) => failures.Add(id));
        try
        {
            var settings = BridgeStartSelectionTests.Settings();
            await runtime.StartProfileAsync(settings, 1);
            await runtime.StartProfileAsync(settings, 2);
            factory.FailClose = true;
            var errors = await runtime.StopAllAsync("offline uncertain stop");
            Assert.HasCount(1, errors);
            CollectionAssert.AreEqual(new[] { 1 }, failures);
            Assert.HasCount(1, runtime.Profiles);
            Assert.AreEqual(1, runtime.Profiles[0].ProfileId);
            Assert.AreEqual("Faulted", runtime.Profiles[0].ActualState);
            Assert.IsTrue(factory.Inner.Get("COM21").IsDisposed);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => runtime.StartProfileAsync(settings, 1));
        }
        finally
        {
            factory.FailClose = false;
            await runtime.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task ConcurrentRawCapturesKeepSeparateSessionsWhenOneStops()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ComConTest_stage13_capture_{Guid.NewGuid():N}");
        try
        {
            await using var capture = new RawCatCapture(path);
            capture.SetEnabled(true);
            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var runtime = new ComConBridgeRuntime(_ => factory, (_, _) => Assert.Fail(), capture);
            var settings = BridgeStartSelectionTests.Settings();
            await runtime.StartProfileAsync(settings, 1);
            await runtime.StartProfileAsync(settings, 2);
            await runtime.StopProfileAsync(1, "offline capture stop");
            Assert.AreEqual(RawCatCaptureStatus.Capturing, capture.Snapshot.Status);
            Assert.AreEqual(2, runtime.Profiles.Single().ProfileId);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    private sealed class CloseFailureFactory : IByteBridgeEndpointFactory
    {
        public readonly CommanderByteBridgeHostTests.FakeEndpointFactory Inner = new();
        public bool FailClose { get; set; }
        public async ValueTask<IByteBridgeEndpoint> OpenAsync(string portName, SerialSettings settings,
            CancellationToken cancellationToken)
        {
            var endpoint = await Inner.OpenAsync(portName, settings, cancellationToken);
            return new CloseFailureEndpoint(endpoint, this, portName);
        }
    }

    private sealed class CloseFailureEndpoint(
        IByteBridgeEndpoint inner, CloseFailureFactory factory, string port) : IByteBridgeEndpoint
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer, cancellationToken);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync()
        {
            if (factory.FailClose && port == "COM11") throw new IOException("Offline unknown close outcome.");
            return inner.DisposeAsync();
        }
    }
}
