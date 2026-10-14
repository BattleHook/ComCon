using System.Globalization;
using ComCon.Core.Routing;
using ComCon.Core.Transport;

namespace ComCon.Core.Ipc;

/// <summary>Accepted-session CAT adapter; composition supplies bounded byte and control transports.</summary>
public sealed class ComConCommandEndpoint : ISharedRadioIpcEndpoint
{
    private readonly IByteBridgeEndpoint cat;
    private readonly ComConCommandSessions sessions;
    private readonly ComConCommandSession session;
    private readonly ComConCommandNotifications notifications;
    private readonly object gate = new();
    private bool disposed;
    private bool preserveControlOnDispose;
    private bool catEof;
    private Action<string>? onCatEof;

    internal void SetCatEofHandler(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (gate)
        {
            if (onCatEof is not null)
                throw new InvalidOperationException("The CAT EOF handler is already configured.");
            onCatEof = handler;
        }
    }

    internal void PrepareOrderlyStop()
    {
        lock (gate)
        {
            if (disposed || !sessions.CanRelayCat(session))
                throw new InvalidOperationException("Orderly shutdown requires an active IPC endpoint.");
            preserveControlOnDispose = true;
        }
    }

    internal bool UsesTracker(ComConCommandNotifications tracker) => ReferenceEquals(notifications, tracker);

    public ComConCommandEndpoint(
        IByteBridgeEndpoint cat, ComConCommandSessions sessions,
        ComConCommandSession session, ComConCommandNotifications notifications)
    {
        ArgumentNullException.ThrowIfNull(cat);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(notifications);
        if (!sessions.CanRelayCat(session))
            throw new InvalidOperationException("CAT adapter requires an accepted active session.");
        if (!notifications.IsForSession(sessions, session))
            throw new ArgumentException("Notification tracker belongs to another session.", nameof(notifications));
        this.cat = cat;
        this.sessions = sessions;
        this.session = session;
        this.notifications = notifications;
    }

    public int ProfileId => session.ProfileId;
    public string? Fault => session.EndReason;

    public bool Poll()
    {
        lock (gate)
        {
            if (disposed || !sessions.CanRelayCat(session))
                return false;
            try
            {
                notifications.CheckTimeouts();
                return true;
            }
            catch (ComConCommandProtocolException)
            {
                return false;
            }
        }
    }

    public void OnDiagnostic(SharedRadioDiagnostic diagnostic)
    {
        lock (gate)
        {
            if (disposed || !sessions.CanRelayCat(session) || diagnostic.Client != SharedRadioClient.IpcClient)
                return;
            try
            {
                if (diagnostic.Kind is SharedRadioDiagnosticKind.FramingAnomaly or SharedRadioDiagnosticKind.FramingOverflow)
                {
                    notifications.Enqueue(new Dictionary<string, object>
                    {
                        ["messageType"] = "framing.notification",
                        ["errorCode"] = diagnostic.Kind == SharedRadioDiagnosticKind.FramingOverflow
                            ? "FRAMING_OVERFLOW" : "FRAMING_ANOMALY",
                        ["reason"] = diagnostic.Message
                    });
                }
                else if (diagnostic.RecordSequence is { } sequence && diagnostic.Outcome is { } outcome)
                {
                    var fields = new Dictionary<string, object>
                    {
                        ["messageType"] = diagnostic.Dispatched ? "record.outcome" : "record.rejected",
                        ["recordSequence"] = sequence.ToString(CultureInfo.InvariantCulture),
                        ["deliveryStatus"] = outcome switch
                        {
                            SharedRadioOutcome.WriteSettled => "WRITE_SUCCEEDED",
                            SharedRadioOutcome.NotSent => "NOT_SENT",
                            SharedRadioOutcome.UnknownDelivery => "UNKNOWN_DELIVERY",
                            _ => throw new ArgumentOutOfRangeException(nameof(diagnostic))
                        }
                    };
                    if (outcome != SharedRadioOutcome.WriteSettled)
                        fields["reason"] = diagnostic.Message;
                    notifications.Enqueue(fields);
                }
                else if (diagnostic.Kind == SharedRadioDiagnosticKind.ClientDisconnected && !preserveControlOnDispose)
                {
                    if (catEof && onCatEof is not null)
                    {
                        preserveControlOnDispose = true;
                        onCatEof(diagnostic.Message);
                    }
                    else
                        notifications.Close(diagnostic.Message);
                }
            }
            catch (ComConCommandProtocolException exception)
            {
                if (sessions.CanRelayCat(session))
                    notifications.Close($"IPC notification failure ({exception.ErrorCode}): {exception.Message}");
            }
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        RequireActive();
        var count = await cat.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        RequireActive();
        if (count == 0)
            lock (gate) catEof = true;
        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        RequireActive();
        await cat.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        RequireActive();
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (!preserveControlOnDispose)
                notifications.Close("IPC CAT endpoint closed.");
        }
        await cat.DisposeAsync().ConfigureAwait(false);
    }

    private void RequireActive()
    {
        lock (gate)
            if (disposed || !sessions.CanRelayCat(session))
                throw new IOException($"IPC CAT session is unavailable: {session.EndReason}");
    }
}
