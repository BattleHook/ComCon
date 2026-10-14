using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ComCon.Core.Ipc;

public sealed record ComConIpcRequest(
    long Id, string Operation, string? SessionId, JsonElement Arguments);

public sealed class ComConIpcJsonLineReader(Stream stream)
{
    private readonly byte[] buffer = new byte[1024];
    private int offset;
    private int available;

    public async ValueTask<ComConIpcRequest> ReadRequestAsync(
        CancellationToken cancellationToken = default, TimeSpan? partialFrameTimeout = null)
    {
        using var frame = new MemoryStream();
        using var partialDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            partialDeadline.Token.ThrowIfCancellationRequested();
            if (offset == available)
            {
                available = await stream.ReadAsync(buffer, partialDeadline.Token).ConfigureAwait(false);
                offset = 0;
                if (available == 0)
                {
                    if (frame.Length == 0) throw new EndOfStreamException("IPC peer closed the connection.");
                    throw new InvalidDataException("IPC peer closed before terminating its frame.");
                }
            }

            if (frame.Length == 0 && partialFrameTimeout is { } timeout)
                partialDeadline.CancelAfter(timeout);
            var newline = Array.IndexOf(buffer, (byte)'\n', offset, available - offset);
            var count = newline < 0 ? available - offset : newline - offset;
            if (frame.Length + count >= ComConIpcProtocol.MaximumFrameBytes)
                throw new InvalidDataException("IPC frame exceeds its byte limit.");
            frame.Write(buffer, offset, count);
            offset += count;
            if (newline >= 0)
            {
                offset++;
                return ComConIpcProtocol.ParseRequest(frame.GetBuffer().AsSpan(0, (int)frame.Length));
            }
        }
    }
}

public static class ComConIpcProtocol
{
    public const int Version = 1;
    public const int MaximumFrameBytes = 16_384;
    private const int MaximumDepth = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> RequestProperties =
        ["v", "kind", "id", "op", "args", "sessionId"];

    public static ComConIpcRequest ParseRequest(ReadOnlySpan<byte> line)
    {
        if (line.Length == 0 || line.Length >= MaximumFrameBytes)
            throw new InvalidDataException("IPC frame is empty or exceeds its byte limit.");
        if (line[^1] == (byte)'\r') line = line[..^1];
        if (line.Length == 0 || line.IndexOf((byte)'\n') >= 0 || line.IndexOf((byte)'\r') >= 0)
            throw new InvalidDataException("IPC frame contains an invalid line terminator.");
        if (line.Length >= 3 && line[0] == 0xEF && line[1] == 0xBB && line[2] == 0xBF)
            throw new InvalidDataException("IPC frames must not include a UTF-8 byte-order mark.");

        string json;
        try
        {
            json = StrictUtf8.GetString(line);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("IPC frame is not valid UTF-8.", exception);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = MaximumDepth
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("IPC frame contains invalid or over-nested JSON.", exception);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("IPC request must be a JSON object.");

            EnsureUniqueProperties(root);
            foreach (var property in root.EnumerateObject())
                if (!RequestProperties.Contains(property.Name))
                    throw new InvalidDataException($"Unknown IPC request property '{property.Name}'.");

            if (!TryGetInteger(root, "v", out var version) || version != Version ||
                !TryGetString(root, "kind", out var kind) || kind != "request" ||
                !TryGetString(root, "id", out var idText) || !TryParseId(idText, out var id) ||
                !TryGetString(root, "op", out var operation) ||
                !root.TryGetProperty("args", out var arguments) || arguments.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("IPC request envelope is invalid or unsupported.");

            string? sessionId = null;
            if (root.TryGetProperty("sessionId", out var sessionElement))
            {
                if (sessionElement.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(sessionElement.GetString()))
                    throw new InvalidDataException("IPC sessionId must be a non-empty string.");
                sessionId = sessionElement.GetString();
            }

            return new(id, operation, sessionId, arguments.Clone());
        }
    }

    public static async ValueTask WriteJsonLineAsync<T>(
        Stream stream, T message, SemaphoreSlim writer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(writer);
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length + 1 > MaximumFrameBytes)
            throw new InvalidDataException("IPC response exceeds its byte limit.");

        await writer.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.Release();
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length != 0;
    }

    private static bool TryGetInteger(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value);
    }

    private static bool TryParseId(string text, out long id)
    {
        id = 0;
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) &&
            id > 0 && id.ToString(CultureInfo.InvariantCulture) == text;
    }

    private static void EnsureUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                    throw new InvalidDataException($"Duplicate IPC property '{property.Name}'.");
                EnsureUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                EnsureUniqueProperties(item);
        }
    }
}
