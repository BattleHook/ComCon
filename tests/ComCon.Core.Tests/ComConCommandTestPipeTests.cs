using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Windows;

namespace ComCon.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ComConCommandTestPipeTests
{
    [TestMethod]
    public async Task ProtectedTestPipesCompleteSetupAndPreserveOpaqueBytesBothWays()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using var control = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        Assert.AreEqual("control.bound",
            (await ComConCommandProtocol.ReadEnvelopeAsync(control)).GetProperty("messageType").GetString());
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, Ready(accepted), writer);
        await using var pipes = await accept.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(context.Sessions.CanRelayCat(pipes.Session));
        byte[] bytes = [0xFE, 0xFE, 0x94, 0xE1, 0, 0xFD];
        await cat.WriteAsync(bytes);
        var read = new byte[bytes.Length];
        await pipes.Cat.ReadExactlyAsync(read);
        CollectionAssert.AreEqual(bytes, read);
        await pipes.CreateCatEndpoint().WriteAsync(bytes, CancellationToken.None);
        await cat.ReadExactlyAsync(read);
        CollectionAssert.AreEqual(bytes, read);
        var tracker = new ComConCommandNotifications(
            context.Sessions, pipes.Session, context.Clock, 8, 16384, 2000, 500);
        var loop = new ComConCommandControlLoop(pipes.Control, tracker, 8, TimeSpan.FromMilliseconds(2));
        loop.RequestOrderlyClose("Test finished.");
        var run = loop.RunAsync();
        var notification = await ComConCommandProtocol.ReadEnvelopeAsync(control);
        await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = pipes.Session.SessionId,
            notificationSequence = notification.GetProperty("notificationSequence").GetString()
        }), writer);
        await run.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(ComConCommandSessionState.Ended, pipes.Session.State);
    }

    [TestMethod]
    public async Task InvalidCompetingBindIsRejectedWithoutReleasingProvisionalOwner()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using (var bad = await Connect(context.Acceptor.ControlPipeName(1)))
        {
            await ComConCommandProtocol.WriteEnvelopeAsync(bad, Bind(accepted, Guid.NewGuid().ToString("D")), writer);
            var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(bad);
            Assert.AreEqual("UNKNOWN_SESSION", rejection.GetProperty("errorCode").GetString());
            Assert.IsFalse(rejection.TryGetProperty("sessionId", out _));
        }
        Assert.AreEqual("PROFILE_BUSY",
            context.Sessions.AcceptCatHandshake(context.Handshake()).Response.GetProperty("errorCode").GetString());
        using var good = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(good, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(good);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, Ready(accepted), writer);
        await using var pipes = await accept.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(context.Sessions.CanRelayCat(pipes.Session));
    }

    [TestMethod]
    public async Task UnsupportedHandshakeGetsStructuredRejectionAndNoSession()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.handshake", contractVersion = "comcon-ipc-v99", rigProfileId = context.Rig
        });
        await WriteRaw(cat, payload);
        var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        Assert.AreEqual("UNSUPPORTED_VERSION", rejection.GetProperty("errorCode").GetString());
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() => accept);
        Assert.AreEqual("UNSUPPORTED_VERSION", exception.ErrorCode);
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    public async Task PartialHandshakeDeadlineClosesWithoutProfileReservation()
    {
        var context = new Context(TimeSpan.FromMilliseconds(300));
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        await cat.WriteAsync(new byte[] { 0, 0 });
        await Assert.ThrowsAsync<OperationCanceledException>(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    public async Task MissingControlBindDeadlineReleasesAllocatedReservation()
    {
        var context = new Context(TimeSpan.FromMilliseconds(400));
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        await Assert.ThrowsAsync<OperationCanceledException>(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RawCatWithoutReadyFailsClosedWhetherBeforeOrAfterControlBound(bool afterBound)
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        byte[] raw = [0xFE, 0xFE, 0x94, 0xE1, 1, 0xFD];
        if (!afterBound) await cat.WriteAsync(raw);
        using var control = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(control);
        if (afterBound) await cat.WriteAsync(raw);
        var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() => accept);
        Assert.AreEqual("FRAME_TOO_LARGE", exception.ErrorCode);
        Assert.AreEqual(exception.ErrorCode, rejection.GetProperty("errorCode").GetString());
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    public async Task FirstInstanceProtectionRejectsAnotherServerWithSameTestName()
    {
        var context = new Context();
        using var stop = new CancellationTokenSource();
        var first = context.Acceptor.AcceptAsync(stop.Token);
        var second = new ComConCommandTestPipeAcceptor(context.Acceptor.CatPipeName,
            context.Sessions, TimeSpan.FromSeconds(3));
        await Assert.ThrowsExactlyAsync<System.ComponentModel.Win32Exception>(() => second.AcceptAsync());
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
    }

    [TestMethod]
    public void ProductionNamesAreNotAllowed()
    {
        var context = new Context();
        Assert.ThrowsExactly<ArgumentException>(() => new ComConCommandTestPipeAcceptor(
            "ComConPipe_Profile1", context.Sessions, TimeSpan.FromSeconds(1)));
    }

    [TestMethod]
    public async Task MalformedControlCanReconnectWithoutReleasingReservation()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using (var bad = await Connect(context.Acceptor.ControlPipeName(1)))
        {
            await WriteRaw(bad, "{"u8.ToArray());
            var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(bad);
            Assert.AreEqual("control.rejected", rejection.GetProperty("messageType").GetString());
            Assert.IsFalse(rejection.TryGetProperty("sessionId", out _));
        }
        Assert.AreEqual("PROFILE_BUSY",
            context.Sessions.AcceptCatHandshake(context.Handshake()).Response.GetProperty("errorCode").GetString());
        using var good = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(good, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(good);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, Ready(accepted), writer);
        await using var pipes = await accept.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(context.Sessions.CanRelayCat(pipes.Session));
    }

    [TestMethod]
    [DataRow(0u)]
    [DataRow(16385u)]
    public async Task InvalidDeclaredLengthIsRejectedWithoutWaitingForPayload(uint length)
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, length);
        await cat.WriteAsync(prefix);
        var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        Assert.AreEqual("cat.rejected", rejection.GetProperty("messageType").GetString());
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() => accept);
        Assert.AreEqual(exception.ErrorCode, rejection.GetProperty("errorCode").GetString());
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    public async Task HandshakeEofClosesAttemptAndAllowsNameReuse()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using (var cat = await Connect(context.Acceptor.CatPipeName))
            await cat.WriteAsync(new byte[] { 0, 0 });
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        using var stop = new CancellationTokenSource();
        var replacement = new ComConCommandTestPipeAcceptor(
            context.Acceptor.CatPipeName, context.Sessions, TimeSpan.FromSeconds(3));
        var next = replacement.AcceptAsync(stop.Token);
        using var peer = await Connect(replacement.CatPipeName);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => next);
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    public async Task ControlEofReleasesOnlyItsAllocatedReservation()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using (var control = await Connect(context.Acceptor.ControlPipeName(1)))
            await control.WriteAsync(new byte[] { 0, 0 });
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4)]
    public async Task AllocatedProfileAndSingleEndpointOwnershipRemainPinned(int profileId)
    {
        var context = new Context(profileId: profileId);
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        Assert.AreEqual(profileId, accepted.GetProperty("profileId").GetInt32());
        using var control = await Connect(context.Acceptor.ControlPipeName(profileId));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(control);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, Ready(accepted), writer);
        await using var pipes = await accept.WaitAsync(TimeSpan.FromSeconds(3));
        await using var endpoint = pipes.CreateCatEndpoint();
        Assert.ThrowsExactly<InvalidOperationException>(() => pipes.CreateCatEndpoint());
        context.Sessions.ReplaceMappings([new(context.Rig, profileId == 1 ? 4 : 1)]);
        Assert.AreEqual(profileId, pipes.Session.ProfileId);
        Assert.IsTrue(context.Sessions.CanRelayCat(pipes.Session));
        await pipes.DisposeAsync();
        Assert.AreEqual(ComConCommandSessionState.Ended, pipes.Session.State);
        Assert.ThrowsExactly<ObjectDisposedException>(() => pipes.CreateCatEndpoint());
        await pipes.DisposeAsync();
    }

    [TestMethod]
    public async Task ActiveOwnerRejectionDoesNotEndOriginalSession()
    {
        var context = new Context();
        var owner = context.Sessions.AcceptCatHandshake(context.Handshake()).Session!;
        context.Sessions.ConfirmCatAcceptanceSent(owner);
        context.Sessions.BindControl(JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId = owner.SessionId
        }), 1);
        context.Sessions.ConfirmControlAcceptanceSent(owner);
        context.Sessions.AcceptCatReady(owner, Ready(JsonSerializer.SerializeToElement(new
        {
            profileId = 1, sessionId = owner.SessionId
        })));
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        Assert.AreEqual("PROFILE_BUSY", rejection.GetProperty("errorCode").GetString());
        await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() => accept);
        Assert.IsTrue(context.Sessions.CanRelayCat(owner));
        context.Sessions.EndSession(owner, "Test finished.");
    }

    [TestMethod]
    public async Task AcceptorCannotStartTwiceAndCancellationClosesAllPipeNames()
    {
        var context = new Context();
        using var stop = new CancellationTokenSource();
        var first = context.Acceptor.AcceptAsync(stop.Token);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => context.Acceptor.AcceptAsync());
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        using var cat = ComConReadOnlyPipeHost.CreateProtectedPipe(context.Acceptor.CatPipeName);
        for (var profile = 1; profile <= 4; profile++)
        {
            using var control = ComConReadOnlyPipeHost.CreateProtectedPipe(context.Acceptor.ControlPipeName(profile));
        }
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(4096)]
    public async Task FragmentedOrCoalescedReadyConsumesOnlyEnvelopeAndPreservesFollowingRawCat(int chunkSize)
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using var control = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(control);
        byte[] raw = [0xFE, 0xFE, 0x94, 0xE1, 0, 0xFD, 0xFE, 0xFE, 0x94, 0xE1, 2, 0xFD];
        using var combined = new MemoryStream();
        await ComConCommandProtocol.WriteEnvelopeAsync(combined, Ready(accepted), writer);
        await combined.WriteAsync(raw);
        var bytes = combined.ToArray();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            await cat.WriteAsync(bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset)));
        await using var pipes = await accept.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(context.Sessions.CanRelayCat(pipes.Session));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var received = new byte[raw.Length];
        await pipes.Cat.ReadExactlyAsync(received, timeout.Token);
        CollectionAssert.AreEqual(raw, received);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MissingOrPartialReadyHasBoundedDeadlineAndReleasesOwner(bool partial)
    {
        var context = new Context(TimeSpan.FromMilliseconds(400));
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using var control = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(control);
        Assert.IsFalse(accept.IsCompleted);
        if (partial) await cat.WriteAsync(new byte[] { 0, 0 });
        await Assert.ThrowsAsync<OperationCanceledException>(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    [DataRow("session", "UNKNOWN_SESSION")]
    [DataRow("profile", "PROFILE_SESSION_MISMATCH")]
    [DataRow("type", "INVALID_MESSAGE_TYPE")]
    [DataRow("malformed", "INVALID_JSON")]
    public async Task InvalidReadyReturnsCatRejectionAndNeverActivates(string mutation, string code)
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using var control = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(control);
        var payload = mutation switch
        {
            "type" => Bind(accepted),
            "malformed" => "{"u8.ToArray(),
            _ => JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
                profileId = mutation == "profile" ? 4 : 1,
                sessionId = mutation == "session" ? Guid.NewGuid().ToString("D") : accepted.GetProperty("sessionId").GetString()
            })
        };
        await WriteRaw(cat, payload);
        var rejection = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        Assert.AreEqual("cat.rejected", rejection.GetProperty("messageType").GetString());
        Assert.AreEqual(code, rejection.GetProperty("errorCode").GetString());
        Assert.IsFalse(rejection.TryGetProperty("sessionId", out _));
        var exception = await Assert.ThrowsExactlyAsync<ComConCommandProtocolException>(() => accept);
        Assert.AreEqual(code, exception.ErrorCode);
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    [TestMethod]
    public async Task CatEofWhileAwaitingReadyReleasesProvisionalOwner()
    {
        var context = new Context();
        var accept = context.Acceptor.AcceptAsync();
        using var cat = await Connect(context.Acceptor.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, context.Handshake(), writer);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat);
        using var control = await Connect(context.Acceptor.ControlPipeName(1));
        await ComConCommandProtocol.WriteEnvelopeAsync(control, Bind(accepted), writer);
        await ComConCommandProtocol.ReadEnvelopeAsync(control);
        cat.Dispose();
        await Assert.ThrowsExactlyAsync<EndOfStreamException>(() => accept.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.IsTrue(context.Sessions.AcceptCatHandshake(context.Handshake()).Accepted);
    }

    private static byte[] Bind(JsonElement accepted, string? sessionId = null) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
        profileId = accepted.GetProperty("profileId").GetInt32(),
        sessionId = sessionId ?? accepted.GetProperty("sessionId").GetString()
    });

    private static byte[] Ready(JsonElement accepted) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
        profileId = accepted.GetProperty("profileId").GetInt32(), sessionId = accepted.GetProperty("sessionId").GetString()
    });

    private static async Task<NamedPipeClientStream> Connect(string name)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(2000); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    private static async Task WriteRaw(Stream stream, byte[] payload)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(payload);
    }

    private sealed class Context
    {
        public string Rig { get; } = Guid.NewGuid().ToString("D");
        public StopwatchClock Clock { get; } = new();
        public ComConCommandSessions Sessions { get; }
        public ComConCommandTestPipeAcceptor Acceptor { get; }

        public Context(TimeSpan? timeout = null, int profileId = 1)
        {
            Sessions = new(Clock, 5000, [new(Rig, profileId)]);
            Acceptor = new("ComConTest_Command_" + Guid.NewGuid().ToString("N"), Sessions,
                timeout ?? TimeSpan.FromSeconds(5));
        }

        public byte[] Handshake() => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.handshake", contractVersion = ComConCommandProtocol.ContractVersion, rigProfileId = Rig
        });
    }
}
