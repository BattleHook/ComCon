using System.IO.Ports;
using ComCon.Core.Models;
using ComCon.Core.Transport;

namespace ComCon.Windows;

public sealed class SerialPortByteBridgeEndpointFactory : IByteBridgeEndpointFactory
{
    private const int WriteTimeoutMs = 500;

    public ValueTask<IByteBridgeEndpoint> OpenAsync(
        string portName, SerialSettings settings, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();

        var port = new SerialPort(
            portName,
            settings.BaudRate,
            ToParity(settings.Parity),
            settings.DataBits,
            ToStopBits(settings.StopBits))
        {
            Handshake = ToHandshake(settings.Handshake),
            DtrEnable = settings.DtrEnable,
            RtsEnable = settings.RtsEnable,
            WriteTimeout = WriteTimeoutMs
        };

        try
        {
            port.Open();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IByteBridgeEndpoint>(new SerialPortByteBridgeEndpoint(
                port.BaseStream, port, () => port.BytesToRead));
        }
        catch
        {
            port.Dispose();
            throw;
        }
    }

    private static Parity ToParity(SerialParity parity) => parity switch
    {
        SerialParity.None => Parity.None,
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => throw new ArgumentOutOfRangeException(nameof(parity))
    };

    private static StopBits ToStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => StopBits.One,
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => throw new ArgumentOutOfRangeException(nameof(stopBits))
    };

    private static Handshake ToHandshake(SerialHandshake handshake) => handshake switch
    {
        SerialHandshake.None => Handshake.None,
        SerialHandshake.XOnXOff => Handshake.XOnXOff,
        SerialHandshake.RequestToSend => Handshake.RequestToSend,
        SerialHandshake.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
        _ => throw new ArgumentOutOfRangeException(nameof(handshake))
    };

    internal sealed class SerialPortByteBridgeEndpoint(
        Stream stream, IDisposable owner, Func<int>? bufferedInputBytes = null) : IBufferedByteBridgeEndpoint
    {
        public int BufferedInputBytes => bufferedInputBytes?.Invoke() ??
            throw new NotSupportedException("This endpoint has no buffered-input observation capability.");

        public ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken) =>
            stream.ReadAsync(buffer, cancellationToken);

        public async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = buffer.ToArray();
            try
            {
                // Windows SerialStream async writes do not enforce WriteTimeout or cancellation.
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    stream.Write(bytes, 0, bytes.Length);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException(
                    $"Serial write exceeded {WriteTimeoutMs} ms; delivery outcome is unknown.", exception);
            }
        }

        public ValueTask DisposeAsync()
        {
            owner.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
