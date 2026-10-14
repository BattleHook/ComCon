using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class SerialPortByteBridgeEndpointTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    [SupportedOSPlatform("windows")]
    public async Task ActualHscSafetyHelperSharesSubmissionDeadlineWithCommanderQueue(bool expire)
    {
        const string uuid = "12345678-1234-4234-8234-123456789abc";
        var defaults = AppSettings.CreateDefault();
        var profile = defaults.Profiles[0] with
        {
            Enabled = true, CommandIpcEnabled = true, Framing = FramingMode.Civ,
            Adapter = AdapterKind.Passthrough,
            PhysicalPort = "COM101", VirtualPort = "COM102", CommanderPort = "COM103",
            LegacyClientAddress = 0xE0, IpcClientAddress = 0xE1, RadioAddress = 0x7A
        };
        var settings = defaults with { Profiles = [profile], RigProfileMappings = [new(uuid, profile.Id)] };
        Assert.IsTrue(ComConProductionCoordinatorOptions.TryCreate(profile, out var options));
        Assert.IsNotNull(options);
        var factory = new NativeEndpointFactory();
        factory.Legacy.FailWrite = false;
        var names = ComConCommandPipeNames.IsolatedTest("ComConTest_Safety_" + Guid.NewGuid().ToString("N"));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SharedRadioDiagnostic>();
        var settled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var supervisor = new ComConCommandGroupSupervisor(settings,
            [new(profile.Id, factory, options)], names, new StopwatchClock(),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (_, failure) => failures.Enqueue(failure),
            (_, diagnostic) =>
            {
                diagnostics.Enqueue(diagnostic);
                if (diagnostic.Kind == SharedRadioDiagnosticKind.RecordWriteCompleted &&
                    diagnostic.Client == SharedRadioClient.LegacyClient)
                    settled.TrySetResult(Stopwatch.GetTimestamp());
            });
        await supervisor.StartAsync();
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CommandClient.ps1");
        var script = $$"""
            . '{{helper.Replace("'", "''")}}' -CatPipeName '{{names.CatPipeName}}' -ControlPipeName '{{names.ControlPipeName(1)}}' -RigProfileUuid '{{uuid}}'
            $submitted = Get-ComConMonotonicMilliseconds
            $deadline = $submitted + 5000
            $ProgressPreference = 'SilentlyContinue'
            $samples = @{ pttState = 0; pttStateUnixMs = 12345; rfPowerSetting = 34; rfPowerSettingUnixMs = 23456; updatedUnixMs = 0 }
            $ticks = New-Object 'System.Collections.Generic.List[long]'
            Start-Sleep -Milliseconds 2000
            $result = Invoke-ComConSafetyPttOff -RadioAddress '7A' -ControllerAddress 'E1' -RigProfileUuid '{{uuid}}' -TransactionDeadlineTick64 $deadline -OnHeartbeat {
                $ticks.Add((Get-ComConMonotonicMilliseconds))
                $samples.updatedUnixMs = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
            } -OnStage {
                param($stage)
                [Console]::Error.WriteLine('STAGE: ' + $stage)
                if ($stage -eq 'CAT_WRITE_START_PTT_OFF') {
                    [Console]::Out.WriteLine('READY')
                    if ([Console]::In.ReadLine() -ne 'GO') { throw 'TEST_SYNC_FAILED' }
                }
                if ($stage -eq 'CAT_WRITE_COMPLETE_PTT_OFF') { [Console]::Out.WriteLine('SENT') }
            }
            @{ result = $result; submitted = $submitted; deadline = $deadline; completed = (Get-ComConMonotonicMilliseconds); ticks = @($ticks.ToArray()); samples = $samples } | ConvertTo-Json -Compress -Depth 10
            """;
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            }
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) })
            child.StartInfo.ArgumentList.Add(argument);
        Console.WriteLine($"START safety case: {(expire ? "deadline expiry" : "success")}");
        var started = false;
        try
        {
            started = child.Start();
            Assert.IsTrue(started);
            var stages = Task.Run(async () =>
            {
                while (await child.StandardError.ReadLineAsync() is { } line)
                    Console.WriteLine(line);
            });
            var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual("READY", ready, ready);
            await factory.Legacy.Send(Convert.FromHexString("FEFE7AE01502FD"));
            CollectionAssert.AreEqual(Convert.FromHexString("FEFE7AE01502FD"), await factory.Radio.ReceiveWritten());
            var commanderStart = await settled.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await child.StandardInput.WriteLineAsync("GO");
            await child.StandardInput.FlushAsync();
            Assert.AreEqual("SENT", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            while (Stopwatch.GetElapsedTime(commanderStart).TotalMilliseconds < 600)
                await Task.Delay(Math.Max(1, (int)Math.Ceiling(600 - Stopwatch.GetElapsedTime(commanderStart).TotalMilliseconds)));
            Assert.IsLessThan(750d, Stopwatch.GetElapsedTime(commanderStart).TotalMilliseconds);
            Assert.IsFalse(factory.Radio.TryReceiveWritten(out _), "Safety command must stay queued behind Commander.");
            await factory.Radio.Send(Convert.FromHexString("FEFEE07A15020120FD"));
            CollectionAssert.AreEqual(Convert.FromHexString("FEFE7AE11C0000FD"), await factory.Radio.ReceiveWritten());
            Console.WriteLine("RADIO: exactly one queued PTT_OFF dispatched");
            if (!expire)
            {
                await Task.Delay(550);
                await factory.Radio.Send(Convert.FromHexString("FEFEE17AFBFD"));
                CollectionAssert.AreEqual(Convert.FromHexString("FEFE7AE11C00FD"), await factory.Radio.ReceiveWritten());
                await factory.Radio.Send(Convert.FromHexString("FEFEE17A1C0000FD"));
            }
            var output = child.StandardOutput.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await stages.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(0, child.ExitCode);
            using var document = JsonDocument.Parse(await output);
            var root = document.RootElement;
            var result = root.GetProperty("result");
            Console.WriteLine(result.GetRawText());
            Assert.AreEqual(expire ? "ERROR" : "OK", result.GetProperty("status").GetString(), result.GetRawText());
            Assert.AreEqual(5000L, root.GetProperty("deadline").GetInt64() - root.GetProperty("submitted").GetInt64());
            var elapsed = root.GetProperty("completed").GetInt64() - root.GetProperty("submitted").GetInt64();
            Console.WriteLine($"Submission-to-completion: {elapsed} ms; deadline: 5000 ms.");
            var outcomes = result.GetProperty("notifications").EnumerateArray().Where(item =>
                item.GetProperty("messageType").GetString() == "record.outcome").ToArray();
            Assert.HasCount(expire ? 1 : 2, outcomes);
            for (var i = 0; i < outcomes.Length; i++)
            {
                Assert.AreEqual((i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    outcomes[i].GetProperty("recordSequence").GetString());
                Assert.AreEqual("WRITE_SUCCEEDED", outcomes[i].GetProperty("deliveryStatus").GetString());
            }
            if (expire)
            {
                Assert.AreEqual("TRANSACTION_DEADLINE", result.GetProperty("errorCode").GetString());
                StringAssert.Contains(result.GetProperty("error").GetString()!, "CLEANUP_FAILED");
                Assert.IsGreaterThanOrEqualTo(5000L, elapsed);
                Assert.IsLessThan(6500L, elapsed);
                Assert.AreEqual(JsonValueKind.Null, result.GetProperty("safety").ValueKind);
            }
            else
            {
                Assert.IsLessThan(5000L, elapsed);
                var safety = result.GetProperty("safety");
                Assert.AreEqual("FE FE E1 7A FB FD", safety.GetProperty("pttOffAckHex").GetString());
                Assert.AreEqual("FE FE E1 7A 1C 00 00 FD", safety.GetProperty("pttReadbackHex").GetString());
                Assert.IsGreaterThan(23456L, safety.GetProperty("pttReadbackReceivedUnixMs").GetInt64());
                Assert.IsTrue(result.GetProperty("notifications").EnumerateArray().Any(item =>
                    item.GetProperty("messageType").GetString() == "session.notification" &&
                    item.GetProperty("event").GetString() == "session.ended"));
            }
            var samples = root.GetProperty("samples");
            Assert.AreEqual(12345L, samples.GetProperty("pttStateUnixMs").GetInt64());
            Assert.AreEqual(23456L, samples.GetProperty("rfPowerSettingUnixMs").GetInt64());
            Assert.AreEqual(0, samples.GetProperty("pttState").GetInt32());
            Assert.AreEqual(34, samples.GetProperty("rfPowerSetting").GetInt32());
            Assert.IsGreaterThan(0L, samples.GetProperty("updatedUnixMs").GetInt64());
            var ticks = root.GetProperty("ticks").EnumerateArray().Select(item => item.GetInt64()).ToArray();
            Assert.IsGreaterThanOrEqualTo(2, ticks.Length);
            for (var i = 1; i < ticks.Length; i++)
                Assert.IsLessThan(650L, ticks[i] - ticks[i - 1]);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (supervisor.ActiveSessions.Count != 0)
                await Task.Delay(5, cleanup.Token);
            Assert.IsEmpty(supervisor.ActiveSessions);
            if (expire)
            {
                Assert.HasCount(1, failures, string.Join(Environment.NewLine, failures));
                Assert.IsInstanceOfType<IOException>(failures.Single());
                if (failures.Single() is EndOfStreamException eof)
                    Assert.AreEqual("Control stream closed before orderly closure was acknowledged.", eof.Message);
                else
                    StringAssert.Contains(failures.Single().StackTrace!, "ReadAcknowledgmentsAsync",
                        "Expired cleanup may report EOF or a Windows broken-pipe read, not an unrelated failure.");
            }
            else
                Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(expire ? 2 : 3, factory.Radio.NativeWrites);
            Assert.IsFalse(factory.Radio.TryReceiveWritten(out _), "No retry or post-deadline readback.");
            Assert.IsFalse(factory.Radio.Disposed);
            Assert.IsFalse(factory.Legacy.Disposed);
            Assert.IsFalse(diagnostics.Any(item => item.Kind == SharedRadioDiagnosticKind.ProfileFaulted));
            await supervisor.StopAsync();
        }
        finally
        {
            if (started && !child.HasExited) child.Kill(entireProcessTree: true);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    [SupportedOSPlatform("windows")]
    public async Task CommanderAndActualHscHelperCompleteFrequencySession(
        bool concurrentCommander, bool missingCommanderReply, bool exhaustRecovery)
    {
        const string uuid = "12345678-1234-4234-8234-123456789abc";
        var defaults = AppSettings.CreateDefault();
        var profile = defaults.Profiles[0] with
        {
            Enabled = true, CommandIpcEnabled = true, Framing = FramingMode.Civ,
            Adapter = AdapterKind.Passthrough,
            PhysicalPort = "COM101", VirtualPort = "COM102", CommanderPort = "COM103",
            LegacyClientAddress = 0xE0, IpcClientAddress = 0xE1, RadioAddress = 0x7A
        };
        var settings = defaults with
        {
            Profiles = [profile],
            RigProfileMappings = [new(uuid, profile.Id)]
        };
        Assert.IsTrue(ComConProductionCoordinatorOptions.TryCreate(profile, out var options));
        Assert.IsNotNull(options);
        var factory = new NativeEndpointFactory();
        factory.Legacy.FailWrite = !concurrentCommander;
        var names = ComConCommandPipeNames.IsolatedTest("ComConTest_NativeWrite_" + Guid.NewGuid().ToString("N"));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<SharedRadioDiagnostic>();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyWriteSettled = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answerTimeout = new TaskCompletionSource<SharedRadioDiagnostic>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource<SharedRadioDiagnostic>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var profileFault = new TaskCompletionSource<SharedRadioDiagnostic>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        long faultStarted = 0;
        await using var supervisor = new ComConCommandGroupSupervisor(settings,
            [new(profile.Id, factory, options)], names, new StopwatchClock(),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
            (_, failure) => failures.Enqueue(failure),
            (_, diagnostic) =>
            {
                diagnostics.Enqueue(diagnostic);
                if (diagnostic.Kind == SharedRadioDiagnosticKind.ProfileFaulted)
                {
                    Interlocked.Exchange(ref faultStarted, Stopwatch.GetTimestamp());
                    profileFault.TrySetResult(diagnostic);
                }
                if (diagnostic.Client == SharedRadioClient.LegacyClient)
                {
                    if (diagnostic.Kind == SharedRadioDiagnosticKind.RadioReplyTimeout)
                        answerTimeout.TrySetResult(diagnostic);
                    if (diagnostic.Kind == SharedRadioDiagnosticKind.RadioExchangeRecovered)
                        recovered.TrySetResult(diagnostic);
                }
                if (diagnostic.Kind == SharedRadioDiagnosticKind.RecordWriteCompleted &&
                    diagnostic.Client == SharedRadioClient.LegacyClient)
                    legacyWriteSettled.TrySetResult(Stopwatch.GetTimestamp());
                if (diagnostic.Kind == SharedRadioDiagnosticKind.ClientDisconnected &&
                    diagnostic.Client == SharedRadioClient.LegacyClient)
                    disconnected.TrySetResult();
            });
        await supervisor.StartAsync();

        if (!concurrentCommander)
        {
            // A startup echo fans out to the unopened Commander peer.
            await factory.Radio.Send(Convert.FromHexString("FEFE7AE003FD"));
            await Task.WhenAny(disconnected.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.IsTrue(disconnected.Task.IsCompleted,
                $"Radio read bytes: {factory.Radio.ReadBytes}; legacy writes: {factory.Legacy.NativeWrites}. " +
                string.Join(Environment.NewLine, diagnostics.Select(item => $"{item.Kind}: {item.Message}")));
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!factory.Legacy.Disposed)
                await Task.Delay(5, cleanupTimeout.Token);
            Assert.IsTrue(factory.Legacy.Disposed);
            Assert.AreEqual(1, factory.Legacy.NativeWrites);
        }
        Assert.IsFalse(factory.Radio.Disposed);
        Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles.Single().State);

        Task radioReply = Task.CompletedTask;
        using var child = new Process
        {
            StartInfo = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = concurrentCommander
            }
        };
        var helperPath = Path.Combine(AppContext.BaseDirectory, "ComCon-CommandClient.ps1");
        var script = $$"""
            . '{{helperPath.Replace("'", "''")}}' -CatPipeName '{{names.CatPipeName}}' -ControlPipeName '{{names.ControlPipeName(1)}}' -RigProfileUuid '{{uuid}}'
            $closedTick64 = 0
            $result = Invoke-ComConReadOnlyCommand -CatPipeName '{{names.CatPipeName}}' -ControlPipeName '{{names.ControlPipeName(1)}}' -RigProfileUuid '{{uuid}}' -CommandList Frequency -OnStage {
                param($stage)
                if ($stage -eq 'CAT_WRITE_START_FREQUENCY') {
                    [Console]::Out.WriteLine('READY')
                    if ([Console]::In.ReadLine() -ne 'GO') { throw 'TEST_SYNC_FAILED' }
                }
                if ($stage -eq 'CAT_WRITE_COMPLETE_FREQUENCY') { [Console]::Out.WriteLine('SENT') }
                if ($stage -eq 'SESSION_END_WAIT') { $script:closedTick64 = Get-ComConMonotonicMilliseconds }
            }
            $result | Add-Member -NotePropertyName testCatClosedTick64 -NotePropertyValue $closedTick64
            $result | ConvertTo-Json -Compress -Depth 8
            """;
        var arguments = concurrentCommander
            ? new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)) }
            : new[]
        {
            "-NoProfile", "-NonInteractive", "-File",
            helperPath,
            "-CatPipeName", names.CatPipeName, "-ControlPipeName", names.ControlPipeName(1),
            "-RigProfileUuid", uuid, "-CommandList", "Frequency"
        };
        foreach (var argument in arguments)
            child.StartInfo.ArgumentList.Add(argument);
        var childStarted = false;
        try
        {
            childStarted = child.Start();
            Assert.IsTrue(childStarted);
            if (concurrentCommander)
            {
                var ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual("READY", ready, ready);
                await factory.Legacy.Send(Convert.FromHexString("FEFE7AE01502FD"));
                CollectionAssert.AreEqual(Convert.FromHexString("FEFE7AE01502FD"), await factory.Radio.ReceiveWritten());
                var legacyStart = await legacyWriteSettled.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await child.StandardInput.WriteLineAsync("GO");
                await child.StandardInput.FlushAsync();
                Assert.AreEqual("SENT", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)));
                while (Stopwatch.GetElapsedTime(legacyStart) < TimeSpan.FromMilliseconds(600))
                {
                    var remaining = TimeSpan.FromMilliseconds(600) - Stopwatch.GetElapsedTime(legacyStart);
                    await Task.Delay(Math.Max(1, (int)Math.Ceiling(remaining.TotalMilliseconds)));
                }
                Assert.IsTrue(Stopwatch.GetElapsedTime(legacyStart) >= TimeSpan.FromMilliseconds(600));
                Assert.IsTrue(Stopwatch.GetElapsedTime(legacyStart) < TimeSpan.FromMilliseconds(750),
                    "Test scheduling exceeded the normal-answer budget; this is not a recovery check.");
                Assert.IsFalse(factory.Radio.TryReceiveWritten(out _),
                    "HSC must stay buffered until the Commander reply or qualified recovery.");
                if (exhaustRecovery)
                {
                    using var noiseTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while (!profileFault.Task.IsCompleted)
                    {
                        noiseTimeout.Token.ThrowIfCancellationRequested();
                        Assert.IsFalse(factory.Radio.TryReceiveWritten(out _),
                            "Neither the queued HSC command nor a Commander retry may be sent.");
                        await factory.Radio.Send([0x55]);
                        await Task.WhenAny(profileFault.Task, Task.Delay(100, noiseTimeout.Token));
                    }
                    var fault = await profileFault.Task;
                    var timeout = await answerTimeout.Task.WaitAsync(TimeSpan.FromSeconds(1));
                    var write = diagnostics.Single(item =>
                        item.Kind == SharedRadioDiagnosticKind.RecordWriteCompleted &&
                        item.Client == SharedRadioClient.LegacyClient);
                    Assert.IsNotNull(write.MonotonicTimestampMs);
                    Assert.IsNotNull(timeout.MonotonicTimestampMs);
                    Assert.IsNotNull(fault.MonotonicTimestampMs);
                    Assert.IsGreaterThanOrEqualTo(750L,
                        timeout.MonotonicTimestampMs.Value - write.MonotonicTimestampMs.Value);
                    Assert.IsGreaterThanOrEqualTo(1500L,
                        fault.MonotonicTimestampMs.Value - write.MonotonicTimestampMs.Value);
                    Assert.IsLessThan(2000L,
                        fault.MonotonicTimestampMs.Value - write.MonotonicTimestampMs.Value);
                    StringAssert.Contains(fault.Message, "CI-V recovery exhausted");
                }
                else if (missingCommanderReply)
                {
                    var timeout = await answerTimeout.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    Assert.IsFalse(factory.Radio.TryReceiveWritten(out _),
                        "Answer timeout alone must not release the radio.");
                    var recovery = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    var write = diagnostics.Single(item =>
                        item.Kind == SharedRadioDiagnosticKind.RecordWriteCompleted &&
                        item.Client == SharedRadioClient.LegacyClient);
                    Assert.IsNotNull(write.MonotonicTimestampMs);
                    Assert.IsNotNull(timeout.MonotonicTimestampMs);
                    Assert.IsNotNull(recovery.MonotonicTimestampMs);
                    Assert.IsGreaterThanOrEqualTo(750L,
                        timeout.MonotonicTimestampMs.Value - write.MonotonicTimestampMs.Value);
                    Assert.IsGreaterThanOrEqualTo(1000L,
                        recovery.MonotonicTimestampMs.Value - write.MonotonicTimestampMs.Value);
                    Assert.IsLessThan(1500L,
                        recovery.MonotonicTimestampMs.Value - write.MonotonicTimestampMs.Value);
                    StringAssert.Contains(recovery.Message, "uninterrupted RX silence");
                }
                else
                {
                    await factory.Radio.Send(Convert.FromHexString("FEFEE07A15020120FD"));
                    CollectionAssert.AreEqual(Convert.FromHexString("FEFEE07A15020120FD"), await factory.Legacy.ReceiveWritten());
                }
            }
            if (!exhaustRecovery)
            {
                radioReply = Task.Run(async () =>
                {
                    var query = await factory.Radio.ReceiveWritten(TimeSpan.FromSeconds(10));
                    CollectionAssert.AreEqual(Convert.FromHexString("FEFE7AE103FD"), query);
                    await factory.Radio.Send(Convert.FromHexString("FEFEE17A030040070700FD"));
                });
            }
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, child.ExitCode, await errors);
            using var result = JsonDocument.Parse(await output);
            Assert.AreEqual(exhaustRecovery ? "ERROR" : "OK", result.RootElement.GetProperty("status").GetString(),
                result.RootElement.GetRawText());
            await radioReply;
            Assert.AreEqual(1, result.RootElement.GetProperty("profileId").GetInt32());
            var commands = result.RootElement.GetProperty("commands");
            Assert.AreEqual(exhaustRecovery ? 0 : 1, commands.EnumerateObject().Count());
            if (!exhaustRecovery)
                Assert.AreEqual("FE FE E1 7A 03 00 40 07 07 00 FD",
                    commands.GetProperty("Frequency").GetProperty("replyHex").GetString());
            var notifications = result.RootElement.GetProperty("notifications").EnumerateArray().ToArray();
            Assert.IsTrue(notifications.Any(item =>
                item.GetProperty("messageType").GetString() == (exhaustRecovery ? "record.rejected" : "record.outcome") &&
                item.GetProperty("recordSequence").GetString() == "1" &&
                item.GetProperty("deliveryStatus").GetString() == (exhaustRecovery ? "NOT_SENT" : "WRITE_SUCCEEDED")));
            if (exhaustRecovery)
            {
                Assert.AreEqual("CAT_RECORD_NOT_SENT", result.RootElement.GetProperty("errorCode").GetString());
                Assert.IsFalse(result.RootElement.GetProperty("error").GetString()!.Contains("CLEANUP_FAILED",
                    StringComparison.Ordinal), result.RootElement.GetRawText());
                Assert.IsFalse(notifications.Any(item =>
                    item.TryGetProperty("deliveryStatus", out var delivery) && delivery.GetString() == "WRITE_SUCCEEDED"));
            }
            Assert.IsTrue(notifications.Any(item =>
                item.GetProperty("messageType").GetString() == "session.notification" &&
                item.GetProperty("event").GetString() == "session.ended"), result.RootElement.GetRawText());
            using var sessionCleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (supervisor.ActiveSessions.Count != 0)
                await Task.Delay(5, sessionCleanupTimeout.Token);
            Assert.IsEmpty(supervisor.ActiveSessions);
            if (concurrentCommander && !exhaustRecovery)
            {
                var catClosedTick = result.RootElement.GetProperty("testCatClosedTick64").GetInt64();
                Assert.IsGreaterThan(0L, catClosedTick);
                Assert.IsLessThan(3000L, Environment.TickCount64 - catClosedTick,
                    "Session release must be observed within three seconds of actual CAT closure.");
            }
            if (exhaustRecovery)
                Assert.IsLessThan(3000d,
                    Stopwatch.GetElapsedTime(Interlocked.Read(ref faultStarted)).TotalMilliseconds,
                    "Error-path session release must finish within three seconds of the earlier TX fault.");
            Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
            Assert.AreEqual(exhaustRecovery ? 1 : 0,
                diagnostics.Count(item => item.Kind == SharedRadioDiagnosticKind.ProfileFaulted));
            Assert.AreEqual(missingCommanderReply ? 1 : 0,
                diagnostics.Count(item => item.Kind == SharedRadioDiagnosticKind.RadioReplyTimeout));
            Assert.AreEqual(missingCommanderReply && !exhaustRecovery ? 1 : 0,
                diagnostics.Count(item => item.Kind == SharedRadioDiagnosticKind.RadioExchangeRecovered));
            if (concurrentCommander)
            {
                Assert.IsFalse(factory.Legacy.Disposed);
                Assert.AreEqual(exhaustRecovery ? SharedRadioHostState.Faulted : SharedRadioHostState.Running,
                    supervisor.Profiles.Single().State);
            }
            if (exhaustRecovery)
            {
                Assert.IsFalse(factory.Radio.Disposed);
                var rxAfterFault = Convert.FromHexString("FEFEE07A15020120FD");
                await factory.Radio.Send(rxAfterFault);
                CollectionAssert.AreEqual(rxAfterFault, await factory.Legacy.ReceiveWritten());
                Assert.AreEqual(SharedRadioHostState.Faulted, supervisor.Profiles.Single().State);
            }
            Assert.AreEqual(concurrentCommander && !exhaustRecovery ? 2 : 1, factory.Radio.NativeWrites);
            Assert.IsFalse(factory.Radio.TryReceiveWritten(out _));
            Assert.AreEqual(0, factory.Radio.AsyncWrites);
            if (concurrentCommander)
            {
                if (exhaustRecovery)
                {
                    var faultWrites = factory.Radio.NativeWrites;
                    var refusal = await RunFreshHelper(names, factory, expectSuccess: false);
                    Assert.AreEqual("ERROR", refusal.GetProperty("status").GetString(), refusal.GetRawText());
                    Assert.IsFalse(string.IsNullOrWhiteSpace(refusal.GetProperty("errorCode").GetString()));
                    Assert.IsFalse(string.IsNullOrWhiteSpace(refusal.GetProperty("error").GetString()));
                    Assert.AreEqual(faultWrites, factory.Radio.NativeWrites);
                    Assert.IsFalse(factory.Radio.TryReceiveWritten(out _));
                    using var rejectedCleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    while (supervisor.ActiveSessions.Count != 0)
                        await Task.Delay(5, rejectedCleanup.Token);
                    Assert.IsEmpty(supervisor.ActiveSessions);
                    Assert.AreEqual(SharedRadioHostState.Faulted, supervisor.Profiles.Single().State);
                    Assert.HasCount(1, failures, string.Join(Environment.NewLine, failures));
                    var failure = failures.Single();
                    Assert.IsInstanceOfType<ComConCommandProtocolException>(failure);
                    Assert.AreEqual("SETUP_NOT_READY", ((ComConCommandProtocolException)failure).ErrorCode);
                    await supervisor.StopAsync();
                    Assert.IsTrue(factory.Radio.Disposed);
                    Assert.IsTrue(factory.Legacy.Disposed);
                    var freshFactory = new NativeEndpointFactory();
                    freshFactory.Legacy.FailWrite = false;
                    var freshNames = ComConCommandPipeNames.IsolatedTest(
                        "ComConTest_Restart_" + Guid.NewGuid().ToString("N"));
                    var freshFailures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
                    await using var fresh = new ComConCommandGroupSupervisor(settings,
                        [new(profile.Id, freshFactory, options)], freshNames, new StopwatchClock(),
                        TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3),
                        (_, failure) => freshFailures.Enqueue(failure), (_, _) => { });
                    await fresh.StartAsync();
                    Assert.IsFalse(freshFactory.Radio.TryReceiveWritten(out _), "Fresh runtime must not replay old work.");
                    var restarted = await RunFreshHelper(freshNames, freshFactory, expectSuccess: true);
                    Assert.AreEqual("OK", restarted.GetProperty("status").GetString(), restarted.GetRawText());
                    Assert.AreNotEqual(result.RootElement.GetProperty("sessionId").GetString(),
                        restarted.GetProperty("sessionId").GetString());
                    Assert.AreEqual(1, freshFactory.Radio.NativeWrites);
                    Assert.IsFalse(freshFactory.Radio.TryReceiveWritten(out _));
                    Assert.IsEmpty(fresh.ActiveSessions);
                    Assert.IsEmpty(freshFailures, string.Join(Environment.NewLine, freshFailures));
                    Assert.AreEqual(SharedRadioHostState.Running, fresh.Profiles.Single().State);
                    await fresh.StopAsync();
                }
                else
                {
                    var before = factory.Radio.NativeWrites;
                    var next = await RunFreshHelper(names, factory, expectSuccess: true);
                    Assert.AreEqual("OK", next.GetProperty("status").GetString(), next.GetRawText());
                    Assert.AreNotEqual(result.RootElement.GetProperty("sessionId").GetString(),
                        next.GetProperty("sessionId").GetString());
                    Assert.AreEqual(before + 1, factory.Radio.NativeWrites);
                    Assert.IsFalse(factory.Radio.TryReceiveWritten(out _));
                    Assert.IsEmpty(supervisor.ActiveSessions);
                    Assert.IsEmpty(failures, string.Join(Environment.NewLine, failures));
                    Assert.AreEqual(SharedRadioHostState.Running, supervisor.Profiles.Single().State);
                    var commanderRx = Convert.FromHexString("FEFEE07A030050070700FD");
                    await factory.Radio.Send(commanderRx);
                    CollectionAssert.AreEqual(commanderRx, await factory.Legacy.ReceiveWritten());
                    Assert.IsFalse(factory.Legacy.Disposed);
                    Assert.IsFalse(factory.Radio.Disposed);
                }
            }
            await supervisor.StopAsync();
        }
        finally
        {
            if (childStarted && !child.HasExited) child.Kill(entireProcessTree: true);
            await radioReply.WaitAsync(TimeSpan.FromSeconds(3));
        }

        async Task<JsonElement> RunFreshHelper(
            ComConCommandPipeNames pipeNames, NativeEndpointFactory endpoints, bool expectSuccess)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(child.StartInfo.FileName)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-File", helperPath,
                "-CatPipeName", pipeNames.CatPipeName, "-ControlPipeName", pipeNames.ControlPipeName(1),
                "-RigProfileUuid", uuid, "-CommandList", "Frequency" })
                process.StartInfo.ArgumentList.Add(argument);
            Assert.IsTrue(process.Start());
            Console.WriteLine($"LIFECYCLE: fresh helper, expected {(expectSuccess ? "success" : "faulted-host refusal")}");
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                if (expectSuccess)
                {
                    CollectionAssert.AreEqual(Convert.FromHexString("FEFE7AE103FD"),
                        await endpoints.Radio.ReceiveWritten(TimeSpan.FromSeconds(10)));
                    await endpoints.Radio.Send(Convert.FromHexString("FEFEE17A030050070700FD"));
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.AreEqual(0, process.ExitCode, await stderr);
                using var json = JsonDocument.Parse(await stdout);
                var payload = json.RootElement.Clone();
                Console.WriteLine(payload.GetRawText());
                if (expectSuccess)
                {
                    Assert.AreEqual("FE FE E1 7A 03 00 50 07 07 00 FD",
                        payload.GetProperty("commands").GetProperty("Frequency").GetProperty("replyHex").GetString(),
                        "New session must receive the new reply, not stale previous data.");
                    var notifications = payload.GetProperty("notifications").EnumerateArray().ToArray();
                    Assert.IsTrue(notifications.Any(item =>
                        item.GetProperty("messageType").GetString() == "record.outcome" &&
                        item.GetProperty("recordSequence").GetString() == "1" &&
                        item.GetProperty("notificationSequence").GetString() == "1"));
                    Assert.IsTrue(notifications.Any(item =>
                        item.GetProperty("messageType").GetString() == "session.notification" &&
                        item.GetProperty("event").GetString() == "session.ended"));
                }
                else
                    Assert.IsEmpty(payload.GetProperty("commands").EnumerateObject());
                return payload;
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
        }
    }

    [TestMethod]
    public async Task WriteUsesNativeTimeoutInsteadOfUncancellableAsyncSerialWrite()
    {
        using var stream = new NativeTimeoutStream { FailWrite = true };
        await using var endpoint = new SerialPortByteBridgeEndpointFactory.SerialPortByteBridgeEndpoint(
            stream, stream);
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsExactlyAsync<TimeoutException>(() =>
            endpoint.WriteAsync(new byte[] { 0xFE, 0xFD }, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(2)));

        Assert.AreEqual(1, stream.NativeWrites);
        Assert.AreEqual(0, stream.AsyncWrites);
        Assert.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(1));
        Assert.IsFalse(stream.Disposed, "A write timeout must not close the radio RX stream.");
    }

    [TestMethod]
    public async Task SuccessfulNativeWritePreservesBytesAndReadEndpoint()
    {
        using var stream = new NativeTimeoutStream();
        await using var endpoint = new SerialPortByteBridgeEndpointFactory.SerialPortByteBridgeEndpoint(
            stream, stream);
        byte[] bytes = [0xFE, 0xFE, 0x7A, 0xE1, 0x03, 0xFD];

        await endpoint.WriteAsync(bytes, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        CollectionAssert.AreEqual(bytes, stream.Written);
        Assert.AreEqual(1, stream.NativeWrites);
        Assert.AreEqual(0, stream.AsyncWrites);
        Assert.IsFalse(stream.Disposed);
    }

    [TestMethod]
    public async Task CancelledWriteDoesNotDispatchNativeBytes()
    {
        using var stream = new NativeTimeoutStream();
        await using var endpoint = new SerialPortByteBridgeEndpointFactory.SerialPortByteBridgeEndpoint(
            stream, stream);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            endpoint.WriteAsync(new byte[] { 0xFE, 0xFD }, stop.Token).AsTask());

        Assert.AreEqual(0, stream.NativeWrites);
        Assert.AreEqual(0, stream.AsyncWrites);
    }

    private sealed class NativeTimeoutStream : Stream
    {
        public bool FailWrite { get; set; }
        public int NativeWrites { get; private set; }
        public int AsyncWrites { get; private set; }
        public byte[] Written { get; private set; } = [];
        public bool Disposed { get; private set; }
        public int ReadBytes { get; private set; }
        private readonly TaskCompletionSource pendingWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Channel<byte[]> incoming = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<byte[]> written = Channel.CreateUnbounded<byte[]>();
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            NativeWrites++;
            if (FailWrite)
            {
                Thread.Sleep(50);
                throw new TimeoutException("Synthetic native serial write timeout.");
            }
            Written = buffer.AsSpan(offset, count).ToArray();
            written.Writer.TryWrite(Written);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            AsyncWrites++;
            return new(pendingWrite.Task);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            pendingWrite.TrySetException(new ObjectDisposedException(nameof(NativeTimeoutStream)));
            _ = pendingWrite.Task.Exception;
            incoming.Writer.TryComplete();
            written.Writer.TryComplete();
            base.Dispose(disposing);
        }

        public override void Flush() { }
        public ValueTask Send(byte[] bytes) => incoming.Writer.WriteAsync(bytes);
        public async Task<byte[]> ReceiveWritten(TimeSpan? timeout = null) =>
            await written.Reader.ReadAsync().AsTask().WaitAsync(timeout ?? TimeSpan.FromSeconds(3));
        public bool TryReceiveWritten(out byte[]? bytes) => written.Reader.TryRead(out bytes);
        public int BufferedInputBytes => incoming.Reader.TryPeek(out var bytes) ? bytes.Length : 0;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var bytes = await incoming.Reader.ReadAsync(cancellationToken);
            ReadBytes += bytes.Length;
            bytes.AsMemory().CopyTo(buffer);
            return bytes.Length;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class NativeEndpointFactory : IByteBridgeEndpointFactory
    {
        public NativeTimeoutStream Legacy { get; } = new() { FailWrite = true };
        public NativeTimeoutStream Radio { get; } = new();

        public ValueTask<IByteBridgeEndpoint> OpenAsync(string portName, SerialSettings settings,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = portName == "COM101" ? Radio : Legacy;
            return ValueTask.FromResult<IByteBridgeEndpoint>(
                new SerialPortByteBridgeEndpointFactory.SerialPortByteBridgeEndpoint(
                    stream, stream, () => stream.BufferedInputBytes));
        }
    }
}
