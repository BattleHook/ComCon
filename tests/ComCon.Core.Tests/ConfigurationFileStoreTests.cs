using System.Text;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ConfigurationFileStoreTests
{
    private string _testDirectory = string.Empty;

    [TestInitialize]
    public void Setup()
    {
        _testDirectory = CreateUniqueTempDirectory();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void Constructor_NormalizesFilePathToFullPath()
    {
        var relative = Path.Combine(_testDirectory, "..", Path.GetFileName(_testDirectory), "settings.json");
        var store = new ConfigurationFileStore(relative);

        Assert.AreEqual(Path.GetFullPath(relative), store.FilePath);
    }

    [TestMethod]
    public void Load_MissingFile_ThrowsFileNotFoundException()
    {
        var store = new ConfigurationFileStore(TargetPath());

        var exception = Assert.ThrowsExactly<FileNotFoundException>(() => store.Load());
        Assert.AreEqual(store.FilePath, exception.FileName);
    }

    [TestMethod]
    [DataRow("{", DisplayName = "Truncated object")]
    [DataRow("""{"schemaVersion":1,"profiles":[],"routingLimits":{},"unknown":true}""", DisplayName = "Unknown member")]
    [DataRow("""{"schemaVersion":2,"schemaVersion":1}""", DisplayName = "Duplicate member")]
    public void Load_MalformedOrUnknownOrDuplicateJson_ThrowsConfigurationSerializationException(string json)
    {
        var path = TargetPath();
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var store = new ConfigurationFileStore(path);

        Assert.ThrowsExactly<ConfigurationSerializationException>(() => store.Load());
    }

    [TestMethod]
    public void Load_InvalidSettings_ThrowsConfigurationValidationException()
    {
        var path = TargetPath();
        File.WriteAllText(
            path,
            """{"schemaVersion":2,"profiles":[{"id":7,"label":"","enabled":true}]}""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var store = new ConfigurationFileStore(path);

        var exception = Assert.ThrowsExactly<ConfigurationValidationException>(() => store.Load());
        Assert.IsTrue(exception.Errors.Any(error => error.Path == "schemaVersion"));
    }

    [TestMethod]
    public void SaveThenLoad_RoundTripsSettings()
    {
        var settings = AppSettings.CreateDefault() with
        {
            Profiles = [AppSettings.CreateDefault().Profiles[0] with { Label = "Round trip" }],
            RigProfileMappings = [new ComConRigProfileMapping("12345678-abcd-4321-abcd-123456789abc", 1)]
        };
        var store = new ConfigurationFileStore(TargetPath());

        store.Save(settings);
        var loaded = store.Load();

        Assert.AreEqual(ConfigurationJson.Serialize(settings), ConfigurationJson.Serialize(loaded));
        CollectionAssert.AreEqual(settings.RigProfileMappings.ToArray(), loaded.RigProfileMappings.ToArray());
    }

    [TestMethod]
    public void InvalidMappingSavePreservesExistingSettingsAndLeavesNoTemporaryFiles()
    {
        var store = new ConfigurationFileStore(TargetPath());
        var rig = "12345678-abcd-4321-abcd-123456789abc";
        var settings = AppSettings.CreateDefault() with { RigProfileMappings = [new(rig, 1)] };
        store.Save(settings);
        var original = File.ReadAllBytes(store.FilePath);
        var invalid = settings with { RigProfileMappings = [new(rig, 1), new(rig, 4)] };
        Assert.ThrowsExactly<ConfigurationValidationException>(() => store.Save(invalid));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(store.FilePath));
        Assert.AreEqual(1, store.Load().RigProfileMappings.Single().ProfileId);
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Save_InvalidSettings_WhenTargetMissing_ThrowsAndDoesNotCreateFile()
    {
        var path = TargetPath();
        var store = new ConfigurationFileStore(path);
        var invalid = AppSettings.CreateDefault() with { SchemaVersion = 2 };

        Assert.ThrowsExactly<ConfigurationValidationException>(() => store.Save(invalid));

        Assert.IsFalse(File.Exists(path));
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Save_InvalidSettings_WhenTargetExists_ThrowsAndPreservesOriginalFile()
    {
        var path = TargetPath();
        var store = new ConfigurationFileStore(path);
        store.Save(AppSettings.CreateDefault());
        var originalBytes = File.ReadAllBytes(path);

        var invalid = AppSettings.CreateDefault() with { SchemaVersion = 2 };
        Assert.ThrowsExactly<ConfigurationValidationException>(() => store.Save(invalid));

        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Load_FileExceedsSizeLimit_Throws()
    {
        var path = TargetPath();
        File.WriteAllBytes(path, new byte[ConfigurationFileStore.MaxFileSizeBytes + 1]);
        var store = new ConfigurationFileStore(path);

        Assert.ThrowsExactly<ConfigurationSizeLimitExceededException>(() => store.Load());
    }

    [TestMethod]
    public void Save_WithoutPriorLoad_WhenTargetAlreadyExists_ThrowsConcurrencyExceptionAndPreservesOriginal()
    {
        var path = TargetPath();
        File.WriteAllText(path, "not json, pre-existing external content");
        var originalBytes = File.ReadAllBytes(path);
        var store = new ConfigurationFileStore(path);

        Assert.ThrowsExactly<ConfigurationConcurrencyException>(() => store.Save(AppSettings.CreateDefault()));

        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Save_WithoutPriorLoad_WhenTargetMissing_CreatesFile()
    {
        var path = TargetPath();
        var store = new ConfigurationFileStore(path);

        store.Save(AppSettings.CreateDefault());

        Assert.IsTrue(File.Exists(path));
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Save_AfterLoad_WhenFileChangedExternally_ThrowsConcurrencyExceptionAndPreservesExternalContent()
    {
        var path = TargetPath();
        var store = new ConfigurationFileStore(path);
        store.Save(AppSettings.CreateDefault());
        store.Load();

        File.WriteAllText(path, "externally changed content", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var externalBytes = File.ReadAllBytes(path);

        Assert.ThrowsExactly<ConfigurationConcurrencyException>(() =>
            store.Save(AppSettings.CreateDefault() with { SchemaVersion = 1 }));

        CollectionAssert.AreEqual(externalBytes, File.ReadAllBytes(path));
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Save_AfterLoad_WhenFileDeletedExternally_ThrowsConcurrencyExceptionAndDoesNotRecreateFile()
    {
        var path = TargetPath();
        var store = new ConfigurationFileStore(path);
        store.Save(AppSettings.CreateDefault());
        store.Load();

        File.Delete(path);

        Assert.ThrowsExactly<ConfigurationConcurrencyException>(() => store.Save(AppSettings.CreateDefault()));

        Assert.IsFalse(File.Exists(path));
        AssertNoTemporaryFilesRemain();
    }

    [TestMethod]
    public void Save_AfterLoad_WhenUnchangedOnDisk_OverwritesSuccessfully()
    {
        var path = TargetPath();
        var store = new ConfigurationFileStore(path);
        store.Save(AppSettings.CreateDefault());
        store.Load();

        var updated = AppSettings.CreateDefault() with
        {
            Profiles = [AppSettings.CreateDefault().Profiles[0] with { Label = "Updated" }]
        };
        store.Save(updated);

        Assert.AreEqual(ConfigurationJson.Serialize(updated), ConfigurationJson.Serialize(store.Load()));
        AssertNoTemporaryFilesRemain();
    }

    private string TargetPath() => Path.Combine(_testDirectory, "settings.json");

    private void AssertNoTemporaryFilesRemain()
    {
        var leftovers = Directory.GetFiles(_testDirectory, "*.tmp");
        Assert.IsEmpty(leftovers);
    }

    private static string CreateUniqueTempDirectory()
    {
        var objDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "obj"));
        var uniqueDirectory = Path.Combine(objDirectory, "ConfigurationFileStoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uniqueDirectory);
        return uniqueDirectory;
    }
}
