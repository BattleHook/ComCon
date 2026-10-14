using System.Text.Json;

namespace ComCon.Core.Presentation;

public sealed record FirstUseState(bool WelcomeDismissed, DateTimeOffset? DismissedUtc);

public sealed class FirstUseStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public FirstUseStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public static FirstUseStore CreateDefault() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ComCon", "FirstUse.json"));

    // A missing, unreadable or corrupt file shows the welcome again rather than hiding it.
    public bool ShouldShowWelcome()
    {
        try
        {
            if (!File.Exists(FilePath)) return true;
            var state = JsonSerializer.Deserialize<FirstUseState>(File.ReadAllText(FilePath), JsonOptions);
            return state is null || !state.WelcomeDismissed;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    // Returns null on success, otherwise a message the caller must show to the user.
    public string? MarkWelcomeDismissed(DateTimeOffset nowUtc)
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new FirstUseState(true, nowUtc), JsonOptions);
            File.WriteAllText(FilePath, json);
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"ComCon could not save the welcome dismissal to '{FilePath}': {exception.Message} " +
                "The welcome will be shown again at the next start.";
        }
    }
}
