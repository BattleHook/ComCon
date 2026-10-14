using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ComCon.Core.Ipc;
using ComCon.Windows;

namespace ComCon.Core.Tests;

[TestClass]
public sealed class ComConPowerShellClientInteropTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [SupportedOSPlatform("windows")]
    public async Task PowerShellClientConnectsToProtectedComConPipeAndReadsHandshakeRejection()
    {
        var names = ComConCommandPipeNames.IsolatedTest(
            "ComConTest_PowerShellInterop_" + Guid.NewGuid().ToString("N"));
        const string rigProfileId = "12345678-1234-4234-8234-123456789abc";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var cat = ComConReadOnlyPipeHost.CreateProtectedPipe(names.CatPipeName);
        using var writer = new SemaphoreSlim(1, 1);

        var elapsed = Stopwatch.StartNew();
        void Trace(string stage) =>
            TestContext.WriteLine($"{DateTimeOffset.UtcNow:O}; elapsed={elapsed.Elapsed.TotalMilliseconds:F1} ms; {stage}");

        async Task ServeHandshakeAsync()
        {
            Trace("Server connection wait armed");
            await cat.WaitForConnectionAsync(timeout.Token);
            Trace("Server connected");
            var handshake = await ComConCommandProtocol.ReadEnvelopeAsync(cat, timeout.Token);
            Trace("Server handshake read");
            Assert.AreEqual("cat.handshake", handshake.GetProperty("messageType").GetString());
            Assert.AreEqual(ComConCommandProtocol.ContractVersion,
                handshake.GetProperty("contractVersion").GetString());
            Assert.AreEqual(rigProfileId, handshake.GetProperty("rigProfileId").GetString());

            var rejection = JsonSerializer.SerializeToUtf8Bytes(new
            {
                messageType = "cat.rejected",
                contractVersion = ComConCommandProtocol.ContractVersion,
                errorCode = "RIG_PROFILE_NOT_MAPPED",
                reason = "offline interoperability probe"
            });
            Trace("Server rejection write starting");
            await ComConCommandProtocol.WriteEnvelopeAsync(cat, rejection, writer, timeout.Token);
            Trace("Server rejection written");
        }

        var clientPath = Path.Combine(AppContext.BaseDirectory, "ComCon-CommandClient.ps1");
        Assert.IsTrue(File.Exists(clientPath), $"PowerShell client script was not deployed: {clientPath}");
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var powerShellPath = Path.Combine(windowsDirectory,
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.IsTrue(File.Exists(powerShellPath), $"Windows PowerShell was not found: {powerShellPath}");

        var script = $$"""
            $ErrorActionPreference = 'Stop'
            . '{{clientPath}}' -CatPipeName '{{names.CatPipeName}}' -ControlPipeName '{{names.ControlPipeName(1)}}' -RigProfileUuid '{{rigProfileId}}' -CommandList 'Frequency'
            $stage = { param($name) [Console]::Error.WriteLine(('{0:o}; client stage={1}' -f [DateTime]::UtcNow, $name)) }
            $result = Invoke-ComConReadOnlyCommand -CatPipeName '{{names.CatPipeName}}' -ControlPipeName '{{names.ControlPipeName(1)}}' -RigProfileUuid '{{rigProfileId}}' -CommandList 'Frequency' -ConnectTimeoutMs 3000 -SetupTimeoutMs 3000 -OnStage $stage
            $result | ConvertTo-Json -Compress -Depth 8
            """;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(powerShellPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add("-NoLogo");
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
        process.StartInfo.ArgumentList.Add("Bypass");
        process.StartInfo.ArgumentList.Add("-EncodedCommand");
        process.StartInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));

        // Arm the asynchronous pipe wait before launching the client, without queued startup.
        var handshakeTask = ServeHandshakeAsync();
        try
        {
            Trace("Client process starting");
            Assert.IsTrue(process.Start());
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            Trace("Client process exited");
            var output = await outputTask;
            var error = await errorTask;
            TestContext.WriteLine($"Client exit code: {process.ExitCode}");
            TestContext.WriteLine($"Client stdout: {output}");
            TestContext.WriteLine($"Client stderr: {error}");
            await handshakeTask;

            Assert.AreEqual(0, process.ExitCode, error);
            using var response = JsonDocument.Parse(output);
            Assert.AreEqual("ERROR", response.RootElement.GetProperty("status").GetString());
            Assert.AreEqual("RIG_PROFILE_NOT_MAPPED",
                response.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
