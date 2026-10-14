namespace ComCon.Core.Models;

public sealed record RoutingLimits
{
    public int ConnectionsPerProfile { get; init; } = 16;

    public int HelloDeadlineMs { get; init; } = 3_000;

    public int PartialJsonDeadlineMs { get; init; } = 2_000;

    public int LeaseDurationMs { get; init; } = 5_000;

    public int ClientRenewalTargetMs { get; init; } = 1_000;

    public int NormalQueueCapacityPerProfile { get; init; } = 32;

    public int PendingRequestsPerClient { get; init; } = 4;

    public int LeaseControlQueueCapacityPerProfile { get; init; } = 16;

    public int HardwareQueueWaitMs { get; init; } = 250;

    public int CatTransactionTimeoutMs { get; init; } = 500;

    public int CatWriteTimeoutMs { get; init; } = 250;

    public int CatReplyTimeoutMs { get; init; } = 250;

    public int IpcTimeoutMinimumMs { get; init; } = 100;

    public int IpcTimeoutMaximumMs { get; init; } = 3_000;

    public int OutboundQueueFrameCapacityPerClient { get; init; } = 32;

    public int OutboundQueueByteCapacityPerClient { get; init; } = 256 * 1_024;

    public int BlockedPipeWriteTimeoutMs { get; init; } = 2_000;

    public int ShutdownGracePeriodMs { get; init; } = 3_000;

    public int QuietGuardMs { get; init; } = 50;

    public int TransitionTimeoutMs { get; init; } = 1_000;
}
