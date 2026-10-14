// Minimal .NET 10 console example: establish/hold a ComCon IPC session and ACK notifications.
// It intentionally sends no CAT bytes. Run with a mapped rig-profile UUID as the only argument.
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;

internal static class Program
{
    private const string ContractVersion = "comcon-ipc-v1";
    private const int MaximumPayloadBytes = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || !Guid.TryParseExact(args[0], "D", out var rigId) ||
            rigId.ToString("D") != args[0])
        {
            Console.Error.WriteLine("Usage: dotnet run -- <mapped-lowercase-rig-profile-uuid>");
            return 2;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };

        try
        {
            using var cat = new NamedPipeClientStream(".", "ComConCmd_Cat", PipeDirection.InOut, PipeOptions.Asynchronous);
            await cat.ConnectAsync(5_000, shutdown.Token);
            await WriteEnvelopeAsync(cat, new
            {
                messageType = "cat.handshake",
                contractVersion = ContractVersion,
                rigProfileId = rigId.ToString("D")
            }, shutdown.Token);

            int profileId;
            string sessionId;
            using (var accepted = await ReadEnvelopeAsync(cat, shutdown.Token))
            {
                var messageType = accepted.RootElement.GetProperty("messageType").GetString();
                if (messageType != "cat.accepted")
                    throw Rejected("CAT handshake", accepted.RootElement);
                profileId = accepted.RootElement.GetProperty("profileId").GetInt32();
                sessionId = accepted.RootElement.GetProperty("sessionId").GetString()
                    ?? throw new InvalidDataException("Accepted response has no sessionId.");
            }

            using var control = new NamedPipeClientStream(
                ".", $"ComConCmd_Cat_Control_Profile{profileId}", PipeDirection.InOut, PipeOptions.Asynchronous);
            await control.ConnectAsync(5_000, shutdown.Token);
            await WriteEnvelopeAsync(control, new
            {
                messageType = "control.bind",
                contractVersion = ContractVersion,
                profileId,
                sessionId
            }, shutdown.Token);

            using (var bound = await ReadEnvelopeAsync(control, shutdown.Token))
            {
                if (bound.RootElement.GetProperty("messageType").GetString() != "control.bound")
                    throw Rejected("Control bind", bound.RootElement);
            }

            await WriteEnvelopeAsync(cat, new
            {
                messageType = "cat.ready",
                contractVersion = ContractVersion,
                profileId,
                sessionId
            }, shutdown.Token);

            Console.WriteLine($"IPC session ready: profile {profileId}, session {sessionId}.");
            Console.WriteLine("No CAT bytes were sent. Press Ctrl+C to close the session.");
            while (!shutdown.IsCancellationRequested)
            {
                using var notification = await ReadEnvelopeAsync(control, shutdown.Token);
                var root = notification.RootElement;
                var type = root.GetProperty("messageType").GetString();
                Console.WriteLine(JsonSerializer.Serialize(root));

                if (type is "record.rejected" or "record.outcome" or "framing.notification" or "session.notification")
                {
                    await WriteEnvelopeAsync(control, new
                    {
                        messageType = "notification.ack",
                        contractVersion = ContractVersion,
                        profileId,
                        sessionId,
                        notificationSequence = root.GetProperty("notificationSequence").GetString()
                    }, shutdown.Token);
                }

                if (type == "session.notification" &&
                    root.GetProperty("event").GetString() == "session.ended")
                    break;
            }
            return 0;
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidDataException || exception is JsonException || exception is TimeoutException || exception is UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Exception Rejected(string step, JsonElement response)
    {
        var code = response.TryGetProperty("errorCode", out var errorCode) ? errorCode.GetString() : "unexpected-response";
        var reason = response.TryGetProperty("reason", out var detail) ? detail.GetString() : null;
        return new InvalidDataException($"{step} rejected ({code}): {reason}");
    }

    private static async Task WriteEnvelopeAsync(Stream stream, object envelope, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        if (payload.Length is 0 or > MaximumPayloadBytes)
            throw new InvalidDataException("Envelope payload is outside ComCon's 16,384-byte limit.");
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<JsonDocument> ReadEnvelopeAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length is 0 or > MaximumPayloadBytes)
            throw new InvalidDataException("Received envelope length is outside ComCon's 16,384-byte limit.");
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonDocument.Parse(payload);
    }
}

