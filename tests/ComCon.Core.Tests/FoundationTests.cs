using ComCon.Core.Presentation;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class FoundationTests
{
    [TestMethod]
    public void FoundationDoesNotAdvertiseHardwareRouting()
    {
        Assert.AreEqual(4, FoundationCapabilities.MaximumProfiles);
        Assert.IsFalse(FoundationCapabilities.HardwareRoutingImplemented);
    }

    [TestMethod]
    [DataRow(-10001, 0, false)]
    [DataRow(0, -10001, false)]
    [DataRow(-10000, -10000, true)]
    [DataRow(0, 0, true)]
    [DataRow(100000, 100000, true)]
    [DataRow(100001, 0, false)]
    [DataRow(0, 100001, false)]
    public void PositionCoordinateBoundsAreExplicit(int x, int y, bool expected) =>
        Assert.AreEqual(expected, new WindowPosition(x, y).IsReasonable);
}
