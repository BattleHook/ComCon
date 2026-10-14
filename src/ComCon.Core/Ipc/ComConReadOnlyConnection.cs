using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

public static class ComConReadOnlyConnection
{
    public static async Task ServeAsync(
        Stream stream, int profileId, string serverInstanceId,
        CancellationToken cancellationToken = default, SimulatedReceiveJournal? observations = null,
        PassiveTelemetryLifecycle? telemetry = null)
    {
        var session = new ComConReadOnlySession(profileId, serverInstanceId, new StopwatchClock(), observations, telemetry);
        var reader = new ComConIpcJsonLineReader(stream);
        using var writer = new SemaphoreSlim(1, 1);
        using var helloDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloDeadline.CancelAfter(TimeSpan.FromSeconds(3));

        while (true)
        {
            ComConIpcRequest request;
            try
            {
                request = await reader.ReadRequestAsync(
                    session.HasSession ? cancellationToken : helloDeadline.Token,
                    TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("IPC HELLO or partial-frame deadline expired.");
            }

            var reply = session.Process(request);
            using var writeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            writeDeadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await ComConIpcProtocol.WriteJsonLineAsync(
                    stream, reply.Message, writer, writeDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("IPC response write deadline expired.");
            }
            if (reply.CloseConnection)
                throw new InvalidDataException(reply.Message.GetProperty("error").GetProperty("code").GetString());
        }
    }
}
