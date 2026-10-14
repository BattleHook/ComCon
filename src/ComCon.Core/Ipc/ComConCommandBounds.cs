namespace ComCon.Core.Ipc;

public static class ComConCommandBounds
{
    public const int SetupTimeoutMs = 5_000;
    public const int MaximumPendingNotifications = 32;
    public const int MaximumPendingNotificationBytes = 32 * 1_024;
    public const int AcknowledgmentTimeoutMs = 2_000;
    public const int AcknowledgmentRetryIntervalMs = 500;
    public const int MaximumDeferredAcknowledgments = 8;
    public const int ControlPollIntervalMs = 2;
}
