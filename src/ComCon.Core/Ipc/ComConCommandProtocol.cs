using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace ComCon.Core.Ipc;

public sealed class ComConCommandProtocolException(
    string errorCode, string message, Exception? innerException = null)
    : IOException(message, innerException)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>Stateless command-envelope framing and validation; never carries raw CAT records.</summary>
public static class ComConCommandProtocol
{
    public const string ContractVersion = "comcon-ipc-v1";
    public const int MaximumPayloadBytes = 16_384;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] BaseFields = ["messageType", "contractVersion"];
    private static readonly string[] SessionFields = ["profileId", "sessionId"];
    private static readonly HashSet<string> SetupErrorCodes =
    [
        "INVALID_JSON", "INVALID_UTF8", "FRAME_TOO_LARGE", "MISSING_FIELD",
        "UNKNOWN_FIELD", "DUPLICATE_FIELD", "INVALID_FIELD", "INVALID_MESSAGE_TYPE",
        "UNSUPPORTED_VERSION", "INVALID_RIG_PROFILE_ID", "INVALID_PROFILE_ID",
        "INVALID_SESSION_ID", "RIG_PROFILE_NOT_MAPPED", "AMBIGUOUS_RIG_MAPPING",
        "PROFILE_BUSY", "UNKNOWN_SESSION", "PROFILE_SESSION_MISMATCH", "CONTROL_BIND_TIMEOUT",
        "CONTROL_ALREADY_BOUND", "SETUP_NOT_READY", "CAT_READY_TIMEOUT"
    ];

    public static JsonElement ParseEnvelope(ReadOnlySpan<byte> payload)
    {
        ValidateLength(payload.Length);
        string json;
        try
        {
            json = StrictUtf8.GetString(payload);
        }
        catch (DecoderFallbackException exception)
        {
            throw new ComConCommandProtocolException(
                "INVALID_UTF8", "Command envelope is not strict UTF-8.", exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        }
        catch (JsonException exception)
        {
            throw new ComConCommandProtocolException(
                "INVALID_JSON", "Command envelope contains invalid JSON.", exception);
        }

        using (document)
        {
            ValidateEnvelope(document.RootElement);
            return document.RootElement.Clone();
        }
    }

    /// <summary>Use one reader per stream. Truncation or cancellation ends the current framing attempt.</summary>
    public static async ValueTask<JsonElement> ReadEnvelopeAsync(
        Stream stream, CancellationToken cancellationToken = default)
        => await ReadEnvelopeOrEofAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Command stream closed before an envelope.");

    internal static async ValueTask<JsonElement?> ReadEnvelopeOrEofAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var prefix = new byte[4];
        if (await stream.ReadAsync(prefix.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
            return null;
        await stream.ReadExactlyAsync(prefix.AsMemory(1), cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (length > MaximumPayloadBytes)
            throw Error("FRAME_TOO_LARGE", "Command envelope exceeds its payload limit.");
        ValidateLength((int)length);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return ParseEnvelope(payload);
    }

    /// <summary>Share the writer gate per stream. A failed partial write must not be retried on that stream.</summary>
    public static async ValueTask WriteEnvelopeAsync(
        Stream stream, ReadOnlyMemory<byte> payload, SemaphoreSlim writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(writer);
        ValidateLength(payload.Length);
        var snapshot = payload.ToArray();
        ParseEnvelope(snapshot);
        var prefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)snapshot.Length);
        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Error("INVALID_JSON", "Command envelope must be a JSON object.");

        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            string name;
            try
            {
                name = property.Name;
            }
            catch (InvalidOperationException exception)
            {
                throw new ComConCommandProtocolException(
                    "INVALID_JSON", "Command property name contains an invalid Unicode escape.", exception);
            }
            if (!present.Add(name))
                throw Error("DUPLICATE_FIELD", $"Duplicate property '{name}'.");
        }

        var messageType = RequireString(root, "messageType", "INVALID_MESSAGE_TYPE");
        string[] required;
        string[] optional = [];
        switch (messageType)
        {
            case "cat.handshake":
                required = ["rigProfileId"];
                break;
            case "cat.accepted":
            case "cat.ready":
            case "control.bind":
            case "control.bound":
                required = SessionFields;
                break;
            case "cat.rejected":
                required = ["errorCode", "reason"];
                break;
            case "control.rejected":
                required = ["errorCode", "reason"];
                optional = SessionFields;
                break;
            case "record.rejected":
                required = ["profileId", "sessionId", "notificationSequence",
                    "recordSequence", "deliveryStatus", "reason"];
                break;
            case "record.outcome":
                required = ["profileId", "sessionId", "notificationSequence",
                    "recordSequence", "deliveryStatus"];
                optional = ["reason"];
                break;
            case "framing.notification":
                required = ["profileId", "sessionId", "notificationSequence", "errorCode", "reason"];
                break;
            case "session.notification":
                required = ["profileId", "sessionId", "notificationSequence", "event", "reason"];
                break;
            case "notification.ack":
                required = ["profileId", "sessionId", "notificationSequence"];
                break;
            default:
                throw Error("INVALID_MESSAGE_TYPE", "Unsupported command messageType.");
        }

        var allowed = new HashSet<string>(BaseFields, StringComparer.Ordinal);
        allowed.UnionWith(required);
        allowed.UnionWith(optional);
        foreach (var name in present)
            if (!allowed.Contains(name))
                throw Error("UNKNOWN_FIELD", $"Unknown property '{name}' for '{messageType}'.");
        foreach (var name in BaseFields.Concat(required))
            if (!present.Contains(name))
                throw Error("MISSING_FIELD", $"Required property '{name}' is absent.");

        if (RequireString(root, "contractVersion") != ContractVersion)
            throw Error("UNSUPPORTED_VERSION", "Unsupported command contractVersion.");
        if (present.Contains("rigProfileId"))
            ValidateUuid(root, "rigProfileId", "INVALID_RIG_PROFILE_ID");
        if (present.Contains("sessionId"))
            ValidateUuid(root, "sessionId", "INVALID_SESSION_ID");
        if (present.Contains("profileId") &&
            (root.GetProperty("profileId").ValueKind != JsonValueKind.Number ||
             !root.GetProperty("profileId").TryGetInt32(out var profileId) || profileId is < 1 or > 4))
            throw Error("INVALID_PROFILE_ID", "profileId must be an integer from 1 through 4.");
        if (messageType == "control.rejected" &&
            present.Contains("profileId") != present.Contains("sessionId"))
            throw Error("MISSING_FIELD", "Control rejection identity requires both profileId and sessionId.");

        foreach (var name in new[] { "recordSequence", "notificationSequence" })
            if (present.Contains(name))
                ValidateSequence(RequireString(root, name), name);
        if (present.Contains("reason"))
            RequireString(root, "reason");
        if (present.Contains("event") && RequireString(root, "event") != "session.ended")
            throw Error("INVALID_FIELD", "Unsupported session event.");
        if (present.Contains("errorCode"))
        {
            var code = RequireString(root, "errorCode");
            var valid = messageType == "framing.notification"
                ? code is "FRAMING_ANOMALY" or "FRAMING_OVERFLOW"
                : SetupErrorCodes.Contains(code);
            if (!valid)
                throw Error("INVALID_FIELD", "Unsupported errorCode for this message.");
        }
        if (present.Contains("deliveryStatus"))
        {
            var status = RequireString(root, "deliveryStatus");
            if (status is not ("WRITE_SUCCEEDED" or "NOT_SENT" or "UNKNOWN_DELIVERY") ||
                (messageType == "record.rejected" && status != "NOT_SENT"))
                throw Error("INVALID_FIELD", "Invalid deliveryStatus for this message.");
            if (messageType == "record.outcome")
            {
                if (status == "WRITE_SUCCEEDED" && present.Contains("reason"))
                    throw Error("INVALID_FIELD", "Successful record outcomes omit reason.");
                if (status != "WRITE_SUCCEEDED" && !present.Contains("reason"))
                    throw Error("MISSING_FIELD", "Non-success record outcomes require reason.");
            }
        }
    }

    private static string RequireString(
        JsonElement root, string name, string invalidCode = "INVALID_FIELD")
    {
        if (!root.TryGetProperty(name, out var value))
            throw Error("MISSING_FIELD", $"Required property '{name}' is absent.");
        if (value.ValueKind != JsonValueKind.String)
            throw Error(invalidCode, $"Property '{name}' must be a nonempty string.");
        string? text;
        try
        {
            text = value.GetString();
        }
        catch (InvalidOperationException exception)
        {
            throw new ComConCommandProtocolException(
                "INVALID_JSON", $"Property '{name}' contains an invalid Unicode escape.", exception);
        }
        if (string.IsNullOrEmpty(text))
            throw Error(invalidCode, $"Property '{name}' must be a nonempty string.");
        return text;
    }

    private static void ValidateUuid(JsonElement root, string name, string invalidCode)
    {
        var text = RequireString(root, name, invalidCode);
        if (!Guid.TryParseExact(text, "D", out var uuid) || uuid.ToString("D") != text)
            throw Error(invalidCode, $"Property '{name}' must be a canonical lowercase UUID.");
    }

    private static void ValidateSequence(string text, string name)
    {
        if (text[0] is < '1' or > '9' || text.Any(character => character is < '0' or > '9'))
            throw Error("INVALID_FIELD", $"Property '{name}' must be a canonical positive decimal string.");
    }

    private static void ValidateLength(int length)
    {
        if (length > MaximumPayloadBytes)
            throw Error("FRAME_TOO_LARGE", "Command envelope exceeds its payload limit.");
        if (length == 0)
            throw Error("INVALID_JSON", "Command envelope payload is empty.");
    }

    private static ComConCommandProtocolException Error(string code, string message) => new(code, message);
}
