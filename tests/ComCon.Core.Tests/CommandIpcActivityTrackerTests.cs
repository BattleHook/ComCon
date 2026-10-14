using ComCon.UI.ViewModels;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class CommandIpcActivityTrackerTests
{
    [TestMethod]
    public void ActivityRemainsVisibleBetweenPollingTransactions()
    {
        var tracker = new CommandIpcActivityTracker(TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.Parse("2026-10-13T13:00:00Z");

        var active = tracker.Observe([new(1, "rig-1")], [1], start);
        var betweenTransactions = tracker.Observe([], [1], start.AddMilliseconds(750));

        Assert.HasCount(1, active);
        Assert.HasCount(1, betweenTransactions);
        Assert.AreEqual(1, betweenTransactions[0].ProfileId);
        Assert.AreEqual("rig-1", betweenTransactions[0].RigProfileId);
    }

    [TestMethod]
    public void ActivityExpiresWhenPollingDoesNotReturn()
    {
        var tracker = new CommandIpcActivityTracker(TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.Parse("2026-10-13T13:00:00Z");

        tracker.Observe([new(1, "rig-1")], [1], start);
        var expired = tracker.Observe([], [1], start.AddMilliseconds(2001));

        Assert.IsEmpty(expired);
    }

    [TestMethod]
    public void StoppedProfileIsRemovedWithoutRetentionDelay()
    {
        var tracker = new CommandIpcActivityTracker(TimeSpan.FromSeconds(2));
        var start = DateTimeOffset.Parse("2026-10-13T13:00:00Z");

        tracker.Observe([new(1, "rig-1")], [1], start);
        var stopped = tracker.Observe([], [], start.AddMilliseconds(1));

        Assert.IsEmpty(stopped);
    }
}
