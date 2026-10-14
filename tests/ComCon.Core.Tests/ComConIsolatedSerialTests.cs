using System.Collections.Concurrent;
using System.IO.Ports;
using System.Runtime.Versioning;
using System.Text.Json;
using ComCon.Core.Configuration;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    private const string AllowedSerialPortNames = "COM23,COM24,COM33,COM34";

    [TestMethod]
    [TestCategory("IsolatedCom")]
    [DoNotParallelize]
    [SupportedOSPlatform("windows")]
    public async Task ComConIsolatedSerialCommanderAndIpcShareRadioWithoutCrossDelivery()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var serial = new IsolatedSerialFixture();
        await using var supervisor = serial.NewSupervisor();
        await supervisor.StartAsync(timeout.Token);
        using var peer = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            serial.Rig, 1, timeout.Token, null);
        for (byte sequence = 1; sequence <= 8; sequence++)
        {
            serial.Commander.Write(IsolatedRead(0xE0), 0, 6);
            await peer.Cat.WriteAsync(IsolatedRead(0xE1), timeout.Token);
            CollectionAssert.AreEqual(IsolatedReply(0xE0, sequence),
                await ReadSerialResponse(serial.Commander, serial.CommanderEchoes, timeout.Token));
            CollectionAssert.AreEqual(IsolatedReply(0xE1, sequence),
                await ReadPipeResponse(peer.Cat, serial.IpcEchoes, timeout.Token));
            var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            AssertOutcome(outcome, "record.outcome", sequence.ToString(), "WRITE_SUCCEEDED");
            await peer.Ack(outcome.GetProperty("notificationSequence").GetString()!, timeout.Token);
        }
        await serial.WaitForAcknowledgements(timeout.Token);
        await serial.AssertEchoes(peer.Cat, 16, timeout.Token);
        await AssertPipeQuiet(peer.Cat, timeout.Token);
        await serial.AssertQuiet(16, timeout.Token);
        Assert.HasCount(8, serial.Requests.Where(frame => frame[3] == 0xE0).ToArray());
        Assert.HasCount(8, serial.Requests.Where(frame => frame[3] == 0xE1).ToArray());
        Assert.IsEmpty(serial.Failures);
        await supervisor.StopAsync();
        serial.AssertFactoryClosed();
    }

    [TestMethod]
    [TestCategory("IsolatedCom")]
    [DoNotParallelize]
    [SupportedOSPlatform("windows")]
    public async Task ComConIsolatedSerialPortOpenFailureRollsBackPortsAndPipeNames()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var serial = new IsolatedSerialFixture();
        await using var supervisor = serial.NewSupervisor();
        using (var busy = OpenAllowedSerial("COM34"))
        {
            try
            {
                await supervisor.StartAsync(timeout.Token);
                Assert.Fail("Startup must fail while the radio-side port is exclusively owned.");
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            serial.AssertFactoryClosed();
            CollectionAssert.AreEqual(new[] { "COM33" }, serial.Factory.Opened.ToArray());
            using var reopened = OpenAllowedSerial("COM33");
            using var cat = ComConReadOnlyPipeHost.CreateProtectedPipe(serial.PipeName);
            for (var id = 1; id <= 4; id++)
            {
                using var control = ComConReadOnlyPipeHost.CreateProtectedPipe(serial.PipeName + "_Control_Profile" + id);
            }
        }
        using var radioReopened = OpenAllowedSerial("COM34");
        Assert.IsEmpty(supervisor.Profiles);
    }

    [TestMethod]
    [TestCategory("IsolatedCom")]
    [DoNotParallelize]
    [SupportedOSPlatform("windows")]
    public void ComConIsolatedSerialWriteStallRequiresDeterministicDriverSupport()
    {
        using var serial = new IsolatedSerialFixture();
        Assert.Inconclusive("com0com's presence and an idle peer do not establish deterministic RTS/CTS " +
            "flow-control emulation or a >500 ms write stall. With 19200 8N1/no handshake, " +
            "not reading is not proof of a blocked kernel write. No synthetic endpoint failure, " +
            "driver setting change, or flaky timeout assertion substitutes for this serial-boundary gate; " +
            "uncertain-write latch/no-retry remains unverified on these ports.");
    }

    [TestMethod]
    [TestCategory("IsolatedCom")]
    [DoNotParallelize]
    [SupportedOSPlatform("windows")]
    public async Task ComConIsolatedSerialRestartHasNoReplayAndRequiresFreshCatReady()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var serial = new IsolatedSerialFixture();
        string oldSession;
        await using (var old = serial.NewSupervisor())
        {
            await old.StartAsync(timeout.Token);
            using var peer = await MultiProfilePeer.Open(old.Profiles[0].Listener, serial.Rig, 1, timeout.Token, null);
            serial.Commander.Write(IsolatedRead(0xE0), 0, 6);
            CollectionAssert.AreEqual(IsolatedReply(0xE0, 1), await ReadSerialResponse(serial.Commander, serial.CommanderEchoes, timeout.Token));
            await peer.Cat.WriteAsync(IsolatedRead(0xE1), timeout.Token);
            CollectionAssert.AreEqual(IsolatedReply(0xE1, 1), await ReadPipeResponse(peer.Cat, serial.IpcEchoes, timeout.Token));
            var unacknowledged = await ComConCommandProtocol.ReadEnvelopeAsync(peer.Control, timeout.Token);
            AssertOutcome(unacknowledged, "record.outcome", "1", "WRITE_SUCCEEDED");
            oldSession = unacknowledged.GetProperty("sessionId").GetString()!;
            Assert.AreEqual(1, serial.Trackers.Single().PendingCount);
            await serial.AssertEchoes(peer.Cat, 2, timeout.Token);
            await AssertPipeQuiet(peer.Cat, timeout.Token);
            await old.StopAsync();
        }
        serial.AssertFactoryClosed();
        Assert.AreEqual(0, serial.Trackers.Single().PendingCount);
        serial.Trackers.Clear();
        await using var fresh = serial.NewSupervisor();
        await fresh.StartAsync(timeout.Token);
        await serial.AssertQuiet(2, timeout.Token);
        using var cat = await MultiProfilePeer.Connect(fresh.Profiles[0].Listener.CatPipeName, timeout.Token);
        using var writer = new SemaphoreSlim(1, 1);
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, MultiProfilePeer.Handshake(serial.Rig), writer, timeout.Token);
        var accepted = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
        var sessionId = accepted.GetProperty("sessionId").GetString()!;
        Assert.AreNotEqual(oldSession, sessionId);
        using var control = await MultiProfilePeer.Connect(fresh.Profiles[0].Listener.ControlPipeName(1), timeout.Token);
        await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "control.bind", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId
        }), writer, timeout.Token);
        Assert.AreEqual("control.bound",
            (await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token)).GetProperty("messageType").GetString());
        await serial.AssertQuiet(2, timeout.Token);
        Assert.IsEmpty(serial.Trackers, "A new handshake/control.bind cannot attach IPC without cat.ready.");
        await ComConCommandProtocol.WriteEnvelopeAsync(cat, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "cat.ready", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId
        }), writer, timeout.Token);
        await cat.WriteAsync(IsolatedRead(0xE1), timeout.Token);
        CollectionAssert.AreEqual(IsolatedReply(0xE1, 2), await ReadPipeResponse(cat, serial.IpcEchoes, timeout.Token));
        var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(control, timeout.Token);
        AssertOutcome(outcome, "record.outcome", "1", "WRITE_SUCCEEDED");
        Assert.AreEqual("1", outcome.GetProperty("notificationSequence").GetString());
        Assert.AreEqual(sessionId, outcome.GetProperty("sessionId").GetString());
        await ComConCommandProtocol.WriteEnvelopeAsync(control, JsonSerializer.SerializeToUtf8Bytes(new
        {
            messageType = "notification.ack", contractVersion = ComConCommandProtocol.ContractVersion,
            profileId = 1, sessionId, notificationSequence = "1"
        }), writer, timeout.Token);
        await serial.WaitForAcknowledgements(timeout.Token);
        await serial.AssertEchoes(cat, 3, timeout.Token);
        await AssertPipeQuiet(cat, timeout.Token);
        await serial.AssertQuiet(3, timeout.Token);
        Assert.IsEmpty(serial.Failures);
        await fresh.StopAsync();
        serial.AssertFactoryClosed();
    }

    private static byte[] IsolatedRead(byte source) => [0xFE, 0xFE, 0x94, source, 0x03, 0xFD];
    private static byte[] IsolatedReply(byte destination, byte sequence) =>
        [0xFE, 0xFE, destination, 0x94, 0x03, sequence, 0, 0, 0, 0, 0xFD];

    private static void AssertAllowedSerial(string name) => Assert.IsTrue(
        AllowedSerialPortNames.Split(',').Contains(name, StringComparer.Ordinal),
        $"Opening port {name} is forbidden by the isolated serial test allow-list.");

    private static SerialPort OpenAllowedSerial(string name)
    {
        AssertAllowedSerial(name);
        var port = new SerialPort(name, 19200, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None, ReadTimeout = 200, WriteTimeout = 500
        };
        try { port.Open(); return port; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            port.Dispose();
            Assert.Inconclusive($"Allowed isolated serial port {name} is missing or busy: {error.Message}");
            throw;
        }
        catch { port.Dispose(); throw; }
    }

    private static Task<byte[]> ReadSerialFrame(SerialPort port, CancellationToken token) => Task.Run(() =>
    {
        var frame = new List<byte>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int value;
            try { value = port.ReadByte(); }
            catch (TimeoutException) { continue; }
            frame.Add((byte)value);
            Assert.IsLessThanOrEqualTo(64, frame.Count, "The serial responder must emit bounded CI-V frames.");
            if (value == 0xFD) return frame.ToArray();
        }
    }, token);

    private static async Task<byte[]> ReadPipeFrame(Stream stream, CancellationToken token)
    {
        var frame = new List<byte>();
        var one = new byte[1];
        while (await stream.ReadAsync(one, token) == 1)
        {
            frame.Add(one[0]);
            Assert.IsLessThanOrEqualTo(64, frame.Count, "IPC must receive one bounded CI-V response, not an echo/cross-delivery.");
            if (one[0] == 0xFD) return frame.ToArray();
        }
        Assert.Fail("CAT closed before a complete response arrived.");
        return [];
    }

    private static void RecordEcho(byte[] frame, ConcurrentQueue<byte[]> echoes)
    {
        Assert.HasCount(6, frame);
        Assert.IsTrue(frame[3] is 0xE0 or 0xE1);
        CollectionAssert.AreEqual(IsolatedRead(frame[3]), frame);
        echoes.Enqueue(frame);
    }

    private static async Task<byte[]> ReadSerialResponse(
        SerialPort port, ConcurrentQueue<byte[]> echoes, CancellationToken token)
    {
        while (true)
        {
            var frame = await ReadSerialFrame(port, token);
            if (frame[2] != 0x94) return frame;
            RecordEcho(frame, echoes);
        }
    }

    private static async Task<byte[]> ReadPipeResponse(
        Stream stream, ConcurrentQueue<byte[]> echoes, CancellationToken token)
    {
        while (true)
        {
            var frame = await ReadPipeFrame(stream, token);
            if (frame[2] != 0x94) return frame;
            RecordEcho(frame, echoes);
        }
    }

    private static async Task AssertPipeQuiet(Stream stream, CancellationToken token)
    {
        using var observation = CancellationTokenSource.CreateLinkedTokenSource(token);
        observation.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            var count = await stream.ReadAsync(new byte[1], observation.Token);
            Assert.Fail($"CAT produced unexpected bytes or disconnected during the quiet window (count {count}).");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
    }

    private sealed class AllowedSerialFactory : IByteBridgeEndpointFactory
    {
        private readonly SerialPortByteBridgeEndpointFactory real = new();
        public ConcurrentQueue<string> Opened { get; } = new();
        public ConcurrentQueue<string> Closed { get; } = new();
        public async ValueTask<IByteBridgeEndpoint> OpenAsync(string name, SerialSettings settings, CancellationToken token)
        {
            AssertAllowedSerial(name);
            var endpoint = await real.OpenAsync(name, settings, token);
            Opened.Enqueue(name);
            return new TrackedSerialEndpoint(endpoint, () => Closed.Enqueue(name));
        }
    }

    private sealed class TrackedSerialEndpoint(IByteBridgeEndpoint endpoint, Action closed) : IBufferedByteBridgeEndpoint
    {
        private int disposed;
        public int BufferedInputBytes => ((IBufferedByteBridgeEndpoint)endpoint).BufferedInputBytes;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token) => endpoint.ReadAsync(buffer, token);
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token) => endpoint.WriteAsync(buffer, token);
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await endpoint.DisposeAsync();
            closed();
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class IsolatedSerialFixture : IDisposable
    {
        private readonly SerialPort responder;
        private readonly CancellationTokenSource stop = new();
        private readonly Task responderTask;
        public SerialPort Commander { get; }
        public AllowedSerialFactory Factory { get; } = new();
        public ConcurrentQueue<byte[]> Requests { get; } = new();
        public ConcurrentQueue<byte[]> CommanderEchoes { get; } = new();
        public ConcurrentQueue<byte[]> IpcEchoes { get; } = new();
        public ConcurrentQueue<Exception> Failures { get; } = new();
        public ConcurrentQueue<ComConCommandNotifications> Trackers { get; } = new();
        public string Rig { get; } = Guid.NewGuid().ToString("D");
        public string PipeName { get; } = "ComConTest_IsolatedSerial_" + Guid.NewGuid().ToString("N");

        public IsolatedSerialFixture()
        {
            var opened = new List<SerialPort>();
            try
            {
                // Own all four at preflight, then release only ComCon's two endpoints.
                foreach (var name in AllowedSerialPortNames.Split(',')) opened.Add(OpenAllowedSerial(name));
                Commander = opened.Single(port => port.PortName == "COM23");
                responder = opened.Single(port => port.PortName == "COM24");
                foreach (var port in opened.Where(port => port != Commander && port != responder)) port.Dispose();
                responderTask = Task.Run(RespondAsync);
            }
            catch
            {
                foreach (var port in opened) port.Dispose();
                stop.Dispose();
                throw;
            }
        }

        public ComConOfflineSupervisor NewSupervisor()
        {
            var settings = AppSettings.CreateDefault() with
            {
                Profiles = [new ProfileConfig
                {
                    Id = 1, Label = "Isolated com0com IC-7600", Enabled = true,
                    PhysicalPort = "COM34", VirtualPort = "COM33", CommanderPort = "COM23",
                    Adapter = AdapterKind.IcomIc7600, Framing = FramingMode.Civ,
                    RadioAddress = 0x94, ControllerAddress = 0xE0, SerialSettings = new SerialSettings()
                }],
                RigProfileMappings = [new ComConRigProfileMapping(Rig, 1)]
            };
            return new(settings, [new ComConOfflineProfilePlan(1, PipeName, Factory, Options())],
                new StopwatchClock(), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15),
                (_, error) => Failures.Enqueue(error), (_, _) => { })
            {
                WrapControlStream = (_, stream, tracker) => { Trackers.Enqueue(tracker); return stream; }
            };
        }

        private async Task RespondAsync()
        {
            var sequences = new Dictionary<byte, byte>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var request = await ReadSerialFrame(responder, stop.Token);
                    Assert.HasCount(6, request);
                    Assert.IsTrue(request[3] is 0xE0 or 0xE1);
                    CollectionAssert.AreEqual(IsolatedRead(request[3]), request);
                    Requests.Enqueue(request);
                    var sequence = sequences.GetValueOrDefault(request[3]);
                    sequences[request[3]] = ++sequence;
                    // Request echoes are unmatched-address broadcast traffic, not addressed replies.
                    responder.Write(request, 0, request.Length);
                    var reply = IsolatedReply(request[3], sequence);
                    responder.Write(reply, 0, reply.Length);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { Failures.Enqueue(error); }
        }

        public async Task WaitForAcknowledgements(CancellationToken token)
        {
            while (Trackers.IsEmpty || Trackers.Any(tracker => tracker.PendingCount != 0))
            {
                Assert.IsEmpty(Failures);
                await Task.Delay(20, token);
            }
        }

        public async Task AssertEchoes(Stream cat, int expected, CancellationToken token)
        {
            while (CommanderEchoes.Count < expected)
                RecordEcho(await ReadSerialFrame(Commander, token), CommanderEchoes);
            while (IpcEchoes.Count < expected)
                RecordEcho(await ReadPipeFrame(cat, token), IpcEchoes);
            Assert.HasCount(expected, CommanderEchoes);
            Assert.HasCount(expected, IpcEchoes);
            CollectionAssert.AreEqual(Requests.SelectMany(frame => frame).ToArray(),
                CommanderEchoes.SelectMany(frame => frame).ToArray());
            CollectionAssert.AreEqual(Requests.SelectMany(frame => frame).ToArray(),
                IpcEchoes.SelectMany(frame => frame).ToArray());
        }

        public async Task AssertQuiet(int requests, CancellationToken token)
        {
            // An observation window, not a tight scheduling/latency assertion.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(100, token);
                Assert.HasCount(requests, Requests);
                Assert.AreEqual(0, Commander.BytesToRead, "Commander received stale, duplicate, or IPC bytes.");
                Assert.IsEmpty(Failures);
            }
        }

        public void AssertFactoryClosed() => CollectionAssert.AreEquivalent(Factory.Opened.ToArray(), Factory.Closed.ToArray());

        public void Dispose()
        {
            stop.Cancel();
            try { responderTask.GetAwaiter().GetResult(); }
            finally { Commander.Dispose(); responder.Dispose(); stop.Dispose(); }
        }
    }
}
