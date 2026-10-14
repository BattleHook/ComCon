namespace ComCon.UI.ViewModels;

internal sealed record CommandIpcActivity(int ProfileId, string RigProfileId);

internal sealed class CommandIpcActivityTracker(TimeSpan retention)
{
    private readonly Dictionary<int, (CommandIpcActivity Activity, DateTimeOffset LastSeen)> recent = [];

    public IReadOnlyList<CommandIpcActivity> Observe(
        IEnumerable<CommandIpcActivity> active,
        IEnumerable<int> runningProfileIds,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(runningProfileIds);
        if (retention <= TimeSpan.Zero) throw new InvalidOperationException("IPC activity retention must be positive.");

        var running = runningProfileIds.ToHashSet();
        foreach (var activity in active)
        {
            if (running.Contains(activity.ProfileId))
                recent[activity.ProfileId] = (activity, now);
        }

        foreach (var profileId in recent
                     .Where(item => !running.Contains(item.Key) || now - item.Value.LastSeen > retention)
                     .Select(item => item.Key)
                     .ToArray())
            recent.Remove(profileId);

        return recent.Values
            .Select(item => item.Activity)
            .OrderBy(item => item.ProfileId)
            .ToArray();
    }
}
