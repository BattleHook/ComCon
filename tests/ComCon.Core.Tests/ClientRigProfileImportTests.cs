using System.Text;
using System.Text.Json;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.UI.ViewModels;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ClientRigProfileImportTests
{
    private const string Rig = "12345678-1234-4234-8234-123456789abc";
    private const string CivJson = """
        {"schema":"ham-shack-console/rig-profile","schema_version":1,
         "rig_profile_uuid":"12345678-1234-4234-8234-123456789abc",
         "display_name":"Icom IC-7600","transport":"CIV","civ_address":"7A",
         "capabilities":{"ptt":{}}}
        """;

    [TestMethod]
    public void ImportAddsMappingRefreshesDescriptionsAndPersistsWithoutChangingClientIdentity()
    {
        var model = CreateModel();
        var imported = Parse(CivJson);
        model.ImportClientRigProfile(imported);
        Assert.IsTrue(model.IsDirty);
        var reloaded = ConfigurationJson.Deserialize(ConfigurationJson.Serialize(model.Export()));
        Assert.AreEqual(Rig, reloaded.RigProfileMappings.Single().RigProfileId);
        Assert.AreEqual("Icom IC-7600", reloaded.RigProfileMappings.Single().RigModelName);
        Assert.AreEqual("7A", reloaded.RigProfileMappings.Single().CivAddress);
        model.ImportClientRigProfile(imported with
        {
            Mapping = imported.Mapping with { RigModelName = "Updated name" }
        });
        Assert.HasCount(1, model.RigProfileMappings);
        Assert.AreEqual("Updated name", model.RigProfileMappings.Single().RigModelName);
    }

    [TestMethod]
    public void ImportRequiresOptInMatchingFramingAndStoppedRuntimeAndPreservesConflictingMappings()
    {
        var imported = Parse(CivJson);
        var model = CreateModel();
        model.SelectedProfile!.CommandIpcEnabled = false;
        Assert.ThrowsExactly<InvalidOperationException>(() => model.ImportClientRigProfile(imported));
        model.SelectedProfile.CommandIpcEnabled = true;
        model.SelectedProfile.Framing = FramingMode.KenwoodAscii;
        Assert.ThrowsExactly<InvalidOperationException>(() => model.ImportClientRigProfile(imported));
        model.SelectedProfile.Framing = FramingMode.Civ;
        model.SetActiveProfiles([model.SelectedProfile.Export(0)], []);
        Assert.ThrowsExactly<InvalidOperationException>(() => model.ImportClientRigProfile(imported));

        model = CreateModel([new(Rig, 2), new("12345678-abcd-4321-abcd-123456789abc", 1)]);
        var before = model.RigProfileMappings.ToArray();
        Assert.ThrowsExactly<InvalidOperationException>(() => model.ImportClientRigProfile(imported));
        CollectionAssert.AreEqual(before, model.RigProfileMappings.ToArray());
        Assert.IsFalse(model.IsDirty);
    }

    [TestMethod]
    public void KenwoodImportsModelWithoutBusAddress()
    {
        var json = CivJson.Replace("\"CIV\"", "\"KENWOOD_ASCII\"", StringComparison.Ordinal);
        var imported = Parse(json);
        Assert.IsNull(imported.Mapping.CivAddress);
        var model = CreateModel();
        model.SelectedProfile!.Framing = FramingMode.KenwoodAscii;
        model.ImportClientRigProfile(imported);
        Assert.IsFalse(model.RigProfileMappingEditors.Single().SupportsCivAddress);
        Assert.IsNull(model.Export().RigProfileMappings.Single().CivAddress);
    }

    [TestMethod]
    [DataRow("rig_profile_uuid", "bad-uuid")]
    [DataRow("civ_address", "7G")]
    public void InvalidMappingMetadataFailsValidation(string field, string value)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(CivJson)!;
        node[field] = value;
        Assert.ThrowsExactly<ConfigurationValidationException>(() => Parse(node.ToJsonString()));
    }

    [TestMethod]
    public void MalformedMissingDuplicateAndUnsupportedProfilesFailExplicitly()
    {
        Assert.Throws<JsonException>(() => Parse("{"));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse("{}"));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(
            CivJson.Replace("\"transport\":\"CIV\"", "\"transport\":\"YAESU_ASCII\"", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(
            CivJson.Replace("\"transport\":\"CIV\"", "\"transport\":\"CIV\",\"transport\":\"CIV\"", StringComparison.Ordinal)));
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(
            CivJson.Replace("\"schema_version\":1", "\"schema_version\":2", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void MissingClientUuidReportsRequiredFieldAndLeavesMappingDraftUnchanged()
    {
        var model = CreateModel();
        var before = model.RigProfileMappings.ToArray();
        var node = System.Text.Json.Nodes.JsonNode.Parse(CivJson)!.AsObject();
        node.Remove("rig_profile_uuid");
        var error = Assert.ThrowsExactly<InvalidDataException>(() =>
            model.ImportClientRigProfile(Parse(node.ToJsonString())));
        StringAssert.Contains(error.Message, "rig_profile_uuid");
        CollectionAssert.AreEqual(before, model.RigProfileMappings.ToArray());
    }

    [TestMethod]
    public void FileImportReadsClientWithoutModifyingItAndRejectsOversizeInput()
    {
        var path = Path.Combine(Path.GetTempPath(), $"comcon-import-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, CivJson);
            var before = File.ReadAllBytes(path);
            Assert.AreEqual(Rig, ClientRigProfileJson.Load(path, 1).Mapping.RigProfileId);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            File.WriteAllBytes(path, new byte[ConfigurationFileStore.MaxFileSizeBytes + 1]);
            Assert.ThrowsExactly<InvalidDataException>(() => ClientRigProfileJson.Load(path, 1));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void BundledSyntheticClientRigProfilesCanBeLoadedSuccessfully()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "ClientRigProfiles");

        var ic7600 = ClientRigProfileJson.Load(Path.Combine(directory, "IC7600.json"), 1);
        Assert.AreEqual(Rig, ic7600.Mapping.RigProfileId);
        Assert.AreEqual("Icom IC-7600", ic7600.Mapping.RigModelName);
        Assert.AreEqual("7A", ic7600.Mapping.CivAddress);
        Assert.AreEqual(FramingMode.Civ, ic7600.Framing);

        var ic7400 = ClientRigProfileJson.Load(Path.Combine(directory, "IC7400.json"), 2);
        Assert.AreEqual("12345678-1234-4234-8234-123456789abd", ic7400.Mapping.RigProfileId);
        Assert.AreEqual("Icom IC-7400", ic7400.Mapping.RigModelName);
        Assert.AreEqual("66", ic7400.Mapping.CivAddress);
        Assert.AreEqual(FramingMode.Civ, ic7400.Framing);

        var ts590 = ClientRigProfileJson.Load(Path.Combine(directory, "TS590SG.json"), 3);
        Assert.AreEqual("12345678-1234-4234-8234-123456789abe", ts590.Mapping.RigProfileId);
        Assert.AreEqual("Kenwood TS-590SG", ts590.Mapping.RigModelName);
        Assert.IsNull(ts590.Mapping.CivAddress);
        Assert.AreEqual(FramingMode.KenwoodAscii, ts590.Framing);
    }

    private static ClientRigProfile Parse(string json) =>
        ClientRigProfileJson.Parse(Encoding.UTF8.GetBytes(json), 1);

    private static MainViewModel CreateModel(ComConRigProfileMapping[]? mappings = null)
    {
        var defaults = AppSettings.CreateDefault();
        var model = new MainViewModel();
        model.Load(defaults with
        {
            Profiles = defaults.Profiles.Select(profile => profile with
            {
                Framing = FramingMode.Civ,
                Enabled = profile.Id == 1,
                Adapter = AdapterKind.Passthrough,
                PhysicalPort = profile.Id == 1 ? "COM11" : "",
                VirtualPort = profile.Id == 1 ? "COM12" : "",
                CommanderPort = profile.Id == 1 ? "COM13" : "",
                CommandIpcEnabled = false
            }).ToArray(),
            RigProfileMappings = mappings ?? []
        });
        model.SelectedProfile!.CommandIpcEnabled = true;
        if (mappings is not null) model.Saved(model.Export());
        return model;
    }
}
