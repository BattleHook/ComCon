using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ConfigurationTests
{
    private const string Rig = "12345678-abcd-4321-abcd-123456789abc";
    private const string OtherRig = "87654321-abcd-4321-abcd-123456789abc";

    [TestMethod]
    public void RigMappingsAreCopiedReadOnlyAndRoundTripWithMultipleRigsPerProfile()
    {
        var mappings = new[]
        {
            new ComConRigProfileMapping(Rig, 1) { RigModelName = "Icom IC-7600", CivAddress = "7A" },
            new ComConRigProfileMapping(OtherRig, 1)
        };
        var defaults = AppSettings.CreateDefault();
        var settings = defaults with
        {
            Profiles = defaults.Profiles.Select(profile => profile.Id == 1
                ? profile with { Framing = FramingMode.Civ }
                : profile).ToArray(),
            RigProfileMappings = mappings
        };
        mappings[0] = new(Rig, 4);
        Assert.AreEqual(1, settings.RigProfileMappings[0].ProfileId);
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ComConRigProfileMapping>)settings.RigProfileMappings).Clear());
        var json = ConfigurationJson.Serialize(settings);
        StringAssert.Contains(json, "\"rigProfileMappings\"");
        var loaded = ConfigurationJson.Deserialize(json);
        CollectionAssert.AreEqual(settings.RigProfileMappings.ToArray(), loaded.RigProfileMappings.ToArray());
    }

    [TestMethod]
    public void OldConfigurationWithoutMappingsLoadsEmptyAndExplicitNullFails()
    {
        var loaded = ConfigurationJson.Deserialize("""{"schemaVersion":1,"profiles":[{"id":1,"label":"One"}]}""");
        Assert.IsEmpty(loaded.RigProfileMappings);
        Assert.IsEmpty(AppSettings.CreateDefault().RigProfileMappings);
        Assert.IsFalse(loaded.Profiles[0].CommandIpcEnabled);
        Assert.AreEqual(0xE0, loaded.Profiles[0].LegacyClientAddress);
        Assert.AreEqual(0xE1, loaded.Profiles[0].IpcClientAddress);
        Assert.ThrowsExactly<ConfigurationSerializationException>(() => ConfigurationJson.Deserialize(
            """{"profiles":[{"id":1,"label":"One"}],"rigProfileMappings":null}"""));
    }

    [TestMethod]
    public void RigMappingDescriptionsAreOptionalBackwardCompatibleAndValidated()
    {
        var legacy = ConfigurationJson.Deserialize(
            """{"schemaVersion":1,"profiles":[{"id":1,"label":"One","framing":"civ"}],"rigProfileMappings":[{"rigProfileId":"12345678-abcd-4321-abcd-123456789abc","profileId":1}]}""");
        Assert.IsNull(legacy.RigProfileMappings[0].RigModelName);
        Assert.IsNull(legacy.RigProfileMappings[0].CivAddress);
        Assert.IsFalse(ConfigurationJson.Serialize(legacy).Contains("\"rigModelName\"", StringComparison.Ordinal));
        Assert.IsFalse(ConfigurationJson.Serialize(legacy).Contains("\"civAddress\"", StringComparison.Ordinal));

        var described = legacy with
        {
            RigProfileMappings =
            [
                legacy.RigProfileMappings[0] with { RigModelName = "Icom IC-7600", CivAddress = "7A" }
            ]
        };
        var json = ConfigurationJson.Serialize(described);
        StringAssert.Contains(json, "\"rigModelName\": \"Icom IC-7600\"");
        StringAssert.Contains(json, "\"civAddress\": \"7A\"");
        CollectionAssert.AreEqual(described.RigProfileMappings.ToArray(),
            ConfigurationJson.Deserialize(json).RigProfileMappings.ToArray());

        var invalid = described with
        {
            RigProfileMappings =
            [
                described.RigProfileMappings[0] with { CivAddress = "7G" }
            ]
        };
        var error = Assert.ThrowsExactly<ConfigurationValidationException>(() => ConfigurationJson.Serialize(invalid));
        Assert.AreEqual("rigProfileMappings[0].civAddress", error.Errors.Single().Path);

        var nonCiv = described with
        {
            Profiles = described.Profiles.Select(profile => profile with
            {
                Framing = FramingMode.KenwoodAscii
            }).ToArray()
        };
        error = Assert.ThrowsExactly<ConfigurationValidationException>(() => ConfigurationJson.Serialize(nonCiv));
        Assert.AreEqual("rigProfileMappings[0].civAddress", error.Errors.Single().Path);
    }

    [TestMethod]
    public void CommandModeProfileSettingsAreEditableDistinctAndBackwardCompatible()
    {
        var profile = AppSettings.CreateDefault().Profiles[0] with
        {
            Enabled = true,
            Adapter = AdapterKind.Passthrough,
            Framing = FramingMode.Civ,
            PhysicalPort = "COM11",
            VirtualPort = "COM12",
            CommanderPort = "COM13"
        };
        var editor = new ProfileEditor(profile)
        {
            CommandIpcEnabled = true,
            LegacyClientAddress = "e2",
            IpcClientAddress = "E3"
        };
        var saved = editor.Export(0);
        Assert.IsTrue(saved.CommandIpcEnabled);
        Assert.AreEqual(0xE2, saved.LegacyClientAddress);
        Assert.AreEqual(0xE3, saved.IpcClientAddress);

        var settings = AppSettings.CreateDefault() with
        {
            Profiles = [saved],
            RigProfileMappings = [new(Rig, saved.Id)]
        };
        Assert.IsEmpty(ConfigurationValidator.Validate(settings));
        var reloaded = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(settings));
        Assert.IsTrue(reloaded.Profiles[0].CommandIpcEnabled);
        Assert.AreEqual(0xE2, reloaded.Profiles[0].LegacyClientAddress);
        Assert.AreEqual(0xE3, reloaded.Profiles[0].IpcClientAddress);
    }

    [TestMethod]
    public void CommandModeRequiresEnabledProfileDistinctAddressesAndRigMapping()
    {
        var profile = AppSettings.CreateDefault().Profiles[0] with
        {
            CommandIpcEnabled = true,
            Framing = FramingMode.Civ
        };
        var noMapping = AppSettings.CreateDefault() with { Profiles = [profile] };
        Assert.IsTrue(ConfigurationValidator.Validate(noMapping)
            .Any(error => error.Path == "profiles[0].commandIpcEnabled"));

        var sameAddress = noMapping with
        {
            Profiles = [profile with { Enabled = true, IpcClientAddress = profile.LegacyClientAddress }],
            RigProfileMappings = [new(Rig, profile.Id)]
        };
        Assert.IsTrue(ConfigurationValidator.Validate(sameAddress)
            .Any(error => error.Path == "profiles[0].ipcClientAddress"));

        var disabled = noMapping with
        {
            RigProfileMappings = [new(Rig, profile.Id)]
        };
        Assert.IsTrue(ConfigurationValidator.Validate(disabled)
            .Any(error => error.Path == "profiles[0].commandIpcEnabled"));
    }

    [TestMethod]
    public void CommandModeClientAddressEditorRequiresExactlyTwoHexDigits()
    {
        var editor = new ProfileEditor(AppSettings.CreateDefault().Profiles[0])
        {
            LegacyClientAddress = "0xE0"
        };
        var error = Assert.ThrowsExactly<ConfigurationValidationException>(() => editor.Export(0));
        Assert.AreEqual("profiles[0].legacyClientAddress", error.Errors.Single().Path);
    }

    [TestMethod]
    [DataRow("12345678-ABCD-4321-ABCD-123456789ABC")]
    [DataRow("12345678abcd4321abcd123456789abc")]
    [DataRow("")]
    [DataRow(null)]
    public void MappingUuidMustBeCanonical(string? rig)
    {
        var settings = AppSettings.CreateDefault() with { RigProfileMappings = [new(rig!, 1)] };
        var error = ConfigurationValidator.Validate(settings).Single();
        Assert.AreEqual("rigProfileMappings[0].rigProfileId", error.Path);
        Assert.ThrowsExactly<ConfigurationValidationException>(() => ConfigurationJson.Serialize(settings));
    }

    [TestMethod]
    public void MappingValidationRejectsDuplicateMissingAndNullEntries()
    {
        var defaults = AppSettings.CreateDefault();
        foreach (var target in new[] { 1, 4 })
        {
            var settings = defaults with { RigProfileMappings = [new(Rig, 1), new(Rig, target)] };
            Assert.AreEqual("rigProfileMappings[1].rigProfileId", ConfigurationValidator.Validate(settings).Single().Path);
        }
        foreach (var target in new[] { 0, 2, 5 })
        {
            var settings = defaults with { Profiles = [defaults.Profiles[0]], RigProfileMappings = [new(Rig, target)] };
            Assert.AreEqual("rigProfileMappings[0].profileId", ConfigurationValidator.Validate(settings).Single().Path);
        }
        var nullEntry = defaults with { RigProfileMappings = [null!] };
        Assert.AreEqual("rigProfileMappings[0]", ConfigurationValidator.Validate(nullEntry).Single().Path);
        Assert.ThrowsExactly<ConfigurationValidationException>(() => ConfigurationJson.Deserialize(
            """{"profiles":[{"id":1,"label":"One"}],"rigProfileMappings":[null]}"""));
        Assert.ThrowsExactly<ConfigurationSerializationException>(() => ConfigurationJson.Deserialize(
            """{"profiles":[{"id":1,"label":"One"}],"rigProfileMappings":[{"rigProfileId":"12345678-abcd-4321-abcd-123456789abc","profileId":1,"profileId":4}]}"""));
    }

    [TestMethod]
    public void CreateDefault_ReturnsFourDisabledUnassignedProfiles()
    {
        var settings = AppSettings.CreateDefault();

        Assert.AreEqual(1, settings.SchemaVersion);
        Assert.HasCount(4, settings.Profiles);
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, settings.Profiles.Select(profile => profile.Id).ToArray());
        Assert.IsTrue(settings.Profiles.All(profile =>
            !profile.Enabled &&
            !profile.AutoConnect &&
            profile.PhysicalPort is null &&
            profile.VirtualPort is null &&
            profile.CommanderPort is null));
        Assert.IsFalse(settings.Profiles[0].SerialSettings.DtrEnable);
        Assert.IsFalse(settings.Profiles[0].SerialSettings.RtsEnable);
        Assert.IsNull(settings.Profiles[0].RadioAddress);
        Assert.IsNull(settings.Profiles[0].ControllerAddress);
        Assert.IsEmpty(ConfigurationValidator.Validate(settings));
    }

    [TestMethod]
    public void Profiles_AreCopiedAndExposedReadOnly()
    {
        var source = new[] { new ProfileConfig { Id = 1, Label = "Profile 1" } };
        var settings = new AppSettings { Profiles = source };

        source[0] = source[0] with { Label = "changed" };

        Assert.AreEqual("Profile 1", settings.Profiles[0].Label);
        Assert.IsInstanceOfType(settings.Profiles, typeof(IList<ProfileConfig>));
        Assert.ThrowsExactly<NotSupportedException>(() =>
            ((IList<ProfileConfig>)settings.Profiles).Add(new ProfileConfig()));
    }

    [TestMethod]
    public void JsonRoundTrip_PreservesSettingsAndUsesLowerCamelCaseEnums()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Profiles =
            [
                AppSettings.CreateDefault().Profiles[0] with
                {
                    Adapter = AdapterKind.Passthrough,
                    SerialSettings = new SerialSettings
                    {
                        Parity = SerialParity.Even,
                        StopBits = SerialStopBits.Two,
                        DtrEnable = false,
                        RtsEnable = false
                    }
                }
            ]
        };

        var json = ConfigurationJson.Serialize(settings);
        var deserialized = ConfigurationJson.Deserialize(json);

        Assert.AreEqual(AdapterKind.Passthrough, deserialized.Profiles[0].Adapter);
        Assert.AreEqual(SerialParity.Even, deserialized.Profiles[0].SerialSettings.Parity);
        Assert.AreEqual(SerialStopBits.Two, deserialized.Profiles[0].SerialSettings.StopBits);
        StringAssert.Contains(json, "\"passthrough\"");
        StringAssert.Contains(json, "\"even\"");
    }

    [TestMethod]
    public void Deserialize_RejectsMalformedUnknownAndNonCanonicalEnumJson()
    {
        Assert.ThrowsExactly<ConfigurationSerializationException>(() =>
            ConfigurationJson.Deserialize("{"));
        Assert.ThrowsExactly<ConfigurationSerializationException>(() =>
            ConfigurationJson.Deserialize("{\"schemaVersion\":1,\"profiles\":[],\"routingLimits\":{},\"unknown\":true}"));
        Assert.ThrowsExactly<ConfigurationSerializationException>(() =>
            ConfigurationJson.Deserialize("""
                {"schemaVersion":1,"profiles":[{"id":1,"label":"One","adapter":"Passthrough"}]}
                """));
        Assert.ThrowsExactly<ConfigurationSerializationException>(() =>
            ConfigurationJson.Deserialize("""
                {"schemaVersion":1,"profiles":[{"id":1,"label":"One","adapter":999}]}
                """));
    }

    [TestMethod]
    [DataRow("""{"schemaVersion":2,"schemaVersion":1}""")]
    [DataRow("""{"profiles":[{"id":2,"id":1}]}""")]
    [DataRow("""{"routingLimits":{"leaseDurationMs":0,"leaseDurationMs":5000}}""")]
    public void Deserialize_RejectsDuplicatePropertiesAtEveryDepth(string json)
    {
        var exception = Assert.ThrowsExactly<ConfigurationSerializationException>(
            () => ConfigurationJson.Deserialize(json));
        StringAssert.Contains(exception.Message, "Duplicate property");
    }

    [TestMethod]
    public void Deserialize_ValidatesSettingsAndReportsPropertyPaths()
    {
        var exception = Assert.ThrowsExactly<ConfigurationValidationException>(() =>
            ConfigurationJson.Deserialize("""
                {"schemaVersion":2,"profiles":[{"id":7,"label":"","enabled":true}]}
                """));

        Assert.IsTrue(exception.Errors.Any(error => error.Path == "schemaVersion"));
        Assert.IsTrue(exception.Errors.Any(error => error.Path == "profiles[0].id"));
        Assert.IsTrue(exception.Errors.Any(error => error.Path == "profiles[0].physicalPort"));
    }

    [TestMethod]
    public void Validate_RejectsInvalidProfileCountsAndIds()
    {
        var defaults = AppSettings.CreateDefault();
        var tooMany = defaults with
        {
            Profiles = defaults.Profiles.Concat([new ProfileConfig { Id = 5, Label = "Extra" }]).ToArray()
        };
        var duplicateIds = defaults with
        {
            Profiles = [defaults.Profiles[0], defaults.Profiles[0] with { Label = "Duplicate" }]
        };

        Assert.IsTrue(ConfigurationValidator.Validate(tooMany).Any(error => error.Path == "profiles"));
        Assert.IsTrue(ConfigurationValidator.Validate(tooMany).Any(error => error.Path == "profiles[4].id"));
        Assert.IsTrue(ConfigurationValidator.Validate(duplicateIds).Any(error => error.Path == "profiles[1].id"));
    }

    [TestMethod]
    public void Validate_RequiresCompleteDistinctPortsOnlyForEnabledProfiles()
    {
        var defaults = AppSettings.CreateDefault();
        var validDisabled = defaults.Profiles[0] with { PhysicalPort = "COM3" };
        Assert.IsEmpty(ConfigurationValidator.Validate(defaults with { Profiles = [validDisabled] }));

        var incomplete = validDisabled with { Enabled = true };
        var incompleteErrors = ConfigurationValidator.Validate(defaults with { Profiles = [incomplete] });
        Assert.IsTrue(incompleteErrors.Any(error => error.Path == "profiles[0].virtualPort"));
        Assert.IsTrue(incompleteErrors.Any(error => error.Path == "profiles[0].commanderPort"));
        Assert.IsTrue(incompleteErrors.Any(error => error.Path == "profiles[0].radioAddress"));
        Assert.IsTrue(incompleteErrors.Any(error => error.Path == "profiles[0].controllerAddress"));

        var samePorts = incomplete with
        {
            VirtualPort = "COM4",
            CommanderPort = "com4"
        };
        Assert.IsTrue(ConfigurationValidator.Validate(defaults with { Profiles = [samePorts] })
            .Any(error => error.Path == "profiles[0].commanderPort"));
    }

    [TestMethod]
    public void Validate_AcceptsDocumentedBoundaryValues()
    {
        var profile = AppSettings.CreateDefault().Profiles[0] with
        {
            Enabled = true,
            PhysicalPort = "COM1",
            VirtualPort = "COM2",
            CommanderPort = "COM3",
            RadioAddress = 0x01,
            ControllerAddress = 0xEF,
            SerialSettings = new SerialSettings
            {
                BaudRate = 50,
                DataBits = 5,
                StopBits = SerialStopBits.OnePointFive
            }
        };
        var limits = new RoutingLimits
        {
            CatTransactionTimeoutMs = 500,
            CatWriteTimeoutMs = 250,
            CatReplyTimeoutMs = 250,
            IpcTimeoutMinimumMs = 100,
            IpcTimeoutMaximumMs = 3_000,
            QuietGuardMs = 50,
            TransitionTimeoutMs = 1_000
        };

        Assert.IsEmpty(ConfigurationValidator.Validate(
            AppSettings.CreateDefault() with { Profiles = [profile], RoutingLimits = limits }));
    }

    [TestMethod]
    public void Validate_AutoConnectRequiresEnabledProfile()
    {
        var profile = AppSettings.CreateDefault().Profiles[0] with { AutoConnect = true };

        Assert.IsTrue(ConfigurationValidator.Validate(
                AppSettings.CreateDefault() with { Profiles = [profile] })
            .Any(error => error.Path == "profiles[0].autoConnect"));
    }

    [TestMethod]
    public void Validate_RejectsInvalidComNamesAliasesAndGlobalOverlap()
    {
        var defaults = AppSettings.CreateDefault();
        var malformed = defaults.Profiles[0] with
        {
            PhysicalPort = "COM01",
            VirtualPort = "COM0",
            CommanderPort = "COM-3"
        };
        var invalidNames = ConfigurationValidator.Validate(defaults with { Profiles = [malformed] });
        Assert.AreEqual(3, invalidNames.Count(error => error.Message.Contains("COM port name", StringComparison.Ordinal)));

        var overlapping = defaults with
        {
            Profiles =
            [
                defaults.Profiles[0] with { PhysicalPort = "COM3" },
                defaults.Profiles[1] with { VirtualPort = "com3" }
            ]
        };
        Assert.IsTrue(ConfigurationValidator.Validate(overlapping)
            .Any(error => error.Path == "profiles[1].virtualPort"));
    }

    [TestMethod]
    public void Validate_RejectsInvalidAddressesAndSerialFramingBoundaries()
    {
        var defaults = AppSettings.CreateDefault();
        var invalid = defaults.Profiles[0] with
        {
            RadioAddress = 0,
            ControllerAddress = 256,
            SerialSettings = new SerialSettings
            {
                BaudRate = 0,
                DataBits = 9,
                StopBits = SerialStopBits.OnePointFive
            }
        };

        var errors = ConfigurationValidator.Validate(defaults with { Profiles = [invalid] });
        Assert.IsTrue(errors.Any(error => error.Path == "profiles[0].radioAddress"));
        Assert.IsTrue(errors.Any(error => error.Path == "profiles[0].controllerAddress"));
        Assert.IsTrue(errors.Any(error => error.Path == "profiles[0].serialSettings.baudRate"));
        Assert.IsTrue(errors.Any(error => error.Path == "profiles[0].serialSettings.dataBits"));
        Assert.IsTrue(errors.Any(error => error.Path == "profiles[0].serialSettings.stopBits"));
    }

    [TestMethod]
    public void Validate_RejectsIncoherentAndOutOfRangeRoutingLimits()
    {
        var limits = new RoutingLimits
        {
            ClientRenewalTargetMs = 5_000,
            CatWriteTimeoutMs = 250,
            CatReplyTimeoutMs = 251,
            CatTransactionTimeoutMs = 500,
            IpcTimeoutMinimumMs = 3_001,
            IpcTimeoutMaximumMs = 100,
            OutboundQueueByteCapacityPerClient = int.MaxValue
        };

        var errors = ConfigurationValidator.Validate(AppSettings.CreateDefault() with { RoutingLimits = limits });
        Assert.IsTrue(errors.Any(error => error.Path == "routingLimits.clientRenewalTargetMs"));
        Assert.IsTrue(errors.Any(error => error.Path == "routingLimits.catReplyTimeoutMs"));
        Assert.IsTrue(errors.Any(error => error.Path == "routingLimits.catTransactionTimeoutMs"));
        Assert.IsTrue(errors.Any(error => error.Path == "routingLimits.ipcTimeoutMinimumMs"));
        Assert.IsTrue(errors.Any(error => error.Path == "routingLimits.outboundQueueByteCapacityPerClient"));
    }

    [TestMethod]
    public void ConfigurationValidationException_FormatsHumanReadableMessage()
    {
        var errors = new ConfigurationError[]
        {
            new("profiles[1].physicalPort", "Overlaps the COM port assigned at profiles[0].physicalPort."),
            new("profiles[1].virtualPort", "Overlaps the COM port assigned at profiles[0].virtualPort."),
            new("profiles[1].commanderPort", "Overlaps the COM port assigned at profiles[0].commanderPort."),
            new("profiles[1].commandIpcEnabled", "An opted-in command profile requires at least one persisted rig-profile mapping."),
            new("rigProfileMappings[0].civAddress", "When provided, must be a two-digit hexadecimal CI-V address.")
        };

        var exception = new ConfigurationValidationException(errors);
        var message = exception.Message;

        StringAssert.Contains(message, "Configuration validation failed with 5 error(s):");
        StringAssert.Contains(message, "• Profile 2 - Physical Radio COM: Overlaps the COM port assigned at Profile 1 - Physical Radio COM.");
        StringAssert.Contains(message, "• Profile 2 - ComCon Virtual COM: Overlaps the COM port assigned at Profile 1 - ComCon Virtual COM.");
        StringAssert.Contains(message, "• Profile 2 - Legacy Client COM: Overlaps the COM port assigned at Profile 1 - Legacy Client COM.");
        StringAssert.Contains(message, "• Profile 2 - Command IPC: An opted-in command profile requires at least one persisted rig-profile mapping.");
        StringAssert.Contains(message, "• Rig Profile Mapping 1 - CI-V Address: When provided, must be a two-digit hexadecimal CI-V address.");
        Assert.IsFalse(message.Contains("commanderPort", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.Contains("profiles[", StringComparison.Ordinal));
        Assert.HasCount(5, exception.Errors);
    }

    [TestMethod]
    public void FramingMode_ToDisplayName_ReturnsRequiredCosmeticNames()
    {
        Assert.AreEqual("CI-V", FramingMode.Civ.ToDisplayName());
        Assert.AreEqual("Kenwood ASCII", FramingMode.KenwoodAscii.ToDisplayName());
        Assert.AreEqual("YAESU ASCII", FramingMode.YaesuAscii.ToDisplayName());
        Assert.AreEqual("None", FramingMode.None.ToDisplayName());
    }
}
