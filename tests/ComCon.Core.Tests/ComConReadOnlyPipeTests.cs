using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;
using Microsoft.Win32.SafeHandles;

namespace ComCon.Core.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class ComConReadOnlyPipeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentLogRotatesAtByteLimitAndRetainsOnlyTwoArchives(bool powershell)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_logrotation_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        try
        {
            WriteFaultLogSeed(path, "active");
            WriteFaultLogSeed(path + ".1", "archive1");
            WriteFaultLogSeed(path + ".2", "archive2");
            await AppendDevelopmentFaultAsync(path, powershell);
            var files = Directory.GetFiles(directory);
            Assert.HasCount(3, files);
            Assert.IsTrue(files.All(file => new FileInfo(file).Length <= DevelopmentFaultLog.MaximumFileBytes));
            Assert.IsLessThanOrEqualTo(3L * DevelopmentFaultLog.MaximumFileBytes, files.Sum(file => new FileInfo(file).Length));
            using var current = JsonDocument.Parse(File.ReadAllText(path));
            Assert.AreEqual(new string('\u00e9', 2048), current.RootElement.GetProperty("message").GetString());
            Assert.AreEqual(2, current.RootElement.GetProperty("profileId").GetInt32());
            Assert.IsTrue(current.RootElement.TryGetProperty("timestampUtc", out _));
            Assert.AreEqual((byte)'{', File.ReadAllBytes(path)[0]);
            using var newest = JsonDocument.Parse(File.ReadAllText(path + ".1"));
            using var oldest = JsonDocument.Parse(File.ReadAllText(path + ".2"));
            Assert.AreEqual("active", newest.RootElement.GetProperty("marker").GetString());
            Assert.AreEqual("archive1", oldest.RootElement.GetProperty("marker").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentLogTrimsOversizedLegacyFilesToRecentCompleteUtf8Records(bool powershell)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_loglegacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        try
        {
            foreach (var file in new[] { path, path + ".1", path + ".2" })
            {
                WriteFaultLogSeed(file, "discarded");
                var recent = JsonSerializer.Serialize(new { marker = "recent_\u00e9" },
                    new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
                File.AppendAllText(file, recent + Environment.NewLine, new UTF8Encoding(false));
            }
            await AppendDevelopmentFaultAsync(path, powershell);
            foreach (var file in Directory.GetFiles(directory))
            {
                Assert.IsLessThanOrEqualTo((long)DevelopmentFaultLog.MaximumFileBytes, new FileInfo(file).Length);
                var lines = File.ReadAllLines(file, Encoding.UTF8);
                using var recent = JsonDocument.Parse(lines[0]);
                Assert.AreEqual("recent_\u00e9", recent.RootElement.GetProperty("marker").GetString());
                foreach (var line in lines)
                {
                    using var record = JsonDocument.Parse(line);
                    Assert.AreEqual(JsonValueKind.Object, record.RootElement.ValueKind);
                }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentLogRejectsIncompleteOversizedHistoryWithoutSilentlyDiscardingIt(bool powershell)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_logpartial_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        try
        {
            var contents = new string('x', DevelopmentFaultLog.MaximumFileBytes + 1);
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            if (powershell)
            {
                var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
                var result = await RunPowerShellAsync($$"""
                    . '{{helper}}'
                    $ErrorActionPreference = 'Continue'
                    $failed = $false
                    try { Write-ComConDevelopmentFaultLog -Path '{{path.Replace("'", "''")}}' -ProfileId 2 -Message 'fault' }
                    catch { if ($_.Exception.Message -notmatch 'incomplete record') { throw }; $failed = $true }
                    if (-not $failed) { throw 'Malformed oversized history accepted' }
                    'partial-rejected'
                    """);
                Assert.AreEqual("partial-rejected", result.Trim());
            }
            else
            {
                var failure = Assert.ThrowsExactly<IOException>(() => new DevelopmentFaultLog(path).Append("test", 2, "fault"));
                StringAssert.Contains(failure.Message, "incomplete record");
            }
            Assert.AreEqual(contents, File.ReadAllText(path));
            Assert.HasCount(1, Directory.GetFiles(directory));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentLogRotationFailureIsExplicitAndPreservesExistingHistory(bool powershell)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_loglocked_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        try
        {
            foreach (var file in new[] { path, path + ".1", path + ".2" }) WriteFaultLogSeed(file, "unchanged");
            using (var locked = new FileStream(path + ".2", FileMode.Open, FileAccess.Read, FileShare.None))
            {
                if (powershell)
                {
                    var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
                    var result = await RunPowerShellAsync($$"""
                        . '{{helper}}'
                        $ErrorActionPreference = 'Continue'
                        $failed = $false
                        try { Write-ComConDevelopmentFaultLog -Path '{{path.Replace("'", "''")}}' -ProfileId 2 -Message 'fault' }
                        catch {
                            if ($_.Exception -isnot [System.IO.IOException] -and
                                $_.Exception.InnerException -isnot [System.IO.IOException]) { throw }
                            $failed = $true
                        }
                        if (-not $failed) { throw 'Locked archive failure silently accepted' }
                        'locked-rejected'
                        """);
                    Assert.AreEqual("locked-rejected", result.Trim());
                }
                else
                    Assert.ThrowsExactly<IOException>(() => new DevelopmentFaultLog(path).Append("test", 2, "fault"));
            }
            foreach (var file in Directory.GetFiles(directory))
            {
                Assert.AreEqual(DevelopmentFaultLog.MaximumFileBytes, new FileInfo(file).Length);
                using var record = JsonDocument.Parse(File.ReadAllText(file));
                Assert.AreEqual("unchanged", record.RootElement.GetProperty("marker").GetString());
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task DevelopmentLogSerializesPowerShellAndCSharpWritersAcrossRotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_logmixed_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        var ready = Path.Combine(directory, "ready");
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        try
        {
            WriteFaultLogSeed(path, "seed");
            var script = RunPowerShellAsync($$"""
                . '{{helper}}'
                [System.IO.File]::WriteAllText('{{ready.Replace("'", "''")}}', 'ready')
                for ($i = 0; $i -lt 128; $i++) {
                    Write-ComConDevelopmentFaultLog -Path '{{path.Replace("'", "''")}}' -ProfileId 2 -Message "ps_$i"
                }
                """);
            var core = Task.Run(async () =>
            {
                var clock = Stopwatch.StartNew();
                while (!File.Exists(ready))
                {
                    if (clock.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("PowerShell log writer readiness timeout.");
                    await Task.Delay(10);
                }
                Parallel.For(0, 128, index => new DevelopmentFaultLog(path).Append("core", 2, $"core_{index}"));
            });
            await Task.WhenAll(script, core);
            var lines = File.ReadAllLines(path);
            Assert.HasCount(256, lines);
            var messages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in lines)
            {
                using var record = JsonDocument.Parse(line);
                Assert.IsTrue(messages.Add(record.RootElement.GetProperty("message").GetString()!));
            }
            foreach (var file in new[] { path, path + ".1" })
                Assert.IsLessThanOrEqualTo((long)DevelopmentFaultLog.MaximumFileBytes, new FileInfo(file).Length);
            Assert.IsFalse(File.Exists(path + ".2"));
            using var retained = JsonDocument.Parse(File.ReadAllText(path + ".1"));
            Assert.AreEqual("seed", retained.RootElement.GetProperty("marker").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentLogLockTimeoutIsExplicitAndRetryWorks(bool powershell)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_logtimeout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        try
        {
            await Task.Run(() =>
            {
                using var mutex = CreateFaultLogMutex(path);
                Assert.IsTrue(mutex.WaitOne(0));
                try
                {
                    if (powershell)
                    {
                        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
                        var result = RunPowerShellAsync($$"""
                            . '{{helper}}'
                            $failed = $false
                            try { Write-ComConDevelopmentFaultLog -Path '{{path.Replace("'", "''")}}' -ProfileId 2 -Message 'fault' }
                            catch { if ($_.Exception.Message -notmatch 'Timed out waiting') { throw }; $failed = $true }
                            if (-not $failed) { throw 'Lock contention silently accepted' }
                            'timeout-rejected'
                            """).GetAwaiter().GetResult();
                        Assert.AreEqual("timeout-rejected", result.Trim());
                    }
                    else
                    {
                        var failure = Task.Factory.StartNew(() => Assert.ThrowsExactly<IOException>(() =>
                            new DevelopmentFaultLog(path).Append("test", 2, "fault")), CancellationToken.None,
                            TaskCreationOptions.LongRunning, TaskScheduler.Default).GetAwaiter().GetResult();
                        StringAssert.Contains(failure.Message, "Timed out waiting");
                    }
                    Assert.IsFalse(File.Exists(path));
                }
                finally { mutex.ReleaseMutex(); }
            });
            await AppendDevelopmentFaultAsync(path, powershell);
            Assert.HasCount(1, File.ReadAllLines(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentLogAbandonedLockRequiresExplicitRetry(bool powershell)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ComConTest_logabandoned_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault.jsonl");
        try
        {
            using var mutex = CreateFaultLogMutex(path);
            var owner = new Thread(() => mutex.WaitOne());
            owner.Start();
            owner.Join();
            if (powershell)
            {
                var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
                var result = await RunPowerShellAsync($$"""
                    . '{{helper}}'
                    $failed = $false
                    try { Write-ComConDevelopmentFaultLog -Path '{{path.Replace("'", "''")}}' -ProfileId 2 -Message 'fault' }
                    catch { if ($_.Exception.Message -notmatch 'lock was abandoned') { throw }; $failed = $true }
                    if (-not $failed) { throw 'Abandoned lock silently accepted' }
                    'abandoned-rejected'
                    """);
                Assert.AreEqual("abandoned-rejected", result.Trim());
            }
            else
            {
                var failure = Assert.ThrowsExactly<IOException>(() => new DevelopmentFaultLog(path).Append("test", 2, "fault"));
                StringAssert.Contains(failure.Message, "lock was abandoned");
            }
            Assert.IsFalse(File.Exists(path));
            await AppendDevelopmentFaultAsync(path, powershell);
            Assert.HasCount(1, File.ReadAllLines(path));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static Mutex CreateFaultLogMutex(string path)
    {
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant())));
        return new Mutex(false, "ComConDevelopmentFaultLog_" + identity);
    }

    private static void WriteFaultLogSeed(string path, string marker)
    {
        var json = JsonSerializer.Serialize(new { marker });
        File.WriteAllText(path, new string(' ', DevelopmentFaultLog.MaximumFileBytes -
            Encoding.UTF8.GetByteCount(json + Environment.NewLine)) + json + Environment.NewLine, new UTF8Encoding(false));
    }

    private static async Task AppendDevelopmentFaultAsync(string path, bool powershell)
    {
        if (!powershell)
        {
            new DevelopmentFaultLog(path).Append("test", 2, new string('\u00e9', 3000));
            return;
        }
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var result = await RunPowerShellAsync($$"""
            . '{{helper}}'
            Write-ComConDevelopmentFaultLog -Path '{{path.Replace("'", "''")}}' -ProfileId 2 -Message (([string][char]0x00E9) * 3000)
            """);
        Assert.AreEqual("", result.Trim());
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(130)]
    public async Task HscExplicitDisplayResetSkipsRetainedHistoryAndAwaitsNewFrames(int retainedFrames)
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await bridge.StartAsync(CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ });
        await bridge.Telemetry.EnableSimulationAsync();
        var journal = bridge.Telemetry.SimulationJournal!;
        for (var index = 0; index < retainedFrames; index++)
            journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        await using var fixture = new HostFixture(telemetry: bridge.Telemetry);
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            $old = New-ComConCivDisplayState -LifecycleBound -MaximumAgeMs 60000
            $old.requiresReset = $true; $old.readings['pttState'] = 'old reading'
            $fresh = Reset-ComConCivDisplayState $old '{{fixture.Name}}'
            if (-not $old.requiresReset -or $old.telemetryEnabled -or $old.readings.Count -ne 0) {
                throw 'Reset did not retire old display'
            }
            $view = Receive-ComConCivDisplayState $fresh '{{fixture.Name}}'
            if ($view.status -cne 'Unavailable' -or $view.reason -cne 'AWAITING_FRESH_OBSERVATIONS' -or
                $view.readings.Count -ne 0 -or $view.nextObservationId -cne '{{retainedFrames}}' -or
                $view.requiresReset) { throw 'Reset replayed history or failed gap skip' }
            Get-ComConCivDisplaySnapshot $fresh | ConvertTo-Json -Depth 8 -Compress
            """);
        using var result = JsonDocument.Parse(output);
        var snapshot = result.RootElement;
        journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0001FD"));
        var next = await RunPowerShellAsync($$"""
            . '{{helper}}'
            $state = New-ComConCivDisplayState -LifecycleBound -MaximumAgeMs 60000 `
                -ServerInstanceId '{{snapshot.GetProperty("serverInstanceId").GetString()}}' `
                -CaptureId '{{snapshot.GetProperty("captureId").GetString()}}' `
                -ProfileGeneration '{{snapshot.GetProperty("profileGeneration").GetString()}}' `
                -OwnershipEpoch '{{snapshot.GetProperty("ownershipEpoch").GetString()}}' `
                -AfterObservationId '{{retainedFrames}}'
            $view = Receive-ComConCivDisplayState $state '{{fixture.Name}}'
            if ($view.status -cne 'Available' -or $view.readings.pttState.value -ne 1 -or
                $view.readings.pttState.observationId -cne '{{retainedFrames + 1}}') { throw 'New frame not admitted after reset' }
            'explicit-reset-fresh-only'
            """);
        Assert.AreEqual("explicit-reset-fresh-only", next.Trim());
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        Assert.HasCount(2, endpoints.OpenedPorts);
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HscDisplayResetFailureClearsOldDataAndRemainsResetRequired(bool failBaseline)
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await bridge.StartAsync(CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ });
        await bridge.Telemetry.EnableSimulationAsync();
        await using var fixture = new HostFixture(telemetry: bridge.Telemetry);
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var mock = failBaseline
            ? "function Get-ComConIpcObservations { throw 'IDENTITY_CHANGED' }"
            : "function Get-ComConIpcTelemetryStatus { throw 'fake reset transport failure' }";
        var expectedError = failBaseline ? "IDENTITY_CHANGED" : "fake reset transport failure";
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            {{mock}}
            $state = New-ComConCivDisplayState -LifecycleBound
            $state.telemetryEnabled = $true; $state.readings['pttState'] = 'old reading'
            try { $null = Reset-ComConCivDisplayState $state '{{fixture.Name}}'; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -notmatch '{{expectedError}}') { throw } }
            if (-not $state.requiresReset -or $state.telemetryEnabled -or $state.readings.Count -ne 0) {
                throw 'Failed reset retained old success'
            }
            'failed-reset-cleared'
            """);
        Assert.AreEqual("failed-reset-cleared", output.Trim());
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task HscHostDisconnectAndRestartRequireExplicitNewDisplayState(bool detectDisconnect)
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await bridge.StartAsync(CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ });
        await bridge.Telemetry.EnableSimulationAsync();
        bridge.Telemetry.SimulationJournal!.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        var name = "ComConTest_restart_" + Guid.NewGuid().ToString("N");
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        string pipeIdentity;
        await using (var first = new HostFixture(telemetry: bridge.Telemetry, pipeName: name))
        {
            var output = await RunPowerShellAsync($$"""
                . '{{helper}}'
                $state = New-ComConCivDisplayState -LifecycleBound -MaximumAgeMs 60000
                $view = Receive-ComConCivDisplayState $state '{{name}}'
                if ($view.status -cne 'Available') { throw 'Initial read failed' }
                $state.serverInstanceId
                """);
            pipeIdentity = output.Trim();
        }
        var capture = bridge.Telemetry.Snapshot;
        var seed = $$"""
            $state = New-ComConCivDisplayState -LifecycleBound -MaximumAgeMs 60000 `
                -ServerInstanceId '{{pipeIdentity}}' -CaptureId '{{capture.ServerInstanceId}}' `
                -ProfileGeneration '{{capture.ProfileGeneration}}' -OwnershipEpoch '{{capture.OwnershipEpoch}}' `
                -AfterObservationId '1'
            $state.telemetryEnabled = $true
            $state.readings['pttState'] = 'old reading'
            """;
        if (detectDisconnect)
        {
            var disconnected = await RunPowerShellAsync($$"""
                . '{{helper}}'
                {{seed}}
                try { $null = Receive-ComConCivDisplayState $state '{{name}}' -TimeoutMs 100; throw 'Unexpected success' }
                catch { if ($_.Exception.Message -ceq 'Unexpected success') { throw } }
                if (-not $state.requiresReset -or $state.telemetryEnabled -or $state.readings.Count -ne 0) {
                    throw 'Disconnect retained display or failed to latch reset'
                }
                function Get-ComConIpcTelemetryStatus { throw 'Unexpected reconnect IO' }
                try { $null = Receive-ComConCivDisplayLifecycle $state '{{name}}'; throw 'Unexpected success' }
                catch { if ($_.Exception.Message -notmatch 'RESET_REQUIRED') { throw } }
                'disconnect-latched'
                """);
            Assert.AreEqual("disconnect-latched", disconnected.Trim());
        }
        await using var restarted = new HostFixture(telemetry: bridge.Telemetry, pipeName: name);
        var result = await RunPowerShellAsync($$"""
            . '{{helper}}'
            {{seed}}
            $state.requiresReset = ${{detectDisconnect.ToString().ToLowerInvariant()}}
            try { $null = Receive-ComConCivDisplayState $state '{{name}}'; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -notmatch '{{(detectDisconnect ? "RESET_REQUIRED" : "IDENTITY_CHANGED")}}') { throw } }
            if (-not $state.requiresReset -or $state.telemetryEnabled -or $state.readings.Count -ne 0) {
                throw 'Restart merged old display history'
            }
            $fresh = Reset-ComConCivDisplayState $state '{{name}}'
            $view = Receive-ComConCivDisplayState $fresh '{{name}}'
            if ($view.status -cne 'Unavailable' -or $view.readings.Count -ne 0 -or
                $view.reason -cne 'AWAITING_FRESH_OBSERVATIONS' -or
                $fresh.serverInstanceId -ceq '{{pipeIdentity}}' -or
                $fresh.lifecycleIdentity -cne '{{capture.ServerInstanceId}}') { throw 'Explicit reset did not bind new host' }
            'restart-reset-ok'
            """);
        Assert.AreEqual("restart-reset-ok", result.Trim());
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        Assert.IsEmpty(restarted.Failures);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("Off")]
    [DataRow("Available")]
    [DataRow("Faulted")]
    public async Task OfflinePreviewWorkerRendersBoundLifecycleWithoutStationAccess(string expected)
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await bridge.StartAsync(CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ });
        if (expected != "Off")
        {
            await bridge.Telemetry.EnableSimulationAsync();
            var bytes = expected == "Faulted" ? new byte[] { 0xFC }
                : Convert.FromHexString("FEFEE07A1C0000FDFEFEE07A030040071400FD");
            await endpoints.Get("COM3").SendAsync(bytes);
            await endpoints.Get("COM31").ReadWrittenAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (expected == "Faulted" ? bridge.Telemetry.Snapshot.Fault is null
                : bridge.Telemetry.SimulationJournal!.ReadAfter(0).Observations.Count == 0)
                await Task.Delay(5, deadline.Token);
        }
        await using var fixture = new HostFixture(telemetry: bridge.Telemetry);
        var script = Path.Combine(AppContext.BaseDirectory, "ComCon-OfflinePreview.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync(
            $"& '{script}' -SelfTest -LifecycleBound -TestPipeName '{fixture.Name}' " +
            $"-MaximumAgeMs 60000 -SelfTestExpectedState '{expected}' -SelfTestReset" +
            (expected == "Available" ? " -SelfTestExpectedFrequencyHz 14074000" : ""));
        StringAssert.Contains(output, "offline-preview-worker-ok");
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        Assert.AreEqual(expected == "Available", bridge.Telemetry.Snapshot.Enabled);
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    public async Task HscBoundDisplayRejectsUnvalidatedCaptureAndLegacyDowngrade()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            function Batch {
                [pscustomobject]@{
                    serverInstanceId = 'pipe-a'; profileId = 1; captureId = 'capture-a'
                    profileGeneration = '7'; ownershipEpoch = '9'
                    implementation = 'offlinePassiveLifecycleSimulation'; source = 'simulatedPassiveRx'
                    safetyConfirmation = $false; nextObservationId = '1'; latestObservationId = '1'
                    gap = $false; hasMore = $false
                    observations = @([pscustomobject]@{ observationId = '1'; hex = 'FEFEE07A1C0000FD'; ageMs = 0; source = 'simulatedPassiveRx' })
                }
            }
            function State {
                $s = New-ComConCivDisplayState -LifecycleBound
                $s.telemetryEnabled = $true
                $s.serverInstanceId = 'pipe-a'; $s.profileGeneration = '7'; $s.ownershipEpoch = '9'
                $s.lifecycleIdentity = 'capture-a'; $s.lifecycleGeneration = '7'; $s.lifecycleEpoch = '9'
                return $s
            }
            $state = State
            $view = Update-ComConCivDisplayState $state (Batch)
            if ($view.captureId -cne 'capture-a' -or $view.profileGeneration -cne '7' -or
                $view.ownershipEpoch -cne '9' -or $view.readings.pttState.value -ne 0) { throw 'Bound snapshot invalid' }
            foreach ($kind in @('capture', 'legacy', 'unvalidated')) {
                $state = State
                $batch = Batch
                if ($kind -ceq 'capture') { $batch.captureId = 'capture-b' }
                elseif ($kind -ceq 'legacy') {
                    $batch.implementation = 'offlinePassiveRxSimulation'
                    $batch.profileGeneration = '1'; $batch.ownershipEpoch = '0'
                    $state.profileGeneration = '1'; $state.ownershipEpoch = '0'
                }
                else { $state.lifecycleIdentity = $null }
                $state.readings['pttState'] = 'old reading'
                try { $null = Update-ComConCivDisplayState $state $batch; throw 'Unexpected success' }
                catch { if ($_.Exception.Message -notmatch 'IDENTITY_CHANGED') { throw } }
                if (-not $state.requiresReset -or $state.readings.Count -ne 0) { throw 'Unsafe batch accepted' }
            }
            try { $null = New-ComConCivDisplayState -LifecycleBound -AfterObservationId '1' -ServerInstanceId pipe-a; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -notmatch 'capture identity') { throw } }
            $state = New-ComConCivDisplayState -LifecycleBound -AfterObservationId '1' `
                -ServerInstanceId pipe-a -CaptureId capture-a -ProfileGeneration '7' -OwnershipEpoch '9'
            if ($state.telemetryEnabled -or $state.lifecycleIdentity -cne 'capture-a') { throw 'Resync implicitly enabled capture' }
            'capture-guards-ok'
            """);
        Assert.AreEqual("capture-guards-ok", output.Trim());
    }

    [TestMethod]
    public async Task HscBoundObservationsUseCurrentCaptureAndClearOnDisableReenableAndStop()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await using var fixture = new HostFixture(telemetry: bridge.Telemetry);
        var profile = CommanderByteBridgeHostTests.CreateProfile() with { Framing = FramingMode.Civ };
        await bridge.StartAsync(profile);
        await bridge.Telemetry.EnableSimulationAsync();
        await endpoints.Get("COM3").SendAsync(Convert.FromHexString("FEFEE07A1C0000FD"));
        await endpoints.Get("COM31").ReadWrittenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (bridge.Telemetry.SimulationJournal!.ReadAfter(0).Observations.Count == 0)
            await Task.Delay(5, deadline.Token);
        var capture = bridge.Telemetry.Snapshot;
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var seed = $$"""
            $state = New-ComConCivDisplayState -LifecycleBound -MaximumAgeMs 60000
            $null = Receive-ComConCivDisplayLifecycle $state '{{fixture.Name}}'
            """;
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            {{seed}}
            $snapshot = Receive-ComConCivDisplayState $state '{{fixture.Name}}'
            if ($snapshot.status -cne 'Available' -or $snapshot.readings.pttState.value -ne 0 -or
                $state.lifecycleIdentity -cne '{{capture.ServerInstanceId}}' -or
                $state.profileGeneration -cne '{{capture.ProfileGeneration}}' -or
                $state.ownershipEpoch -cne '{{capture.OwnershipEpoch}}') { throw 'Capture-bound decode failed' }
            $first = $state.readings.pttState
            $null = Receive-ComConCivDisplayState $state '{{fixture.Name}}'
            if ($state.readings.pttState.receiptTicks -ne $first.receiptTicks) { throw 'Empty poll refreshed data' }
            Get-ComConIpcTelemetryStatus -TestPipeName '{{fixture.Name}}' | ConvertTo-Json -Depth 8 -Compress
            """);
        using var status = JsonDocument.Parse(output);
        var pipeIdentity = status.RootElement.GetProperty("serverInstanceId").GetString();
        var oldState = $$"""
            $state = New-ComConCivDisplayState -LifecycleBound -MaximumAgeMs 60000 `
                -ServerInstanceId '{{pipeIdentity}}' -CaptureId '{{capture.ServerInstanceId}}' `
                -ProfileGeneration '{{capture.ProfileGeneration}}' -OwnershipEpoch '{{capture.OwnershipEpoch}}' `
                -AfterObservationId '1'
            $state.readings['ptt'] = 'old reading'
            """;
        async Task CheckRejectedAsync(string reason)
        {
            var result = await RunPowerShellAsync($$"""
                . '{{helper}}'
                {{oldState}}
                try { $null = Receive-ComConCivDisplayState $state '{{fixture.Name}}'; throw 'Unexpected success' }
                catch { if ($_.Exception.Message -notmatch '{{reason}}') { throw } }
                if ($state.telemetryEnabled -or $state.readings.Count -ne 0) { throw 'Old reading survived' }
                'old-capture-cleared'
                """);
            Assert.AreEqual("old-capture-cleared", result.Trim());
        }
        await bridge.Telemetry.DisableAsync();
        await CheckRejectedAsync("DISPLAY_DISABLED");
        await bridge.Telemetry.EnableSimulationAsync();
        bridge.Telemetry.SimulationJournal!.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        await CheckRejectedAsync("IDENTITY_CHANGED");
        var fresh = await RunPowerShellAsync($$"""
            . '{{helper}}'
            {{seed}}
            $view = Receive-ComConCivDisplayState $state '{{fixture.Name}}'
            if ($view.status -cne 'Available' -or $state.lifecycleIdentity -ceq '{{capture.ServerInstanceId}}') {
                throw 'Fresh state did not bind new capture'
            }
            'new-capture-ok'
            """);
        Assert.AreEqual("new-capture-ok", fresh.Trim());
        await bridge.StopAsync();
        var stopped = await RunPowerShellAsync($$"""
            . '{{helper}}'
            {{seed}}
            if ($state.reason -cne 'TELEMETRY_OFF' -or $state.telemetryEnabled) { throw 'Stopped capture enabled' }
            'stopped-off'
            """);
        Assert.AreEqual("stopped-off", stopped.Trim());
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    public async Task LifecyclePipeRejectsStationCompositionAndMixedObservationFeed()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var station = new CommanderByteBridgeHost(endpoints);
        await using var simulation = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConReadOnlyPipeHost(1, _ => { }, telemetry: simulation.Telemetry));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConReadOnlyPipeHost(1, _ => { }, "ComConTest_station", telemetry: station.Telemetry));
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConReadOnlyPipeHost(1, _ => { }, "ComConTest_mixed",
                new SimulatedReceiveJournal(new StopwatchClock()), simulation.Telemetry));
        Assert.IsEmpty(endpoints.OpenedPorts);
    }

    [TestMethod]
    public async Task HscLifecyclePollingReadsOffObservingFaultStopAndChangedProfileOverProtectedPipe()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
        await using var fixture = new HostFixture(telemetry: bridge.Telemetry);
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        async Task<JsonElement> PollAsync(string setup = "")
        {
            var output = await RunPowerShellAsync($$"""
                . '{{helper}}'
                $state = New-ComConCivDisplayState
                {{setup}}
                Receive-ComConCivDisplayLifecycle -State $state -TestPipeName '{{fixture.Name}}' |
                    ConvertTo-Json -Depth 8 -Compress
                """);
            using var document = JsonDocument.Parse(output);
            return document.RootElement.Clone();
        }
        var off = await PollAsync();
        Assert.AreEqual("TELEMETRY_OFF", off.GetProperty("reason").GetString());
        Assert.IsFalse(off.GetProperty("telemetryEnabled").GetBoolean());
        Assert.IsEmpty(endpoints.OpenedPorts);
        var profile = CommanderByteBridgeHostTests.CreateProfile() with
        {
            Label = "Fake lifecycle pipe", Framing = FramingMode.Civ
        };
        await bridge.StartAsync(profile);
        await bridge.Telemetry.EnableSimulationAsync();
        var observing = await PollAsync();
        Assert.IsTrue(observing.GetProperty("telemetryEnabled").GetBoolean());
        Assert.IsFalse(observing.GetProperty("safetyConfirmation").GetBoolean());
        var prior = JsonSerializer.Serialize(bridge.Telemetry.Snapshot,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var seed = $"$null = Update-ComConCivDisplayLifecycle $state ('{prior}' | ConvertFrom-Json); " +
            "$state.readings['ptt'] = 'old reading';";
        await endpoints.Get("COM3").SendAsync([0xFC]);
        await endpoints.Get("COM31").ReadWrittenAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (bridge.Telemetry.Snapshot.Fault is null) await Task.Delay(5, deadline.Token);
        var fault = await PollAsync(seed);
        StringAssert.Contains(fault.GetProperty("reason").GetString(), bridge.Telemetry.Snapshot.Fault);
        Assert.AreEqual(0, fault.GetProperty("readings").EnumerateObject().Count());
        Assert.IsFalse(fault.GetProperty("telemetryEnabled").GetBoolean());
        Assert.AreEqual(CommanderBridgeState.Running, bridge.State);
        await bridge.StopAsync();
        var stopped = await PollAsync(seed);
        Assert.AreEqual("TELEMETRY_OFF", stopped.GetProperty("reason").GetString());
        Assert.AreEqual(0, stopped.GetProperty("readings").EnumerateObject().Count());
        await bridge.StartAsync(profile with { Id = 2, VirtualPort = "COM32", CommanderPort = "COM22" });
        var changed = await RunPowerShellAsync($$"""
            . '{{helper}}'
            $state = New-ComConCivDisplayState
            {{seed}}
            try { $null = Receive-ComConCivDisplayLifecycle $state '{{fixture.Name}}'; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -notmatch 'PROFILE_CHANGED') { throw } }
            if (-not $state.requiresReset -or $state.telemetryEnabled -or $state.readings.Count -ne 0) {
                throw 'Profile change retained old display'
            }
            'profile-change-cleared'
            """);
        Assert.AreEqual("profile-change-cleared", changed.Trim());
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    public async Task HscLifecyclePollingRejectsStationNamesMalformedMetadataAndClearsOnTransportFailure()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            try { $null = Get-ComConIpcTelemetryStatus -TestPipeName ComConPipe_Profile1; throw 'Unexpected success' }
            catch [System.Management.Automation.ParameterBindingException] {}
            try { $null = Invoke-ComConReadOnlyQuery -Operation GET_TELEMETRY_STATUS; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -notmatch 'isolated test pipe') { throw } }
            function Get-ComConIpcTelemetryStatus { [pscustomobject]@{ telemetry = [pscustomobject]@{ state = 'Observing' } } }
            $state = New-ComConCivDisplayState
            $state.readings['ptt'] = 'old reading'
            try { $null = Receive-ComConCivDisplayLifecycle $state ComConTest_invalid; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -ceq 'Unexpected success') { throw } }
            if ($state.telemetryEnabled -or $state.readings.Count -ne 0) { throw 'Malformed lifecycle retained display' }
            function Get-ComConIpcTelemetryStatus { throw 'fake transport fault' }
            $state = New-ComConCivDisplayState
            $state.readings['ptt'] = 'old reading'
            try { $null = Receive-ComConCivDisplayLifecycle $state ComConTest_invalid; throw 'Unexpected success' }
            catch { if ($_.Exception.Message -notmatch 'fake transport fault') { throw } }
            if ($state.telemetryEnabled -or $state.readings.Count -ne 0) { throw 'Transport fault retained display' }
            'lifecycle-fail-closed'
            """);
        Assert.AreEqual("lifecycle-fail-closed", output.Trim());
    }

    [TestMethod]
    public async Task HscLifecycleOffFaultSwitchAndUnsupportedModelClearDisplayAndLogFaults()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var log = Path.Combine(Path.GetTempPath(), $"ComConTest_hscfault_{Guid.NewGuid():N}.jsonl");
        try
        {
            var output = await RunPowerShellAsync($$"""
                . '{{helper}}'
                function Check($ok, $message) { if (-not $ok) { throw $message } }
                function Lifecycle($phase = 'Off') {
                    [pscustomobject]@{
                        profileId = 1; serverInstanceId = 'capture-a'; profileGeneration = '1'
                        ownershipEpoch = '1'; state = $phase; fault = $null
                        enabled = ($phase -ceq 'Observing'); safetyConfirmation = $false
                        implementation = 'offlinePassiveLifecyclePreparation'
                    }
                }
                function Batch {
                    [pscustomobject]@{
                        serverInstanceId = 'pipe-a'; profileId = 1; profileGeneration = '1'; ownershipEpoch = '0'
                        implementation = 'offlinePassiveRxSimulation'; source = 'simulatedPassiveRx'; safetyConfirmation = $false
                        nextObservationId = '1'; latestObservationId = '1'; gap = $false; hasMore = $false
                        observations = @([pscustomobject]@{ observationId = '1'; hex = 'FEFEE07A1C0000FD'; ageMs = 0; source = 'simulatedPassiveRx' })
                    }
                }
                $state = New-ComConCivDisplayState -FaultLogPath '{{log.Replace("'", "''")}}'
                $null = Update-ComConCivDisplayLifecycle $state (Lifecycle 'Observing')
                $null = Update-ComConCivDisplayState $state (Batch)
                $off = Update-ComConCivDisplayLifecycle $state (Lifecycle)
                Check ($off.reason -ceq 'TELEMETRY_OFF' -and $off.readings.Count -eq 0 -and -not $off.telemetryEnabled) 'OFF retained success'
                function Get-ComConIpcObservations { throw 'unexpected IO' }
                try { $null = Receive-ComConCivDisplayState $state 'ComConTest_off'; throw 'unexpected success' }
                catch { Check ($_.Exception.Message -match 'DISPLAY_DISABLED') 'disabled state attempted IO' }
                $fault = Lifecycle 'Faulted'
                $fault.fault = 'offline framing fault'
                $null = Update-ComConCivDisplayLifecycle $state $fault
                $null = Update-ComConCivDisplayLifecycle $state $fault
                Check ($state.readings.Count -eq 0 -and $state.reason -match 'RECEIVE_FAULT') 'fault not shown'
                $logged = @(Get-Content -LiteralPath '{{log.Replace("'", "''")}}' | ForEach-Object { $_ | ConvertFrom-Json })
                Check (@($logged | Where-Object { $_.message -match 'offline framing fault' }).Count -eq 1) 'fault log repeated or absent'
                $changed = Lifecycle 'Observing'
                $changed.serverInstanceId = 'capture-b'
                try { $null = Update-ComConCivDisplayLifecycle $state $changed; throw 'unexpected success' }
                catch { Check ($_.Exception.Message -match 'IDENTITY_CHANGED') 'new capture not rejected' }
                Check ($state.requiresReset -and -not $state.telemetryEnabled) 'capture mismatch not latched'
                $state = New-ComConCivDisplayState
                $null = Update-ComConCivDisplayState $state (Batch)
                $changed = Lifecycle
                $changed.profileId = 2
                try { $null = Update-ComConCivDisplayLifecycle $state $changed; throw 'unexpected success' }
                catch { Check ($_.Exception.Message -match 'PROFILE_CHANGED') 'wrong profile not rejected' }
                Check ($state.readings.Count -eq 0 -and $state.requiresReset) 'profile switch retained data'
                $state = New-ComConCivDisplayState -Model TS590SG
                $unsupported = Update-ComConCivDisplayLifecycle $state (Lifecycle 'Observing')
                Check ($unsupported.reason -ceq 'UNSUPPORTED_MODEL' -and -not $unsupported.telemetryEnabled) 'unsupported model reused decoder'
                try { $null = Update-ComConCivDisplayState $state (Batch); throw 'unexpected success' }
                catch { Check ($_.Exception.Message -match 'DISPLAY_DISABLED') 'unsupported decoder ran' }
                'lifecycle-hsc-ok'
                """);
            Assert.AreEqual("lifecycle-hsc-ok", output.Trim());
        }
        finally { File.Delete(log); }
    }

    [TestMethod]
    public async Task OfflinePreviewRendersAndExpiresWithoutStationAccess()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "ComCon-OfflinePreview.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($"& '{script}' -SelfTest");
        Assert.AreEqual("offline-preview-ok", output.Trim());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task OfflinePreviewWorkerUsesProtectedTestPipeWithoutShowingStationWindow()
    {
        var journal = new SimulatedReceiveJournal(new StopwatchClock());
        journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        await using var fixture = new HostFixture(journal);
        var script = Path.Combine(AppContext.BaseDirectory, "ComCon-OfflinePreview.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($"& '{script}' -SelfTest -TestPipeName '{fixture.Name}' -MaximumAgeMs 60000");
        StringAssert.Contains(output, "offline-preview-worker-ok");
    }

    [TestMethod]
    public async Task OfflinePreviewRefusesStationPipeBeforeGuiOrIo()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "ComCon-OfflinePreview.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            try { & '{{script}}' -TestPipeName 'ComConPipe_Profile1'; throw 'Unexpected station acceptance.' }
            catch [System.Management.Automation.ParameterBindingException] { 'station-pipe-rejected' }
            """);
        Assert.AreEqual("station-pipe-rejected", output.Trim());
    }

    [TestMethod]
    public async Task OfflineDisplayStateClearsStaleFaultGapAndChangedIdentity()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            function Check($condition, $message) { if (-not $condition) { throw $message } }
            function Batch($id = '1', $hex = 'FEFEE07A1C0000FD', $age = 0) {
                [pscustomobject]@{
                    serverInstanceId = 'server-a'; profileId = 1; profileGeneration = '1'; ownershipEpoch = '0'
                    implementation = 'offlinePassiveRxSimulation'; source = 'simulatedPassiveRx'; safetyConfirmation = $false
                    nextObservationId = $id; latestObservationId = $id; gap = $false; hasMore = $false
                    observations = @([pscustomobject]@{ observationId = $id; hex = $hex; ageMs = $age; source = 'simulatedPassiveRx' })
                }
            }
            $state = New-ComConCivDisplayState
            $first = Update-ComConCivDisplayState $state (Batch)
            Check ($first.status -ceq 'Available' -and $first.readings.Count -eq 1 -and -not $first.safetyConfirmation) 'fresh display failed'
            $empty = Batch
            $empty.observations = @()
            $same = Update-ComConCivDisplayState $state $empty
            Check ($same.status -ceq 'Available') 'empty poll erased fresh value'
            foreach ($entry in $state.readings.Values) { $entry.ageMs = 1000 }
            $expired = Get-ComConCivDisplaySnapshot $state
            Check ($expired.reason -ceq 'STALE' -and $expired.readings.Count -eq 0) 'exact expiry failed'
            $stale = Update-ComConCivDisplayState $state (Batch '2' 'FEFEE07A1C0000FD' 1000)
            Check ($stale.status -ceq 'Unavailable' -and $stale.readings.Count -eq 0) 'stale ingress accepted'
            $fresh = Update-ComConCivDisplayState $state (Batch '3')
            $echo = Update-ComConCivDisplayState $state (Batch '4' 'FEFE7AE01C00FD')
            Check ($echo.status -ceq 'Available' -and $echo.nextObservationId -ceq '4') 'echo handling failed'
            $bad = Update-ComConCivDisplayState $state (Batch '5' 'FEFEE07A1C0002FD')
            Check ($bad.readings.Count -eq 0 -and $bad.reason -ceq 'VALUE_OUT_OF_RANGE') 'invalid value retained'
            $null = Update-ComConCivDisplayState $state (Batch '6')
            $changed = Batch '1'
            $changed.serverInstanceId = 'server-b'
            try { $null = Update-ComConCivDisplayState $state $changed; throw 'unexpected success' }
            catch { Check ($_.Exception.Message -match 'IDENTITY_CHANGED') 'wrong identity failure' }
            Check ($state.requiresReset -and $state.readings.Count -eq 0 -and $state.nextObservationId -ceq '6') 'identity mixed histories'
            $state = New-ComConCivDisplayState
            $gap = Batch '3'
            $gap.gap = $true
            try { $null = Update-ComConCivDisplayState $state $gap; throw 'unexpected success' }
            catch { Check ($_.Exception.Message -match 'OBSERVATION_GAP') 'wrong gap failure' }
            Check ($state.requiresReset -and $state.readings.Count -eq 0 -and $state.nextObservationId -ceq '0') 'gap advanced cursor'
            $state = New-ComConCivDisplayState -AfterObservationId $state.resynchronizeAfterObservationId -ServerInstanceId $state.serverInstanceId
            $empty = Batch '3'
            $empty.observations = @()
            $emptyDisplay = Update-ComConCivDisplayState $state $empty
            Check ($emptyDisplay.status -ceq 'Unavailable' -and -not $emptyDisplay.requiresReset) 'explicit gap resynchronization failed'
            $nextDisplay = Update-ComConCivDisplayState $state (Batch '4')
            Check ($nextDisplay.status -ceq 'Available') 'resynchronized future reading failed'
            $state = New-ComConCivDisplayState
            $null = Update-ComConCivDisplayState $state (Batch)
            function Get-ComConIpcObservations { throw 'RECEIVE_FAULT: offline fault' }
            try { $null = Receive-ComConCivDisplayState $state 'ComConTest_display'; throw 'unexpected success' }
            catch { Check ($_.Exception.Message -match 'RECEIVE_FAULT') 'fault swallowed' }
            Check ($state.status -ceq 'Unavailable' -and $state.readings.Count -eq 0) 'fault retained success'
            'display-state-ok'
            """);
        Assert.AreEqual("display-state-ok", output.Trim());
    }

    [TestMethod]
    public async Task DisplayLocalAgeIdentityDimensionsAndMalformedBatchFailClosed()
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            function Batch {
                [pscustomobject]@{
                    serverInstanceId = 'server'; profileId = 1; profileGeneration = '1'; ownershipEpoch = '0'
                    implementation = 'offlinePassiveRxSimulation'; source = 'simulatedPassiveRx'; safetyConfirmation = $false
                    nextObservationId = '1'; latestObservationId = '1'; gap = $false; hasMore = $false
                    observations = @([pscustomobject]@{ observationId = '1'; hex = 'FEFEE07A1C0000FD'; ageMs = 0; source = 'simulatedPassiveRx' })
                }
            }
            foreach ($field in @('profileGeneration', 'ownershipEpoch', 'profileId')) {
                $state = New-ComConCivDisplayState
                $null = Update-ComConCivDisplayState $state (Batch)
                $batch = Batch
                if ($field -ceq 'profileId') { $batch.$field = 2 } else { $batch.$field = '2' }
                try { $null = Update-ComConCivDisplayState $state $batch; throw 'unexpected success' }
                catch { if ($_.Exception.Message -ceq 'unexpected success') { throw } }
                if ($state.status -cne 'Unavailable' -or $state.readings.Count -ne 0) { throw 'identity field retained display' }
            }
            $state = New-ComConCivDisplayState
            $null = Update-ComConCivDisplayState $state (Batch)
            $entry = $state.readings['pttState']
            $entry.receiptTicks = [long]([System.Diagnostics.Stopwatch]::GetTimestamp() - [System.Diagnostics.Stopwatch]::Frequency)
            $display = Get-ComConCivDisplaySnapshot $state
            if ($display.reason -cne 'STALE' -or $display.readings.Count -ne 0) { throw 'local age did not expire' }
            $state = New-ComConCivDisplayState
            $null = Update-ComConCivDisplayState $state (Batch)
            $bad = Batch
            $bad.nextObservationId = '3'
            try { $null = Update-ComConCivDisplayState $state $bad; throw 'unexpected success' }
            catch { if ($_.Exception.Message -ceq 'unexpected success') { throw } }
            if ($state.readings.Count -ne 0 -or $state.nextObservationId -cne '1') { throw 'malformed batch advanced state' }
            try { $null = New-ComConCivDisplayState -AfterObservationId '3'; throw 'unexpected success' }
            catch { if ($_.Exception.Message -ceq 'unexpected success') { throw } }
            'display-guards-ok'
            """);
        Assert.AreEqual("display-guards-ok", output.Trim());
    }

    [TestMethod]
    public async Task DisplayReconnectUsesSameHistoryAndRejectsNewServerBeforeOldCursor()
    {
        var journal = new SimulatedReceiveJournal(new StopwatchClock());
        journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        await using var fixture = new HostFixture(journal);
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-CivObservations.ps1").Replace("'", "''");
        var output = await RunPowerShellAsync($$"""
            . '{{helper}}'
            $state = New-ComConCivDisplayState -MaximumAgeMs 60000
            $first = Receive-ComConCivDisplayState $state '{{fixture.Name}}'
            $second = Receive-ComConCivDisplayState $state '{{fixture.Name}}'
            if ($first.status -cne 'Available' -or $second.readings.Count -ne 1 -or $second.nextObservationId -cne '1') { throw 'reconnect failed' }
            $state.serverInstanceId = 'obsolete-server'
            $state.nextObservationId = '9223372036854775807'
            try { $null = Receive-ComConCivDisplayState $state '{{fixture.Name}}'; throw 'unexpected success' }
            catch { if ($_.Exception.Message -notmatch 'IDENTITY_CHANGED') { throw } }
            if (-not $state.requiresReset -or $state.readings.Count -ne 0) { throw 'new identity reused old display' }
            'reconnect-ok'
            """);
        Assert.AreEqual("reconnect-ok", output.Trim());
    }

    [TestMethod]
    public async Task FakeBridgeReceiveReachesProtectedPipeAndStopInvalidatesReplay()
    {
        var endpoints = new CommanderByteBridgeHostTests.FakeEndpointFactory();
        await using var observer = new SimulatedBridgeReceiveObserver(FramingMode.Civ, new StopwatchClock());
        await using var bridge = CommanderByteBridgeHost.CreateSimulation(endpoints, observer);
        await bridge.StartAsync(CommanderByteBridgeHostTests.CreateProfile());
        await using var fixture = new HostFixture(observer.Journal);
        using var client = await fixture.ConnectAsync();
        var hello = await ExchangeAsync(client, Hello);
        var session = hello.GetProperty("result").GetProperty("sessionId").GetString();
        var frame = Convert.FromHexString("FEFEE07A1C0000FD");
        await endpoints.Get("COM3").SendAsync(frame[..4]);
        await endpoints.Get("COM31").ReadWrittenAsync();
        await endpoints.Get("COM3").SendAsync(frame[4..]);
        await endpoints.Get("COM31").ReadWrittenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (observer.Journal.ReadAfter(0).Observations.Count == 0)
            await Task.Delay(5, timeout.Token);
        var request = JsonSerializer.Serialize(new
        {
            v = 1, kind = "request", id = "2", op = "GET_OBSERVATIONS", sessionId = session,
            args = new { afterObservationId = "0" }
        });
        var reply = await ExchangeAsync(client, request);
        Assert.AreEqual(Convert.ToHexString(frame),
            reply.GetProperty("result").GetProperty("observations")[0].GetProperty("hex").GetString());
        Assert.IsFalse(reply.GetProperty("result").GetProperty("safetyConfirmation").GetBoolean());
        await bridge.StopAsync();
        var replay = await ExchangeAsync(client, request);
        Assert.AreEqual("RECEIVE_FAULT", replay.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task ProtectedNativePipeSupportsHelloStatusAndReconnect()
    {
        await using var fixture = new HostFixture();
        string firstSession;
        string instance;
        using (var client = await fixture.ConnectAsync())
        {
            var hello = await ExchangeAsync(client, Hello);
            firstSession = hello.GetProperty("result").GetProperty("sessionId").GetString()!;
            instance = hello.GetProperty("result").GetProperty("serverInstanceId").GetString()!;
            var status = await ExchangeAsync(client, Status(firstSession));
            Assert.IsFalse(status.GetProperty("result").GetProperty("ready").GetBoolean());
            Assert.IsFalse(status.GetProperty("result").GetProperty("hardwareAccess").GetBoolean());
        }
        using (var client = await fixture.ConnectAsync())
        {
            var hello = await ExchangeAsync(client, Hello);
            Assert.AreNotEqual(firstSession, hello.GetProperty("result").GetProperty("sessionId").GetString());
            Assert.AreEqual(instance, hello.GetProperty("result").GetProperty("serverInstanceId").GetString());
        }
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    public async Task FirstInstanceProtectionRejectsCompetingHost()
    {
        await using var fixture = new HostFixture();
        var competing = new ComConReadOnlyPipeHost(1, _ => Assert.Fail("Unexpected connection failure."), fixture.Name);
        await Assert.ThrowsExactlyAsync<Win32Exception>(() => competing.RunAsync(CancellationToken.None));
    }

    [TestMethod]
    public void NativePipeDaclGrantsAccessOnlyToCurrentUserAndHandleIsNotInherited()
    {
        var name = "ComConTest_" + Guid.NewGuid().ToString("N");
        var host = new ComConReadOnlyPipeHost(1, _ => Assert.Fail("Unexpected connection failure."), name);
        using var pipe = host.CreatePipe();
        var error = GetSecurityInfo(pipe.SafePipeHandle, 6, 4,
            out _, out _, out _, out _, out var descriptor);
        Assert.AreEqual(0U, error, "GetSecurityInfo failed.");
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(descriptor)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var security = new RawSecurityDescriptor(bytes, 0);
            Assert.IsNotNull(security.DiscretionaryAcl);
            Assert.HasCount(1, security.DiscretionaryAcl);
            var ace = security.DiscretionaryAcl[0] as CommonAce;
            Assert.IsNotNull(ace);
            Assert.AreEqual(AceQualifier.AccessAllowed, ace.AceQualifier);
            using var identity = WindowsIdentity.GetCurrent();
            Assert.AreEqual(identity.User, ace.SecurityIdentifier);
            Assert.IsTrue(GetHandleInformation(pipe.SafePipeHandle, out var flags));
            Assert.AreEqual(0U, flags & 1U, "Pipe handle must not be inheritable.");
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [TestMethod]
    public async Task MalformedFrameIsReportedAndNextConnectionStillWorks()
    {
        await using var fixture = new HostFixture();
        using (var client = await fixture.ConnectAsync())
            await client.WriteAsync("not-json\n"u8.ToArray());
        await fixture.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var next = await fixture.ConnectAsync();
        var hello = await ExchangeAsync(next, Hello);
        Assert.IsTrue(hello.GetProperty("ok").GetBoolean());
        Assert.IsInstanceOfType<InvalidDataException>(fixture.Failures.Single());
    }

    [TestMethod]
    public async Task PartialFrameHasDeadlineButIdleEstablishedSessionDoesNot()
    {
        await using var fixture = new HostFixture();
        using (var client = await fixture.ConnectAsync())
        {
            var hello = await ExchangeAsync(client, Hello);
            var session = hello.GetProperty("result").GetProperty("sessionId").GetString()!;
            await Task.Delay(2200);
            Assert.IsTrue((await ExchangeAsync(client, Status(session))).GetProperty("ok").GetBoolean());
            await client.WriteAsync("{"u8.ToArray());
            await fixture.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.IsInstanceOfType<TimeoutException>(fixture.Failures.Single());
    }

    [TestMethod]
    public async Task HelloDeadlineClosesSilentPeer()
    {
        await using var fixture = new HostFixture();
        using var client = await fixture.ConnectAsync();
        await fixture.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsInstanceOfType<TimeoutException>(fixture.Failures.Single());
    }

    [TestMethod]
    public async Task PowerShellClientCompletesActualProtectedPipeRoundTrip()
    {
        await using var fixture = new HostFixture();
        var output = await RunPowerShellAsync(
            $"Get-ComConIpcStatus -ProfileId 1 -TestPipeName '{fixture.Name}' | ConvertTo-Json -Depth 8 -Compress");
        using var json = JsonDocument.Parse(output);
        Assert.AreEqual("offlineStatusOnly", json.RootElement.GetProperty("implementation").GetString());
        Assert.AreEqual("Stopped", json.RootElement.GetProperty("state").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("ready").GetBoolean());
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    [DataRow("""{"v":1,"kind":"response","id":"1","ok":true,"result":{"x":1,"x":2}}""")]
    [DataRow("""{"v":1,"v":1,"kind":"response","id":"1","ok":true,"result":{}}""")]
    [DataRow("""{"v":1,"kind":"response","id":1,"ok":true,"result":{}}""")]
    [DataRow("""{"v":1,"kind":"response","id":"1","ok":true,"result":{},"unknown":0}""")]
    [DataRow("""{"v":1,"kind":"response","id":"1","ok":true,"result":{"a":{"b":{"c":{"d":{"e":{"f":{"g":{}}}}}}}}""")]
    public async Task PowerShellResponseValidatorRejectsDuplicatesTypesFieldsAndDepth(string json)
    {
        var escaped = json.Replace("'", "''", StringComparison.Ordinal);
        var output = await RunPowerShellAsync(
            "$rejected = $false; try { Assert-ComConResponseJson " +
            $"-Bytes ([Text.Encoding]::UTF8.GetBytes('{escaped}')) " +
            "} catch { $rejected = $true }; if (-not $rejected) { throw 'Invalid JSON was accepted' }; 'rejected'");
        Assert.AreEqual("rejected", output.Trim());
    }

    [TestMethod]
    public async Task PowerShellClientRejectsControlOperationBeforeIo()
    {
        var output = await RunPowerShellAsync(
            "$p = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ComConTest_unused', " +
            "[System.IO.Pipes.PipeDirection]::InOut); " +
            "try { $rejected = $false; try { Invoke-ComConStatusExchange -Pipe $p -Id '1' " +
            "-TimeoutMs 100 -Request @{op='CAT_REQUEST'} } catch { " +
            "if ($_.Exception.Message -notlike '*permits only read-only status, lifecycle and observation operations*') { throw }; $rejected = $true }; " +
            "if (-not $rejected) { throw 'Control operation accepted' }; 'rejected' } finally { $p.Dispose() }");
        Assert.AreEqual("rejected", output.Trim());
    }

    [TestMethod]
    public async Task PowerShellValidatorAcceptsNestedArraysAndEscapedPropertyNames()
    {
        var output = await RunPowerShellAsync(
            """Assert-ComConResponseJson -Bytes ([Text.Encoding]::UTF8.GetBytes('{"v":1,"kind":"response","id":"1","ok":true,"result":{"array":[1,2,{"name":"test"}],"weird:name":true}}')); 'valid'""");
        Assert.AreEqual("valid", output.Trim());
    }

    [TestMethod]
    public void SimulationCannotBePublishedOnStationProfilePipe()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            new ComConReadOnlyPipeHost(1, _ => { }, observations: new SimulatedReceiveJournal(new StopwatchClock())));
    }

    [TestMethod]
    public async Task PassiveSimulationRoundTripDecodesOnlyFreshRelevantRadioFrames()
    {
        var feed = new SimulatedReceiveFeed(FramingMode.Civ, new StopwatchClock(),
            _ => Assert.Fail("Unexpected framing fault."));
        feed.Feed(Convert.FromHexString("FEFEE07A1C"));
        feed.Feed(Convert.FromHexString("0000FDFEFEE07A030000100700FDFEFE7AE0"));
        feed.Feed(Convert.FromHexString("1C00FDFEFEE07A1C0002FD"));
        await using var fixture = new HostFixture(feed.Journal);
        var output = await RunPowerShellAsync(
            $$"""
            $decoder = Join-Path (Split-Path '{{Path.Combine(AppContext.BaseDirectory, "ComCon-IpcClient.ps1")}}') 'ComCon-CivObservations.ps1'
            . $decoder
            $telemetry = Get-ComConCivTelemetry -TestPipeName '{{fixture.Name}}' -MaximumAgeMs 60000
            $telemetry.readings | ConvertTo-Json -Depth 8 -Compress
            """);
        using var json = JsonDocument.Parse(output);
        var rows = json.RootElement;
        Assert.AreEqual(4, rows.GetArrayLength());
        Assert.AreEqual("pttState", rows[0].GetProperty("reading").GetString());
        Assert.AreEqual(0, rows[0].GetProperty("value").GetInt32());
        Assert.AreEqual(7_100_000L, rows[1].GetProperty("value").GetInt64());
        Assert.IsFalse(rows[0].GetProperty("safetyConfirmation").GetBoolean());
        Assert.AreEqual("UNRELATED_OR_ECHO", rows[2].GetProperty("reason").GetString());
        Assert.AreEqual("VALUE_OUT_OF_RANGE", rows[3].GetProperty("reason").GetString());
        Assert.IsEmpty(fixture.Failures);
    }

    [TestMethod]
    public async Task KenwoodChunkFramingPreservesRecordsThroughProtectedPipe()
    {
        var feed = new SimulatedReceiveFeed(FramingMode.KenwoodAscii, new StopwatchClock(),
            _ => Assert.Fail("Unexpected framing fault."));
        feed.Feed("FA00007"u8);
        feed.Feed("100000;MD"u8);
        feed.Feed("2;"u8);
        await using var fixture = new HostFixture(feed.Journal);
        using var json = JsonDocument.Parse(await RunPowerShellAsync(
            $"Get-ComConIpcObservations -TestPipeName '{fixture.Name}' | ConvertTo-Json -Depth 8 -Compress"));
        var observations = json.RootElement.GetProperty("observations");
        Assert.AreEqual(2, observations.GetArrayLength());
        Assert.AreEqual(Convert.ToHexString("FA00007100000;"u8), observations[0].GetProperty("hex").GetString());
        Assert.AreEqual(Convert.ToHexString("MD2;"u8), observations[1].GetProperty("hex").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("safetyConfirmation").GetBoolean());
    }

    [TestMethod]
    public async Task FramingFaultInvalidatesNewQueriesAndRetainedObservationReplay()
    {
        var feed = new SimulatedReceiveFeed(FramingMode.Civ, new StopwatchClock(), _ => { });
        feed.Feed(Convert.FromHexString("FEFEE07A1C0000FD"));
        await using var fixture = new HostFixture(feed.Journal);
        using var client = await fixture.ConnectAsync();
        var hello = await ExchangeAsync(client, Hello);
        var session = hello.GetProperty("result").GetProperty("sessionId").GetString();
        var request = JsonSerializer.Serialize(new
        {
            v = 1, kind = "request", id = "2", op = "GET_OBSERVATIONS", sessionId = session,
            args = new { afterObservationId = "0" }
        });
        Assert.IsTrue((await ExchangeAsync(client, request)).GetProperty("ok").GetBoolean());
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Feed([0xFC]));
        var replay = await ExchangeAsync(client, request);
        Assert.AreEqual("RECEIVE_FAULT", replay.GetProperty("error").GetProperty("code").GetString());
        var fresh = await ExchangeAsync(client, request.Replace("\"id\":\"2\"", "\"id\":\"3\"", StringComparison.Ordinal));
        Assert.AreEqual("RECEIVE_FAULT", fresh.GetProperty("error").GetProperty("code").GetString());
        var status = await ExchangeAsync(client, Status(session!).Replace("\"id\":\"2\"", "\"id\":\"4\"", StringComparison.Ordinal));
        Assert.AreEqual("Civ: Collision", status.GetProperty("result").GetProperty("fault").GetString());
        Assert.ThrowsExactly<InvalidDataException>(() => feed.Journal.RecordFrame([0]));
    }

    [TestMethod]
    public async Task HscTelemetryConsumerFailsExplicitlyOnEvictedHistory()
    {
        var journal = new SimulatedReceiveJournal(new StopwatchClock());
        for (var i = 0; i < 129; i++) journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        await using var fixture = new HostFixture(journal);
        var output = await RunPowerShellAsync($$"""
            . (Join-Path '{{AppContext.BaseDirectory}}' 'ComCon-CivObservations.ps1')
            $rejected = $false
            try { Get-ComConCivTelemetry -TestPipeName '{{fixture.Name}}' }
            catch { if ($_.Exception.Message -notlike 'OBSERVATION_GAP:*') { throw }; $rejected = $true }
            if (-not $rejected) { throw 'Gap was silently accepted' }
            'rejected'
            """);
        Assert.AreEqual("rejected", output.Trim());
    }

    [TestMethod]
    public async Task HscDecoderIncludesLocalDelayWithoutRejuvenatingObservation()
    {
        var output = await RunPowerShellAsync($$"""
            . (Join-Path '{{AppContext.BaseDirectory}}' 'ComCon-CivObservations.ps1')
            $received = [System.Diagnostics.Stopwatch]::GetTimestamp() - [System.Diagnostics.Stopwatch]::Frequency * 2
            $observation = [pscustomobject]@{
                observationId='1'; hex='FEFEE07A1C0000FD'; ageMs=0; source='simulatedPassiveRx'
                clientReceiptTicks=[long]$received
            }
            ConvertFrom-ComConCivObservation -Observation $observation | ConvertTo-Json -Compress
            """);
        using var json = JsonDocument.Parse(output);
        Assert.AreEqual("STALE", json.RootElement.GetProperty("reason").GetString());
        Assert.IsGreaterThanOrEqualTo(2000L, json.RootElement.GetProperty("ageMs").GetInt64());
    }

    [TestMethod]
    [DataRow("$batch.observations[0].ageMs = -1")]
    [DataRow("$batch.observations[0].source = 'hardware'")]
    [DataRow("$batch.observations[0].observationId = '2'")]
    [DataRow("$batch.safetyConfirmation = $true")]
    [DataRow("$batch.hasMore = $true")]
    [DataRow("$batch.nextObservationId = '01'")]
    public async Task HscRejectsInvalidObservationBatchInsteadOfReturningTelemetry(string mutation)
    {
        var output = await RunPowerShellAsync($$"""
            $batch = '{"implementation":"offlinePassiveRxSimulation","source":"simulatedPassiveRx","safetyConfirmation":false,"profileGeneration":"1","ownershipEpoch":"0","nextObservationId":"1","latestObservationId":"1","gap":false,"hasMore":false,"observations":[{"observationId":"1","hex":"FEFEE07A1C0000FD","ageMs":0,"source":"simulatedPassiveRx"}]}' | ConvertFrom-Json
            {{mutation}}
            $rejected = $false
            try { Assert-ComConObservationBatch -Batch $batch -AfterObservationId '0' }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Invalid observation metadata was accepted' }
            'rejected'
            """);
        Assert.AreEqual("rejected", output.Trim());
    }

    [TestMethod]
    public async Task HostSurvivesPeersClosingBeforeTheyReadTheirResponses()
    {
        await using var fixture = new HostFixture();
        for (var i = 0; i < 4; i++)
        {
            using var client = await fixture.ConnectAsync();
            await client.WriteAsync(Encoding.UTF8.GetBytes(Hello + "\n"));
        }
        using var next = await fixture.ConnectAsync();
        Assert.IsTrue((await ExchangeAsync(next, Hello)).GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task StaleObservationKeepsItsOriginalAgeAcrossReconnect()
    {
        var clock = new SimulatedReceiveJournalTests.TestClock();
        var journal = new SimulatedReceiveJournal(clock);
        journal.RecordFrame(Convert.FromHexString("FEFEE07A1C0000FD"));
        clock.NowMs = 1000;
        await using var fixture = new HostFixture(journal);
        var command = $"""
            . (Join-Path '{AppContext.BaseDirectory}' 'ComCon-CivObservations.ps1')
            $batch = Get-ComConIpcObservations -TestPipeName '{fixture.Name}'
            ConvertFrom-ComConCivObservation -Observation $batch.observations[0] | ConvertTo-Json -Compress
            """;
        using var first = JsonDocument.Parse(await RunPowerShellAsync(command));
        Assert.AreEqual("STALE", first.RootElement.GetProperty("reason").GetString());
        clock.NowMs = 2000;
        using var second = JsonDocument.Parse(await RunPowerShellAsync(command));
        Assert.AreEqual("STALE", second.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual("1", second.RootElement.GetProperty("observationId").GetString());
        Assert.IsGreaterThanOrEqualTo(2000L, second.RootElement.GetProperty("ageMs").GetInt64());
    }

    [TestMethod]
    [DataRow("FEFEE07A1C0000FD", 999, "PASSIVE_DISPLAY_ONLY")]
    [DataRow("FEFEE07A1C0000FD", 1000, "STALE")]
    [DataRow("FEFEE07A1C0000FD", 1001, "STALE")]
    [DataRow("FEFEE07A1C0001FD", 0, "PASSIVE_DISPLAY_ONLY")]
    [DataRow("FEFEE17A1C0000FD", 0, "UNRELATED_OR_ECHO")]
    [DataRow("FEFEE07B1C0000FD", 0, "UNRELATED_OR_ECHO")]
    [DataRow("FEFE7AE01C0000FD", 0, "UNRELATED_OR_ECHO")]
    [DataRow("FEFEE07A1C00FD", 0, "MALFORMED_DATA")]
    [DataRow("FEFEE07A1C000000FD", 0, "MALFORMED_DATA")]
    [DataRow("FEFEE07AFBFD", 0, "UNSUPPORTED_COMMAND")]
    [DataRow("FEFEE07A050000100700FD", 0, "UNSUPPORTED_COMMAND")]
    [DataRow("FEFEE07A0300001A0700FD", 0, "INVALID_BCD")]
    [DataRow("FEFEE07A140A0256FD", 0, "VALUE_OUT_OF_RANGE")]
    [DataRow("FEFEE07A15020100FD", 0, "PASSIVE_DISPLAY_ONLY")]
    [DataRow("FEFEE07A151201FFFD", 0, "PASSIVE_DISPLAY_ONLY")]
    [DataRow("FEFEE07A1C0000FD00", 0, "MALFORMED_FRAME")]
    [DataRow("FEFEE07A1C0000FDFEFEE07A1C0000FD", 0, "MALFORMED_FRAME")]
    public async Task HscPassiveDecoderReportsRejectionsAndExactFreshnessThreshold(string hex, int age, string reason)
    {
        var output = await RunPowerShellAsync(
            $$"""
            . (Join-Path '{{AppContext.BaseDirectory}}' 'ComCon-CivObservations.ps1')
            $observation = [pscustomobject]@{observationId='1';hex='{{hex}}';ageMs={{age}};source='simulatedPassiveRx'}
            ConvertFrom-ComConCivObservation -Observation $observation | ConvertTo-Json -Depth 8 -Compress
            """);
        using var json = JsonDocument.Parse(output);
        Assert.AreEqual(reason, json.RootElement.GetProperty("reason").GetString());
        Assert.AreEqual(reason == "PASSIVE_DISPLAY_ONLY", json.RootElement.GetProperty("accepted").GetBoolean());
        Assert.IsFalse(json.RootElement.GetProperty("safetyConfirmation").GetBoolean());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PowerShellNearLimitResponsesMeetUnchangedDeadline()
    {
        var name = "ComConTest_" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var output = RunPowerShellAsync($$"""
            $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', '{{name}}',
                [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
            try {
                $pipe.Connect(1000)
                $times = @()
                for ($i = 0; $i -lt 8; $i++) {
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $reply = Invoke-ComConStatusExchange -Pipe $pipe -TimeoutMs 1000 -Id '1' -Request @{op='HELLO'}
                    if ($reply.result.payload.Length -ne 15000) { throw 'Response payload changed' }
                    $times += $clock.ElapsedMilliseconds
                }
                'exchange-ms=' + ($times -join ',')
            } finally { $pipe.Dispose() }
            """);
        await pipe.WaitForConnectionAsync(deadline.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        var response = JsonSerializer.Serialize(new
        {
            v = 1, kind = "response", id = "1", ok = true,
            result = new { payload = new string('x', 15000) }
        });
        try
        {
            for (var index = 0; index < 8; index++)
            {
                Assert.IsNotNull(await reader.ReadLineAsync(deadline.Token));
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(response + "\n"), deadline.Token);
            }
        }
        catch (IOException)
        {
            await output;
            throw;
        }
        Console.WriteLine(await output);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("fragmented", "accepted")]
    [DataRow("utf8fragmented", "accepted")]
    [DataRow("crlf", "accepted")]
    [DataRow("limit", "accepted")]
    [DataRow("oversized", "frame byte limit")]
    [DataRow("trailing", "unsolicited bytes")]
    [DataRow("incomplete", "complete response")]
    [DataRow("stall", "IPC_TIMEOUT: HELLO read")]
    [DataRow("utf8", "System.Text.DecoderFallbackException")]
    public async Task PowerShellResponseChunksPreserveFramingGuards(string mode, string expected)
    {
        var name = "ComConTest_" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = RunPowerShellAsync($$"""
            $pipe = New-Object IO.Pipes.NamedPipeClientStream('.', '{{name}}',
                [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
            try {
                $pipe.Connect(1000)
                $clock = [Diagnostics.Stopwatch]::StartNew()
                try {
                    $null = Invoke-ComConStatusExchange -Pipe $pipe -TimeoutMs 1000 -Id '1' -Request @{op='HELLO'}
                    $result = 'accepted'
                } catch {
                    $failure = $_.Exception
                    while ($failure.InnerException) { $failure = $failure.InnerException }
                    $result = $_.Exception.Message + ' ' + $failure.GetType().FullName
                }
                if ($result -notlike '*{{expected}}*') { throw "Unexpected response: $result" }
                if ('{{mode}}' -eq 'stall' -and $clock.ElapsedMilliseconds -gt 2500) { throw 'Deadline did not bound pending I/O' }
                'checked'
            } finally { $pipe.Dispose() }
            """);
        await pipe.WaitForConnectionAsync(deadline.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        Assert.IsNotNull(await reader.ReadLineAsync(deadline.Token));
        const string prefix = """{"v":1,"kind":"response","id":"1","ok":true,"result":{"payload":""" + "\"";
        const string suffix = "\"}}\n";
        var payload = mode is "limit" or "oversized"
            ? new string('x', 16384 - Encoding.UTF8.GetByteCount(prefix + suffix) + (mode == "oversized" ? 1 : 0))
            : "fragment";
        var bytes = Encoding.UTF8.GetBytes(prefix + payload + suffix);
        if (mode == "utf8fragmented") bytes = Encoding.UTF8.GetBytes(prefix + "\u00E9\u6F22" + suffix);
        if (mode == "crlf") bytes = Encoding.UTF8.GetBytes(prefix + payload + suffix.Replace("\n", "\r\n", StringComparison.Ordinal));
        if (mode == "trailing") bytes = Encoding.UTF8.GetBytes("{}\n{}");
        if (mode == "utf8") bytes = [0xFF, (byte)'\n'];
        if (mode is "fragmented" or "utf8fragmented")
        {
            var chunkSize = mode == "utf8fragmented" ? 1 : 7;
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                await pipe.WriteAsync(bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset)), deadline.Token);
                if (mode == "fragmented" || offset == Encoding.UTF8.GetByteCount(prefix) ||
                    offset == Encoding.UTF8.GetByteCount(prefix) + 2)
                    await Task.Delay(5, deadline.Token);
            }
        }
        else if (mode is "incomplete" or "stall")
        {
            await pipe.WriteAsync(Encoding.UTF8.GetBytes("{"), deadline.Token);
            if (mode == "incomplete") pipe.Disconnect();
        }
        else
        {
            try
            {
                await pipe.WriteAsync(bytes, deadline.Token);
            }
            catch (IOException) when (mode == "oversized")
            {
                // Rejecting at the cap may close before the final server byte completes.
                Assert.AreEqual("checked", (await output).Trim());
                return;
            }
        }
        Assert.AreEqual("checked", (await output).Trim());
    }

    [TestMethod]
    public async Task PowerShellClientRejectsMismatchedResponseId()
    {
        var name = "ComConTest_" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var output = RunPowerShellAsync(
            "$rejected = $false; try { Get-ComConIpcStatus -TestPipeName '" + name +
            "' } catch { if ($_.Exception.Message -notlike '*response envelope is invalid*') { throw }; " +
            "$rejected = $true }; if (-not $rejected) { throw 'Mismatched ID accepted' }; 'rejected'");
        await pipe.WaitForConnectionAsync(deadline.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
        Assert.IsNotNull(await reader.ReadLineAsync(deadline.Token));
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(
            """{"v":1,"kind":"response","id":"9","ok":true,"result":{}}""" + "\n"), deadline.Token);
        Assert.AreEqual("rejected", (await output).Trim());
    }

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityInfo(SafePipeHandle handle, uint objectType, uint securityInformation,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(SafePipeHandle handle, out uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private const string Hello =
        """{"v":1,"kind":"request","id":"1","op":"HELLO","args":{"clientName":"Test","supportedVersions":[1]}}""";

    private static string Status(string session) =>
        JsonSerializer.Serialize(new { v = 1, kind = "request", id = "2", op = "GET_STATUS", sessionId = session, args = new { } });

    private static async Task<JsonElement> ExchangeAsync(NamedPipeClientStream client, string request)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.WriteAsync(Encoding.UTF8.GetBytes(request + "\n"), deadline.Token);
        using var frame = new MemoryStream();
        var buffer = new byte[1];
        while (true)
        {
            Assert.AreEqual(1, await client.ReadAsync(buffer, deadline.Token));
            if (buffer[0] == (byte)'\n') break;
            Assert.IsLessThan(16383L, frame.Length);
            frame.WriteByte(buffer[0]);
        }
        using var document = JsonDocument.Parse(frame.ToArray());
        return document.RootElement.Clone();
    }

    private static async Task<string> RunPowerShellAsync(string command)
    {
        var helper = Path.Combine(AppContext.BaseDirectory, "ComCon-IpcClient.ps1").Replace("'", "''", StringComparison.Ordinal);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"$ErrorActionPreference = 'Stop'; . '{helper}'; {command}");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start test PowerShell.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(0, process.ExitCode, await errors);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private sealed class HostFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource shutdown = new();
        private readonly Task hostTask;
        public string Name { get; }
        public ConcurrentQueue<Exception> Failures { get; } = new();
        public TaskCompletionSource<Exception> FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HostFixture(SimulatedReceiveJournal? observations = null, PassiveTelemetryLifecycle? telemetry = null,
            string? pipeName = null)
        {
            Name = pipeName ?? "ComConTest_" + Guid.NewGuid().ToString("N");
            hostTask = new ComConReadOnlyPipeHost(1, exception =>
            {
                Failures.Enqueue(exception);
                FirstFailure.TrySetResult(exception);
            }, Name, observations, telemetry).RunAsync(shutdown.Token);
        }

        public async Task<NamedPipeClientStream> ConnectAsync()
        {
            var client = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(5000);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            shutdown.Cancel();
            try
            {
                await hostTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally
            {
                shutdown.Dispose();
            }
        }
    }
}
