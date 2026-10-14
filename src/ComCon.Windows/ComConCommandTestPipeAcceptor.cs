using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text;
using ComCon.Core.Ipc;
using ComCon.Core.Transport;

namespace ComCon.Windows;

[SupportedOSPlatform("windows")]
public sealed class ComConAcceptedTestPipes : IAsyncDisposable
{
    private readonly ComConCommandSessions sessions;
    private readonly bool leaveOpen;
    private int endpointOwnership;
    public ComConCommandSession Session { get; }
    public Stream Cat { get; }
    public Stream Control { get; }

    internal ComConAcceptedTestPipes(
        ComConCommandSessions sessions, ComConCommandSession session,
        Stream cat, Stream control, bool leaveOpen = false)
    {
        this.sessions = sessions;
        this.leaveOpen = leaveOpen;
        Session = session;
        Cat = leaveOpen ? new BorrowedStream(cat) : cat;
        Control = leaveOpen ? new BorrowedStream(control) : control;
    }

    public IByteBridgeEndpoint CreateCatEndpoint()
    {
        var previous = Interlocked.CompareExchange(ref endpointOwnership, 1, 0);
        ObjectDisposedException.ThrowIf(previous == 2, this);
        if (previous != 0)
            throw new InvalidOperationException("The CAT endpoint has already been handed off.");
        return new CatPipeEndpoint(Cat);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref endpointOwnership, 2) == 2) return;
        sessions.EndSession(Session, "Accepted command pipes disposed.");
        try { await Cat.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            try { await Control.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (!leaveOpen) sessions.ReleaseTransportOwnership(Session);
            }
        }
    }

    private sealed class CatPipeEndpoint(Stream pipe) : IByteBridgeEndpoint
    {
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            pipe.ReadAsync(buffer, cancellationToken);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) =>
            pipe.WriteAsync(buffer, cancellationToken);
        public ValueTask DisposeAsync() => pipe.DisposeAsync();
    }

    private sealed class BorrowedStream(Stream inner) : Stream
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource closed = new();
        private int disposed;
        public override bool CanRead => Volatile.Read(ref disposed) == 0 && inner.CanRead;
        public override bool CanWrite => Volatile.Read(ref disposed) == 0 && inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        private void RequireOpen() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        private CancellationTokenSource LinkOperation(CancellationToken cancellationToken)
        {
            lock (gate)
            {
                RequireOpen();
                return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
            }
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var operation = LinkOperation(cancellationToken);
            return await inner.ReadAsync(buffer, operation.Token).ConfigureAwait(false);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var operation = LinkOperation(cancellationToken);
            await inner.WriteAsync(buffer, operation.Token).ConfigureAwait(false);
        }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            using var operation = LinkOperation(cancellationToken);
            await inner.FlushAsync(operation.Token).ConfigureAwait(false);
        }
        protected override void Dispose(bool disposing)
        {
            lock (gate)
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0)
                {
                    closed.Cancel();
                    closed.Dispose();
                }
            }
            base.Dispose(disposing);
        }
        public override void Flush() => throw new NotSupportedException("Borrowed test pipes require asynchronous I/O.");
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Borrowed test pipes require asynchronous I/O.");
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("Borrowed test pipes require asynchronous I/O.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>One setup attempt using names validated by ComConCommandPipeNames.</summary>
[SupportedOSPlatform("windows")]
public sealed class ComConCommandTestPipeAcceptor
{
    private readonly ComConCommandSessions sessions;
    private readonly ComConCommandPipeNames names;
    private readonly TimeSpan setupTimeout;
    private readonly int? endpointProfileId;
    private int started;

    public ComConCommandTestPipeAcceptor(
        string testPipeName, ComConCommandSessions sessions, TimeSpan setupTimeout, int? endpointProfileId = null)
        : this(ComConCommandPipeNames.IsolatedTest(testPipeName), sessions, setupTimeout, endpointProfileId)
    {
    }

    public ComConCommandTestPipeAcceptor(
        ComConCommandPipeNames names, ComConCommandSessions sessions,
        TimeSpan setupTimeout, int? endpointProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(sessions);
        if (endpointProfileId is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(endpointProfileId));
        if (setupTimeout <= TimeSpan.Zero || setupTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(setupTimeout));
        this.names = names;
        CatPipeName = names.CatPipeName;
        this.sessions = sessions;
        this.setupTimeout = setupTimeout;
        this.endpointProfileId = endpointProfileId;
    }

    public string CatPipeName { get; }
    public string ControlPipeName(int profileId) => names.ControlPipeName(profileId);

    public async Task<ComConAcceptedTestPipes> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
            throw new InvalidOperationException("A test pipe acceptor may run only once.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(setupTimeout);
        NamedPipeServerStream? cat = null;
        var controls = new List<NamedPipeServerStream>();
        ComConCommandSession? session = null;
        var transferred = false;
        try
        {
            cat = ComConReadOnlyPipeHost.CreateProtectedPipe(CatPipeName);
            for (var profile = 1; profile <= 4; profile++)
                controls.Add(ComConReadOnlyPipeHost.CreateProtectedPipe(ControlPipeName(profile)));
            await cat.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
            var accepted = await AcceptConnectedAsync(cat, controls, deadline.Token).ConfigureAwait(false);
            session = accepted.Session;
            foreach (var unused in controls.Where(pipe => !ReferenceEquals(pipe, accepted.Control)))
                unused.Dispose();
            transferred = true;
            return accepted;
        }
        finally
        {
            if (!transferred)
            {
                if (session is not null)
                    sessions.EndSession(session, "Test pipe setup failed or exceeded its deadline.");
                cat?.Dispose();
                foreach (var pipe in controls) pipe.Dispose();
            }
        }
    }

    internal async Task<ComConAcceptedTestPipes> AcceptConnectedAsync(
        NamedPipeServerStream cat, IReadOnlyList<NamedPipeServerStream> controls,
        CancellationToken cancellationToken, bool leaveOpen = false,
        Action<ComConCommandSession>? reserveTransport = null)
    {
        using var writer = new SemaphoreSlim(1, 1);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(setupTimeout);
        ComConCommandSession? session = null;
        var transferred = false;
        try
        {
            var handshake = await ReadSetupAsync(cat, "cat.rejected", writer, deadline.Token).ConfigureAwait(false);
            var result = sessions.AcceptCatHandshake(handshake, endpointProfileId, retainTransportOwnership: true);
            session = result.Session;
            if (session is not null) reserveTransport?.Invoke(session);
            await WriteResponseAsync(cat, result, writer, deadline.Token).ConfigureAwait(false);
            if (!result.Accepted)
                throw new ComConCommandProtocolException(
                    result.Response.GetProperty("errorCode").GetString()!, result.Response.GetProperty("reason").GetString()!);
            sessions.ConfirmCatAcceptanceSent(session!);
            var control = controls[session!.ProfileId - 1];
            while (true)
            {
                await control.WaitForConnectionAsync(deadline.Token).ConfigureAwait(false);
                ComConCommandSetupResult binding;
                try
                {
                    var payload = await ReadSetupAsync(control, "control.rejected", writer, deadline.Token)
                        .ConfigureAwait(false);
                    var envelope = ComConCommandProtocol.ParseEnvelope(payload);
                    if (envelope.GetProperty("messageType").GetString() == "control.bind" &&
                        envelope.GetProperty("sessionId").GetString() != session.SessionId)
                    {
                        var rejection = System.Text.Json.JsonSerializer.SerializeToElement(new
                        {
                            messageType = "control.rejected", contractVersion = ComConCommandProtocol.ContractVersion,
                            errorCode = "UNKNOWN_SESSION", reason = "This setup attempt does not own the supplied session."
                        });
                        binding = new(false, rejection, null);
                    }
                    else
                        binding = sessions.BindControl(payload, session.ProfileId);
                }
                catch (ComConCommandProtocolException)
                {
                    control.Disconnect();
                    continue;
                }
                if (!binding.Accepted)
                {
                    await WriteResponseAsync(control, binding, writer, deadline.Token).ConfigureAwait(false);
                    control.Disconnect();
                    if (session.State == ComConCommandSessionState.Ended)
                        throw new ComConCommandProtocolException("CONTROL_BIND_TIMEOUT", session.EndReason!);
                    continue;
                }
                await WriteResponseAsync(control, binding, writer, deadline.Token).ConfigureAwait(false);
                sessions.ConfirmControlAcceptanceSent(session);
                var ready = await ReadSetupAsync(cat, "cat.rejected", writer, deadline.Token).ConfigureAwait(false);
                try { sessions.AcceptCatReady(session, ready); }
                catch (ComConCommandProtocolException exception)
                {
                    await WriteRejectionAsync(cat, "cat.rejected", exception, writer, deadline.Token).ConfigureAwait(false);
                    throw;
                }
                transferred = true;
                return new(sessions, session, cat, control, leaveOpen);
            }
        }
        finally
        {
            if (!transferred)
            {
                if (session is not null)
                {
                    sessions.EndSession(session, "Test pipe setup failed or exceeded its deadline.");
                    if (reserveTransport is null) sessions.ReleaseTransportOwnership(session);
                }
            }
        }
    }

    private static async Task<byte[]> ReadSetupAsync(
        Stream stream, string rejectionType, SemaphoreSlim writer, CancellationToken cancellationToken)
    {
        try
        {
            var envelope = await ComConCommandProtocol.ReadEnvelopeAsync(stream, cancellationToken).ConfigureAwait(false);
            return Encoding.UTF8.GetBytes(envelope.GetRawText());
        }
        catch (ComConCommandProtocolException exception)
        {
            await WriteRejectionAsync(stream, rejectionType, exception, writer, cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static ValueTask WriteRejectionAsync(
        Stream stream, string rejectionType, ComConCommandProtocolException exception,
        SemaphoreSlim writer, CancellationToken cancellationToken) =>
        ComConCommandProtocol.WriteEnvelopeAsync(stream, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = rejectionType, contractVersion = ComConCommandProtocol.ContractVersion,
            errorCode = exception.ErrorCode, reason = exception.Message
        }), writer, cancellationToken);

    private static ValueTask WriteResponseAsync(
        Stream stream, ComConCommandSetupResult result, SemaphoreSlim writer, CancellationToken cancellationToken) =>
        ComConCommandProtocol.WriteEnvelopeAsync(
            stream, Encoding.UTF8.GetBytes(result.Response.GetRawText()), writer, cancellationToken);

}
