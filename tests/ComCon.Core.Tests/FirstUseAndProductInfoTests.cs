using ComCon.Core.Presentation;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class FirstUseAndProductInfoTests
{
    private string directory = string.Empty;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), "ComConFirstUse-" + Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    [TestMethod]
    public void MissingFileShowsWelcome()
    {
        var store = new FirstUseStore(Path.Combine(directory, "FirstUse.json"));
        Assert.IsTrue(store.ShouldShowWelcome());
    }

    [TestMethod]
    public void DismissalIsPersistedAndCreatesDirectory()
    {
        var store = new FirstUseStore(Path.Combine(directory, "nested", "FirstUse.json"));
        Assert.IsNull(store.MarkWelcomeDismissed(DateTimeOffset.UnixEpoch));
        Assert.IsFalse(store.ShouldShowWelcome());
        Assert.IsFalse(new FirstUseStore(store.FilePath).ShouldShowWelcome());
    }

    [TestMethod]
    public void CorruptFileShowsWelcomeAgain()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "FirstUse.json");
        File.WriteAllText(path, "{ not json");
        Assert.IsTrue(new FirstUseStore(path).ShouldShowWelcome());
    }

    [TestMethod]
    public void SaveFailureIsReportedNotSwallowed()
    {
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "blocker");
        File.WriteAllText(blocker, "file, not a directory");
        var store = new FirstUseStore(Path.Combine(blocker, "FirstUse.json"));
        var error = store.MarkWelcomeDismissed(DateTimeOffset.UnixEpoch);
        Assert.IsNotNull(error);
        StringAssert.Contains(error, "shown again");
        Assert.IsTrue(store.ShouldShowWelcome());
    }

    [TestMethod]
    public void VersionDropsSourceRevisionMetadata()
    {
        Assert.AreEqual("0.1.0-beta.1", ProductInfo.FormatVersion("0.1.0-beta.1+abc123"));
        Assert.AreEqual("0.1.0-beta.1", ProductInfo.FormatVersion("0.1.0-beta.1"));
        Assert.AreEqual("unknown", ProductInfo.FormatVersion(null));
    }

    [TestMethod]
    public void BundledHelpResolvesNextToExecutable()
    {
        var resolved = ProductInfo.ResolveBundledFile(@"C:\Apps\ComCon\", ProductInfo.HelpRelativePath);
        Assert.AreEqual(@"C:\Apps\ComCon\Help\index.html", resolved);
    }

    [TestMethod]
    public void DonationTextStatesNoFeatureGating()
    {
        StringAssert.Contains(ProductInfo.DonationText, "do not unlock features");
        StringAssert.Contains(ProductInfo.DonationText, "one-time");
    }
}
