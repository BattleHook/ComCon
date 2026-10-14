using ComCon.Core.Models;
using ComCon.Core.Presentation;
using ComCon.UI.ViewModels;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class BridgeStartSelectionTests
{
    internal static AppSettings Settings() => AppSettings.CreateDefault() with
    {
        Profiles = Enumerable.Range(1, 4).Select(id => new ProfileConfig
        {
            Id = id, Label = $"Radio {id}", Enabled = true, AutoConnect = true,
            PhysicalPort = $"COM{id * 10 + 1}", VirtualPort = $"COM{id * 10 + 2}",
            CommanderPort = $"COM{id * 10 + 3}", Framing = FramingMode.Civ,
            RadioAddress = 0x7A,
            Adapter = AdapterKind.Passthrough
        }).ToArray(),
        RigProfileMappings = Enumerable.Range(1, 4)
            .Select(id => new ComCon.Core.Ipc.ComConRigProfileMapping($"00000000-0000-0000-0000-00000000000{id}", id))
            .ToArray()
    };

    [TestMethod]
    public void ListShowsAllPortsSavedModeActualModeAndLocksRunningRows()
    {
        var settings = Settings();
        var active = settings.Profiles[0];
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 1
                ? profile with { CommandIpcEnabled = true } : profile).ToArray()
        };
        var rows = BridgeStartSelection.Create(settings,
            [new(active, true, "Running RAW", null, false, false)], 1, false, "test_cat");
        Assert.HasCount(4, rows);
        Assert.AreEqual("IPC", rows[0].SavedMode);
        Assert.AreEqual("Running RAW", rows[0].ActualState);
        Assert.AreEqual("COM13", rows[0].LegacyClientCom);
        Assert.AreEqual("COM12", rows[0].ComConVirtualCom);
        Assert.AreEqual("COM11", rows[0].TransceiverCom);
        Assert.AreEqual("Not used", rows[0].IpcDetails);
        Assert.IsFalse(rows[0].CanStart);
        rows[0].IsSelected = true;
        Assert.IsFalse(rows[0].IsSelected);
        Assert.IsTrue(rows[1].CanStart);
        Assert.IsFalse(rows[1].IsSelected);
    }

    [TestMethod]
    public void AutoconnectListChecksOnlySelectedEligibleStoppedIpc()
    {
        var settings = Settings();
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile with
            {
                CommandIpcEnabled = profile.Id != 4, AutoConnect = profile.Id != 3
            }).ToArray()
        };
        var rows = BridgeStartSelection.Create(settings, [], 2, true, "test_cat");
        Assert.IsTrue(rows[0].CanStart);
        Assert.IsFalse(rows[0].IsSelected);
        Assert.IsTrue(rows[1].IsSelected);
        Assert.IsFalse(rows[2].CanStart);
        Assert.IsFalse(rows[3].CanStart);
        Assert.AreEqual("Stopped", rows[1].ActualState);
        StringAssert.Contains(rows[1].IpcDetails, "test_cat");
        StringAssert.Contains(rows[1].Reason, "saved");
    }

    [TestMethod]
    public void RuntimePortReservationPreventsStartAndPeerCommanderPortIsNotOwned()
    {
        var settings = Settings();
        var active = settings.Profiles[0] with { PhysicalPort = settings.Profiles[1].VirtualPort };
        var rows = BridgeStartSelection.Create(settings,
            [new(active, true, "Faulted", "Close uncertain", false, false)], 2, false, "test_cat");
        Assert.IsFalse(rows[1].CanStart);
        StringAssert.Contains(rows[1].Reason, "COM22");
        Assert.AreEqual("COM22", rows[0].TransceiverCom);
        active = settings.Profiles[0] with { CommanderPort = settings.Profiles[1].PhysicalPort };
        rows = BridgeStartSelection.Create(settings, [new(active, true, "Running RAW", null, false, false)],
            2, false, "test_cat");
        Assert.IsTrue(rows[1].CanStart);
    }

    [TestMethod]
    public void SelectedBatchRejectsCrossModeEndpointConflict()
    {
        var settings = Settings();
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 2
                ? profile with { VirtualPort = settings.Profiles[0].PhysicalPort, CommandIpcEnabled = true }
                : profile).ToArray()
        };
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            BridgeStartSelection.ValidateSelectedPorts(settings, [1, 2]));
    }

    [TestMethod]
    public void ServiceFailureDisablesStoppedIpcCandidatesButLeavesRawCandidatesEligible()
    {
        var settings = Settings();
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 1
                ? profile with { CommandIpcEnabled = true } : profile).ToArray()
        };
        var rows = BridgeStartSelection.Create(settings, [], 1, false, "test_cat", "Listener unavailable");
        Assert.IsFalse(rows[0].CanStart);
        Assert.IsFalse(rows[0].IsSelected);
        StringAssert.Contains(rows[0].Reason, "Recover IPC");
        Assert.IsTrue(rows[1].CanStart);
    }

    [TestMethod]
    public void RunningRawAllowsOnlySavedOptInAndStoppedSiblingEdits()
    {
        var model = new MainViewModel();
        var settings = Settings();
        model.Load(settings);
        model.SetActiveProfiles([settings.Profiles[0]], [1]);
        Assert.IsFalse(model.IsDirty);
        Assert.IsFalse(model.SelectedProfileEditable);
        Assert.IsTrue(model.SelectedProfileCanEnableIpc);
        model.Profiles[0].CommandIpcEnabled = true;
        Assert.IsFalse(model.SelectedProfileCanEnableIpc);
        model.Profiles[1].Label = "Stopped radio edited";
        var exported = model.Export();
        model.Saved(exported);
        Assert.IsTrue(model.SavedSettings.Profiles[0].CommandIpcEnabled);
        model.Profiles[0].PhysicalPort = "COM99";
        Assert.ThrowsExactly<InvalidOperationException>(() => model.Export());
        model.DiscardDraft();
        Assert.AreEqual("COM11", model.Profiles[0].PhysicalPort);
        Assert.IsTrue(model.Profiles[0].CommandIpcEnabled);
        model.Profiles[0].CommandIpcEnabled = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => model.Export());
        model.DiscardDraft();
        model.SelectedProfile = model.Profiles[1];
        Assert.IsTrue(model.SelectedProfileEditable);
        Assert.IsFalse(model.IsDirty);
    }

    [TestMethod]
    public void RunningIpcSettingsAndMappingsCannotBeSaved()
    {
        var settings = Settings();
        settings = settings with
        {
            Profiles = settings.Profiles.Select(profile => profile.Id == 1
                ? profile with { CommandIpcEnabled = true } : profile).ToArray(),
            RigProfileMappings = [new("12345678-abcd-4321-abcd-123456789abc", 1)]
        };
        var model = new MainViewModel();
        model.Load(settings);
        model.SetActiveProfiles([settings.Profiles[0]], []);
        Assert.IsFalse(model.SelectedProfileCanEnableIpc);
        Assert.IsTrue(model.RigProfileMappingEditors[0].IsRuntimeLocked);
        model.RigProfileMappingEditors[0].ModelName = "Changed";
        Assert.ThrowsExactly<InvalidOperationException>(() => model.Export());
        model.DiscardDraft();
        model.Profiles[0].CommandIpcEnabled = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => model.Export());
    }

    [TestMethod]
    public void SavingStoppedSiblingPreservesExactRunningMappingAndDoesNotStartAnything()
    {
        var settings = Settings() with
        {
            RigProfileMappings = [new("12345678-abcd-4321-abcd-123456789abc", 1)
            {
                RigModelName = "  Existing description  "
            }]
        };
        var model = new MainViewModel();
        model.Load(settings);
        model.SetActiveProfiles([settings.Profiles[0]], [1]);
        model.SelectedProfile = model.Profiles[1];
        model.SelectedProfile!.AutoConnect = false;
        var candidate = model.Export();
        Assert.AreEqual("  Existing description  ", candidate.RigProfileMappings[0].RigModelName);
        model.Saved(candidate);
        Assert.IsFalse(model.IsDirty);
        Assert.IsTrue(model.SelectedProfileEditable);
        Assert.IsFalse(model.SavedSettings.Profiles[1].AutoConnect);
    }
}
