using System.Text;
using System.Text.Json;
using System.Security;
using System.Threading.Channels;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace ComCon.Core.Ipc;

public enum RawCatCaptureStatus
{
    Off,
    Armed,
    Capturing,
    Error
}

public sealed record RawCatCaptureSnapshot(
    RawCatCaptureStatus Status, bool Enabled, string? Error, string LogPath, long LogSizeBytes);

public sealed class RawCatCapture : IAsyncDisposable
{
    public const int QueueCapacity = 256;
    public const int MaximumLogFiles = 10;
    private const int MaximumBatchRecords = 32;
    private static readonly JsonSerializerOptions CaptureJsonOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    private readonly string preferencePath;
    private readonly BoundedJsonLinesFile log;
    private readonly string logPath;
    private readonly Channel<LogCommand> queue = Channel.CreateBounded<LogCommand>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    private readonly Task writer;
    private readonly object gate = new();
    private Task? overflowPreferenceWrite;
    private bool preferenceEnabled;
    private readonly Dictionary<Guid, CaptureSession> sessions = [];
    private CaptureSession? bridgeSession;
    private bool disposed;
    private bool captureFailed;
    private string? error;
    private string? preferenceWarning;
    private string? framingWarning;

    public RawCatCapture(string localDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localDataDirectory);
        var directory = Path.Combine(Path.GetFullPath(localDataDirectory), "ComCon", "Logging");
        preferencePath = Path.Combine(directory, "ComCon-raw-cat-capture.json");
        logPath = Path.Combine(directory, "ComCon-raw-cat.jsonl");
        log = new(logPath, "ComConRawCatLog_", MaximumLogFiles);
        preferenceEnabled = LoadPreference(preferencePath);
        writer = Task.Run(WriterAsync);
    }

    public event EventHandler<RawCatCaptureSnapshot>? StatusChanged;

    public RawCatCaptureSnapshot Snapshot
    {
        get
        {
            lock (gate) return CreateSnapshot();
        }
    }

    public CaptureSession BridgeStarted(int id, string label, string framing)
    {
        if (bridgeSession is not null)
            throw new InvalidOperationException("A raw CAT capture session is already active for the bridge.");
        bridgeSession = StartSession(id, label, framing);
        return bridgeSession;
    }

    public void BridgeStopped()
    {
        if (bridgeSession is not { } session) return;
        bridgeSession = null;
        StopSession(session);
    }

    public CaptureSession StartSession(int id, string label, string framing)
    {
        if (id < 1) throw new ArgumentOutOfRangeException(nameof(id));
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentException.ThrowIfNullOrWhiteSpace(framing);
        CaptureSession session;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            session = new(this, id, label, Guid.NewGuid().ToString("N"), framing,
                framing is "Civ" or "KenwoodAscii");
            sessions.Add(session.Id, session);
            UpdateFramingWarning();
        }
        RaiseStatus();
        return session;
    }

    public void StopSession(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!ReferenceEquals(session.Owner, this))
            throw new ArgumentException("Capture session belongs to another capture.", nameof(session));
        lock (gate)
        {
            if (!sessions.Remove(session.Id)) return;
            UpdateFramingWarning();
        }
        RaiseStatus();
    }

    public void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            preferenceEnabled = enabled;
            captureFailed = false;
            error = null;
            preferenceWarning = null;
            var preferenceSaved = true;
            try { SavePreference(preferencePath, enabled); }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                preferenceWarning = $"Capture preference could not be saved: {exception.Message}";
                preferenceEnabled = enabled;
                preferenceSaved = false;
            }
            if (preferenceSaved) UpdateFramingWarning();
        }
        RaiseStatus();
    }

    public bool CanCapture(CaptureSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (gate) return CanCaptureUnderLock(session);
    }

    private bool CanCaptureUnderLock(CaptureSession session) =>
        ReferenceEquals(session.Owner, this) && !disposed && !captureFailed &&
        preferenceEnabled && sessions.ContainsKey(session.Id) && session.FramingSupported;

    public bool TryCapture(
        CaptureSession session, string source, string direction, string outcome, ReadOnlySpan<byte> bytes,
        long? monotonicTimestampMs = null, long? monotonicTimestampTicks = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(direction);
        ArgumentException.ThrowIfNullOrWhiteSpace(outcome);
        lock (gate)
        {
            if (!CanCaptureUnderLock(session)) return false;
            var entry = new CaptureEntry(
                DateTimeOffset.UtcNow, session.ProfileId, session.ProfileLabel, session.SessionId, session.Framing, source,
                direction, outcome, Convert.ToHexString(bytes), monotonicTimestampMs, monotonicTimestampTicks,
                monotonicTimestampTicks is null ? null : Stopwatch.Frequency);
            if (queue.Writer.TryWrite(new AppendCommand(entry))) return true;
        }
        DisableAfterQueueOverflow("Raw CAT capture queue overflow; capture stopped. CAT forwarding continues.");
        return false;
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new ClearCommand(completion)))
            throw new IOException("Raw CAT capture queue is full; logs were not cleared.");
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriterAsync()
    {
        LogCommand? pending = null;
        while (pending is not null || await queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var command = pending ?? await queue.Reader.ReadAsync().ConfigureAwait(false);
            pending = null;
            try
            {
                switch (command)
                {
                    case AppendCommand append:
                        var records = new List<byte[]>(MaximumBatchRecords);
                        var batchBytes = 0;
                        while (true)
                        {
                            var line = JsonSerializer.SerializeToUtf8Bytes(append.Entry, CaptureJsonOptions);
                            var record = new byte[line.Length + 1];
                            line.CopyTo(record, 0);
                            record[^1] = (byte)'\n';
                            if (records.Count > 0 &&
                                batchBytes + record.Length > BoundedJsonLinesFile.MaximumFileBytes)
                            {
                                pending = append;
                                break;
                            }
                            records.Add(record);
                            batchBytes += record.Length;
                            if (records.Count >= MaximumBatchRecords ||
                                batchBytes >= BoundedJsonLinesFile.MaximumFileBytes ||
                                !queue.Reader.TryRead(out var next))
                                break;
                            if (next is not AppendCommand nextAppend)
                            {
                                pending = next;
                                break;
                            }
                            append = nextAppend;
                        }
                        log.AppendBatch(records);
                        break;
                    case ClearCommand clear:
                        log.Clear();
                        clear.Completion.TrySetResult();
                        break;
                }
                RaiseStatus();
            }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                if (command is ClearCommand clear) clear.Completion.TrySetException(exception);
                DisableAfterFailure($"Raw CAT capture failed: {exception.Message}. CAT forwarding continues.");
            }
        }
    }

    private void DisableAfterFailure(string message)
    {
        lock (gate)
        {
            preferenceEnabled = false;
            captureFailed = true;
            error = message;
            try { SavePreference(preferencePath, false); }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                error += $" Capture could not be persisted as off: {exception.Message}";
            }
        }
        RaiseStatus();
    }

    private void DisableAfterQueueOverflow(string message)
    {
        lock (gate)
        {
            preferenceEnabled = false;
            captureFailed = true;
            error = message;
            overflowPreferenceWrite = Task.Run(PersistQueueOverflowPreference);
        }
        RaiseStatus();
    }

    private void PersistQueueOverflowPreference()
    {
        lock (gate)
        {
            if (!captureFailed || preferenceEnabled) return;
            try { SavePreference(preferencePath, false); }
            catch (Exception exception) when (IsStorageFailure(exception))
            {
                error += $" Capture could not be persisted as off: {exception.Message}";
            }
        }
        RaiseStatus();
    }

    private RawCatCaptureSnapshot CreateSnapshot()
    {
        var snapshotError = string.Join(" ", new[] { error, preferenceWarning, framingWarning }
            .Where(message => !string.IsNullOrWhiteSpace(message)));
        var capturing = sessions.Values.Any(session => session.FramingSupported);
        var status = snapshotError.Length != 0
            ? RawCatCaptureStatus.Error
            : !preferenceEnabled ? RawCatCaptureStatus.Off
            : capturing ? RawCatCaptureStatus.Capturing
            : RawCatCaptureStatus.Armed;
        return new(status, preferenceEnabled, snapshotError.Length == 0 ? null : snapshotError,
            logPath, log.Size);
    }

    private void UpdateFramingWarning() =>
        framingWarning = preferenceEnabled && sessions.Values.Any(session => !session.FramingSupported)
            ? "Raw capture needs CI-V or Kenwood ASCII record framing; it is suspended for an active profile."
            : null;

    private void RaiseStatus()
    {
        var handler = StatusChanged;
        if (handler is null) return;
        RawCatCaptureSnapshot snapshot;
        lock (gate) snapshot = CreateSnapshot();
        handler(this, snapshot);
    }

    private static bool LoadPreference(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("enabled", out var enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new IOException("Raw CAT capture preference is malformed.");
            return enabled.GetBoolean();
        }
        catch (JsonException exception)
        {
            throw new IOException("Raw CAT capture preference is malformed.", exception);
        }
    }

    private static void SavePreference(string path, bool enabled)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new { enabled }) + Environment.NewLine,
                new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool IsStorageFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;

    public async ValueTask DisposeAsync()
    {
        Task? pendingPreferenceWrite;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            queue.Writer.TryComplete();
            pendingPreferenceWrite = overflowPreferenceWrite;
        }
        await writer.ConfigureAwait(false);
        if (pendingPreferenceWrite is not null)
            await pendingPreferenceWrite.ConfigureAwait(false);
    }

    private abstract record LogCommand;
    private sealed record AppendCommand(CaptureEntry Entry) : LogCommand;
    private sealed record ClearCommand(TaskCompletionSource Completion) : LogCommand;
    private sealed record CaptureEntry(
        DateTimeOffset TimestampUtc, int ProfileId, string ProfileLabel, string SessionId,
        string Framing, string Source, string Direction, string Outcome, string Hex,
        long? MonotonicTimestampMs, long? MonotonicTimestampTicks, long? MonotonicTimestampFrequency);

    public sealed class CaptureSession
    {
        internal CaptureSession(
            RawCatCapture owner, int profileId, string profileLabel, string sessionId,
            string framing, bool framingSupported)
        {
            Owner = owner;
            ProfileId = profileId;
            ProfileLabel = profileLabel;
            SessionId = sessionId;
            Framing = framing;
            FramingSupported = framingSupported;
        }

        internal RawCatCapture Owner { get; }
        internal Guid Id { get; } = Guid.NewGuid();
        internal int ProfileId { get; }
        internal string ProfileLabel { get; }
        internal string SessionId { get; }
        internal string Framing { get; }
        internal bool FramingSupported { get; }
    }
}
