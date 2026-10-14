using System.Text;
using System.Text.Json;

namespace ComCon.Core.Ipc;

public sealed class DevelopmentFaultLog(string path)
{
    public const int MaximumFileBytes = BoundedJsonLinesFile.MaximumFileBytes;
    private readonly BoundedJsonLinesFile file = new(path, "ComConDevelopmentFaultLog_");

    public void Append(string component, int? profileId, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var line = JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow, component, profileId,
            message = message.Length > 2048 ? message[..2048] : message
        });
        var bytes = new UTF8Encoding(false).GetBytes(line + Environment.NewLine);
        if (bytes.Length > MaximumFileBytes)
            throw new ArgumentException("The development fault record exceeds the file-size limit.", nameof(component));
        file.Append(bytes);
    }
}
