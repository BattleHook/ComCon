using System.Text;
using System.Threading.Channels;

namespace ComCon.Core.Ipc;

/// <summary>One established control stream; composition owns CAT quiescence before orderly closure.</summary>
public sealed class ComConCommandControlLoop
{
    private readonly Stream stream;
    private readonly ComConCommandNotifications notifications;
    private readonly Channel<byte[]> acknowledgments;
    private readonly TimeSpan pollInterval;
    private readonly object gate = new();
    private bool started;
    private bool finished;
    private string? orderlyReason;

    internal bool UsesTracker(ComConCommandNotifications tracker) => ReferenceEquals(notifications, tracker);

    public ComConCommandControlLoop(
        Stream stream, ComConCommandNotifications notifications,
        int maximumDeferredAcks, TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(notifications);
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("Control stream must support reading and writing.", nameof(stream));
        if (maximumDeferredAcks <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDeferredAcks));
        if (pollInterval <= TimeSpan.Zero || pollInterval.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        this.stream = stream;
        this.notifications = notifications;
        this.pollInterval = pollInterval;
        acknowledgments = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(maximumDeferredAcks)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Call only after CAT admission/dispatch is quiescent and final outcomes are enqueued.</summary>
    public void RequestOrderlyClose(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            if (finished) throw new InvalidOperationException("Control loop has already finished.");
            if (orderlyReason is not null)
                throw new InvalidOperationException("Orderly closure has already been requested.");
            notifications.Enqueue(new Dictionary<string, object>
            {
                ["messageType"] = "session.notification",
                ["event"] = "session.ended",
                ["reason"] = reason
            });
            orderlyReason = reason;
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            if (started) throw new InvalidOperationException("A control loop may run only once.");
            started = true;
        }
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var writer = new SemaphoreSlim(1, 1);
        var reader = ReadAcknowledgmentsAsync(shutdown.Token);
        Task? write = null;
        ComConNotificationTransmission? transmission = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.IsCompleted)
                    await reader.ConfigureAwait(false);
                notifications.CheckTimeouts();
                if (write is { IsCompleted: true })
                {
                    await write.ConfigureAwait(false);
                    notifications.ConfirmTransmission(transmission!);
                    write = null;
                    transmission = null;
                }
                if (write is null)
                {
                    while (acknowledgments.Reader.TryRead(out var payload))
                        notifications.Acknowledge(payload);

                    lock (gate)
                    {
                        if (orderlyReason is not null && notifications.PendingCount == 0)
                        {
                            notifications.Close(orderlyReason);
                            finished = true;
                            return;
                        }
                    }
                    if (reader.IsCompleted)
                        throw new EndOfStreamException("Control stream closed before orderly closure was acknowledged.");
                    transmission = notifications.BeginTransmission();
                    if (transmission is not null)
                        write = ComConCommandProtocol.WriteEnvelopeAsync(
                            stream, Encoding.UTF8.GetBytes(transmission.Envelope.GetRawText()),
                            writer, shutdown.Token).AsTask();
                }
                await Task.Delay(pollInterval, shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or OperationCanceledException)
        {
            var code = exception is ComConCommandProtocolException protocol ? protocol.ErrorCode : exception.GetType().Name;
            notifications.Close($"Control loop ended ({code}): {exception.Message}");
            throw;
        }
        finally
        {
            lock (gate) finished = true;
            shutdown.Cancel();
            // Disposal must unblock injected I/O before awaiting its pending operations.
            try
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                await ObserveShutdownAsync(reader).ConfigureAwait(false);
                if (write is not null)
                    await ObserveShutdownAsync(write).ConfigureAwait(false);
            }
        }
    }

    private async Task ReadAcknowledgmentsAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var received = await ComConCommandProtocol.ReadEnvelopeOrEofAsync(stream, cancellationToken).ConfigureAwait(false);
            if (received is null)
                return;
            var envelope = received.Value;
            if (envelope.GetProperty("messageType").GetString() != "notification.ack")
                throw new ComConCommandProtocolException("INVALID_MESSAGE_TYPE", "Expected notification.ack.");
            if (!acknowledgments.Writer.TryWrite(Encoding.UTF8.GetBytes(envelope.GetRawText())))
                throw new ComConCommandProtocolException(
                    "CONTROL_INGRESS_OVERFLOW", "Bounded deferred ACK capacity was exceeded.");
        }
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or
            OperationCanceledException or ObjectDisposedException)
        {
            // The actor already reports the terminal cause; these tasks are cleanup observations.
        }
    }
}
