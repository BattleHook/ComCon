using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.UI.ViewModels;
using System.Text.Json.Nodes;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ProfileEditorTests
{
    [TestMethod]
    public void RigMappingDisplayIncludesDescriptionsAndKeepsUuidVisible()
    {
        var mapping = new ComConRigProfileMapping("12345678-abcd-4321-abcd-123456789abc", 4)
        {
            RigModelName = "Icom IC-7600",
            CivAddress = "7a"
        };

        Assert.AreEqual(
            "Icom IC-7600; CI-V address 7A; UUID 12345678-abcd-4321-abcd-123456789abc -> Profile 4 'Shared radio'",
            MainViewModel.FormatRigProfileMapping(mapping, "Shared radio"));
        Assert.AreEqual(
            "UUID 12345678-abcd-4321-abcd-123456789abc -> Profile 4",
            MainViewModel.FormatRigProfileMapping(new(mapping.RigProfileId, mapping.ProfileId)));
    }

    [TestMethod]
    public void ExistingRigMappingDescriptionsEditWithoutChangingIdentityAndAddressIsCivOnly()
    {
        var defaults = AppSettings.CreateDefault();
        var settings = defaults with
        {
            Profiles = defaults.Profiles.Select(profile => profile.Id == 1
                ? profile with { Framing = FramingMode.Civ }
                : profile).ToArray(),
            RigProfileMappings =
            [
                new ComConRigProfileMapping("12345678-abcd-4321-abcd-123456789abc", 1)
            ]
        };
        var model = new MainViewModel();
        model.Load(settings);
        var mapping = model.RigProfileMappingEditors.Single();

        Assert.IsTrue(mapping.SupportsCivAddress);
        mapping.ModelName = "Icom IC-7600";
        mapping.CivAddress = "7A";
        var exported = model.Export().RigProfileMappings.Single();
        Assert.AreEqual(settings.RigProfileMappings[0].RigProfileId, exported.RigProfileId);
        Assert.AreEqual(settings.RigProfileMappings[0].ProfileId, exported.ProfileId);
        Assert.AreEqual("Icom IC-7600", exported.RigModelName);
        Assert.AreEqual("7A", exported.CivAddress);
        Assert.IsTrue(model.IsDirty);

        model.SelectedProfile!.Framing = FramingMode.KenwoodAscii;
        Assert.IsFalse(mapping.SupportsCivAddress);
        Assert.AreEqual("Not applicable", mapping.CivAddressDisplay);
        Assert.AreEqual("", mapping.CivAddress);
        Assert.IsNull(model.Export().RigProfileMappings.Single().CivAddress);
    }

    [TestMethod]
    public void OfflineProfileEditsAndSaveRetainLoadedMappings()
    {
        var mappings = new[]
        {
            new ComConRigProfileMapping("12345678-abcd-4321-abcd-123456789abc", 4)
            {
                RigModelName = "Icom IC-7600",
                CivAddress = "7A"
            }
        };
        var model = new MainViewModel();
        var defaults = AppSettings.CreateDefault();
        model.Load(ConfigurationJson.Deserialize(ConfigurationJson.Serialize(defaults with
        {
            Profiles = defaults.Profiles.Select(profile => profile.Id == 4
                ? profile with { Framing = FramingMode.Civ }
                : profile).ToArray(),
            RigProfileMappings = mappings
        })));
        model.Profiles[0].Label = "Edited offline";
        var exported = model.Export();
        CollectionAssert.AreEqual(mappings, exported.RigProfileMappings.ToArray());
        model.Saved(exported);
        model.Profiles[1].Label = "Another edit";
        var reloaded = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(model.Export()));
        CollectionAssert.AreEqual(mappings, reloaded.RigProfileMappings.ToArray());
        Assert.AreEqual("Edited offline", reloaded.Profiles[0].Label);
        Assert.AreEqual("Another edit", reloaded.Profiles[1].Label);
    }

    [TestMethod]
    public void ExistingUnloadedSaveTargetGivesActionableGuidanceWithoutChangingDraft()
    {
        var model = new MainViewModel();
        model.Profiles[0].Label = "Keep draft";
        var existing = typeof(ProfileEditorTests).Assembly.Location;
        var error = Assert.ThrowsExactly<ConfigurationConcurrencyException>(() =>
            MainViewModel.SelectSaveStore(existing, null));
        StringAssert.Contains(error.Message, "Choose a new filename");
        StringAssert.Contains(error.Message, "Load configuration");
        Assert.AreEqual("Keep draft", model.Profiles[0].Label);
        Assert.IsTrue(model.IsDirty);
    }

    [TestMethod]
    public void SameSaveTargetRetainsStoreSnapshotWhileNewPathGetsItsOwnStore()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"comcon-{Guid.NewGuid():N}.json");
        var store = new ConfigurationFileStore(path);
        Assert.AreSame(store, MainViewModel.SelectSaveStore(path, store));
        var otherPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"comcon-{Guid.NewGuid():N}.json");
        var other = MainViewModel.SelectSaveStore(otherPath, store);
        Assert.AreNotSame(store, other);
        Assert.AreEqual(otherPath, other.FilePath);
    }

    [TestMethod]
    public void PortRefreshDeduplicatesNumericallyAndPreservesDraftsAndSelection()
    {
        var model = new MainViewModel();
        model.Profiles[0].PhysicalPort = "COM99";
        model.Profiles[0].VirtualPort = "COM100";
        model.Profiles[0].CommanderPort = "COM101";
        var selected = model.SelectedProfile;
        model.RefreshPorts(["COM10", "com2", "COM1", "COM2"]);
        CollectionAssert.AreEqual(new[] { "COM1", "COM2", "COM10" }, model.AvailablePorts.ToArray());
        Assert.AreSame(selected, model.SelectedProfile);
        Assert.AreEqual("COM99", model.Profiles[0].PhysicalPort);
        Assert.AreEqual("COM100", model.Profiles[0].VirtualPort);
        Assert.AreEqual("COM101", model.Profiles[0].CommanderPort);
        Assert.IsTrue(model.IsDirty);
    }

    [TestMethod]
    public void RefreshRestoresTextClearedByItemSourceBindingWithoutDirtyingCleanDraft()
    {
        var model = new MainViewModel();
        model.Profiles[0].PhysicalPort = "COM99";
        model.Saved(model.Export());
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.AvailablePorts))
                model.Profiles[0].PhysicalPort = "";
        };
        model.RefreshPorts(["COM1"]);
        Assert.AreEqual("COM99", model.Profiles[0].PhysicalPort);
        Assert.IsFalse(model.IsDirty);
        model.RefreshPorts([]);
        Assert.IsEmpty(model.AvailablePorts);
        Assert.AreEqual("COM99", model.Profiles[0].PhysicalPort);
        Assert.IsFalse(model.IsDirty);
    }

    [TestMethod]
    public void InvalidPortEnumerationKeepsPreviousListAndSurfacesError()
    {
        var model = new MainViewModel();
        model.RefreshPorts(["COM3"]);
        foreach (var invalid in new[] { "COM0", "COM01", "CNCA0", " COM1", "COM2147483648", "COM+2" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => model.RefreshPorts(["COM4", invalid]));
            CollectionAssert.AreEqual(new[] { "COM3" }, model.AvailablePorts.ToArray());
            Assert.IsFalse(model.IsDirty);
        }
        Assert.ThrowsExactly<ArgumentNullException>(() => model.RefreshPorts(null!));
    }

    [TestMethod]
    public void NewUiDraftIsDisabledUnassignedAndModelNeutral()
    {
        var model = new MainViewModel();
        Assert.HasCount(4, model.Profiles);
        var settings = model.Export();
        foreach (var profile in settings.Profiles)
        {
            Assert.IsFalse(profile.Enabled);
            Assert.IsFalse(profile.AutoConnect);
            Assert.IsNull(profile.PhysicalPort);
            Assert.IsNull(profile.VirtualPort);
            Assert.IsNull(profile.CommanderPort);
            Assert.AreEqual(AdapterKind.Passthrough, profile.Adapter);
            Assert.AreEqual(FramingMode.None, profile.Framing);
        }
        Assert.IsFalse(model.IsDirty);
    }

    [TestMethod]
    public void EditedPortsSerialAndFramingRoundTripWithoutStartingConnections()
    {
        var model = new MainViewModel();
        var profile = model.Profiles[0];
        profile.PhysicalPort = "COM3";
        profile.VirtualPort = "COM30";
        profile.CommanderPort = "COM31";
        profile.Enabled = true;
        profile.AutoConnect = true;
        profile.Label = "Broker one";
        profile.BaudRate = "115200";
        profile.DataBits = "7";
        profile.Parity = SerialParity.Even;
        profile.StopBits = SerialStopBits.Two;
        profile.Handshake = SerialHandshake.XOnXOff;
        profile.DtrEnable = true;
        profile.RtsEnable = true;
        profile.Framing = FramingMode.KenwoodAscii;
        Assert.IsTrue(model.IsDirty);
        var settings = model.Export();
        var copy = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(settings)).Profiles[0];
        Assert.AreEqual("COM3", copy.PhysicalPort);
        Assert.AreEqual("COM30", copy.VirtualPort);
        Assert.AreEqual("COM31", copy.CommanderPort);
        Assert.AreEqual("Broker one", copy.Label);
        Assert.IsTrue(copy.Enabled);
        Assert.IsTrue(copy.AutoConnect);
        Assert.AreEqual(115200, copy.SerialSettings.BaudRate);
        Assert.AreEqual(7, copy.SerialSettings.DataBits);
        Assert.AreEqual(SerialParity.Even, copy.SerialSettings.Parity);
        Assert.AreEqual(SerialStopBits.Two, copy.SerialSettings.StopBits);
        Assert.AreEqual(SerialHandshake.XOnXOff, copy.SerialSettings.Handshake);
        Assert.IsTrue(copy.SerialSettings.DtrEnable);
        Assert.IsTrue(copy.SerialSettings.RtsEnable);
        Assert.AreEqual(FramingMode.KenwoodAscii, copy.Framing);
        model.Saved(settings);
        Assert.IsFalse(model.IsDirty);
        profile.Label = "Further edit";
        Assert.IsTrue(model.IsDirty);
    }

    [TestMethod]
    public void PortOverlapAndMissingEnabledDeclarationsSurfaceValidation()
    {
        var model = new MainViewModel();
        model.Profiles[0].Enabled = true;
        var missing = Assert.ThrowsExactly<ConfigurationValidationException>(() => model.Export());
        Assert.IsTrue(missing.Errors.Any(e => e.Path.EndsWith(".physicalPort", StringComparison.Ordinal)));
        var profile = model.Profiles[0];
        profile.PhysicalPort = "COM3";
        profile.VirtualPort = "COM30";
        profile.CommanderPort = "COM31";
        model.Profiles[1].PhysicalPort = "com30";
        var overlap = Assert.ThrowsExactly<ConfigurationValidationException>(() => model.Export());
        Assert.IsTrue(overlap.Errors.Any(e => e.Message.Contains("Overlaps", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void InvalidNumericDraftsAreNotSilentlyCoercedOrDiscarded()
    {
        var model = new MainViewModel();
        foreach (var invalid in new[] { "", "-1", "abc", " 19200", "2147483648" })
        {
            model.Profiles[0].BaudRate = invalid;
            var error = Assert.ThrowsExactly<ConfigurationValidationException>(() => model.Export());
            Assert.AreEqual("profiles[0].serialSettings.baudRate", error.Errors[0].Path);
            Assert.AreEqual(invalid, model.Profiles[0].BaudRate);
            Assert.IsTrue(model.IsDirty);
        }
    }

    [TestMethod]
    public void NumericRangesAndIncoherentSerialOptionsUseExistingValidator()
    {
        var model = new MainViewModel();
        model.Profiles[0].DataBits = "9";
        Assert.ThrowsExactly<ConfigurationValidationException>(() => model.Export());
        model.Profiles[0].DataBits = "8";
        model.Profiles[0].StopBits = SerialStopBits.OnePointFive;
        Assert.ThrowsExactly<ConfigurationValidationException>(() => model.Export());
        model.Profiles[0].DataBits = "5";
        Assert.AreEqual(5, model.Export().Profiles[0].SerialSettings.DataBits);
        model.Profiles[0].AutoConnect = true;
        Assert.ThrowsExactly<ConfigurationValidationException>(() => model.Export());
    }

    [TestMethod]
    public void LoadPreservesLegacyMetadataAndRoutingLimitsWithoutAddingCommandPolicy()
    {
        var settings = AppSettings.CreateDefault() with
        {
            RoutingLimits = new RoutingLimits { NormalQueueCapacityPerProfile = 16 }
        };
        settings = settings with
        {
            Profiles = settings.Profiles.Select(p => p with
            {
                RadioAddress = 0x7A, ControllerAddress = 0xE1, Framing = FramingMode.Civ
            }).ToArray()
        };
        var model = new MainViewModel();
        model.Load(settings);
        model.Profiles[0].Label = "Edited";
        var exported = model.Export();
        Assert.AreEqual(settings.RoutingLimits, exported.RoutingLimits);
        Assert.AreEqual(AdapterKind.IcomIc7600, exported.Profiles[0].Adapter);
        Assert.AreEqual(0x7A, exported.Profiles[0].RadioAddress);
        Assert.AreEqual(0xE1, exported.Profiles[0].ControllerAddress);
        Assert.AreEqual(FramingMode.Civ, exported.Profiles[0].Framing);
    }

    [TestMethod]
    public void InvalidLoadLeavesCurrentDraftUntouched()
    {
        var model = new MainViewModel();
        var first = model.Profiles[0];
        first.Label = "Keep me";
        Assert.ThrowsExactly<ConfigurationValidationException>(() =>
            model.Load(AppSettings.CreateDefault() with { SchemaVersion = 99 }));
        Assert.AreSame(first, model.Profiles[0]);
        Assert.AreEqual("Keep me", model.Profiles[0].Label);
        Assert.IsTrue(model.IsDirty);
    }

    [TestMethod]
    public void ReplacedDraftUnsubscribesOldProfilesAndNotifiesSelection()
    {
        var model = new MainViewModel();
        var old = model.Profiles[0];
        var changed = new List<string?>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        model.Load(model.Export());
        Assert.AreSame(model.Profiles[0], model.SelectedProfile);
        old.Label = "Detached";
        Assert.IsFalse(model.IsDirty);
        CollectionAssert.Contains(changed, nameof(MainViewModel.Profiles));
        CollectionAssert.Contains(changed, nameof(MainViewModel.SelectedProfile));
    }

    [TestMethod]
    public void UnknownFramingValuesAreRejectedAndMissingFieldRemainsCompatible()
    {
        var settings = AppSettings.CreateDefault();
        var invalid = settings with
        {
            Profiles = [settings.Profiles[0] with { Framing = (FramingMode)99 }]
        };
        Assert.ThrowsExactly<ConfigurationValidationException>(() => ConfigurationJson.Serialize(invalid));
        var json = ConfigurationJson.Serialize(settings);
        Assert.ThrowsExactly<ConfigurationSerializationException>(() =>
            ConfigurationJson.Deserialize(json.Replace("\"framing\": \"none\"", "\"framing\": \"unknown\"", StringComparison.Ordinal)));
        var older = JsonNode.Parse(json);
        Assert.IsNotNull(older);
        var profiles = older["profiles"]?.AsArray();
        Assert.IsNotNull(profiles);
        foreach (var profile in profiles)
        {
            Assert.IsNotNull(profile);
            Assert.IsTrue(profile.AsObject().Remove("framing"));
        }
        Assert.AreEqual(FramingMode.None, ConfigurationJson.Deserialize(older.ToJsonString()).Profiles[0].Framing);
    }
}
