using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.UI.ViewModels;
using ComCon.Core.Configuration;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class PassiveTelemetryLifecycleTests
{
    [TestMethod]
    public void SyntheticBridgeConfigurationsKeepOverlapGuardAndAutoconnectOff()
    {
        var first = ConfigurationJson.Deserialize(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "BridgeProfiles", "SyntheticPairA.json")));
        var second = ConfigurationJson.Deserialize(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "BridgeProfiles", "SyntheticPairB.json")));
        var a = first.Profiles.Single();
        var b = second.Profiles.Single();
        Assert.AreEqual(1, a.Id);
        Assert.AreEqual(2, b.Id);
        Assert.AreEqual("Synthetic bridge pair A", a.Label);
        Assert.AreEqual("Synthetic bridge pair B", b.Label);
        Assert.AreEqual("COM121", a.CommanderPort);
        Assert.AreEqual("COM111", a.VirtualPort);
        Assert.AreEqual("COM122", b.CommanderPort);
        Assert.AreEqual("COM112", b.VirtualPort);
        Assert.AreEqual("COM101", a.PhysicalPort);
        Assert.AreEqual(a.PhysicalPort, b.PhysicalPort);
        Assert.IsTrue(a.Enabled);
        Assert.IsTrue(b.Enabled);
        Assert.IsFalse(a.AutoConnect);
        Assert.IsFalse(b.AutoConnect);
        Assert.IsFalse(a.CommandIpcEnabled);
        Assert.IsFalse(b.CommandIpcEnabled);
        Assert.AreEqual(19200, a.SerialSettings.BaudRate);
        Assert.AreEqual(19200, b.SerialSettings.BaudRate);
        Assert.ThrowsExactly<ConfigurationValidationException>(() =>
            ConfigurationJson.Serialize(first with { Profiles = [a, b] }));
    }

    [TestMethod]
    public async Task StationCompositionCannotEnableCaptureAndDoesNotOpenForEnableRequest()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = new CommanderByteBridgeHost(endpoints);
        Assert.AreEqual("Off", bridge.Telemetry.Snapshot.State);
        Assert.IsFalse(bridge.Telemetry.Snapshot.Enabled);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(bridge.Telemetry.EnableSimulationAsync);
        Assert.IsEmpty(endpoints.OpenedPorts);
        await bridge.StartAsync(Profile());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(bridge.Telemetry.EnableSimulationAsync);
        Assert.HasCount(2, endpoints.OpenedPorts);
        Assert.IsNull(bridge.Telemetry.SimulationJournal);
    }

    [TestMethod]
    public async Task ExplicitCaptureRequiresRunningBridgeAndClearsOnDisableStopAndProfileChange()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(bridge.Telemetry.EnableSimulationAsync);
        Assert.IsEmpty(endpoints.OpenedPorts);
        await bridge.StartAsync(Profile());
        var off = bridge.Telemetry.Snapshot;
        Assert.AreEqual("Off", off.State);
        await bridge.Telemetry.EnableSimulationAsync();
        var first = bridge.Telemetry.Snapshot;
        Assert.AreNotEqual(off.ServerInstanceId, first.ServerInstanceId);
        Assert.IsTrue(first.Enabled);
        Assert.IsFalse(first.SafetyConfirmation);
        var journal = bridge.Telemetry.SimulationJournal!;
        await endpoints.Get("COM3").SendAsync(Convert.FromHexString("FEFEE07A1C0000FD"));
        await endpoints.Get("COM31").ReadWrittenAsync();
        await WaitAsync(() => journal.ReadAfter(0).Observations.Count != 0);
        await bridge.Telemetry.DisableAsync();
        Assert.ThrowsExactly<InvalidDataException>(() => journal.ReadAfter(0));
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        Assert.AreEqual("Off", bridge.Telemetry.Snapshot.State);
        await bridge.Telemetry.EnableSimulationAsync();
        var second = bridge.Telemetry.Snapshot;
        Assert.AreNotEqual(first.ServerInstanceId, second.ServerInstanceId);
        var secondJournal = bridge.Telemetry.SimulationJournal!;
        await bridge.StopAsync();
        Assert.AreEqual("Off", bridge.Telemetry.Snapshot.State);
        Assert.IsNull(bridge.Telemetry.SimulationJournal);
        Assert.ThrowsExactly<InvalidDataException>(() => secondJournal.ReadAfter(0));
        await bridge.StartAsync(Profile() with
        {
            Id = 2, Label = "Same radio through second virtual pair", PhysicalPort = "COM3",
            VirtualPort = "COM41", CommanderPort = "COM22"
        });
        var switched = bridge.Telemetry.Snapshot;
        Assert.AreEqual(2, switched.ProfileId);
        Assert.AreEqual("Off", switched.State);
        Assert.AreNotEqual(second.ServerInstanceId, switched.ServerInstanceId);
        Assert.AreNotEqual(second.ProfileGeneration, switched.ProfileGeneration);
        Assert.HasCount(4, endpoints.OpenedPorts);
        Assert.AreEqual("COM3", endpoints.OpenedPorts[3]);
        Assert.IsNull(bridge.Telemetry.SimulationJournal);
    }

    [TestMethod]
    public async Task FramingFaultIsVisibleButDoesNotStopBridgeOrAutomaticallyRecover()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await bridge.StartAsync(Profile());
        await bridge.Telemetry.EnableSimulationAsync();
        await endpoints.Get("COM3").SendAsync([0xFC]);
        await endpoints.Get("COM31").ReadWrittenAsync();
        await WaitAsync(() => bridge.Telemetry.Snapshot.Fault is not null);
        Assert.AreEqual("Faulted", bridge.Telemetry.Snapshot.State);
        Assert.IsFalse(bridge.Telemetry.Snapshot.Enabled);
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(bridge.Telemetry.EnableSimulationAsync);
        await bridge.Telemetry.DisableAsync();
        await bridge.Telemetry.EnableSimulationAsync();
        Assert.IsTrue(bridge.Telemetry.Snapshot.Enabled);
    }

    [TestMethod]
    public async Task UnsupportedFramingCannotEnableAndRelayFaultInvalidatesCapture()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await bridge.StartAsync(Profile() with { Framing = FramingMode.None });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(bridge.Telemetry.EnableSimulationAsync);
        await endpoints.Get("COM3").FailReadAsync(new IOException("offline endpoint fault"));
        await WaitAsync(() => bridge.Telemetry.Snapshot.State == "Faulted");
        StringAssert.Contains(bridge.Telemetry.Snapshot.Fault, "offline endpoint fault");
        Assert.IsFalse(bridge.Telemetry.Snapshot.Enabled);
    }

    [TestMethod]
    public void ProfileSwitchCanBeCancelledAndRunningSiblingAllowsSelectionButBlocksLoading()
    {
        var model = new MainViewModel();
        var original = model.SelectedProfile;
        var calls = 0;
        model.ConfirmProfileSwitch = (previous, next) =>
        {
            calls++;
            Assert.AreSame(original, previous);
            Assert.AreEqual(2, next.Id);
            return false;
        };
        model.SelectedProfile = model.Profiles[1];
        Assert.AreSame(original, model.SelectedProfile);
        Assert.AreEqual(1, calls);
        model.ConfirmProfileSwitch = (_, _) => true;
        model.SelectedProfile = model.Profiles[1];
        StringAssert.Contains(model.TelemetryStatus, "Profile 2");
        StringAssert.Contains(model.TelemetryStatus, "OFF");
        model.SetActiveProfiles([model.Export().Profiles.Single(profile => profile.Id == 2)], [2]);
        model.SelectedProfile = model.Profiles[0];
        Assert.AreSame(model.Profiles[0], model.SelectedProfile);
        Assert.ThrowsExactly<InvalidOperationException>(() => model.Load(model.Export()));
    }

    [TestMethod]
    public void DevelopmentLogPersistsBoundedFaultAndSurfacesWriteFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ComConTest_fault_{Guid.NewGuid():N}.jsonl");
        try
        {
            new DevelopmentFaultLog(path).Append("test", 2, new string('x', 3000));
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(2048, json.RootElement.GetProperty("message").GetString()!.Length);
            Assert.AreEqual(2, json.RootElement.GetProperty("profileId").GetInt32());
            Assert.IsTrue(json.RootElement.TryGetProperty("timestampUtc", out _));
            Assert.ThrowsExactly<IOException>(() =>
                new DevelopmentFaultLog(Path.Combine(path, "impossible.jsonl")).Append("test", 1, "fault"));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void DevelopmentLogRejectsOversizedRecordBeforeCreatingFiles()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ComConTest_faultlarge_{Guid.NewGuid():N}.jsonl");
        var log = new DevelopmentFaultLog(path);
        Assert.ThrowsExactly<ArgumentException>(() =>
            log.Append(new string('x', DevelopmentFaultLog.MaximumFileBytes), 2, "fault"));
        Assert.IsFalse(File.Exists(path));
    }

    private static ProfileConfig Profile() =>
        CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ };

    private static async Task WaitAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!ready()) await Task.Delay(5, timeout.Token);
    }
}
