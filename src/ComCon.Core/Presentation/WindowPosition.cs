namespace ComCon.Core.Presentation;

public sealed record WindowPosition(int X, int Y)
{
    public bool IsReasonable =>
        X >= -10000 && Y >= -10000 && X <= 100000 && Y <= 100000;
}
