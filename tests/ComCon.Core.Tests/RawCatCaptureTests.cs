using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Transport;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class RawCatCaptureTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [DataRow(40, 20, 0)]
    [DataRow(40, 20, 2)]
    [DataRow(4096, 0, 0)]
    public async Task InvestigateDiagnosticBurstCaptureOffline(int burstSize, int pauseMs, int subscriberDelayMs)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_burst_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capture = new RawCatCapture(root);
            var notifications = 0;
            capture.StatusChanged += (_, _) =>
            {
                Interlocked.Increment(ref notifications);
                if (subscriberDelayMs > 0) Thread.Sleep(subscriberDelayMs);
            };
            capture.SetEnabled(true);
            var session = capture.StartSession(1, "Offline diagnostic burst", "Civ");
            var diagnostic = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { kind = "RadioIngressProcessed", message = new string('x', 256) }));
            var radio = Convert.FromHexString("FEFEE07A030040070700FD");
            var accepted = 0;
            var attempted = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                for (var burst = 0; burst < (pauseMs == 0 ? 1 : 100); burst++)
                {
                    for (var record = 0; record < burstSize; record++)
                    {
                        attempted++;
                        var isDiagnostic = record % 2 == 0;
                        if (capture.TryCapture(session, isDiagnostic ? "Coordinator" : "Radio",
                                isDiagnostic ? "CoordinatorDiagnostic" : "RadioToComCon",
                                isDiagnostic ? "RadioIngressProcessed" : "CompleteRecord",
                                isDiagnostic ? diagnostic : radio))
                            accepted++;
                        else break;
                    }
                    if (!capture.Snapshot.Enabled) break;
                    if (pauseMs > 0) await Task.Delay(pauseMs);
                }
            }
            finally { await capture.DisposeAsync(); }
            var written = Directory.GetFiles(Path.GetDirectoryName(capture.Snapshot.LogPath)!,
                    "ComCon-raw-cat.jsonl*")
                .Sum(file => File.ReadLines(file).Count());
            TestContext.WriteLine(
                $"OFFLINE burst={burstSize} pause={pauseMs}ms synchronousSubscriber={subscriberDelayMs}ms " +
                $"attempted={attempted} accepted={accepted} written={written} elapsed={clock.ElapsedMilliseconds}ms " +
                $"statusEvents={notifications} enabled={capture.Snapshot.Enabled} error={capture.Snapshot.Error}");
            Assert.AreEqual(accepted, written, "All accepted burst records must drain, including rotated files.");
            if (pauseMs > 0)
            {
                Assert.AreEqual(burstSize * 100, attempted);
                Assert.AreEqual(attempted, accepted, "The exact paced mixed-burst workload must not overflow.");
                Assert.IsTrue(capture.Snapshot.Enabled, capture.Snapshot.Error);
                Assert.IsNull(capture.Snapshot.Error);
            }
            if (!capture.Snapshot.Enabled)
                StringAssert.Contains(capture.Snapshot.Error, "queue overflow");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(100)]
    [DataRow(500)]
    public async Task InvestigatePacedCaptureThroughputOffline(int recordsPerSecond)
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_rate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var capture = new RawCatCapture(root);
            var notifications = 0;
            capture.StatusChanged += (_, _) => Interlocked.Increment(ref notifications);
            capture.SetEnabled(true);
            var session = capture.StartSession(1, "Offline throughput probe", "Civ");
            var bytes = Convert.FromHexString("FEFEE07A030040070700FD");
            var accepted = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                for (var batch = 0; batch < 300; batch++)
                {
                    for (var record = 0; record < recordsPerSecond / 100; record++)
                        if (capture.TryCapture(session, "Radio", "RadioToComCon", "CompleteRecord", bytes))
                            accepted++;
                    await Task.Delay(10);
                    if (!capture.Snapshot.Enabled) break;
                }
            }
            finally { await capture.DisposeAsync(); }
            var written = File.Exists(capture.Snapshot.LogPath)
                ? File.ReadLines(capture.Snapshot.LogPath).Count() : 0;
            TestContext.WriteLine(
                $"OFFLINE rate={recordsPerSecond}/s accepted={accepted} written={written} " +
                $"elapsed={clock.ElapsedMilliseconds}ms statusEvents={notifications} " +
                $"enabled={capture.Snapshot.Enabled} error={capture.Snapshot.Error}");
            Assert.AreEqual(accepted, written, "Accepted synthetic records must be drained without loss.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task BatchesPreserveOrderClearBarrierAndDisposalDrain()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_barrier_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var releaseWriter = new ManualResetEventSlim();
            await using var capture = new RawCatCapture(root);
            var writerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = 0;
            var notifications = 0;
            capture.StatusChanged += (_, snapshot) =>
            {
                Interlocked.Increment(ref notifications);
                if (snapshot.LogSizeBytes > 0 && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
                {
                    writerBlocked.TrySetResult();
                    if (!releaseWriter.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release the capture writer.");
                }
            };
            capture.SetEnabled(true);
            var session = capture.StartSession(1, "FIFO barrier", "Civ");
            Assert.IsTrue(capture.TryCapture(session, "Radio", "RadioToComCon", "initial", [0]));
            Task clear;
            try
            {
                await writerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
                for (var index = 0; index < 100; index++)
                    Assert.IsTrue(capture.TryCapture(session, "Radio", "RadioToComCon", $"before-{index}", [1]));
                clear = capture.ClearAsync();
                for (var index = 0; index < 100; index++)
                    Assert.IsTrue(capture.TryCapture(session, "Radio", "RadioToComCon", $"after-{index}", [2]));
            }
            finally { releaseWriter.Set(); }
            await clear.WaitAsync(TimeSpan.FromSeconds(3));
            await capture.DisposeAsync();
            var outcomes = File.ReadLines(capture.Snapshot.LogPath).Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.GetProperty("outcome").GetString();
            }).ToArray();
            CollectionAssert.AreEqual(Enumerable.Range(0, 100).Select(index => $"after-{index}").ToArray(), outcomes);
            Assert.IsTrue(capture.Snapshot.Enabled, capture.Snapshot.Error);
            Assert.IsLessThan(50, notifications, "Queued records should publish batch status, not per-record status.");
            TestContext.WriteLine($"FIFO/CLEAR/DRAIN: 100 pre-clear removed; 100 post-clear ordered; statusEvents={notifications}.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void InvestigateCaptureSizeRotationInterleavingOffline()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_size_race_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "capture.jsonl");
            File.WriteAllText(path, "{}\n");
            var log = new BoundedJsonLinesFile(path, "ComConTestSizeRace_");
            Assert.AreEqual(3L, log.Size);

            // Pause the current Size getter's two-step algorithm between existence and length.
            Assert.IsTrue(File.Exists(path));
            var metadata = new FileInfo(path);
            Assert.IsTrue(metadata.Exists);
            File.Move(path, path + ".1");
            Assert.ThrowsExactly<FileNotFoundException>(() => _ = new FileInfo(path).Length);
            Assert.AreEqual(3L, metadata.Length, "One cached metadata sample survives the rename gap.");
            Assert.AreEqual(0L, log.Size, "A lookup wholly within the rename gap already reports zero.");
            log.Append(System.Text.Encoding.UTF8.GetBytes("{}\n"));
            Assert.AreEqual(3L, log.Size);
            TestContext.WriteLine(
                "RACE REPRODUCED: existence succeeds; rotation renames active file; length throws FileNotFoundException. " +
                "Whole-gap Size returns zero; after recreation Size returns 3. No WPF/minimization required.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CaptureSizeSamplingSurvivesConcurrentRealRotations()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_size_concurrent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "capture.jsonl");
            var log = new BoundedJsonLinesFile(path, "ComConTestSizeConcurrent_");
            var record = System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { text = new string('x', 40_000) }) + "\n");
            log.Append(record);
            using var finished = new CancellationTokenSource();
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var samples = 0;
            var reader = Task.Run(() =>
            {
                started.TrySetResult();
                while (!finished.IsCancellationRequested)
                {
                    var size = log.Size;
                    Assert.IsGreaterThanOrEqualTo(0L, size);
                    Assert.IsLessThanOrEqualTo(BoundedJsonLinesFile.MaximumFileBytes, size);
                    Interlocked.Increment(ref samples);
                }
            });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await Task.Run(() =>
                {
                    for (var index = 0; index < 400; index++) log.Append(record);
                }).WaitAsync(TimeSpan.FromSeconds(10));
            }
            finally
            {
                finished.Cancel();
                await reader.WaitAsync(TimeSpan.FromSeconds(2));
            }
            Assert.IsGreaterThan(0, samples);
            Assert.IsTrue(File.Exists(path + ".2"));
            TestContext.WriteLine($"CONCURRENT SIZE: {samples} metadata samples during 400 appends / at least 15 rotations; no exception.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CaptureAdmissionUsesOnlySessionStateWithoutLogMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_admission_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var capture = new RawCatCapture(root);
            var session = capture.StartSession(1, "Admission", "Civ");
            Assert.IsFalse(capture.CanCapture(session));
            capture.SetEnabled(true);
            Assert.IsTrue(capture.CanCapture(session));
            Directory.CreateDirectory(capture.Snapshot.LogPath);
            Assert.IsTrue(capture.CanCapture(session), "Storage failure must be handled by writer, not admission metadata.");
            Assert.IsTrue(capture.TryCapture(session, "Radio", "RadioToComCon", "CompleteRecord", [1]));
            await WaitForStatusAsync(capture, RawCatCaptureStatus.Error);
            Assert.IsFalse(capture.CanCapture(session));
            StringAssert.Contains(capture.Snapshot.Error, "capture failed");
            capture.StopSession(session);
            capture.SetEnabled(true);
            Assert.IsFalse(capture.CanCapture(session));
            var unsupported = capture.StartSession(1, "No framing", "None");
            Assert.IsFalse(capture.CanCapture(unsupported));
            await capture.DisposeAsync();
            Assert.IsFalse(capture.CanCapture(unsupported));
            TestContext.WriteLine("ADMISSION: disabled/active/storage-failed/stopped/unsupported/disposed checks passed without disk lookup.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void BatchAppendRotatesOnRecordBoundariesAndRejectsOversizedRecordBeforeWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_batchfile_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "batch.jsonl");
            var log = new BoundedJsonLinesFile(path, "ComConTestBatch_");
            var records = Enumerable.Range(0, 12).Select(index => System.Text.Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(new { index, text = new string('x', 300_000) }) + "\n")).ToArray();
            log.AppendBatch(records);
            var retained = new List<int>();
            foreach (var file in new[] { path + ".2", path + ".1", path })
            {
                Assert.IsLessThanOrEqualTo(BoundedJsonLinesFile.MaximumFileBytes, new FileInfo(file).Length);
                foreach (var line in File.ReadLines(file))
                {
                    using var document = JsonDocument.Parse(line);
                    retained.Add(document.RootElement.GetProperty("index").GetInt32());
                }
            }
            CollectionAssert.AreEqual(Enumerable.Range(3, 9).ToArray(), retained.ToArray());
            Assert.IsFalse(File.Exists(path + ".3"));
            var before = File.ReadAllBytes(path);
            Assert.ThrowsExactly<ArgumentException>(() =>
                log.AppendBatch([records[0], new byte[BoundedJsonLinesFile.MaximumFileBytes + 1]]));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(path));
            log.Clear();
            Assert.IsEmpty(Directory.GetFiles(root));
            log.Append(records[0]);
            Assert.AreEqual(1, File.ReadLines(path).Count());
            TestContext.WriteLine("BATCH FILE: record-boundary rotation, nine ordered retained records, prevalidation, clear and single append passed.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CaptureIsOptInRecordBasedPersistentAndCanClearWhileForwarding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var capture = new RawCatCapture(root);
            Assert.AreEqual(RawCatCaptureStatus.Off, capture.Snapshot.Status);
            await using var bridge = new CommanderByteBridgeHost(factory, capture);
            await bridge.StartAsync(Profile());
            Assert.AreEqual(RawCatCaptureStatus.Off, capture.Snapshot.Status);

            capture.SetEnabled(true);
            Assert.AreEqual(RawCatCaptureStatus.Capturing, capture.Snapshot.Status);
            var tx = Convert.FromHexString("FEFE7AE11C00FD");
            await factory.Get("COM31").SendAsync(tx[..3]);
            CollectionAssert.AreEqual(tx[..3], await factory.Get("COM3").ReadWrittenAsync());
            await factory.Get("COM31").SendAsync(tx[3..]);
            CollectionAssert.AreEqual(tx[3..], await factory.Get("COM3").ReadWrittenAsync());
            await WaitForSizeAsync(capture, above: 0);

            using var first = JsonDocument.Parse(File.ReadLines(capture.Snapshot.LogPath).Single());
            var entry = first.RootElement;
            Assert.AreEqual(Convert.ToHexString(tx), entry.GetProperty("hex").GetString());
            Assert.AreEqual("Civ", entry.GetProperty("framing").GetString());
            Assert.AreEqual("Commander", entry.GetProperty("source").GetString());
            Assert.AreEqual("CommanderToRadio", entry.GetProperty("direction").GetString());
            Assert.AreEqual("CompleteRecord", entry.GetProperty("outcome").GetString());
            Assert.AreEqual(1, entry.GetProperty("profileId").GetInt32());
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.GetProperty("sessionId").GetString()));

            var bad = new byte[] { 0x11, 0x22 };
            var badEntriesWritten = WaitForAdditionalEntriesAsync(capture, 2);
            await factory.Get("COM3").SendAsync(bad);
            CollectionAssert.AreEqual(bad, await factory.Get("COM31").ReadWrittenAsync());
            await badEntriesWritten;
            Assert.AreEqual(3, File.ReadLines(capture.Snapshot.LogPath).Count());

            var malformed = Convert.FromHexString("FEFEE0FD");
            var malformedEntryWritten = WaitForAdditionalEntriesAsync(capture, 1);
            await factory.Get("COM31").SendAsync(malformed);
            CollectionAssert.AreEqual(malformed, await factory.Get("COM3").ReadWrittenAsync());
            await malformedEntryWritten;
            var capturedHex = File.ReadLines(capture.Snapshot.LogPath).Select(ReadHex).ToArray();
            CollectionAssert.AreEqual(
                new[] { Convert.ToHexString(tx), "11", "22", Convert.ToHexString(malformed) },
                capturedHex);

            capture.SetEnabled(false);
            Assert.AreEqual(RawCatCaptureStatus.Off, capture.Snapshot.Status);
            var noCapture = Convert.FromHexString("FEFEE17A1C0000FD");
            await factory.Get("COM3").SendAsync(noCapture);
            CollectionAssert.AreEqual(noCapture, await factory.Get("COM31").ReadWrittenAsync());
            await Task.Delay(100);
            Assert.AreEqual(4, File.ReadLines(capture.Snapshot.LogPath).Count());

            capture.SetEnabled(true);
            await capture.ClearAsync();
            Assert.AreEqual(0, capture.Snapshot.LogSizeBytes);
            var afterClear = Convert.FromHexString("FEFE7AE11C01FD");
            var afterClearEntryWritten = WaitForAdditionalEntriesAsync(capture, 1);
            await factory.Get("COM31").SendAsync(afterClear);
            CollectionAssert.AreEqual(afterClear, await factory.Get("COM3").ReadWrittenAsync());
            await afterClearEntryWritten;
            Assert.AreEqual(1, File.ReadLines(capture.Snapshot.LogPath).Count());

            await using var reloaded = new RawCatCapture(root);
            Assert.IsTrue(reloaded.Snapshot.Enabled);
            Assert.AreEqual(capture.Snapshot.LogPath, reloaded.Snapshot.LogPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task NoneFramingSuspendsSavedCaptureAndKeepsRelayWorking()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_none_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var bridge = new CommanderByteBridgeHost(factory, capture);
            await bridge.StartAsync(Profile() with { Framing = FramingMode.None });
            Assert.AreEqual(RawCatCaptureStatus.Error, capture.Snapshot.Status);
            Assert.IsTrue(capture.Snapshot.Enabled);
            StringAssert.Contains(capture.Snapshot.Error, "record framing");
            var bytes = new byte[] { 0x01, 0x02, 0x03 };
            await factory.Get("COM31").SendAsync(bytes);
            CollectionAssert.AreEqual(bytes, await factory.Get("COM3").ReadWrittenAsync());
            Assert.IsFalse(File.Exists(capture.Snapshot.LogPath));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task KenwoodCapturePreservesValidRecordsAndMalformedInputBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_kenwood_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var bridge = new CommanderByteBridgeHost(factory, capture);
            var profile = CommanderByteBridgeHostTests.CreateProfile() with
            {
                Framing = FramingMode.KenwoodAscii
            };
            await bridge.StartAsync(profile);

            var valid = System.Text.Encoding.ASCII.GetBytes("FA;");
            var validEntryWritten = WaitForAdditionalEntriesAsync(capture, 1);
            await factory.Get("COM31").SendAsync(valid);
            CollectionAssert.AreEqual(valid, await factory.Get("COM3").ReadWrittenAsync());
            await validEntryWritten;
            Assert.AreEqual(Convert.ToHexString(valid), ReadHex(File.ReadLines(capture.Snapshot.LogPath).Single()));

            var malformed = new byte[] { (byte)'F', (byte)'A', 0x01, (byte)'B', (byte)';' };
            var malformedEntriesWritten = WaitForAdditionalEntriesAsync(capture, 3);
            await factory.Get("COM3").SendAsync(malformed);
            CollectionAssert.AreEqual(malformed, await factory.Get("COM31").ReadWrittenAsync());
            await malformedEntriesWritten;
            var capturedMalformed = File.ReadLines(capture.Snapshot.LogPath).Skip(1)
                .Select(ReadHex).Select(Convert.FromHexString).SelectMany(bytes => bytes).ToArray();
            CollectionAssert.AreEqual(malformed, capturedMalformed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CaptureFileFailureDisablesCaptureButDoesNotStopForwarding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_failure_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            var logPath = capture.Snapshot.LogPath;
            Directory.CreateDirectory(logPath);
            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var bridge = new CommanderByteBridgeHost(factory, capture);
            await bridge.StartAsync(Profile());
            var frame = Convert.FromHexString("FEFE7AE11C00FD");
            await factory.Get("COM31").SendAsync(frame);
            CollectionAssert.AreEqual(frame, await factory.Get("COM3").ReadWrittenAsync());
            await WaitForStatusAsync(capture, RawCatCaptureStatus.Error);
            Assert.IsFalse(capture.Snapshot.Enabled);
            StringAssert.Contains(capture.Snapshot.Error, "capture failed");
            Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
            await factory.Get("COM31").SendAsync(frame);
            CollectionAssert.AreEqual(frame, await factory.Get("COM3").ReadWrittenAsync());
            Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task QueueOverflowDisablesCaptureWithoutBlockingRelayForwarding()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_overflow_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var capture = new RawCatCapture(root);
            using var releaseWriter = new ManualResetEventSlim();
            var writerBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var blocked = 0;
            capture.StatusChanged += (_, snapshot) =>
            {
                if (snapshot.LogSizeBytes > 0 && Interlocked.CompareExchange(ref blocked, 1, 0) == 0)
                {
                    writerBlocked.TrySetResult();
                    if (!releaseWriter.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Test did not release the capture writer.");
                }
            };
            capture.SetEnabled(true);
            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var bridge = new CommanderByteBridgeHost(factory, capture);
            await bridge.StartAsync(Profile());

            var record = Convert.FromHexString("FEFE7AE11C00FD");
            await factory.Get("COM31").SendAsync(record);
            CollectionAssert.AreEqual(record, await factory.Get("COM3").ReadWrittenAsync());
            await writerBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var burst = Enumerable.Range(0, 500).SelectMany(_ => record).ToArray();
            try
            {
                await factory.Get("COM31").SendAsync(burst);
                CollectionAssert.AreEqual(burst, await factory.Get("COM3").ReadWrittenAsync());
                await WaitForStatusAsync(capture, RawCatCaptureStatus.Error);
                StringAssert.Contains(capture.Snapshot.Error, "queue overflow");
                Assert.IsFalse(capture.Snapshot.Enabled);
                Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
            }
            finally { releaseWriter.Set(); }
            await capture.DisposeAsync();
            await using var reloaded = new RawCatCapture(root);
            Assert.IsFalse(reloaded.Snapshot.Enabled);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task CaptureRotatesWithinActiveAndArchiveSizeLimits()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_rotation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            var session = capture.BridgeStarted(1, "Rotation test", FramingMode.Civ.ToString());
            var payload = new byte[400_000];
            payload.AsSpan().Fill(0xA5);

            Assert.IsTrue(capture.TryCapture(session, "Commander", "CommanderToRadio", "CompleteRecord", payload));
            await WaitForSizeAsync(capture, above: 0);
            Assert.IsTrue(capture.TryCapture(session, "Commander", "CommanderToRadio", "CompleteRecord", payload));
            await WaitForFileAsync(capture.Snapshot.LogPath + ".1");
            Assert.IsTrue(capture.TryCapture(session, "Commander", "CommanderToRadio", "CompleteRecord", payload));
            await WaitForFileAsync(capture.Snapshot.LogPath + ".2");

            for (var index = 3; index <= 12; index++)
            {
                payload[0] = (byte)index;
                Assert.IsTrue(capture.TryCapture(session, "Commander", "CommanderToRadio", "CompleteRecord", payload));
            }
            await capture.DisposeAsync();

            var files = Directory.GetFiles(Path.GetDirectoryName(capture.Snapshot.LogPath)!,
                "ComCon-raw-cat.jsonl*");
            Assert.HasCount(10, files);
            Assert.IsFalse(File.Exists(capture.Snapshot.LogPath + ".10"));
            foreach (var path in files)
                Assert.IsLessThanOrEqualTo(BoundedCaptureLimit, new FileInfo(path).Length);
            StringAssert.Contains(File.ReadAllText(capture.Snapshot.LogPath), "\"hex\":\"0C");
            StringAssert.Contains(File.ReadAllText(capture.Snapshot.LogPath + ".9"), "\"hex\":\"03");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task PreferenceWriteFailureAppliesCurrentRunAndReportsNonPersistence()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ComConTest_capture_pref_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var loggingDirectory = Path.Combine(root, "ComCon", "Logging");
            Directory.CreateDirectory(loggingDirectory);
            Directory.CreateDirectory(Path.Combine(loggingDirectory, "ComCon-raw-cat-capture.json"));
            await using var capture = new RawCatCapture(root);
            capture.SetEnabled(true);
            Assert.IsTrue(capture.Snapshot.Enabled);
            StringAssert.Contains(capture.Snapshot.Error, "could not be saved");
            Assert.AreEqual(RawCatCaptureStatus.Error, capture.Snapshot.Status);

            var factory = new CommanderByteBridgeHostTests.FakeEndpointFactory();
            await using var bridge = new CommanderByteBridgeHost(factory, capture);
            await bridge.StartAsync(Profile());
            var frame = Convert.FromHexString("FEFE7AE11C00FD");
            var entryWritten = WaitForAdditionalEntriesAsync(capture, 1);
            await factory.Get("COM31").SendAsync(frame);
            CollectionAssert.AreEqual(frame, await factory.Get("COM3").ReadWrittenAsync());
            await entryWritten;

            Assert.IsTrue(capture.Snapshot.Enabled);
            StringAssert.Contains(capture.Snapshot.Error, "could not be saved");
            Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProfileConfig Profile() =>
        CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ };

    private static string ReadHex(string line)
    {
        using var document = JsonDocument.Parse(line);
        return document.RootElement.GetProperty("hex").GetString()
            ?? throw new InvalidDataException("Capture entry has no hex field.");
    }

    private static async Task WaitForSizeAsync(RawCatCapture capture, long above)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (capture.Snapshot.LogSizeBytes <= above) await Task.Delay(5, timeout.Token);
    }

    private static async Task WaitForFileAsync(string path)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!File.Exists(path)) await Task.Delay(5, timeout.Token);
    }

    private const long BoundedCaptureLimit = 1_048_576;

    private static async Task WaitForAdditionalEntriesAsync(RawCatCapture capture, int count)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var targetEntries = CountCompleteLines(capture.Snapshot.LogPath) + count;
        EventHandler<RawCatCaptureSnapshot> handler = (_, snapshot) =>
        {
            if (CountCompleteLines(snapshot.LogPath) >= targetEntries) completion.TrySetResult();
        };
        capture.StatusChanged += handler;
        try
        {
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            capture.StatusChanged -= handler;
        }
    }

    private static int CountCompleteLines(string path)
    {
        if (!File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Count(character => character == '\n');
    }

    private static async Task WaitForStatusAsync(RawCatCapture capture, RawCatCaptureStatus status)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (capture.Snapshot.Status != status) await Task.Delay(5, timeout.Token);
    }
}
