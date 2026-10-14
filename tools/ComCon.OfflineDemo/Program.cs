using ComCon.Core.Ipc;
using ComCon.Core.Models;
using ComCon.Core.Routing;
using ComCon.Core.Transport;
using ComCon.Windows;
using System.Threading.Channels;

if (args.Length is < 1 or > 3 ||
    args.Skip(1).Any(value => value is not ("--enable-capture" or "--test-controls")) ||
    args.Skip(1).Distinct(StringComparer.Ordinal).Count() != args.Length - 1)
{
    Console.Error.WriteLine("Usage: ComCon.OfflineDemo ComConTest_<unique-name> [--enable-capture] [--test-controls]");
    return 2;
}

using var shutdown = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var endpoints = new DemoEndpointFactory();
await using var bridge = CommanderByteBridgeHost.CreateSimulationWithTelemetry(endpoints, new StopwatchClock());
var host = new ComConReadOnlyPipeHost(1,
    error => Console.Error.WriteLine($"SIMULATION IPC ERROR: {error.Message}"), args[0], telemetry: bridge.Telemetry);
await bridge.StartAsync(new ProfileConfig
{
    Id = 1, Label = "OFFLINE fake IC-7600", Enabled = true, AutoConnect = false,
    PhysicalPort = "COM3", VirtualPort = "COM31", CommanderPort = "COM21",
    Adapter = AdapterKind.Passthrough, Framing = FramingMode.Civ
}, shutdown.Token);
if (args.Contains("--enable-capture", StringComparer.Ordinal)) await bridge.Telemetry.EnableSimulationAsync();
var testControls = args.Contains("--test-controls", StringComparer.Ordinal);
var records = Convert.FromHexString("FEFEE07A1C0000FDFEFEE07A030040071400FD");
var serving = host.RunAsync(shutdown.Token);
if (serving.IsCompleted) await serving;
Console.WriteLine("SIMULATION READY");
var input = Task.Run(Console.ReadLine);
string? reportedCaptureFault = null;
try
{
    while (!shutdown.IsCancellationRequested)
    {
        var delay = Task.Delay(200, shutdown.Token);
        var completed = await Task.WhenAny(serving, input, delay);
        if (completed == serving)
        {
            await serving;
            throw new IOException("Simulation pipe host stopped unexpectedly.");
        }
        if (completed == input)
        {
            var command = await input;
            if (command is null || command == "stop") break;
            if (testControls && command is "enable" or "disable" or "fault")
            {
                if (command == "enable" && bridge.Telemetry.Snapshot.State != "Off")
                {
                    Console.WriteLine("SIMULATION COMMAND REJECTED enable: disable the previous capture first.");
                }
                else if (command == "fault" && !bridge.Telemetry.Snapshot.Enabled)
                {
                    Console.WriteLine("SIMULATION COMMAND REJECTED fault: no enabled simulation capture.");
                }
                else
                {
                    switch (command)
                    {
                        case "enable":
                            await bridge.Telemetry.EnableSimulationAsync();
                            reportedCaptureFault = null;
                            break;
                        case "disable":
                            await bridge.Telemetry.DisableAsync();
                            break;
                        case "fault":
                            await endpoints.Physical.FeedAsync([0xFC], shutdown.Token);
                            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
                            {
                                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                                while (bridge.Telemetry.Snapshot.Fault is null)
                                    await Task.Delay(5, deadline.Token);
                            }
                            break;
                    }
                    Console.WriteLine($"SIMULATION COMMAND OK {command}");
                }
            }
            else
            {
                Console.Error.WriteLine("Unsupported demo command; no radio commands are accepted.");
                Console.WriteLine("SIMULATION COMMAND REJECTED unsupported");
            }
            input = Task.Run(Console.ReadLine);
        }
        else
        {
            await delay;
            if (bridge.State != CommanderBridgeState.Running)
                throw new IOException($"Fake bridge failed: {bridge.Fault}");
            if (bridge.Telemetry.Snapshot.Fault is { } fault && fault != reportedCaptureFault)
            {
                Console.Error.WriteLine($"SIMULATION RECEIVE FAULT: {fault}");
                reportedCaptureFault = fault;
            }
            await endpoints.Physical.FeedAsync(records[..5], shutdown.Token);
            await endpoints.Physical.FeedAsync(records[5..], shutdown.Token);
        }
    }
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
finally
{
    shutdown.Cancel();
    await serving;
    await bridge.StopAsync();
}
return 0;

sealed class DemoEndpointFactory : IByteBridgeEndpointFactory
{
    public DemoEndpoint Physical { get; } = new();
    private DemoEndpoint Virtual { get; } = new();

    public ValueTask<IByteBridgeEndpoint> OpenAsync(string portName, SerialSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IByteBridgeEndpoint>(portName switch
        {
            "COM3" => Physical,
            "COM31" => Virtual,
            _ => throw new InvalidOperationException("The demo supports only its two in-memory endpoints.")
        });
    }
}

sealed class DemoEndpoint : IBufferedByteBridgeEndpoint
{
    private readonly Channel<byte[]> received = Channel.CreateBounded<byte[]>(16);
    public int BufferedInputBytes => received.Reader.TryPeek(out var bytes) ? bytes.Length : 0;

    public ValueTask FeedAsync(byte[] bytes, CancellationToken cancellationToken) =>
        received.Writer.WriteAsync(bytes, cancellationToken);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (!await received.Reader.WaitToReadAsync(cancellationToken)) return 0;
        var bytes = await received.Reader.ReadAsync(cancellationToken);
        if (bytes.Length > buffer.Length) throw new InvalidDataException("Fake RX chunk exceeds the bridge buffer.");
        bytes.CopyTo(buffer);
        return bytes.Length;
    }

    // The other side of this fake endpoint accepts forwarded bytes but never reaches hardware.
    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        received.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
