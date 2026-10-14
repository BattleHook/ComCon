namespace ComCon.Core.Routing;

public enum QueueAdmission
{
    Accepted,
    ProfileCapacityReached,
    ClientCapacityReached
}

public sealed record QueuedRequest<T>(string ClientId, long EnqueuedMs, T Value);

// Actor-confined: admission and removal must run on the profile's serialized loop.
public sealed class BoundedRequestQueue<T>
{
    private readonly int profileCapacity;
    private readonly int clientCapacity;
    private readonly LinkedList<QueuedRequest<T>> requests = new();

    public BoundedRequestQueue(int profileCapacity, int clientCapacity)
    {
        if (profileCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(profileCapacity));
        }
        if (clientCapacity < 1 || clientCapacity > profileCapacity)
        {
            throw new ArgumentOutOfRangeException(nameof(clientCapacity));
        }

        this.profileCapacity = profileCapacity;
        this.clientCapacity = clientCapacity;
    }

    public int Count => requests.Count;

    public QueueAdmission Enqueue(string clientId, long enqueuedMs, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentOutOfRangeException.ThrowIfNegative(enqueuedMs);
        ArgumentNullException.ThrowIfNull(value);
        if (requests.Count >= profileCapacity)
        {
            return QueueAdmission.ProfileCapacityReached;
        }
        if (requests.Count(request => request.ClientId == clientId) >= clientCapacity)
        {
            return QueueAdmission.ClientCapacityReached;
        }

        requests.AddLast(new QueuedRequest<T>(clientId, enqueuedMs, value));
        return QueueAdmission.Accepted;
    }

    public QueuedRequest<T>? Dequeue()
    {
        if (requests.First is not { } first)
        {
            return null;
        }

        requests.RemoveFirst();
        return first.Value;
    }

    public IReadOnlyList<QueuedRequest<T>> RemoveWhere(Predicate<QueuedRequest<T>> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        List<QueuedRequest<T>> removed = [];
        var node = requests.First;
        while (node is not null)
        {
            var next = node.Next;
            if (predicate(node.Value))
            {
                removed.Add(node.Value);
                requests.Remove(node);
            }
            node = next;
        }

        return removed.AsReadOnly();
    }
}
