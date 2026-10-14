using System.Security.Cryptography;
using System.Text.Json;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class HscHelperSnapshotTests
{
    [TestMethod]
    public void DeployedHelpersMatchApprovedByteSnapshots()
    {
        var manifest = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "HscHelpers-sha256.json")));
        Assert.IsNotNull(manifest);
        CollectionAssert.AreEquivalent(
            new[] { "ComCon-IpcClient.ps1", "ComCon-CivObservations.ps1", "CIV-Common.ps1",
                "ComCon-CommandClient.ps1", "ComCon-OfflinePreview.ps1" },
            manifest.Keys.ToArray());
        foreach (var (name, expected) in manifest)
        {
            var path = Path.Combine(AppContext.BaseDirectory, name);
            Assert.IsTrue(File.Exists(path), $"Required helper snapshot is missing: {name}");
            Assert.AreEqual(expected, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                $"Helper snapshot changed without a corresponding reviewed pin: {name}");
        }
    }
}
