using System.Globalization;
using System.Text.Json;
using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

public sealed record ComConNotificationTransmission(long Sequence, JsonElement Envelope, bool IsRetry);

/// <summary>Bounded notification state; transport serializes send completion and inbound ACK processing.</summary>
public sealed class ComConCommandNotifications
{
    private sealed class Pending(JsonElement envelope, int bytes, long deadlineMs)
    {
        public JsonElement Envelope { get; } = envelope;
        public int Bytes { get; } = bytes;
        public long DeadlineMs { get; } = deadlineMs;
        public long? LastSentMs { get; set; }
    }

    private readonly object gate = new();
    private readonly ComConCommandSessions sessions;
    private readonly ComConCommandSession session;
    private readonly IMonotonicClock clock;
    private readonly int maximumPendingCount;
    private readonly int maximumPendingBytes;
    private readonly long acknowledgmentTimeoutMs;
    private readonly long retryIntervalMs;
    private readonly long maximumSequence;
    private readonly SortedDictionary<long, Pending> pending = [];
    private long allocated;
    private long transmitted;
    private ComConNotificationTransmission? inFlight;
    private int retainedBytes;

    public ComConCommandNotifications(
        ComConCommandSessions sessions, ComConCommandSession session, IMonotonicClock clock,
        int maximumPendingCount, int maximumPendingBytes,
        long acknowledgmentTimeoutMs, long retryIntervalMs, long maximumSequence = long.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(clock);
        if (maximumPendingCount <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingCount));
        if (maximumPendingBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumPendingBytes));
        if (acknowledgmentTimeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(acknowledgmentTimeoutMs));
        if (retryIntervalMs <= 0 || retryIntervalMs >= acknowledgmentTimeoutMs)
            throw new ArgumentOutOfRangeException(nameof(retryIntervalMs));
        if (maximumSequence <= 0) throw new ArgumentOutOfRangeException(nameof(maximumSequence));
        if (!sessions.CanRelayCat(session))
            throw new InvalidOperationException("Notification tracking requires an active session owned by this manager.");
        this.sessions = sessions;
        this.session = session;
        this.clock = clock;
        this.maximumPendingCount = maximumPendingCount;
        this.maximumPendingBytes = maximumPendingBytes;
        this.acknowledgmentTimeoutMs = acknowledgmentTimeoutMs;
        this.retryIntervalMs = retryIntervalMs;
        this.maximumSequence = maximumSequence;
    }

    public int PendingCount { get { lock (gate) return pending.Count; } }
    public int RetainedPayloadBytes { get { lock (gate) return retainedBytes; } }
    internal long TransmittedSequence { get { lock (gate) return transmitted; } }

    internal bool IsForSession(ComConCommandSessions owner, ComConCommandSession candidate) =>
        ReferenceEquals(sessions, owner) && ReferenceEquals(session, candidate);

    public long Enqueue(IReadOnlyDictionary<string, object> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        lock (gate)
        {
            RequireActive();
            CheckDeadline();
            if (allocated == maximumSequence)
                throw Terminate("SEQUENCE_EXHAUSTED", "Notification sequence space is exhausted.");

            var sequence = allocated + 1;
            var envelopeFields = new Dictionary<string, object>(fields, StringComparer.Ordinal);
            foreach (var (name, value) in new (string, object)[]
            {
                ("contractVersion", ComConCommandProtocol.ContractVersion),
                ("profileId", session.ProfileId),
                ("sessionId", session.SessionId),
                ("notificationSequence", sequence.ToString(CultureInfo.InvariantCulture))
            })
                if (!envelopeFields.TryAdd(name, value))
                    throw new ArgumentException($"Notification field '{name}' is assigned by the tracker.", nameof(fields));
            var payload = JsonSerializer.SerializeToUtf8Bytes(envelopeFields);
            var envelope = ComConCommandProtocol.ParseEnvelope(payload);
            var type = envelope.GetProperty("messageType").GetString();
            if (type is not ("record.outcome" or "record.rejected" or "framing.notification" or "session.notification"))
                throw new ArgumentException("Only notification message types may be enqueued.", nameof(fields));

            if (pending.Count >= maximumPendingCount || payload.Length > maximumPendingBytes - retainedBytes)
                throw Terminate("NOTIFICATION_OVERFLOW", "Bounded notification retention capacity was exceeded.");
            var now = clock.NowMs;
            if (now < 0 || now > long.MaxValue - acknowledgmentTimeoutMs)
                throw Terminate("NOTIFICATION_CLOCK_ERROR", "The clock cannot represent a notification deadline.");
            pending.Add(sequence, new Pending(envelope, payload.Length, now + acknowledgmentTimeoutMs));
            retainedBytes += payload.Length;
            allocated = sequence;
            return sequence;
        }
    }

    public ComConNotificationTransmission? BeginTransmission()
    {
        lock (gate)
        {
            RequireActive();
            CheckDeadline();
            if (inFlight is not null)
                throw new InvalidOperationException("A notification write is already in progress.");
            foreach (var (sequence, item) in pending)
                if (item.LastSentMs is null)
                    return inFlight = new(sequence, item.Envelope, false);
            var now = clock.NowMs;
            foreach (var (sequence, item) in pending)
                if (item.LastSentMs is { } sent && now - sent >= retryIntervalMs)
                    return inFlight = new(sequence, item.Envelope, true);
            return null;
        }
    }

    public void ConfirmTransmission(ComConNotificationTransmission transmission)
    {
        ArgumentNullException.ThrowIfNull(transmission);
        lock (gate)
        {
            RequireActive();
            CheckDeadline();
            RequireInFlight(transmission);
            var item = pending[transmission.Sequence];
            if (!transmission.IsRetry)
                transmitted = transmission.Sequence;
            item.LastSentMs = clock.NowMs;
            inFlight = null;
        }
    }

    public void FailTransmission(ComConNotificationTransmission transmission, string reason)
    {
        ArgumentNullException.ThrowIfNull(transmission);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            RequireActive();
            RequireInFlight(transmission);
            throw Terminate("NOTIFICATION_WRITE_FAILED", reason);
        }
    }

    public void Acknowledge(ReadOnlySpan<byte> payload)
    {
        lock (gate)
        {
            RequireActive();
            CheckDeadline();
            if (inFlight is not null)
                throw new InvalidOperationException("Process inbound ACKs only after the current write completes.");
            JsonElement envelope;
            try
            {
                envelope = ComConCommandProtocol.ParseEnvelope(payload);
            }
            catch (ComConCommandProtocolException exception)
            {
                throw Terminate(exception.ErrorCode, exception.Message);
            }
            if (envelope.GetProperty("messageType").GetString() != "notification.ack")
                throw Terminate("INVALID_MESSAGE_TYPE", "Expected notification.ack on the established control channel.");
            if (envelope.GetProperty("profileId").GetInt32() != session.ProfileId ||
                envelope.GetProperty("sessionId").GetString() != session.SessionId)
                throw Terminate("PROFILE_SESSION_MISMATCH", "Notification ACK identity does not match this session.");
            if (!long.TryParse(envelope.GetProperty("notificationSequence").GetString(),
                    NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) ||
                sequence > transmitted)
                throw Terminate("INVALID_NOTIFICATION_ACK", "ACK refers to a notification not yet transmitted.");
            if (pending.Remove(sequence, out var item))
                retainedBytes -= item.Bytes;
        }
    }

    public void CheckTimeouts()
    {
        lock (gate)
        {
            RequireActive();
            CheckDeadline();
        }
    }

    public void Close(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        lock (gate)
        {
            sessions.EndSession(session, reason);
            Clear();
        }
    }

    private void RequireInFlight(ComConNotificationTransmission transmission)
    {
        if (!ReferenceEquals(inFlight, transmission))
            throw new InvalidOperationException("Transmission token does not match the current write.");
    }

    private void RequireActive()
    {
        if (!sessions.CanRelayCat(session))
        {
            Clear();
            throw new InvalidOperationException("The notification session is no longer active.");
        }
    }

    private void CheckDeadline()
    {
        var now = clock.NowMs;
        if (pending.Values.Any(item => now >= item.DeadlineMs))
            throw Terminate("NOTIFICATION_ACK_TIMEOUT", "Notification receipt was not acknowledged before its deadline.");
    }

    private ComConCommandProtocolException Terminate(string code, string reason)
    {
        sessions.EndSession(session, reason);
        Clear();
        return new(code, reason);
    }

    private void Clear()
    {
        pending.Clear();
        retainedBytes = 0;
        inFlight = null;
    }
}
