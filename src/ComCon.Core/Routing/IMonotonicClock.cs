namespace ComCon.Core.Routing;

public interface IMonotonicClock
{
    long NowMs { get; }
}
