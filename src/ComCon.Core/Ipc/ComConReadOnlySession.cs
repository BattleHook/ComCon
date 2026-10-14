using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ComCon.Core.Routing;

namespace ComCon.Core.Ipc;

public sealed record ComConIpcReply(JsonElement Message, bool CloseConnection = false);

/// <summary>Offline observation/status admission only; has no serial or dispatch capability.</summary>
public sealed class ComConReadOnlySession
{
    private const int MaximumRetainedResponses = 128;
    private const long RetentionMs = 60_000;
    private readonly IMonotonicClock clock;
    private readonly int profileId;
    private readonly string serverInstanceId;
    private readonly SimulatedReceiveJournal? observations;
    private readonly PassiveTelemetryLifecycle? telemetry;
    private readonly Dictionary<long, CompletedResponse> completed = [];
    private readonly Queue<long> responseOrder = [];
    private long highWaterMark;
    private string? sessionId;

    public ComConReadOnlySession(int profileId, string serverInstanceId, IMonotonicClock clock,
        SimulatedReceiveJournal? observations = null, PassiveTelemetryLifecycle? telemetry = null)
    {
        if (profileId is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(profileId));
        ArgumentException.ThrowIfNullOrWhiteSpace(serverInstanceId);
        ArgumentNullException.ThrowIfNull(clock);
        if (telemetry is { IsSimulation: false })
            throw new ArgumentException("Lifecycle IPC requires explicit simulation composition.", nameof(telemetry));
        if (telemetry is not null && observations is not null)
            throw new ArgumentException("Lifecycle and legacy observation identities are not yet aligned.", nameof(observations));
        this.profileId = profileId;
        this.serverInstanceId = serverInstanceId;
        this.clock = clock;
        this.observations = observations;
        this.telemetry = telemetry;
    }

    public bool HasSession => sessionId is not null;

    public ComConIpcReply Process(ComConIpcRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Prune();
        var normalized = Normalize(request);
        if (completed.TryGetValue(request.Id, out var prior))
        {
            if (prior.Normalized != normalized)
                return Error(request.Id, "ID_CONFLICT", "Request ID was reused with changed content.", true);
            if (request.Operation == "GET_TELEMETRY_STATUS" && prior.Reply.Message.GetProperty("ok").GetBoolean())
                return Dispatch(request);
            return RefreshObservationAges(prior);
        }
        if (request.Id <= highWaterMark)
            return Error(request.Id, "RESULT_EXPIRED", "The retained result is no longer available.");

        highWaterMark = request.Id;
        var startedMs = clock.NowMs;
        var reply = Dispatch(request);
        completed.Add(request.Id, new(normalized, reply, startedMs));
        responseOrder.Enqueue(request.Id);
        Prune();
        return reply;
    }

    private ComConIpcReply Dispatch(ComConIpcRequest request)
    {
        if (sessionId is null)
        {
            if (request.Operation != "HELLO" || request.SessionId is not null)
                return Error(request.Id, "HELLO_REQUIRED", "HELLO must establish the connection first.", true);
            var args = request.Arguments;
            if (!HasOnly(args, "clientName", "supportedVersions") ||
                !args.TryGetProperty("clientName", out var name) ||
                name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                !args.TryGetProperty("supportedVersions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array || versions.GetArrayLength() == 0 ||
                versions.EnumerateArray().Any(value =>
                    value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out _)))
                return Error(request.Id, "INVALID_ARGUMENTS", "HELLO arguments are invalid.", true);
            if (!versions.EnumerateArray().Any(value => value.GetInt32() == ComConIpcProtocol.Version))
                return Error(request.Id, "UNSUPPORTED_VERSION", "Protocol version 1 is required.", true);

            sessionId = Guid.NewGuid().ToString("N");
            return Success(request.Id, new
            {
                sessionId, serverInstanceId, profileId,
                protocolVersion = ComConIpcProtocol.Version,
                model = (string?)null, ready = false,
                implementation = Implementation,
                operations = Operations
            });
        }

        if (request.SessionId != sessionId)
            return Error(request.Id, "SESSION_INVALID", "Session does not belong to this connection.", true);
        if (request.Operation == "HELLO")
            return Error(request.Id, "HELLO_ALREADY_COMPLETED", "HELLO may only establish one session.", true);
        if (request.Operation == "GET_TELEMETRY_STATUS" && telemetry is not null)
        {
            if (request.Arguments.EnumerateObject().Any())
                return Error(request.Id, "INVALID_ARGUMENTS", "GET_TELEMETRY_STATUS requires an empty args object.");
            return Success(request.Id, new
            {
                serverInstanceId, profileId, implementation = Implementation,
                hardwareAccess = false, safetyConfirmation = false, telemetry = telemetry.Snapshot
            }, JsonNamingPolicy.CamelCase);
        }
        if (request.Operation == "GET_OBSERVATIONS" && telemetry is not null)
        {
            var args = request.Arguments;
            if (!HasOnly(args, "afterObservationId", "captureId", "profileGeneration", "ownershipEpoch") ||
                !TryDecimal(args, "afterObservationId", out var afterId) ||
                !TryDecimal(args, "profileGeneration", out _) ||
                !TryDecimal(args, "ownershipEpoch", out _) ||
                !args.TryGetProperty("captureId", out var capture) || capture.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(capture.GetString()))
                return Error(request.Id, "INVALID_ARGUMENTS", "Cursor and matching capture identity/generation/epoch are required.");
            try
            {
                var generation = args.GetProperty("profileGeneration").GetString()!;
                var epoch = args.GetProperty("ownershipEpoch").GetString()!;
                var batch = telemetry.ReadObservations(profileId, capture.GetString()!, generation, epoch, afterId);
                return Success(request.Id, new
                {
                    serverInstanceId, profileId, captureId = capture.GetString(),
                    profileGeneration = generation, ownershipEpoch = epoch,
                    implementation = Implementation, source = "simulatedPassiveRx", safetyConfirmation = false,
                    batch.Observations, batch.NextObservationId, batch.LatestObservationId, batch.Gap, batch.HasMore
                }, JsonNamingPolicy.CamelCase);
            }
            catch (ArgumentOutOfRangeException) { return Error(request.Id, "CURSOR_INVALID", "Cursor is ahead of this capture's history."); }
            catch (InvalidDataException exception) { return Error(request.Id, "RECEIVE_FAULT", exception.Message); }
            catch (InvalidOperationException exception) when (IsCaptureStateError(exception))
            { return CaptureError(request.Id, exception); }
        }
        if (request.Operation == "GET_OBSERVATIONS" && observations is not null)
        {
            if (!HasOnly(request.Arguments, "afterObservationId") ||
                !request.Arguments.TryGetProperty("afterObservationId", out var cursor) ||
                cursor.ValueKind != JsonValueKind.String ||
                !long.TryParse(cursor.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var afterId) ||
                afterId < 0 || afterId.ToString(CultureInfo.InvariantCulture) != cursor.GetString())
                return Error(request.Id, "INVALID_ARGUMENTS", "afterObservationId must be a canonical nonnegative decimal string.");
            ReceiveObservationBatch batch;
            try
            {
                batch = observations.ReadAfter(afterId);
            }
            catch (ArgumentOutOfRangeException)
            {
                return Error(request.Id, "CURSOR_INVALID", "Cursor is ahead of this server's observation history.");
            }
            catch (InvalidDataException exception)
            {
                return Error(request.Id, "RECEIVE_FAULT", exception.Message);
            }
            return Success(request.Id, new
            {
                serverInstanceId, profileId, profileGeneration = "1", ownershipEpoch = "0",
                implementation = Implementation, source = "simulatedPassiveRx", safetyConfirmation = false,
                batch.Observations, batch.NextObservationId, batch.LatestObservationId, batch.Gap, batch.HasMore
            }, JsonNamingPolicy.CamelCase);
        }
        if (request.Operation != "GET_STATUS")
            return Error(request.Id, "OPERATION_DISABLED",
                "The operation is unavailable; CAT and ownership operations are disabled.");
        if (request.Arguments.EnumerateObject().Any())
            return Error(request.Id, "INVALID_ARGUMENTS", "GET_STATUS requires an empty args object.");

        return Success(request.Id, new
        {
            serverInstanceId, profileId, profileGeneration = "1", ownershipEpoch = "0",
            state = "Stopped", owner = "None", model = (string?)null, ready = false,
            fault = observations?.Fault, suspended = false, hardwareAccess = false,
            implementation = Implementation,
            operations = Operations,
            cacheObservations = Array.Empty<object>()
        });
    }

    private void Prune()
    {
        while (responseOrder.TryPeek(out var id) &&
            (completed.Count > MaximumRetainedResponses ||
             clock.NowMs - completed[id].CompletedMs >= RetentionMs))
        {
            responseOrder.Dequeue();
            completed.Remove(id);
        }
    }

    private static bool HasOnly(JsonElement args, params string[] allowed) =>
        args.EnumerateObject().All(property => allowed.Contains(property.Name, StringComparer.Ordinal));

    private static bool TryDecimal(JsonElement args, string name, out long value)
    {
        value = 0;
        return args.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            long.TryParse(property.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out value) &&
            value >= 0 && value.ToString(CultureInfo.InvariantCulture) == property.GetString();
    }

    private static ComConIpcReply CaptureError(long id, InvalidOperationException exception) =>
        Error(id, exception.Message.StartsWith("IDENTITY_CHANGED:", StringComparison.Ordinal)
            ? "IDENTITY_CHANGED" : "TELEMETRY_OFF", exception.Message);

    private static bool IsCaptureStateError(InvalidOperationException exception) =>
        exception.Message.StartsWith("IDENTITY_CHANGED:", StringComparison.Ordinal) ||
        exception.Message.StartsWith("TELEMETRY_OFF:", StringComparison.Ordinal);

    private string Implementation => telemetry is not null ? "offlinePassiveLifecycleSimulation"
        : observations is null ? "offlineStatusOnly" : "offlinePassiveRxSimulation";
    private string[] Operations => telemetry is not null
        ? ["HELLO", "GET_STATUS", "GET_TELEMETRY_STATUS", "GET_OBSERVATIONS"]
        : observations is null
        ? ["HELLO", "GET_STATUS"]
        : ["HELLO", "GET_STATUS", "GET_OBSERVATIONS"];

    private ComConIpcReply RefreshObservationAges(CompletedResponse prior)
    {
        if (!prior.Reply.Message.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("observations", out _))
            return prior.Reply;
        if (telemetry is not null)
        {
            var id = long.Parse(prior.Reply.Message.GetProperty("id").GetString()!, CultureInfo.InvariantCulture);
            try
            {
                telemetry.ValidateCapture(profileId, result.GetProperty("captureId").GetString()!,
                    result.GetProperty("profileGeneration").GetString()!, result.GetProperty("ownershipEpoch").GetString()!);
            }
            catch (InvalidDataException exception) { return Error(id, "RECEIVE_FAULT", exception.Message); }
            catch (InvalidOperationException exception) when (IsCaptureStateError(exception))
            { return CaptureError(id, exception); }
        }
        if (observations?.Fault is { } fault)
            return Error(long.Parse(prior.Reply.Message.GetProperty("id").GetString()!,
                CultureInfo.InvariantCulture), "RECEIVE_FAULT", $"Receive journal is invalid: {fault}");
        // Replay preserves observation identity/data, but never rejuvenates original receipt age.
        var message = JsonNode.Parse(prior.Reply.Message.GetRawText())!.AsObject();
        foreach (var observation in message["result"]!["observations"]!.AsArray())
            observation!["ageMs"] = checked(observation["ageMs"]!.GetValue<long>() + clock.NowMs - prior.CompletedMs);
        return new(JsonSerializer.SerializeToElement(message), prior.Reply.CloseConnection);
    }

    private static ComConIpcReply Success(long id, object result, JsonNamingPolicy? naming = null) =>
        new(JsonSerializer.SerializeToElement(new
        {
            v = ComConIpcProtocol.Version, kind = "response",
            id = id.ToString(CultureInfo.InvariantCulture), ok = true, result
        }, new JsonSerializerOptions { PropertyNamingPolicy = naming }));

    private static ComConIpcReply Error(long id, string code, string message, bool close = false) =>
        new(JsonSerializer.SerializeToElement(new
        {
            v = ComConIpcProtocol.Version, kind = "response",
            id = id.ToString(CultureInfo.InvariantCulture), ok = false,
            error = new { code, message, dispatchState = "notDispatched", retryable = false }
        }), close);

    private static string Normalize(ComConIpcRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            writer.WriteStringValue(request.Operation);
            writer.WriteStringValue(request.SessionId);
            WriteNormalized(writer, request.Arguments);
            writer.WriteEndArray();
        }
        return Convert.ToBase64String(stream.ToArray());
    }

    private static void WriteNormalized(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteNormalized(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteNormalized(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private sealed record CompletedResponse(string Normalized, ComConIpcReply Reply, long CompletedMs);
}
