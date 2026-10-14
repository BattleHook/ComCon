using System.Collections.Concurrent;
using System.Runtime.Versioning;
using ComCon.Core.Ipc;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

public sealed partial class ComConCommandIntegrationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("windows")]
    public async Task ComConOfflineSlowRecipientBlockedNotificationIsolatesRetentionAndAckTimeout(bool expire)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var factories = new[] { new FakeFactory { RadioPort = "COM101" }, new FakeFactory { RadioPort = "COM102" } };
        var settings = SupervisorSettings(2);
        var clock = new FakeClock();
        var failures = new ConcurrentQueue<(int Id, Exception Error)>();
        var diagnostics = new ConcurrentQueue<(int Id, SharedRadioDiagnostic Diagnostic)>();
        var trackers = new ConcurrentDictionary<int, ComConCommandNotifications>();
        var blocked = new TaskCompletionSource<GatedNotificationStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ComConOfflineSupervisor(settings, SupervisorPlans(factories), clock,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5),
            (id, error) => failures.Enqueue((id, error)),
            (id, diagnostic) => diagnostics.Enqueue((id, diagnostic)))
        {
            WrapControlStream = (id, stream, tracker) =>
            {
                trackers[id] = tracker;
                if (id != 1) return stream;
                var gated = new GatedNotificationStream(stream);
                blocked.SetResult(gated);
                return gated;
            }
        };
        await supervisor.StartAsync(timeout.Token);
        using var slow = await MultiProfilePeer.Open(supervisor.Profiles[0].Listener,
            settings.RigProfileMappings[0].RigProfileId, 1, timeout.Token, null);
        using var other = await MultiProfilePeer.Open(supervisor.Profiles[1].Listener,
            settings.RigProfileMappings[1].RigProfileId, 2, timeout.Token, null);
        var gate = await blocked.Task.WaitAsync(timeout.Token);
        await slow.Cat.WriteAsync(Civ(0x94, 1), timeout.Token);
        CollectionAssert.AreEqual(Civ(0x94, 1), await factories[0].Radio.Receive());
        await gate.WriteStarted.Task.WaitAsync(timeout.Token);
        Assert.AreEqual(0L, trackers[1].TransmittedSequence);
        if (!expire)
        {
            for (byte record = 2; record <= 32; record++)
            {
                await slow.Cat.WriteAsync(Civ(0x94, record), timeout.Token);
                CollectionAssert.AreEqual(Civ(0x94, record), await factories[0].Radio.Receive());
            }
            await WaitUntil(() => trackers[1].PendingCount == 32);
            Assert.IsLessThanOrEqualTo(32768, trackers[1].RetainedPayloadBytes);
        }
        else await WaitUntil(() => trackers[1].PendingCount == 1);
        Assert.IsEmpty(failures);
        Assert.IsFalse(gate.Disposed.Task.IsCompleted);
        var affectedSession = supervisor.Sessions.ActiveSessions.Single(session => session.ProfileId == 1);
        await ExerciseOther(1);
        await WaitUntil(() => trackers[2].PendingCount == 0);
        if (expire) clock.NowMs = 2000;
        else
        {
            await slow.Cat.WriteAsync(Civ(0x94, 33), timeout.Token);
            CollectionAssert.AreEqual(Civ(0x94, 33), await factories[0].Radio.Receive());
        }
        await WaitUntil(() => failures.Any(entry => entry.Id == 1));
        await gate.Disposed.Task.WaitAsync(timeout.Token);
        try
        {
            await WaitUntil(() => diagnostics.Any(entry => entry.Id == 1 &&
                entry.Diagnostic.Kind == SharedRadioDiagnosticKind.ClientDisconnected &&
                entry.Diagnostic.Message.Contains(expire ? "not acknowledged before its deadline" :
                    "retention capacity was exceeded", StringComparison.Ordinal)));
        }
        catch (OperationCanceledException)
        {
            var disconnected = diagnostics.Where(entry => entry.Id == 1 &&
                entry.Diagnostic.Kind == SharedRadioDiagnosticKind.ClientDisconnected)
                .Select(entry => $"{entry.Diagnostic.Client}: {entry.Diagnostic.Message}");
            var reported = failures.Where(entry => entry.Id == 1)
                .Select(entry => $"{entry.Error.GetType().Name}: {entry.Error.Message}");
            var runtime = supervisor.Profiles.Single(runtime => runtime.Profile.Id == 1);
            Assert.Fail(
                $"Missing expected disconnect reason (expire={expire}). " +
                $"Disconnects=[{string.Join("; ", disconnected)}]; " +
                $"Failures=[{string.Join("; ", reported)}]; " +
                $"Session={affectedSession.State}, EndReason={affectedSession.EndReason}; " +
                $"Host={runtime.Host.State}, Fault={runtime.Host.Fault}; " +
                $"Pending={trackers[1].PendingCount}, Bytes={trackers[1].RetainedPayloadBytes}, " +
                $"Transmitted={trackers[1].TransmittedSequence}, GateDisposed={gate.Disposed.Task.IsCompleted}, " +
                $"TransportReleaseComplete={supervisor.Sessions.WaitForProfileReleaseAsync(1).IsCompleted}.");
        }
        Assert.AreEqual(0L, trackers[1].TransmittedSequence, "No notification reached the native pipe.");
        Assert.AreEqual(0, trackers[1].PendingCount);
        Assert.AreEqual(0, trackers[1].RetainedPayloadBytes);
        Assert.IsTrue(supervisor.Profiles.All(runtime => runtime.Host.State == SharedRadioHostState.Running));
        await factories[0].Legacy.Send(Civ(0x94, 0x51));
        CollectionAssert.AreEqual(Civ(0x94, 0x51), await factories[0].Radio.Receive());
        await ExerciseOther(2);
        Assert.IsFalse(failures.Any(entry => entry.Id == 2));

        async Task ExerciseOther(int sequence)
        {
            var command = Civ(0x94, (byte)(0x40 + sequence));
            await other.Cat.WriteAsync(command, timeout.Token);
            CollectionAssert.AreEqual(command, await factories[1].Radio.Receive());
            var outcome = await ComConCommandProtocol.ReadEnvelopeAsync(other.Control, timeout.Token);
            AssertOutcome(outcome, "record.outcome", sequence.ToString(), "WRITE_SUCCEEDED");
            await other.Ack(outcome.GetProperty("notificationSequence").GetString()!, timeout.Token);
        }
    }

    private sealed class GatedNotificationStream(Stream inner) : Stream
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WriteStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            WriteStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }
        public override ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return inner.DisposeAsync();
        }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
