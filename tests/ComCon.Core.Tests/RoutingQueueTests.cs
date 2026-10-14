using ComCon.Core.Routing;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class RoutingQueueTests
{
    [TestMethod]
    public void QueueEnforcesProfileAndClientLimitsWithoutDroppingRequests()
    {
        var queue = new BoundedRequestQueue<string>(3, 2);
        Assert.AreEqual(QueueAdmission.Accepted, queue.Enqueue("one", 0, "A"));
        Assert.AreEqual(QueueAdmission.Accepted, queue.Enqueue("one", 0, "B"));
        Assert.AreEqual(QueueAdmission.ClientCapacityReached, queue.Enqueue("one", 0, "C"));
        Assert.AreEqual(QueueAdmission.Accepted, queue.Enqueue("two", 0, "D"));
        Assert.AreEqual(QueueAdmission.ProfileCapacityReached, queue.Enqueue("three", 0, "E"));
        Assert.AreEqual("A", queue.Dequeue()?.Value);
        Assert.AreEqual("B", queue.Dequeue()?.Value);
        Assert.AreEqual("D", queue.Dequeue()?.Value);
        Assert.IsNull(queue.Dequeue());
    }

    [TestMethod]
    public void ExpiryReturnsAllExpiredRequestsAndPreservesRemainingOrder()
    {
        var queue = new BoundedRequestQueue<int>(4, 4);
        queue.Enqueue("one", 0, 1);
        queue.Enqueue("two", 1, 2);
        queue.Enqueue("one", 2, 3);
        Assert.IsEmpty(queue.RemoveWhere(request => 249 - request.EnqueuedMs >= 250));
        Assert.AreEqual(3, queue.Count);
        var expired = queue.RemoveWhere(request => 250 - request.EnqueuedMs >= 250);
        Assert.HasCount(1, expired);
        Assert.AreEqual(1, expired[0].Value);
        Assert.AreEqual(2, queue.Dequeue()?.Value);
        Assert.AreEqual(3, queue.Dequeue()?.Value);
    }

    [TestMethod]
    public void RemovalReleasesClientCapacityAndReturnsRejectedWork()
    {
        var queue = new BoundedRequestQueue<int>(4, 2);
        queue.Enqueue("one", 0, 1);
        queue.Enqueue("two", 0, 2);
        queue.Enqueue("one", 0, 3);
        Assert.HasCount(2, queue.RemoveWhere(request => request.ClientId == "one"));
        Assert.AreEqual(QueueAdmission.Accepted, queue.Enqueue("one", 1, 4));
        Assert.AreEqual(2, queue.Dequeue()?.Value);
        Assert.AreEqual(4, queue.Dequeue()?.Value);
        Assert.IsEmpty(queue.RemoveWhere(_ => true));
    }

    [TestMethod]
    public void DequeueReleasesProfileAndClientCapacity()
    {
        var queue = new BoundedRequestQueue<string>(2, 1);
        queue.Enqueue("one", 0, "A");
        queue.Enqueue("two", 0, "B");
        Assert.AreEqual(QueueAdmission.ProfileCapacityReached, queue.Enqueue("three", 0, "C"));
        Assert.AreEqual("A", queue.Dequeue()?.Value);
        Assert.AreEqual(QueueAdmission.Accepted, queue.Enqueue("one", 1, "D"));
        Assert.AreEqual("B", queue.Dequeue()?.Value);
        Assert.AreEqual("D", queue.Dequeue()?.Value);
        Assert.AreEqual(0, queue.Count);
    }

    [TestMethod]
    public void RemovingInterleavedRequestsPreservesBothOrdersAndAllowsReuse()
    {
        var queue = new BoundedRequestQueue<int>(4, 2);
        queue.Enqueue("one", 0, 1);
        queue.Enqueue("two", 0, 2);
        queue.Enqueue("one", 0, 3);
        queue.Enqueue("two", 0, 4);
        var removed = queue.RemoveWhere(request => request.ClientId == "two");
        Assert.HasCount(2, removed);
        Assert.AreEqual(2, removed[0].Value);
        Assert.AreEqual(4, removed[1].Value);
        Assert.AreEqual(1, queue.Dequeue()?.Value);
        Assert.AreEqual(3, queue.Dequeue()?.Value);
        Assert.IsEmpty(queue.RemoveWhere(_ => true));
        Assert.AreEqual(QueueAdmission.Accepted, queue.Enqueue("two", 1, 5));
        Assert.AreEqual(5, queue.Dequeue()?.Value);
    }

    [TestMethod]
    public void QueuesRemainIsolated()
    {
        var first = new BoundedRequestQueue<int>(1, 1);
        var second = new BoundedRequestQueue<int>(1, 1);
        first.Enqueue("one", 0, 1);
        Assert.IsNull(second.Dequeue());
        Assert.AreEqual(1, first.Count);
    }

    [TestMethod]
    public void InvalidQueueArgumentsFailExplicitly()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BoundedRequestQueue<int>(0, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new BoundedRequestQueue<int>(1, 2));
        var queue = new BoundedRequestQueue<string>(1, 1);
        Assert.ThrowsExactly<ArgumentException>(() => queue.Enqueue("", 0, "value"));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => queue.Enqueue("one", -1, "value"));
        Assert.ThrowsExactly<ArgumentNullException>(() => queue.Enqueue("one", 0, null!));
    }
}
