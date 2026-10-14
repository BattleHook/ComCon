using System.Diagnostics;

namespace ComCon.Core.Routing;

public sealed class StopwatchClock : IMonotonicClock
{
    private readonly Stopwatch stopwatch = Stopwatch.StartNew();

    public long NowMs => stopwatch.ElapsedMilliseconds;
}
