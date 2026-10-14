using ComCon.Core.Models;
using ComCon.Core.Protocol;

namespace ComCon.Core.Routing;

public enum SharedRadioClient
{
    LegacyClient,
    IpcClient
}

public enum SharedRadioCoordinatorState
{
    Running,
    Faulted,
    Stopped
}

public enum SharedRadioOutcome
{
    NotSent,
    UnknownDelivery,
    WriteSettled
}

public enum SharedRadioDiagnosticKind
{
    RecordRejected,
    RecordExpired,
    ClientDisconnected,
    ProfileFaulted,
    FramingAnomaly,
    UnmatchedAddress,
    ReceiveDelivered,
    RecordWriteCompleted,
    FramingOverflow,
    RadioIngressProcessed,
    RadioReplyTimeout,
    RadioExchangeRecovered,
    LegacyLifecycle,
    LegacyPollingConfirmed
}

public sealed record SharedRadioCoordinatorOptions(
    FramingMode Framing,
    byte LegacyClientAddress,
    byte IpcClientAddress,
    int IngressChunksPerSource,
    int IngressBytesPerSource,
    int TxQueueRecordsPerClient,
    int TxQueueBytesPerClient,
    int TxQueueRecordsPerProfile,
    int ClientRxQueueRecordsPerClient,
    int ClientRxQueueBytesPerClient,
    long QueueWaitLimitMs,
    bool AllowUnmatchedEnabled = false,
    IReadOnlySet<byte>? AllowedUnmatchedAddresses = null,
    bool DenyUnmatchedEnabled = false,
    IReadOnlySet<byte>? DeniedUnmatchedAddresses = null,
    byte? InformationalRadioAddress = null,
    bool CivExchangeGateEnabled = false);

public sealed record SharedRadioDiagnostic(
    SharedRadioDiagnosticKind Kind,
    string Message,
    SharedRadioClient? Client = null,
    SharedRadioOutcome? Outcome = null,
    byte? Address = null,
    int ByteCount = 0,
    long? RecordSequence = null,
    bool Dispatched = false,
    long? MonotonicTimestampMs = null,
    long? ParserIdleElapsedMs = null,
    long? ParserTotalElapsedMs = null);

public sealed record SharedRadioWrite(
    Guid Id, SharedRadioClient Client, byte[] Bytes, long EnqueuedMs, long? RecordSequence = null);

public sealed record SharedRadioClientDelivery(
    Guid Id, SharedRadioClient Client, byte[] Bytes, long ReceivedMs, bool ConfirmsLegacyPolling = false);

public sealed record SharedRadioCoordinatorMetrics(
    long OfferedTxRecords,
    long AcceptedTxRecords,
    long DispatchedTxRecords,
    long SettledTxWrites,
    long UnknownTxWrites,
    long NotSentTxRecords,
    long RadioRxRecords,
    long ClientRxDeliveries,
    int TxQueueRecordsHighWater,
    int TxQueueBytesHighWater,
    int LegacyTxQueueRecordsHighWater,
    int LegacyTxQueueBytesHighWater,
    int IpcTxQueueRecordsHighWater,
    int IpcTxQueueBytesHighWater,
    int LegacyRxQueueRecordsHighWater,
    int LegacyRxQueueBytesHighWater,
    int IpcRxQueueRecordsHighWater,
    int IpcRxQueueBytesHighWater,
    int LegacyIngressChunksHighWater,
    int IpcIngressChunksHighWater,
    int RadioIngressChunksHighWater,
    int LegacyIngressBytesHighWater,
    int IpcIngressBytesHighWater,
    int RadioIngressBytesHighWater,
    long TxQueueWaitMsHighWater);

/// <summary>
/// Hardware-free serialized shared-mode coordinator. Adapter threads may post
/// bounded byte chunks; all parsing, scheduling, routing and completion methods
/// are confined to the construction thread.
/// </summary>
public sealed class SharedRadioCoordinator
{
    public const long CivAnswerWaitMs = 750;
    public const long CivRecoverySilenceMs = 250;
    public const long CivRecoveryWaitMs = 750;

    private sealed class CivReservation(SharedRadioWrite write)
    {
        public SharedRadioWrite Write { get; } = write;
        public long? CompletedMs { get; set; }
        public long LastReceiveMs { get; set; }
        public bool ReplyReceived { get; set; }
        public bool Recovering { get; set; }
    }

    private sealed record TxRecord(
        SharedRadioClient Client, byte[] Bytes, long EnqueuedMs, long? RecordSequence);

    private sealed class ClientQueue
    {
        public Queue<SharedRadioClientDelivery> Rx { get; } = new();
        public SharedRadioClientDelivery? RxInFlight { get; set; }
        public int TxBytes { get; set; }
        public int RxBytes { get; set; }
        public bool Connected { get; set; } = true;
    }

    private sealed class BoundedIngress
    {
        private readonly int chunkCapacity;
        private readonly int byteCapacity;
        private readonly Queue<byte[]> chunks = new();
        private int byteCount;
        private bool overflowed;
        private bool overflowReported;
        private int chunksHighWater;
        private int bytesHighWater;

        public BoundedIngress(int chunkCapacity, int byteCapacity)
        {
            this.chunkCapacity = chunkCapacity;
            this.byteCapacity = byteCapacity;
        }

        public bool TryPost(ReadOnlySpan<byte> bytes)
        {
            if (bytes.IsEmpty)
            {
                throw new ArgumentException("Ingress chunks must contain at least one byte.", nameof(bytes));
            }

            lock (chunks)
            {
                if (overflowed)
                {
                    return false;
                }

                if (bytes.Length > byteCapacity || bytes.Length > 4096 ||
                    chunks.Count >= chunkCapacity ||
                    byteCount > byteCapacity - bytes.Length)
                {
                    overflowed = true;
                    return false;
                }

                var copy = bytes.ToArray();
                chunks.Enqueue(copy);
                byteCount += copy.Length;
                chunksHighWater = Math.Max(chunksHighWater, chunks.Count);
                bytesHighWater = Math.Max(bytesHighWater, byteCount);
                return true;
            }
        }

        public (bool Overflowed, byte[]? Chunk) Take()
        {
            lock (chunks)
            {
                if (overflowed && !overflowReported)
                {
                    overflowReported = true;
                    chunks.Clear();
                    byteCount = 0;
                    return (true, null);
                }

                if (!chunks.TryDequeue(out var chunk))
                {
                    return (false, null);
                }

                byteCount -= chunk.Length;
                return (false, chunk);
            }
        }

        public (int Chunks, int Bytes) HighWater
        {
            get
            {
                lock (chunks)
                {
                    return (chunksHighWater, bytesHighWater);
                }
            }
        }

        public void Clear(bool resetOverflow = false)
        {
            lock (chunks)
            {
                chunks.Clear();
                byteCount = 0;
                if (resetOverflow)
                {
                    overflowed = false;
                    overflowReported = false;
                }
            }
        }
    }

    private readonly SharedRadioCoordinatorOptions options;
    private readonly IMonotonicClock clock;
    private readonly Action<SharedRadioDiagnostic> diagnosticSink;
    private readonly Action<int>? radioIngressProcessed;
    private readonly Dictionary<SharedRadioClient, ClientQueue> clients = new()
    {
        [SharedRadioClient.LegacyClient] = new(),
        [SharedRadioClient.IpcClient] = new()
    };
    private readonly Dictionary<SharedRadioClient, BoundedIngress> clientIngress;
    private readonly BoundedIngress radioIngress;
    private readonly object ingressPostGate = new();
    private readonly HashSet<SharedRadioClient> acceptingClientIngress =
        [SharedRadioClient.LegacyClient, SharedRadioClient.IpcClient];
    private readonly HashSet<Guid> cancelledClientDeliveries = [];
    private readonly Queue<TxRecord> legacyTx = new();
    private readonly Queue<TxRecord> ipcTx = new();
    private CivFrameParser? legacyCiv;
    private CivFrameParser? ipcCiv;
    private CivFrameParser? radioCiv;
    private KenwoodAsciiFrameParser? legacyKenwood;
    private KenwoodAsciiFrameParser? ipcKenwood;
    private KenwoodAsciiFrameParser? radioKenwood;
    private SharedRadioWrite? writeInFlight;
    private CivReservation? civReservation;
    private SharedRadioWrite? legacyPollingRead;
    private long legacyPollingWrittenMs;
    private int ingressTurn;
    private bool dispatchLegacyNext = true;
    private int actorThread;
    private int stoppedFlag;
    private bool legacyKenwoodDiscardUntilBoundary;
    private bool ipcKenwoodDiscardUntilBoundary;
    private bool radioKenwoodDiscardUntilBoundary;
    private long offeredTxRecords;
    private long ipcRecordSequence;
    private long acceptedTxRecords;
    private long dispatchedTxRecords;
    private long settledTxWrites;
    private long unknownTxWrites;
    private long notSentTxRecords;
    private long radioRxRecords;
    private long clientRxDeliveries;
    private long txQueueWaitMsHighWater;
    private int txQueueRecordsHighWater;
    private int txQueueBytesHighWater;
    private int legacyTxQueueRecordsHighWater;
    private int legacyTxQueueBytesHighWater;
    private int ipcTxQueueRecordsHighWater;
    private int ipcTxQueueBytesHighWater;
    private int legacyRxQueueRecordsHighWater;
    private int legacyRxQueueBytesHighWater;
    private int ipcRxQueueRecordsHighWater;
    private int ipcRxQueueBytesHighWater;

    public SharedRadioCoordinator(
        SharedRadioCoordinatorOptions options,
        IMonotonicClock clock,
        Action<SharedRadioDiagnostic> diagnosticSink,
        Action<int>? radioIngressProcessed = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        ValidateOptions(options);
        this.options = options with
        {
            AllowedUnmatchedAddresses = options.AllowedUnmatchedAddresses is null
                ? new HashSet<byte>()
                : new HashSet<byte>(options.AllowedUnmatchedAddresses),
            DeniedUnmatchedAddresses = options.DeniedUnmatchedAddresses is null
                ? new HashSet<byte>()
                : new HashSet<byte>(options.DeniedUnmatchedAddresses)
        };
        this.clock = clock;
        this.diagnosticSink = diagnosticSink;
        this.radioIngressProcessed = radioIngressProcessed;
        clientIngress = new Dictionary<SharedRadioClient, BoundedIngress>
        {
            [SharedRadioClient.LegacyClient] = new(options.IngressChunksPerSource, options.IngressBytesPerSource),
            [SharedRadioClient.IpcClient] = new(options.IngressChunksPerSource, options.IngressBytesPerSource)
        };
        radioIngress = new(options.IngressChunksPerSource, options.IngressBytesPerSource);

        if (options.Framing == FramingMode.Civ)
        {
            legacyCiv = new(clock, e => OnCivEvent(SharedRadioClient.LegacyClient, isRadio: false, e));
            ipcCiv = new(clock, e => OnCivEvent(SharedRadioClient.IpcClient, isRadio: false, e));
            radioCiv = new(clock, e => OnCivEvent(null, isRadio: true, e));
        }
        else
        {
            legacyKenwood = new(clock, e => OnKenwoodEvent(SharedRadioClient.LegacyClient, isRadio: false, e));
            ipcKenwood = new(clock, e => OnKenwoodEvent(SharedRadioClient.IpcClient, isRadio: false, e));
            radioKenwood = new(clock, e => OnKenwoodEvent(null, isRadio: true, e));
        }
        actorThread = Environment.CurrentManagedThreadId;
    }

    public SharedRadioCoordinatorState State { get; private set; } = SharedRadioCoordinatorState.Running;
    public string? Fault { get; private set; }
    public int QueuedTxRecords
    {
        get
        {
            CheckActor();
            return legacyTx.Count + ipcTx.Count;
        }
    }

    public bool IsConnected(SharedRadioClient client)
    {
        CheckActor();
        return clients[client].Connected;
    }

    public void NotifyClientDisconnected(SharedRadioClient client, string reason)
    {
        CheckActor();
        if (!Enum.IsDefined(client))
        {
            throw new ArgumentOutOfRangeException(nameof(client));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        DisconnectClient(client, reason);
    }

    public void NotifyRadioFailure(string reason)
    {
        CheckActor();
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        LatchProfileFault(reason);
    }

    public void ReconnectIpcClient()
    {
        CheckActor();
        if (State != SharedRadioCoordinatorState.Running)
            throw new InvalidOperationException("IPC reattachment requires a running coordinator.");
        if (clients[SharedRadioClient.IpcClient].Connected ||
            writeInFlight?.Client == SharedRadioClient.IpcClient)
            throw new InvalidOperationException("The previous IPC session has not quiesced.");
        ipcRecordSequence = 0;
        ipcKenwoodDiscardUntilBoundary = false;
        if (options.Framing == FramingMode.Civ)
            ipcCiv = new(clock, e => OnCivEvent(SharedRadioClient.IpcClient, isRadio: false, e));
        else
            ipcKenwood = new(clock, e => OnKenwoodEvent(SharedRadioClient.IpcClient, isRadio: false, e));
        lock (ingressPostGate)
        {
            clientIngress[SharedRadioClient.IpcClient].Clear(resetOverflow: true);
            clients[SharedRadioClient.IpcClient].Connected = true;
            acceptingClientIngress.Add(SharedRadioClient.IpcClient);
        }
    }

    public void ReconnectLegacyClient()
    {
        CheckActor();
        if (State != SharedRadioCoordinatorState.Running)
            throw new InvalidOperationException("Legacy reattachment requires a running coordinator.");
        if (clients[SharedRadioClient.LegacyClient].Connected ||
            writeInFlight?.Client == SharedRadioClient.LegacyClient ||
            civReservation?.Write.Client == SharedRadioClient.LegacyClient)
            throw new InvalidOperationException("The previous Legacy exchange has not quiesced.");
        legacyKenwoodDiscardUntilBoundary = false;
        if (options.Framing == FramingMode.Civ)
            legacyCiv = new(clock, e => OnCivEvent(SharedRadioClient.LegacyClient, isRadio: false, e));
        else
            legacyKenwood = new(clock, e => OnKenwoodEvent(SharedRadioClient.LegacyClient, isRadio: false, e));
        lock (ingressPostGate)
        {
            clientIngress[SharedRadioClient.LegacyClient].Clear(resetOverflow: true);
            clients[SharedRadioClient.LegacyClient].Connected = true;
            acceptingClientIngress.Add(SharedRadioClient.LegacyClient);
        }
    }

    public SharedRadioCoordinatorMetrics Metrics
    {
        get
        {
            CheckActor();
            var legacyIngressHighWater = clientIngress[SharedRadioClient.LegacyClient].HighWater;
            var ipcIngressHighWater = clientIngress[SharedRadioClient.IpcClient].HighWater;
            var radioIngressHighWater = radioIngress.HighWater;
            return new(
                offeredTxRecords, acceptedTxRecords, dispatchedTxRecords,
                settledTxWrites, unknownTxWrites, notSentTxRecords,
                radioRxRecords, clientRxDeliveries,
                txQueueRecordsHighWater, txQueueBytesHighWater,
                legacyTxQueueRecordsHighWater, legacyTxQueueBytesHighWater,
                ipcTxQueueRecordsHighWater, ipcTxQueueBytesHighWater,
                legacyRxQueueRecordsHighWater, legacyRxQueueBytesHighWater,
                ipcRxQueueRecordsHighWater, ipcRxQueueBytesHighWater,
                legacyIngressHighWater.Chunks, ipcIngressHighWater.Chunks,
                radioIngressHighWater.Chunks,
                legacyIngressHighWater.Bytes, ipcIngressHighWater.Bytes,
                radioIngressHighWater.Bytes,
                txQueueWaitMsHighWater);
        }
    }

    public bool TryPostClientBytes(SharedRadioClient client, ReadOnlySpan<byte> bytes)
    {
        if (!Enum.IsDefined(client))
        {
            throw new ArgumentOutOfRangeException(nameof(client));
        }

        lock (ingressPostGate)
        {
            return stoppedFlag == 0 &&
                acceptingClientIngress.Contains(client) &&
                clientIngress[client].TryPost(bytes);
        }
    }

    public bool TryPostRadioBytes(ReadOnlySpan<byte> bytes)
    {
        lock (ingressPostGate)
        {
            return stoppedFlag == 0 && radioIngress.TryPost(bytes);
        }
    }

    internal void AdmitCompleteTxRecordForTesting(SharedRadioClient client, ReadOnlySpan<byte> bytes)
    {
        CheckActor();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytes.Length, 256);
        ArgumentOutOfRangeException.ThrowIfZero(bytes.Length);
        AdmitTx(client, bytes);
    }

    public void ProcessAvailable()
    {
        CheckActor();
        if (State == SharedRadioCoordinatorState.Stopped)
        {
            return;
        }

        while (true)
        {
            var didWork = false;
            for (var i = 0; i < 3; i++)
            {
                var sourceIndex = (ingressTurn + i) % 3;
                var ingress = GetIngress(sourceIndex);
                var (overflowed, chunk) = ingress.Take();
                if (overflowed)
                {
                    if (sourceIndex == 2)
                    {
                        LatchProfileFault("Radio RX ingress overflow.");
                    }
                    else
                    {
                        DisconnectClient((SharedRadioClient)sourceIndex, "Client TX ingress overflow.");
                    }

                    ingressTurn = (sourceIndex + 1) % 3;
                    didWork = true;
                    break;
                }

                if (chunk is not null)
                {
                    Feed(sourceIndex, chunk);
                    ingressTurn = (sourceIndex + 1) % 3;
                    didWork = true;
                    break;
                }
            }

            if (!didWork)
            {
                TickParsers();
                return;
            }
        }
    }

    public bool TryTakeRadioWrite(out SharedRadioWrite? write)
    {
        CheckActor();
        ExpireQueuedRecords();
        AdvanceCivReservation();
        if (State != SharedRadioCoordinatorState.Running || writeInFlight is not null ||
            civReservation is not null)
        {
            write = null;
            return false;
        }

        var record = DequeueNextTx();
        if (record is null)
        {
            write = null;
            return false;
        }

        write = new(Guid.NewGuid(), record.Client, record.Bytes.ToArray(), record.EnqueuedMs, record.RecordSequence);
        writeInFlight = write;
        if (options.CivExchangeGateEnabled)
            civReservation = new(write);
        txQueueWaitMsHighWater = Math.Max(txQueueWaitMsHighWater, clock.NowMs - record.EnqueuedMs);
        dispatchedTxRecords++;
        return true;
    }

    public void CompleteRadioWrite(Guid writeId, AdapterWriteCompletion completion)
    {
        CheckActor();
        if (!Enum.IsDefined(completion))
        {
            throw new ArgumentOutOfRangeException(nameof(completion));
        }
        if (writeInFlight is null || writeInFlight.Id != writeId)
        {
            throw new InvalidOperationException("Radio write completion does not match the in-flight record.");
        }

        var completed = writeInFlight;
        writeInFlight = null;
        if (completed.Client == SharedRadioClient.LegacyClient)
            legacyPollingRead = null;
        if (completion == AdapterWriteCompletion.Unknown)
        {
            unknownTxWrites++;

            Report(new(SharedRadioDiagnosticKind.RecordRejected,
                "Radio write outcome is UNKNOWN_DELIVERY; no retry will be attempted.",
                completed.Client, SharedRadioOutcome.UnknownDelivery, ByteCount: completed.Bytes.Length,
                RecordSequence: completed.RecordSequence, Dispatched: true));
            LatchProfileFault("Radio write outcome is UNKNOWN_DELIVERY.");
        }
        else if (completion == AdapterWriteCompletion.ProvenZeroBytes)
        {
            civReservation = null;
            Report(new(SharedRadioDiagnosticKind.RecordRejected,
                "Radio write is proven to have emitted no bytes; record is NOT_SENT.",
                completed.Client, SharedRadioOutcome.NotSent, ByteCount: completed.Bytes.Length,
                RecordSequence: completed.RecordSequence, Dispatched: true));
        }
        else
        {
            // Only the supported CI-V frequency-read shape is a readiness probe.
            if (completed.Client == SharedRadioClient.LegacyClient && options.CivExchangeGateEnabled &&
                completed.Bytes.Length == 6 && completed.Bytes[4] == 0x03 &&
                completed.Bytes[2] == options.InformationalRadioAddress)
            {
                legacyPollingRead = completed;
                legacyPollingWrittenMs = clock.NowMs;
            }
            if (civReservation is { } reservation)
            {
                reservation.CompletedMs = clock.NowMs;
                if (reservation.ReplyReceived)
                    civReservation = null;
            }
            settledTxWrites++;
            Report(new(SharedRadioDiagnosticKind.RecordWriteCompleted,
                "Complete Radio write succeeded; radio receipt or execution is not asserted.",
                completed.Client, SharedRadioOutcome.WriteSettled, ByteCount: completed.Bytes.Length,
                RecordSequence: completed.RecordSequence, Dispatched: true));
        }
    }

    public bool TryTakeClientDelivery(SharedRadioClient client, out SharedRadioClientDelivery? delivery)
    {
        CheckActor();
        var queue = clients[client];
        if (!queue.Connected || queue.RxInFlight is not null || !queue.Rx.TryDequeue(out delivery))
        {
            delivery = null;
            return false;
        }

        queue.RxInFlight = delivery;
        return true;
    }

    public void CompleteClientDelivery(
        Guid deliveryId,
        bool delivered,
        string? failureReason = null)
    {
        CheckActor();
        var pair = clients.SingleOrDefault(item => item.Value.RxInFlight?.Id == deliveryId);
        if (pair.Value is null)
        {
            if (cancelledClientDeliveries.Remove(deliveryId))
            {
                return;
            }
            throw new InvalidOperationException("Client delivery completion does not match an in-flight record.");
        }

        var queue = pair.Value;
        var pending = queue.RxInFlight!;
        queue.RxInFlight = null;
        queue.RxBytes -= pending.Bytes.Length;
        if (!delivered)
        {
            DisconnectClient(pair.Key, failureReason ?? "Client RX delivery failed.");
            return;
        }

        Report(new(SharedRadioDiagnosticKind.ReceiveDelivered,
            "Complete Radio RX record delivered.", pair.Key, ByteCount: pending.Bytes.Length));
        clientRxDeliveries++;
        if (pending.ConfirmsLegacyPolling)
            Report(new(SharedRadioDiagnosticKind.LegacyPollingConfirmed,
                "A post-admission CI-V frequency read received a matching fresh reply delivered to Legacy.",
                SharedRadioClient.LegacyClient, ByteCount: pending.Bytes.Length));
    }

    public void Stop()
    {
        CheckActor();
        if (State == SharedRadioCoordinatorState.Stopped)
        {
            return;
        }

        lock (ingressPostGate)
        {
            stoppedFlag = 1;
            acceptingClientIngress.Clear();
            foreach (var ingress in clientIngress.Values)
            {
                ingress.Clear();
            }
            radioIngress.Clear();
        }
        RejectQueued(SharedRadioOutcome.NotSent, "Coordinator stopped before dispatch.");
        if (writeInFlight is { } activeWrite)
        {
            unknownTxWrites++;
            Report(new(SharedRadioDiagnosticKind.RecordRejected,
                "Coordinator stopped while a Radio write was in flight; outcome is UNKNOWN_DELIVERY.",
                activeWrite.Client, SharedRadioOutcome.UnknownDelivery, ByteCount: activeWrite.Bytes.Length,
                RecordSequence: activeWrite.RecordSequence, Dispatched: true));
        }
        writeInFlight = null;
        civReservation = null;
        foreach (var client in clients.Keys)
        {
            ClearClientRx(client);
            clients[client].Connected = false;
        }
        State = SharedRadioCoordinatorState.Stopped;
    }

    private void Feed(int sourceIndex, byte[] bytes)
    {
        if (sourceIndex == 2)
        {
            if (State == SharedRadioCoordinatorState.Stopped)
            {
                return;
            }
            radioIngressProcessed?.Invoke(bytes.Length);
            FeedParser(null, radioCiv, radioKenwood, bytes);
            return;
        }

        var client = (SharedRadioClient)sourceIndex;
        if (!clients[client].Connected)
        {
            return;
        }
        if (client == SharedRadioClient.LegacyClient)
        {
            FeedParser(client, legacyCiv, legacyKenwood, bytes);
        }
        else
        {
            FeedParser(client, ipcCiv, ipcKenwood, bytes);
        }
    }

    private void FeedParser(
        SharedRadioClient? client,
        CivFrameParser? civ,
        KenwoodAsciiFrameParser? kenwood,
        byte[] bytes)
    {
        if (civ is not null)
        {
            civ.Feed(bytes);
        }
        else
        {
            if (IsKenwoodDiscarding(client))
            {
                var boundary = Array.IndexOf(bytes, (byte)';');
                if (boundary < 0)
                {
                    return;
                }
                SetKenwoodDiscarding(client, false);
                bytes = bytes[(boundary + 1)..];
                if (bytes.Length == 0)
                {
                    return;
                }
            }
            kenwood!.Feed(bytes);
        }
    }

    private void TickParsers()
    {
        legacyCiv?.Tick();
        ipcCiv?.Tick();
        radioCiv?.Tick();
        legacyKenwood?.Tick();
        ipcKenwood?.Tick();
        radioKenwood?.Tick();
        AdvanceCivReservation();
    }

    private void AdvanceCivReservation()
    {
        if (State != SharedRadioCoordinatorState.Running ||
            civReservation is not { CompletedMs: { } completedMs } reservation)
            return;

        var now = clock.NowMs;
        var elapsed = now - completedMs;
        if (elapsed < CivAnswerWaitMs)
            return;

        if (!reservation.Recovering)
        {
            reservation.Recovering = true;
            Report(new(SharedRadioDiagnosticKind.RadioReplyTimeout,
                "CI-V answer wait expired; holding the bus for bounded recovery. No retry.",
                reservation.Write.Client));
        }

        var silenceStartedMs = Math.Max(completedMs + CivAnswerWaitMs, reservation.LastReceiveMs);
        var recoveryDeadlineMs = completedMs + CivAnswerWaitMs + CivRecoveryWaitMs;
        if (now - silenceStartedMs >= CivRecoverySilenceMs &&
            silenceStartedMs + CivRecoverySilenceMs <= recoveryDeadlineMs &&
            !radioCiv!.HasPartialFrame)
        {
            civReservation = null;
            Report(new(SharedRadioDiagnosticKind.RadioExchangeRecovered,
                "CI-V bus resumed after uninterrupted RX silence; late replies remain a best-effort risk.",
                reservation.Write.Client));
        }
        else if (elapsed >= CivAnswerWaitMs + CivRecoveryWaitMs)
        {
            LatchProfileFault("CI-V recovery exhausted; radio TX paused, endpoints remain open. Stop/start required.");
        }
    }

    private void OnCivEvent(SharedRadioClient? client, bool isRadio, CivParserEvent parserEvent)
    {
        if (isRadio && parserEvent is CivReceiveActivity activity && civReservation is { } reservation)
            reservation.LastReceiveMs = activity.TimestampMs;
        if (parserEvent is CivFrameReceived frame)
        {
            if (isRadio)
            {
                RouteRx(frame.Frame.AsSpan());
            }
            else
            {
                AdmitTx(client!.Value, frame.Frame.AsSpan());
            }
        }
        else if (parserEvent is CivParserAnomaly anomaly)
        {
            Report(new(anomaly.Kind is CivParserAnomalyKind.CandidateTooLong or
                    CivParserAnomalyKind.PreambleTooLong or CivParserAnomalyKind.IngressOverflow
                    ? SharedRadioDiagnosticKind.FramingOverflow : SharedRadioDiagnosticKind.FramingAnomaly,
                $"CI-V framing anomaly: {anomaly.Kind}.", client,
                ByteCount: checked((int)Math.Min(int.MaxValue, anomaly.ObservedCount)),
                MonotonicTimestampMs: anomaly.TimestampMs,
                ParserIdleElapsedMs: anomaly.CandidateIdleElapsedMs,
                ParserTotalElapsedMs: anomaly.CandidateTotalElapsedMs));
        }
    }

    private bool OnKenwoodEvent(SharedRadioClient? client, bool isRadio, KenwoodAsciiParserEvent parserEvent)
    {
        if (parserEvent is KenwoodAsciiRecordReceived record)
        {
            if (isRadio)
            {
                RouteRx(record.Record.AsSpan());
            }
            else
            {
                AdmitTx(client!.Value, record.Record.AsSpan());
            }
        }
        else if (parserEvent is KenwoodAsciiParserAnomaly anomaly)
        {
            Report(new(anomaly.Kind is KenwoodAsciiParserAnomalyKind.RecordTooLong or
                    KenwoodAsciiParserAnomalyKind.IngressOverflow
                    ? SharedRadioDiagnosticKind.FramingOverflow : SharedRadioDiagnosticKind.FramingAnomaly,
                $"Kenwood ASCII framing anomaly: {anomaly.Kind}.", client, ByteCount: checked((int)Math.Min(int.MaxValue, anomaly.ObservedCount))));
            if (anomaly.Kind is KenwoodAsciiParserAnomalyKind.PartialRecordExpiredIdle or
                KenwoodAsciiParserAnomalyKind.PartialRecordExpiredTotal)
            {
                SetKenwoodDiscarding(client, true);
            }
        }

        return true;
    }

    private void AdmitTx(SharedRadioClient client, ReadOnlySpan<byte> bytes)
    {
        long? sequence = null;
        if (client == SharedRadioClient.IpcClient)
        {
            if (ipcRecordSequence == long.MaxValue)
            {
                DisconnectClient(client, "IPC record sequence space exhausted; no wrap or reuse is allowed.");
                return;
            }
            sequence = ++ipcRecordSequence;
        }
        offeredTxRecords++;
        if (State != SharedRadioCoordinatorState.Running || !clients[client].Connected)
        {
            Report(new(SharedRadioDiagnosticKind.RecordRejected,
                "Profile or client is unavailable; TX record is NOT_SENT.", client, SharedRadioOutcome.NotSent,
                ByteCount: bytes.Length, RecordSequence: sequence));
            return;
        }

        if (options.CivExchangeGateEnabled &&
            (bytes[2] != options.InformationalRadioAddress ||
             bytes[3] != (client == SharedRadioClient.LegacyClient
                 ? options.LegacyClientAddress : options.IpcClientAddress)))
        {
            Report(new(SharedRadioDiagnosticKind.RecordRejected,
                "CI-V exchange gate requires the configured radio and source controller; record is NOT_SENT.",
                client, SharedRadioOutcome.NotSent, ByteCount: bytes.Length, RecordSequence: sequence));
            return;
        }

        var clientQueue = GetTxQueue(client);
        var queuedBytes = GetTxBytes(client);
        var outstanding = writeInFlight ?? civReservation?.Write;
        var inFlightForClient = outstanding?.Client == client;
        var totalPending = QueuedTxRecords + (outstanding is null ? 0 : 1);
        if (clientQueue.Count + (inFlightForClient ? 1 : 0) >= options.TxQueueRecordsPerClient ||
            queuedBytes + (inFlightForClient ? outstanding!.Bytes.Length : 0) >
                options.TxQueueBytesPerClient - bytes.Length ||
            totalPending >= options.TxQueueRecordsPerProfile)
        {
            Report(new(SharedRadioDiagnosticKind.RecordRejected,
                "TX queue capacity reached; record is NOT_SENT.", client, SharedRadioOutcome.NotSent,
                ByteCount: bytes.Length, RecordSequence: sequence));
            return;
        }

        var record = new TxRecord(client, bytes.ToArray(), clock.NowMs, sequence);
        clientQueue.Enqueue(record);
        clients[client].TxBytes += record.Bytes.Length;
        acceptedTxRecords++;
        txQueueRecordsHighWater = Math.Max(txQueueRecordsHighWater, QueuedTxRecords);
        txQueueBytesHighWater = Math.Max(txQueueBytesHighWater,
            legacyTx.Sum(item => item.Bytes.Length) + ipcTx.Sum(item => item.Bytes.Length));
        if (client == SharedRadioClient.LegacyClient)
        {
            legacyTxQueueRecordsHighWater = Math.Max(legacyTxQueueRecordsHighWater, legacyTx.Count);
            legacyTxQueueBytesHighWater = Math.Max(legacyTxQueueBytesHighWater, clients[client].TxBytes);
        }
        else
        {
            ipcTxQueueRecordsHighWater = Math.Max(ipcTxQueueRecordsHighWater, ipcTx.Count);
            ipcTxQueueBytesHighWater = Math.Max(ipcTxQueueBytesHighWater, clients[client].TxBytes);
        }
    }

    private void RouteRx(ReadOnlySpan<byte> bytes)
    {
        if (State == SharedRadioCoordinatorState.Stopped)
        {
            return;
        }

        if (options.Framing == FramingMode.KenwoodAscii)
        {
            radioRxRecords++;
            FanOut(bytes);
            return;
        }

        radioRxRecords++;
        if (civReservation?.CompletedMs is { } completedMs &&
            clock.NowMs > completedMs + CivAnswerWaitMs + CivRecoveryWaitMs)
            LatchProfileFault("CI-V reply arrived after the recovery limit; radio TX paused, endpoints remain open.");
        if (State == SharedRadioCoordinatorState.Running && civReservation is { } reservation &&
            bytes[2] == reservation.Write.Bytes[3] && bytes[3] == reservation.Write.Bytes[2])
        {
            reservation.ReplyReceived = true;
            if (reservation.CompletedMs is not null)
            {
                civReservation = null;
                if (reservation.Recovering)
                    Report(new(SharedRadioDiagnosticKind.RadioExchangeRecovered,
                        "Late directed CI-V reply closed the outstanding bus reservation; no retry.",
                        reservation.Write.Client));
            }
        }
        var address = bytes[2];
        if (address == 0)
        {
            FanOut(bytes);
            return;
        }

        var addressedClient = address == options.LegacyClientAddress
            ? SharedRadioClient.LegacyClient
            : address == options.IpcClientAddress ? SharedRadioClient.IpcClient : (SharedRadioClient?)null;
        if (addressedClient is not null)
        {
            var confirmsPolling = addressedClient == SharedRadioClient.LegacyClient &&
                MatchesLegacyPollingReply(bytes);
            if (confirmsPolling) legacyPollingRead = null;
            EnqueueRx(addressedClient.Value, bytes, confirmsPolling);
            return;
        }

        var denied = options.DenyUnmatchedEnabled && options.DeniedUnmatchedAddresses!.Contains(address);
        var allowed = !options.AllowUnmatchedEnabled || options.AllowedUnmatchedAddresses!.Contains(address);
        var fanOut = !denied && allowed;
        Report(new(SharedRadioDiagnosticKind.UnmatchedAddress,
            fanOut ? "Unmatched CI-V destination fanned out by policy." : "Unmatched CI-V destination suppressed by policy.",
            Address: address, ByteCount: bytes.Length));
        if (fanOut)
        {
            FanOut(bytes);
        }
    }

    private void FanOut(ReadOnlySpan<byte> bytes)
    {
        foreach (var client in Enum.GetValues<SharedRadioClient>())
        {
            EnqueueRx(client, bytes);
        }
    }

    private bool MatchesLegacyPollingReply(ReadOnlySpan<byte> bytes)
    {
        if (legacyPollingRead is not { } read || clock.NowMs - legacyPollingWrittenMs >= CivAnswerWaitMs ||
            bytes.Length != 11 || bytes[2] != read.Bytes[3] || bytes[3] != read.Bytes[2] || bytes[4] != 0x03)
            return false;
        for (var i = 5; i < 10; i++)
            if ((bytes[i] & 0x0F) > 9 || (bytes[i] >> 4) > 9) return false;
        return true;
    }

    private void EnqueueRx(SharedRadioClient client, ReadOnlySpan<byte> bytes, bool confirmsLegacyPolling = false)
    {
        var queue = clients[client];
        if (!queue.Connected)
        {
            return;
        }

        var pendingCount = queue.Rx.Count + (queue.RxInFlight is null ? 0 : 1);
        if (pendingCount >= options.ClientRxQueueRecordsPerClient ||
            queue.RxBytes > options.ClientRxQueueBytesPerClient - bytes.Length)
        {
            DisconnectClient(client, "Client RX queue overflow.");
            return;
        }

        queue.Rx.Enqueue(new(Guid.NewGuid(), client, bytes.ToArray(), clock.NowMs, confirmsLegacyPolling));
        queue.RxBytes += bytes.Length;
        if (client == SharedRadioClient.LegacyClient)
        {
            legacyRxQueueRecordsHighWater = Math.Max(legacyRxQueueRecordsHighWater, queue.Rx.Count);
            legacyRxQueueBytesHighWater = Math.Max(legacyRxQueueBytesHighWater, queue.RxBytes);
        }
        else
        {
            ipcRxQueueRecordsHighWater = Math.Max(ipcRxQueueRecordsHighWater, queue.Rx.Count);
            ipcRxQueueBytesHighWater = Math.Max(ipcRxQueueBytesHighWater, queue.RxBytes);
        }
    }

    private void ExpireQueuedRecords()
    {
        var now = clock.NowMs;
        foreach (var client in Enum.GetValues<SharedRadioClient>())
        {
            var queue = GetTxQueue(client);
            while (queue.TryPeek(out var record) &&
                   now >= record.EnqueuedMs &&
                   now - record.EnqueuedMs >= options.QueueWaitLimitMs)
            {
                queue.Dequeue();
                clients[client].TxBytes -= record.Bytes.Length;
                Report(new(SharedRadioDiagnosticKind.RecordExpired,
                    "Queued TX record expired before dispatch; record is NOT_SENT.",
                    client, SharedRadioOutcome.NotSent, ByteCount: record.Bytes.Length,
                    RecordSequence: record.RecordSequence));
            }
        }
    }

    private TxRecord? DequeueNextTx()
    {
        var preferred = dispatchLegacyNext ? SharedRadioClient.LegacyClient : SharedRadioClient.IpcClient;
        var other = preferred == SharedRadioClient.LegacyClient
            ? SharedRadioClient.IpcClient : SharedRadioClient.LegacyClient;
        var chosen = GetTxQueue(preferred).Count > 0 ? preferred :
            GetTxQueue(other).Count > 0 ? other : (SharedRadioClient?)null;
        if (chosen is null)
        {
            return null;
        }

        var selectedQueue = GetTxQueue(chosen.Value);
        var record = selectedQueue.Dequeue();
        clients[chosen.Value].TxBytes -= record.Bytes.Length;
        dispatchLegacyNext = chosen.Value != SharedRadioClient.LegacyClient;
        return record;
    }

    private void DisconnectClient(SharedRadioClient client, string reason)
    {
        var queue = clients[client];
        if (client == SharedRadioClient.LegacyClient) legacyPollingRead = null;
        if (!queue.Connected)
        {
            return;
        }

        lock (ingressPostGate)
        {
            queue.Connected = false;
            acceptingClientIngress.Remove(client);
            clientIngress[client].Clear();
        }
        var discarded = GetTxQueue(client).Count;
        while (GetTxQueue(client).TryDequeue(out var record))
            Report(new(SharedRadioDiagnosticKind.RecordRejected, reason, client,
                SharedRadioOutcome.NotSent, ByteCount: record.Bytes.Length, RecordSequence: record.RecordSequence));
        queue.TxBytes = 0;
        ClearClientRx(client);
        Report(new(SharedRadioDiagnosticKind.ClientDisconnected,
            $"{reason} {discarded} queued TX record(s) discarded as NOT_SENT.",
            client, SharedRadioOutcome.NotSent));
    }

    private void ClearClientRx(SharedRadioClient client)
    {
        var queue = clients[client];
        queue.Rx.Clear();
        if (queue.RxInFlight is { } cancelled)
        {
            cancelledClientDeliveries.Add(cancelled.Id);
        }
        queue.RxInFlight = null;
        queue.RxBytes = 0;
    }

    private void LatchProfileFault(string reason)
    {
        if (State != SharedRadioCoordinatorState.Running)
        {
            return;
        }

        State = SharedRadioCoordinatorState.Faulted;
        Fault = reason;
        RejectQueued(SharedRadioOutcome.NotSent, reason);
        Report(new(SharedRadioDiagnosticKind.ProfileFaulted, reason));
    }

    private void RejectQueued(SharedRadioOutcome outcome, string reason)
    {
        foreach (var client in Enum.GetValues<SharedRadioClient>())
        {
            var queue = GetTxQueue(client);
            while (queue.TryDequeue(out var record))
            {
                Report(new(SharedRadioDiagnosticKind.RecordRejected,
                    reason, client, outcome, ByteCount: record.Bytes.Length, RecordSequence: record.RecordSequence));
            }
            clients[client].TxBytes = 0;
        }
    }

    private Queue<TxRecord> GetTxQueue(SharedRadioClient client) =>
        client == SharedRadioClient.LegacyClient ? legacyTx : ipcTx;

    private int GetTxBytes(SharedRadioClient client) => clients[client].TxBytes;

    private BoundedIngress GetIngress(int index) =>
        index switch
        {
            0 => clientIngress[SharedRadioClient.LegacyClient],
            1 => clientIngress[SharedRadioClient.IpcClient],
            2 => radioIngress,
            _ => throw new ArgumentOutOfRangeException(nameof(index))
        };

    private void Report(SharedRadioDiagnostic diagnostic)
    {
        if (diagnostic.Outcome == SharedRadioOutcome.NotSent &&
            diagnostic.Kind is SharedRadioDiagnosticKind.RecordRejected or SharedRadioDiagnosticKind.RecordExpired)
        {
            notSentTxRecords++;
        }
        diagnosticSink(diagnostic with
        {
            MonotonicTimestampMs = diagnostic.MonotonicTimestampMs ?? clock.NowMs
        });
    }

    private void CheckActor()
    {
        var thread = Environment.CurrentManagedThreadId;
        if (actorThread != thread)
        {
            throw new InvalidOperationException("SharedRadioCoordinator must be processed on one serialized thread.");
        }
    }

    private static void ValidateOptions(SharedRadioCoordinatorOptions value)
    {
        if (value.CivExchangeGateEnabled &&
            (value.Framing != FramingMode.Civ ||
             value.InformationalRadioAddress is null or 0 or >= 0xF0 ||
             value.LegacyClientAddress is 0 or >= 0xF0 ||
             value.IpcClientAddress is 0 or >= 0xF0 ||
             value.InformationalRadioAddress == value.LegacyClientAddress ||
             value.InformationalRadioAddress == value.IpcClientAddress))
            throw new ArgumentException("CI-V exchange gating requires distinct nonbroadcast radio/controller addresses.", nameof(value));
        if (value.Framing is not (FramingMode.Civ or FramingMode.KenwoodAscii))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Shared mode requires CI-V or Kenwood ASCII framing.");
        }
        if (value.LegacyClientAddress == value.IpcClientAddress)
        {
            throw new ArgumentException("Client controller addresses must be distinct.", nameof(value));
        }
        if (value.IngressChunksPerSource < 1 || value.IngressBytesPerSource < 1 ||
            value.TxQueueRecordsPerClient < 1 || value.TxQueueBytesPerClient < 1 ||
            value.TxQueueRecordsPerProfile < 1 ||
            value.ClientRxQueueRecordsPerClient < 1 || value.ClientRxQueueBytesPerClient < 1 ||
            value.QueueWaitLimitMs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "All queue capacities and the queue-wait limit must be positive.");
        }
        if (value.AllowUnmatchedEnabled && value.AllowedUnmatchedAddresses is null)
        {
            throw new ArgumentException("Enabled unmatched allowlist requires an address set.", nameof(value));
        }
        if (value.DenyUnmatchedEnabled && value.DeniedUnmatchedAddresses is null)
        {
            throw new ArgumentException("Enabled unmatched denylist requires an address set.", nameof(value));
        }
        if (value.AllowedUnmatchedAddresses is { } allowed)
        {
            foreach (var address in allowed)
            {
                ValidateUnmatchedAddress(address, value);
            }
        }
        if (value.DeniedUnmatchedAddresses is { } denied)
        {
            foreach (var address in denied)
            {
                ValidateUnmatchedAddress(address, value);
            }
        }
    }

    private static void ValidateUnmatchedAddress(byte address, SharedRadioCoordinatorOptions value)
    {
        if (address == 0)
        {
            throw new ArgumentException("Broadcast address 00 cannot be an unmatched-address rule.", nameof(value));
        }
        if (address == value.LegacyClientAddress || address == value.IpcClientAddress ||
            address == value.InformationalRadioAddress)
        {
            throw new ArgumentException("Unmatched-address rules cannot contain a configured client or radio address.", nameof(value));
        }
    }

    private bool IsKenwoodDiscarding(SharedRadioClient? client) =>
        client switch
        {
            SharedRadioClient.LegacyClient => legacyKenwoodDiscardUntilBoundary,
            SharedRadioClient.IpcClient => ipcKenwoodDiscardUntilBoundary,
            null => radioKenwoodDiscardUntilBoundary,
            _ => throw new ArgumentOutOfRangeException(nameof(client))
        };

    private void SetKenwoodDiscarding(SharedRadioClient? client, bool value)
    {
        switch (client)
        {
            case SharedRadioClient.LegacyClient:
                legacyKenwoodDiscardUntilBoundary = value;
                break;
            case SharedRadioClient.IpcClient:
                ipcKenwoodDiscardUntilBoundary = value;
                break;
            case null:
                radioKenwoodDiscardUntilBoundary = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(client));
        }
    }
}
